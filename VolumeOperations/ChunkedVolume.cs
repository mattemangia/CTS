using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CTSegmenter
{
    /// <summary>
    /// Efficient storage for large 3D grayscale volumes using a chunked approach
    /// to overcome array limitations with support for 30GB+ datasets
    /// </summary>
    public class ChunkedVolume : IDisposable, IGrayscaleVolumeData
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
        private byte[][] _chunks; // For in-memory mode
        private long _headerSize;
        private MemoryMappedFile _mmf; // For memory-mapped mode
        private MemoryMappedViewAccessor _viewAccessor; // Main view accessor for MM mode
        private readonly bool _useMemoryMapping;

        // Metadata
        private readonly int _bitsPerPixel; // 8 or 16 bit
        private double _pixelSize; // Size in meters per pixel

        // Interface properties
        public int ChunkDim => _chunkDim;
        public int ChunkCountX => _chunkCountX;
        public int ChunkCountY => _chunkCountY;
        public int ChunkCountZ => _chunkCountZ;
        public int TotalChunks => _chunkCountX * _chunkCountY * _chunkCountZ;
        public byte[][] Chunks => _chunks;

        // File header size (for memory-mapped files)
        private const int HEADER_SIZE = 36; // 8 ints (32 bytes) + 1 double (8 bytes)

        // Supported image extensions
        private static readonly string[] SupportedImageExtensions = {
            ".bmp", ".jpg", ".jpeg", ".png", ".tiff", ".tif", ".gif"
        };
        #endregion

        #region Constructors
        /// <summary>
        /// Creates a new in-memory volume with the specified dimensions
        /// </summary>
        public ChunkedVolume(int width, int height, int depth, int chunkDim = 256)
        {
            try
            {
                ValidateDimensions(width, height, depth, chunkDim);

                Width = width;
                Height = height;
                Depth = depth;
                _chunkDim = chunkDim;
                _bitsPerPixel = 8; // Default to 8-bit
                _pixelSize = 1e-6; // Default to 1 micron

                _chunkCountX = (width + chunkDim - 1) / chunkDim;
                _chunkCountY = (height + chunkDim - 1) / chunkDim;
                _chunkCountZ = (depth + chunkDim - 1) / chunkDim;

                _chunks = new byte[_chunkCountX * _chunkCountY * _chunkCountZ][];
                _useMemoryMapping = false;

                Logger.Log($"[ChunkedVolume] Creating in-memory volume: {Width}x{Height}x{Depth}, chunkDim={_chunkDim}");
                InitializeChunks();
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChunkedVolume] Construction error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Creates a volume that uses memory-mapped file storage
        /// </summary>
        public ChunkedVolume(int width, int height, int depth, int chunkDim,
                    MemoryMappedFile mmf, MemoryMappedViewAccessor viewAccessor,
                    long headerSize = 0)
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

                _chunkCountX = (width + chunkDim - 1) / chunkDim;
                _chunkCountY = (height + chunkDim - 1) / chunkDim;
                _chunkCountZ = (depth + chunkDim - 1) / chunkDim;

                _mmf = mmf;
                _viewAccessor = viewAccessor;
                _useMemoryMapping = true;
                _chunks = null; // Not using in-memory chunks

                // Store the header size for offset calculations
                _headerSize = headerSize;

                Logger.Log($"[ChunkedVolume] Created memory-mapped volume: {Width}x{Height}x{Depth}, " +
                           $"chunkDim={_chunkDim}, chunks={_chunkCountX}x{_chunkCountY}x{_chunkCountZ}, headerSize={_headerSize}");
            }
            catch (Exception ex)
            {
                // Clean up resources to avoid leaks
                if (viewAccessor != null)
                {
                    try { viewAccessor.Dispose(); } catch { }
                }

                if (mmf != null)
                {
                    try { mmf.Dispose(); } catch { }
                }

                Logger.Log($"[ChunkedVolume] Construction error: {ex.Message}");
                throw;
            }
        }
        #endregion

        #region Public Interface
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
                    Logger.Log($"[ChunkedVolume] Get voxel error at ({x},{y},{z}): {ex.Message}");
                    return 0; // Return black on error
                }
            }

            set
            {
                try
                {
                    ValidateCoordinates(x, y, z);

                    var (chunkIndex, offset) = CalculateChunkIndexAndOffset(x, y, z);

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
                    Logger.Log($"[ChunkedVolume] Set voxel error at ({x},{y},{z}): {ex.Message}");
                }
            }
        }
        /// <summary>
        /// Creates a volume from a folder of image slices with optimized performance while preventing scrambling
        /// </summary>
        public static ChunkedVolume FromFolder(string folder, int chunkDim, ProgressForm progress, bool useMemoryMapping = false)
        {
            try
            {
                Logger.Log($"[FromFolder] Loading volume from folder: {folder}, useMemoryMapping={useMemoryMapping}");

                // Get all supported image files
                var allImagePaths = GetAllSupportedImagePaths(folder);
                if (allImagePaths.Count == 0)
                {
                    throw new FileNotFoundException("No supported image files found in the folder.");
                }

                // Filter by file extension and sort numerically
                string firstImageExtension = Path.GetExtension(allImagePaths[0]).ToLower();
                var slicePaths = SortImagePathsNumerically(
                    allImagePaths.Where(p => Path.GetExtension(p).ToLower() == firstImageExtension).ToList());

                Logger.Log($"[FromFolder] Using {slicePaths.Count} images with extension {firstImageExtension}");

                // Validate the first and last images
                ValidateImageSet(slicePaths);

                // Get dimensions from the first image
                var dimensions = GetVolumeDimensionsOptimized(slicePaths[0]);
                int width = dimensions.Width;
                int height = dimensions.Height;
                int depth = slicePaths.Count;
                int bitsPerPixel = dimensions.BitsPerPixel;
                double pixelSize = 1e-6; // Default to 1 µm

                Logger.Log($"[FromFolder] Volume dimensions: {width}x{height}x{depth}, bitsPerPixel={bitsPerPixel}");

                // For in-memory mode, process directly
                if (!useMemoryMapping)
                {
                    var volume = new ChunkedVolume(width, height, depth, chunkDim);
                    volume._pixelSize = pixelSize;
                    ProcessSlicesParallel(volume, slicePaths, progress);
                    return volume;
                }

                // For memory mapping, create the file with embedded header
                string volumeBinPath = Path.Combine(folder, "volume.bin");

                // Calculate dimensions
                int cntX = (width + chunkDim - 1) / chunkDim;
                int cntY = (height + chunkDim - 1) / chunkDim;
                int cntZ = (depth + chunkDim - 1) / chunkDim;
                long chunkSize = (long)chunkDim * chunkDim * chunkDim;
                long totalChunks = cntX * cntY * cntZ;
                int headerSize = 36; // 9 integers for header (36 bytes)
                long totalSize = headerSize + totalChunks * chunkSize;

                Logger.Log($"[FromFolder] Creating volume.bin, size: {totalSize:N0} bytes");

                // Delete existing file if needed
                if (File.Exists(volumeBinPath))
                {
                    try { File.Delete(volumeBinPath); }
                    catch (Exception ex) { Logger.Log($"[FromFolder] Warning: Could not delete existing file: {ex.Message}"); }
                }

                // Create the file and write header
                using (FileStream fs = new FileStream(volumeBinPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    // Write header information
                    using (BinaryWriter bw = new BinaryWriter(fs, System.Text.Encoding.Default, leaveOpen: true))
                    {
                        bw.Write(width);
                        bw.Write(height);
                        bw.Write(depth);
                        bw.Write(chunkDim);
                        bw.Write(bitsPerPixel);
                        bw.Write(pixelSize);
                        bw.Write(cntX);
                        bw.Write(cntY);
                        bw.Write(cntZ);
                    }

                    // Pre-allocate space for the entire file
                    fs.SetLength(totalSize);
                    fs.Flush(true);
                }

                // Create the memory-mapped file
                MemoryMappedFile mmf = null;
                MemoryMappedViewAccessor viewAccessor = null;

                try
                {
                    // Generate a unique mapping name
                    string mapName = $"CTSegmenter_Volume_{Guid.NewGuid()}";

                    // Create memory mapping
                    mmf = MemoryMappedFile.CreateFromFile(
                        volumeBinPath,
                        FileMode.Open,
                        mapName,
                        0,
                        MemoryMappedFileAccess.ReadWrite);

                    // Create view accessor
                    viewAccessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                    // Create volume
                    ChunkedVolume volume = new ChunkedVolume(width, height, depth, chunkDim, mmf, viewAccessor, headerSize);
                    volume._pixelSize = pixelSize;

                    // Process slices in optimized parallel mode
                    ProcessSlicesParallelOptimized(volume, slicePaths, progress);

                    // Create volume.chk for backward compatibility
                    CreateVolumeChk(folder, width, height, depth, chunkDim, pixelSize);

                    return volume;
                }
                catch (Exception ex)
                {
                    // Clean up resources
                    viewAccessor?.Dispose();
                    mmf?.Dispose();

                    Logger.Log($"[FromFolder] Error: {ex.Message}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FromFolder] Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Sorts image paths numerically for correct slice order
        /// </summary>
        private static List<string> SortImagePathsNumerically(List<string> paths)
        {
            // Extract numbers from filenames for proper numeric sorting
            return paths.OrderBy(path =>
            {
                string filename = Path.GetFileNameWithoutExtension(path);
                // Extract numeric part (handles filenames like "slice_001.bmp")
                string numericPart = new string(filename.Where(char.IsDigit).ToArray());
                if (!string.IsNullOrEmpty(numericPart) && int.TryParse(numericPart, out int number))
                    return number;
                else
                    return 0;
            }).ToList();
        }

        /// <summary>
        /// Gets volume dimensions from a single image file (optimized)
        /// </summary>
        private static (int Width, int Height, int BitsPerPixel) GetVolumeDimensionsOptimized(string imagePath)
        {
            using (var bmp = new Bitmap(imagePath))
            {
                int bitsPerPixel = Image.GetPixelFormatSize(bmp.PixelFormat);
                return (bmp.Width, bmp.Height, bitsPerPixel);
            }
        }
        /// <summary>
        /// Process slices sequentially to avoid synchronization issues
        /// </summary>
        private static void ProcessSlicesSequentially(ChunkedVolume volume, List<string> slicePaths, ProgressForm progress)
        {
            int totalSlices = slicePaths.Count;
            var exceptions = new List<Exception>();

            for (int z = 0; z < totalSlices; z++)
            {
                try
                {
                    string slicePath = slicePaths[z];
                    Logger.Log($"[ProcessSlicesSequentially] Processing slice {z + 1}/{totalSlices}: {Path.GetFileName(slicePath)}");

                    using (Bitmap originalBmp = LoadBitmapFromFile(slicePath))
                    using (Bitmap processBmp = ConvertTo24bpp(originalBmp))
                    using (FastBitmap fastBmp = new FastBitmap(processBmp))
                    {
                        fastBmp.LockBits();

                        // Process the slice one pixel at a time
                        for (int y = 0; y < volume.Height; y++)
                        {
                            for (int x = 0; x < volume.Width; x++)
                            {
                                byte grayValue = fastBmp.GetGrayValue(x, y);
                                volume[x, y, z] = grayValue;
                            }
                        }
                    }

                    // Update progress
                    progress?.SafeUpdateProgress(z + 1, totalSlices);
                }
                catch (Exception ex)
                {
                    string errorMsg = $"Error processing slice {z}: {ex.Message}";
                    Logger.Log($"[ProcessSlicesSequentially] {errorMsg}");
                    exceptions.Add(new Exception(errorMsg, ex));

                    // Continue processing other slices
                }
            }

            // If any errors occurred, throw an aggregate exception
            if (exceptions.Count > 0)
            {
                throw new AggregateException("Errors occurred while processing image slices:", exceptions);
            }
        }


        /// <summary>
        /// Saves the volume to a binary file
        /// </summary>
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
                Logger.Log($"[SaveAsBin] Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Writes chunks to a binary writer
        /// </summary>
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
                    // For memory-mapped mode, read each chunk and write to output
                    byte[] buffer = new byte[chunkSize];

                    for (int cz = 0; cz < _chunkCountZ; cz++)
                    {
                        for (int cy = 0; cy < _chunkCountY; cy++)
                        {
                            for (int cx = 0; cx < _chunkCountX; cx++)
                            {
                                int chunkIndex = GetChunkIndex(cx, cy, cz);
                                long offset = CalculateGlobalOffset(chunkIndex, 0);

                                // Read the chunk into buffer
                                _viewAccessor.ReadArray(offset, buffer, 0, chunkSize);

                                // Write the buffer to output
                                bw.Write(buffer, 0, chunkSize);
                            }
                        }
                    }
                }
                Logger.Log($"[WriteChunks] {TotalChunks} chunks written");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WriteChunks] Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Reads chunks from a binary reader
        /// </summary>
        public void ReadChunks(BinaryReader br)
        {
            int chunkSize = _chunkDim * _chunkDim * _chunkDim;

            try
            {
                if (!_useMemoryMapping)
                {
                    for (int i = 0; i < _chunks.Length; i++)
                    {
                        _chunks[i] = br.ReadBytes(chunkSize);
                    }
                }
                else
                {
                    // For memory-mapped mode, read from source and write to memory-mapped file
                    byte[] buffer = new byte[chunkSize];

                    for (int cz = 0; cz < _chunkCountZ; cz++)
                    {
                        for (int cy = 0; cy < _chunkCountY; cy++)
                        {
                            for (int cx = 0; cx < _chunkCountX; cx++)
                            {
                                int chunkIndex = GetChunkIndex(cx, cy, cz);
                                long offset = CalculateGlobalOffset(chunkIndex, 0);

                                // Read chunk from source
                                br.Read(buffer, 0, chunkSize);

                                // Write to memory-mapped file
                                _viewAccessor.WriteArray(offset, buffer, 0, chunkSize);
                            }
                        }
                    }
                }
                Logger.Log($"[ReadChunks] {TotalChunks} chunks read");
            }
            catch (Exception ex)
            {
                Logger.Log($"[ReadChunks] Error: {ex.Message}");
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
                if (!_useMemoryMapping)
                {
                    return _chunks[chunkIndex];
                }
                else
                {
                    byte[] buffer = new byte[chunkSize];
                    long offset = CalculateGlobalOffset(chunkIndex, 0);
                    _viewAccessor.ReadArray(offset, buffer, 0, chunkSize);
                    return buffer;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GetChunkBytes] Error for chunk {chunkIndex}: {ex.Message}");
                return new byte[chunkSize]; // Return empty chunk on error
            }
        }

        /// <summary>
        /// Gets the index of a chunk from its coordinates
        /// </summary>
        public int GetChunkIndex(int cx, int cy, int cz)
        {
            return (cz * _chunkCountY + cy) * _chunkCountX + cx;
        }
        #endregion

        #region Private Implementation
        /// <summary>
        /// Initialize empty chunks for in-memory mode
        /// </summary>
        private void InitializeChunks()
        {
            if (!_useMemoryMapping && _chunks != null)
            {
                int chunkSize = _chunkDim * _chunkDim * _chunkDim;

                for (int i = 0; i < _chunks.Length; i++)
                {
                    _chunks[i] = new byte[chunkSize];
                }

                Logger.Log($"[InitializeChunks] Initialized {_chunks.Length} chunks, each {chunkSize} bytes");
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
        /// Write the header to a binary writer
        /// </summary>
        private void WriteHeader(BinaryWriter bw)
        {
            bw.Write(Width);
            bw.Write(Height);
            bw.Write(Depth);
            bw.Write(_chunkDim);
            bw.Write(_bitsPerPixel);
            bw.Write(_pixelSize);
            bw.Write(_chunkCountX);
            bw.Write(_chunkCountY);
            bw.Write(_chunkCountZ);
        }

        /// <summary>
        /// Create volume.chk file for backward compatibility
        /// </summary>
        private static void CreateVolumeChk(string folder, int width, int height, int depth, int chunkDim, double pixelSize)
        {
            try
            {
                string chkPath = Path.Combine(folder, "volume.chk");

                using (FileStream fs = new FileStream(chkPath, FileMode.Create))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(width);
                    bw.Write(height);
                    bw.Write(depth);
                    bw.Write(chunkDim);
                    bw.Write(pixelSize);
                }

                Logger.Log($"[CreateVolumeChk] Created: {chkPath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[CreateVolumeChk] Error: {ex.Message}");
                // Don't throw, just log and continue
            }
        }

        /// <summary>
        /// Get all supported image files from a folder
        /// </summary>
        private static List<string> GetAllSupportedImagePaths(string folder)
        {
            return Directory.GetFiles(folder)
                .Where(f => SupportedImageExtensions.Contains(Path.GetExtension(f).ToLower()))
                .ToList();
        }

        /// <summary>
        /// Validate dimensions
        /// </summary>
        private static void ValidateDimensions(int width, int height, int depth, int chunkDim)
        {
            if (width <= 0 || height <= 0 || depth <= 0 || chunkDim <= 0)
                throw new ArgumentException("Invalid dimensions or chunk size.");
        }

        /// <summary>
        /// Validate coordinates
        /// </summary>
        private void ValidateCoordinates(int x, int y, int z)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth)
                throw new IndexOutOfRangeException($"Coordinates ({x},{y},{z}) out of range.");
        }

        /// <summary>
        /// Validate the image set is consistent
        /// </summary>
        private static void ValidateImageSet(IReadOnlyList<string> imagePaths)
        {
            if (imagePaths.Count == 0)
                throw new ArgumentException("No images to process.");

            // Check that all images have the same extension
            string firstExt = Path.GetExtension(imagePaths[0]).ToLower();
            if (!imagePaths.All(p => Path.GetExtension(p).ToLower() == firstExt))
                throw new ArgumentException("Image set contains mixed file formats.");

            // Check first image can be opened
            using (var img = new Bitmap(imagePaths[0]))
            {
                if (img.Width <= 0 || img.Height <= 0)
                    throw new ArgumentException("First image has invalid dimensions.");
            }
        }

        /// <summary>
        /// Get volume dimensions from the image set
        /// </summary>
        private static (int Width, int Height, int Depth, int BitsPerPixel) GetVolumeDimensions(IReadOnlyList<string> imagePaths)
        {
            using (var bmp = new Bitmap(imagePaths[0]))
            {
                // Detect bits per pixel
                int bitsPerPixel = 8;
                if (bmp.PixelFormat == PixelFormat.Format16bppGrayScale)
                    bitsPerPixel = 16;

                return (bmp.Width, bmp.Height, imagePaths.Count, bitsPerPixel);
            }
        }

        /// <summary>
        /// Process all slices and load them into the volume
        /// </summary>
        
        private static void ProcessSlices(ChunkedVolume volume, List<string> slicePaths, ProgressForm progress)
        {
            var exceptions = new ConcurrentQueue<Exception>();
            int totalSlices = slicePaths.Count;

            // First, process slices sequentially to ensure correct synchronization
            for (int z = 0; z < totalSlices; z++)
            {
                try
                {
                    Logger.Log($"[ProcessSlices] Processing slice {z}/{totalSlices}: {Path.GetFileName(slicePaths[z])}");

                    // Load the bitmap for this slice
                    using (var bitmap = LoadBitmapFromFile(slicePaths[z]))
                    {
                        // Convert to 24bpp if needed
                        using (var processedBitmap = ConvertTo24bpp(bitmap))
                        using (var fastBmp = new FastBitmap(processedBitmap))
                        {
                            fastBmp.LockBits();

                            // Process each pixel in the slice
                            for (int y = 0; y < volume.Height; y++)
                            {
                                for (int x = 0; x < volume.Width; x++)
                                {
                                    byte gray = fastBmp.GetGrayValue(x, y);
                                    volume[x, y, z] = gray;
                                }
                            }
                        }
                    }

                    // Update progress
                    progress?.SafeUpdateProgress(z + 1, totalSlices);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ProcessSlices] Error processing slice {z}: {ex.Message}");
                    exceptions.Enqueue(new Exception($"Error on slice {z}: {ex.Message}", ex));
                }
            }

            if (!exceptions.IsEmpty)
            {
                throw new AggregateException("Errors processing image slices:", exceptions);
            }
        }
        /// <summary>
        /// Process slices in parallel for in-memory volumes
        /// </summary>
        private static void ProcessSlicesParallel(ChunkedVolume volume, List<string> slicePaths, ProgressForm progress)
        {
            int totalSlices = slicePaths.Count;
            var exceptions = new ConcurrentQueue<Exception>();
            int processedCount = 0;
            int maxThreads = Math.Max(1, Environment.ProcessorCount - 1);

            // Process in batches of optimal size
            int batchSize = Math.Min(20, Math.Max(1, totalSlices / maxThreads));

            for (int batchStart = 0; batchStart < totalSlices; batchStart += batchSize)
            {
                int currentBatchSize = Math.Min(batchSize, totalSlices - batchStart);
                int batchEnd = batchStart + currentBatchSize;

                // Process each slice in this batch in parallel
                Parallel.For(batchStart, batchEnd, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, z =>
                {
                    try
                    {
                        using (var bitmap = new Bitmap(slicePaths[z]))
                        {
                            // Process each pixel in the slice
                            for (int y = 0; y < volume.Height; y++)
                            {
                                for (int x = 0; x < volume.Width; x++)
                                {
                                    Color c = bitmap.GetPixel(x, y);
                                    byte grayValue = (byte)((c.R + c.G + c.B) / 3);
                                    volume[x, y, z] = grayValue;
                                }
                            }
                        }

                        // Update progress
                        int current = Interlocked.Increment(ref processedCount);
                        progress?.SafeUpdateProgress(current, totalSlices);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(new Exception($"Error on slice {z}: {ex.Message}", ex));
                    }
                });

                // Allow cleanup between batches
                GC.Collect(0);
            }

            if (!exceptions.IsEmpty)
            {
                throw new AggregateException("Errors processing slices:", exceptions);
            }
        }

        /// <summary>
        /// Writes a buffer of pixel data to a slice in the volume
        /// </summary>
        private static void WriteBufferToVolume(ChunkedVolume volume, byte[] buffer, int z)
        {
            int width = volume.Width;
            int height = volume.Height;

            // Write pixels to volume slice
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte value = buffer[y * width + x];
                    volume[x, y, z] = value;
                }
            }
        }
        /// <summary>
        /// Fast bitmap loading optimized for performance
        /// </summary>
        private static Bitmap LoadBitmapOptimized(string filePath)
        {
            // Create stream and load bitmap
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Load directly from stream for better performance
                return new Bitmap(fs);
            }
        }

        /// <summary>
        /// Extracts grayscale pixels directly to a buffer without using lock/unlock
        /// </summary>
        private static unsafe void ExtractGrayscalePixels(Bitmap bitmap, byte[] buffer)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;

            // Convert to correct format if needed
            if (bitmap.PixelFormat != PixelFormat.Format24bppRgb &&
                bitmap.PixelFormat != PixelFormat.Format32bppArgb)
            {
                using (Bitmap converted = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                using (Graphics g = Graphics.FromImage(converted))
                {
                    g.DrawImage(bitmap, 0, 0, width, height);
                    ExtractGrayscalePixels(converted, buffer);
                    return;
                }
            }

            // Lock bits for faster access
            BitmapData bmpData = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                bitmap.PixelFormat);

            try
            {
                int bytesPerPixel = Image.GetPixelFormatSize(bitmap.PixelFormat) / 8;
                int stride = bmpData.Stride;

                // Use unsafe code for maximum speed
                byte* ptr = (byte*)bmpData.Scan0.ToPointer();

                // Extract pixels
                fixed (byte* bufPtr = buffer)
                {
                    byte* destPtr = bufPtr;

                    for (int y = 0; y < height; y++)
                    {
                        byte* row = ptr + (y * stride);

                        for (int x = 0; x < width; x++)
                        {
                            // Get RGB values
                            byte b = row[x * bytesPerPixel];
                            byte g = row[x * bytesPerPixel + 1];
                            byte r = row[x * bytesPerPixel + 2];

                            // Calculate grayscale and store directly in buffer
                            *destPtr++ = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
                        }
                    }
                }
            }
            finally
            {
                // Always unlock the bitmap
                bitmap.UnlockBits(bmpData);
            }
        }

        /// <summary>
        /// Optimized parallel slice processing that maintains correct slice ordering
        /// </summary>
        private static void ProcessSlicesParallelOptimized(ChunkedVolume volume, List<string> slicePaths, ProgressForm progress)
        {
            int totalSlices = slicePaths.Count;
            int width = volume.Width;
            int height = volume.Height;

            // Use a concurrent queue to track errors
            var exceptions = new ConcurrentQueue<Exception>();

            // Track progress safely across threads
            int processedCount = 0;

            // Determine optimal batch size based on processor count
            int processorCount = Environment.ProcessorCount;
            int optimalBatchSize = Math.Max(1, totalSlices / (processorCount * 2));
            int batchSize = Math.Min(20, Math.Max(1, optimalBatchSize)); // Between 1 and 20

            // Store loaded slices in thread-local storage to avoid memory issues
            ThreadLocal<byte[]> bufferCache = new ThreadLocal<byte[]>(() => new byte[width * height]);

            // Process in batches to control memory usage
            for (int batchStart = 0; batchStart < totalSlices; batchStart += batchSize)
            {
                int currentBatchSize = Math.Min(batchSize, totalSlices - batchStart);
                int batchEnd = batchStart + currentBatchSize;

                // Process this batch in parallel
                Parallel.For(batchStart, batchEnd, new ParallelOptions { MaxDegreeOfParallelism = processorCount }, z =>
                {
                    try
                    {
                        string slicePath = slicePaths[z];
                        byte[] buffer = bufferCache.Value; // Get thread-local buffer

                        // Load and process the image
                        using (var bitmap = LoadBitmapOptimized(slicePath))
                        {
                            // Extract pixel data directly to the buffer
                            ExtractGrayscalePixels(bitmap, buffer);

                            // Write the buffer to the volume slice
                            WriteBufferToVolume(volume, buffer, z);
                        }

                        // Update progress atomically
                        int currentCount = Interlocked.Increment(ref processedCount);
                        progress?.SafeUpdateProgress(currentCount, totalSlices);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(new Exception($"Error on slice {z}: {ex.Message}", ex));
                    }
                });

                // Allow GC to clean up between batches
                GC.Collect(0);
            }

            // Check for errors
            if (!exceptions.IsEmpty)
            {
                throw new AggregateException("Errors processing slices:", exceptions);
            }

            // Clean up
            bufferCache.Dispose();
        }

        /// <summary>
        /// Process a single slice
        /// </summary>
        private static void ProcessSlice(ChunkedVolume volume, int z, FastBitmap fastBmp)
        {
            int width = fastBmp.Width;
            int height = fastBmp.Height;

            // Process in parallel for better performance
            int maxCores = Math.Max(1, Environment.ProcessorCount / 2);
            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = maxCores }, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    byte gray = fastBmp.GetGrayValue(x, y);
                    volume[x, y, z] = gray;
                }
            });
        }
        /// <summary>
        /// Converts a bitmap to 24bpp format
        /// </summary>
        private static Bitmap ConvertTo24bpp(Bitmap source)
        {
            if (source.PixelFormat == PixelFormat.Format24bppRgb)
                return new Bitmap(source); // Return a copy if already 24bpp

            Bitmap result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }
            return result;
        }
        /// <summary>
        /// Loads a bitmap from a file with proper resource handling
        /// </summary>
        private static Bitmap LoadBitmapFromFile(string filePath)
        {
            try
            {
                // Read the entire file into memory to avoid file locking issues
                byte[] fileBytes = File.ReadAllBytes(filePath);
                using (var ms = new MemoryStream(fileBytes))
                {
                    // Create a copy of the bitmap to avoid GDI+ issues
                    using (var originalBmp = new Bitmap(ms))
                    {
                        // Return a clone to ensure we have a clean copy
                        return new Bitmap(originalBmp);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[LoadBitmapFromFile] Error loading {filePath}: {ex.Message}");
                throw;
            }
        }
        #endregion

        #region FastBitmap Helper Class
        /// <summary>
        /// Helper class for fast bitmap access
        /// </summary>
        public sealed class FastBitmap : IDisposable
        {
            private readonly Bitmap _bitmap;
            private BitmapData _data;
            private byte[] _bytes;
            private bool _disposed;
            private int _width;
            private int _height;
            private int _stride;
            private PixelFormat _format;

            public int Width => _width;
            public int Height => _height;

            public FastBitmap(Bitmap bitmap)
            {
                _bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
                _width = bitmap.Width;
                _height = bitmap.Height;
                _format = bitmap.PixelFormat;
            }

            public void LockBits()
            {
                _data = _bitmap.LockBits(
                    new Rectangle(0, 0, _width, _height),
                    ImageLockMode.ReadOnly,
                    _format);

                _stride = _data.Stride;

                // Copy bitmap data to managed array for faster access
                _bytes = new byte[_stride * _height];
                System.Runtime.InteropServices.Marshal.Copy(_data.Scan0, _bytes, 0, _bytes.Length);
            }

            public byte GetGrayValue(int x, int y)
            {
                if (x < 0 || x >= _width || y < 0 || y >= _height)
                    throw new ArgumentOutOfRangeException($"Coordinates ({x},{y}) out of bounds.");

                int bytesPerPixel;

                switch (_format)
                {
                    case PixelFormat.Format8bppIndexed:
                        return _bytes[y * _stride + x];

                    case PixelFormat.Format24bppRgb:
                        bytesPerPixel = 3;
                        break;

                    case PixelFormat.Format32bppRgb:
                    case PixelFormat.Format32bppArgb:
                    case PixelFormat.Format32bppPArgb:
                        bytesPerPixel = 4;
                        break;

                    case PixelFormat.Format16bppGrayScale:
                        // Handle 16-bit grayscale (convert to 8-bit)
                        int offset = y * _stride + x * 2;
                        ushort val = BitConverter.ToUInt16(_bytes, offset);
                        return (byte)(val >> 8); // Scale down to 8-bit

                    default:
                        // For other formats, fall back to GetPixel
                        Color c = _bitmap.GetPixel(x, y);
                        return (byte)((c.R + c.G + c.B) / 3);
                }

                // Calculate RGB values
                int pixelOffset = y * _stride + x * bytesPerPixel;
                byte b = _bytes[pixelOffset];
                byte g = _bytes[pixelOffset + 1];
                byte r = _bytes[pixelOffset + 2];

                // Standard grayscale formula
                return (byte)(0.299 * r + 0.587 * g + 0.114 * b);
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _bitmap.UnlockBits(_data);
                    _disposed = true;
                }
            }
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
                            _viewAccessor = null;
                        }

                        if (_mmf != null)
                        {
                            _mmf.Dispose();
                            _mmf = null;
                        }

                        // Help GC with large arrays
                        if (_chunks != null)
                        {
                            for (int i = 0; i < _chunks.Length; i++)
                            {
                                _chunks[i] = null;
                            }
                            _chunks = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ChunkedVolume] Error during disposal: {ex.Message}");
                    }
                }

                _disposed = true;
            }
        }

        ~ChunkedVolume()
        {
            Dispose(false);
        }
        #endregion
    }
}
