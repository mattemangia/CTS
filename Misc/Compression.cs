using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;
using System.IO.MemoryMappedFiles;

namespace CTS.Compression
{
    /// <summary>
    /// Compresses CTS volumes using 3D chunk-based compression
    /// </summary>
    public class ChunkedVolumeCompressor
    {
        private const string SIGNATURE = "CTS3D";
        private const int VERSION = 1;

        // Compression settings
        private int _compressionLevel;
        private bool _usePredictiveCoding;
        private bool _useRunLengthEncoding;

        // Progress reporting
        private IProgress<int> _progress;
        private long _totalChunks;
        private long _processedChunks;

        public ChunkedVolumeCompressor(int compressionLevel = 5, bool predictiveCoding = true, bool runLengthEncoding = true)
        {
            _compressionLevel = Math.Max(1, Math.Min(9, compressionLevel));
            _usePredictiveCoding = predictiveCoding;
            _useRunLengthEncoding = runLengthEncoding;
        }
        /// <summary>
        /// Compress the currently loaded volume from MainForm
        /// </summary>
        public async Task CompressLoadedVolumeAsync(MainForm mainForm, string outputPath, IProgress<int> progress)
        {
            _progress = progress;
            Logger.Log($"[ChunkedVolumeCompressor] Starting compression of loaded volume to {outputPath}");

            try
            {
                if (mainForm.volumeData == null && mainForm.volumeLabels == null)
                {
                    throw new InvalidOperationException("No volume data loaded");
                }

                var volumeData = mainForm.volumeData;
                var volumeLabels = mainForm.volumeLabels;

                // Create header from loaded data
                VolumeHeader header = new VolumeHeader
                {
                    Width = volumeData?.Width ?? volumeLabels.Width,
                    Height = volumeData?.Height ?? volumeLabels.Height,
                    Depth = volumeData?.Depth ?? volumeLabels.Depth,
                    ChunkDim = volumeData?.ChunkDim ?? volumeLabels.ChunkDim,
                    PixelSize = mainForm.pixelSize,
                    BitsPerPixel = 8,
                    HasLabels = volumeLabels != null
                };

                header.ChunkCountX = (header.Width + header.ChunkDim - 1) / header.ChunkDim;
                header.ChunkCountY = (header.Height + header.ChunkDim - 1) / header.ChunkDim;
                header.ChunkCountZ = (header.Depth + header.ChunkDim - 1) / header.ChunkDim;

                _totalChunks = header.ChunkCountX * header.ChunkCountY * header.ChunkCountZ;
                if (header.HasLabels)
                {
                    _totalChunks *= 2;
                }
                _processedChunks = 0;

                Logger.Log($"[ChunkedVolumeCompressor] Compressing loaded {header.Width}x{header.Height}x{header.Depth} volume");

                using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(outputStream))
                {
                    // Write compressed file header
                    WriteCompressedHeader(writer, header, header.HasLabels);

                    // Compress volume data if present
                    if (volumeData != null)
                    {
                        await CompressLoadedVolumeDataAsync(volumeData, writer, header);
                    }

                    // Compress labels if present
                    if (volumeLabels != null)
                    {
                        Logger.Log("[ChunkedVolumeCompressor] Compressing loaded label data...");
                        await CompressLoadedLabelsAsync(volumeLabels, writer, header);
                    }
                }

                Logger.Log($"[ChunkedVolumeCompressor] Compression completed. Output: {outputPath}");
                progress?.Report(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChunkedVolumeCompressor] Error: {ex.Message}");
                throw;
            }
        }
        private async Task CompressLoadedLabelsAsync(ILabelVolumeData volumeLabels, BinaryWriter writer, VolumeHeader header)
        {
            int totalChunks = volumeLabels.ChunkCountX * volumeLabels.ChunkCountY * volumeLabels.ChunkCountZ;
            int chunkSize = volumeLabels.ChunkDim * volumeLabels.ChunkDim * volumeLabels.ChunkDim;

            writer.Write(volumeLabels.ChunkDim);
            writer.Write(volumeLabels.ChunkCountX);
            writer.Write(volumeLabels.ChunkCountY);
            writer.Write(volumeLabels.ChunkCountZ);

            for (int i = 0; i < totalChunks; i++)
            {
                byte[] chunkData = volumeLabels.GetChunkBytes(i);
                byte[] compressed = CompressLabelChunk(chunkData);

                writer.Write(compressed.Length);
                writer.Write(compressed);

                _processedChunks++;
                _progress?.Report((int)(_processedChunks * 100 / _totalChunks));
            }
        }
        private async Task CompressLoadedVolumeDataAsync(IGrayscaleVolumeData volumeData, BinaryWriter writer, VolumeHeader header)
        {
            int chunkSize = header.ChunkDim * header.ChunkDim * header.ChunkDim;
            int totalChunks = header.ChunkCountX * header.ChunkCountY * header.ChunkCountZ;

            // Process chunks in parallel
            var compressedChunks = new ConcurrentDictionary<int, byte[]>();
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount);

            var tasks = new List<Task>();

            for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                int index = chunkIndex; // Capture for closure

                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        // Get chunk data directly from the loaded volume
                        byte[] chunkData = volumeData.GetChunkBytes(index);

                        // Compress chunk
                        byte[] compressed = Compress3DChunk(chunkData, header.ChunkDim);
                        compressedChunks[index] = compressed;

                        Interlocked.Increment(ref _processedChunks);
                        _progress?.Report((int)(_processedChunks * 100 / _totalChunks));
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);

            // Write compressed chunks in order
            writer.Write(totalChunks);
            for (int i = 0; i < totalChunks; i++)
            {
                var compressed = compressedChunks[i];
                writer.Write(compressed.Length);
                writer.Write(compressed);
            }
        }
        /// <summary>
        /// Compress a CTS volume to a compressed file
        /// </summary>
        public async Task CompressAsync(string inputPath, string outputPath, IProgress<int> progress)
        {
            _progress = progress;
            Logger.Log($"[ChunkedVolumeCompressor] Starting compression of {inputPath} to {outputPath}");

            try
            {
                // Check if inputPath is a folder or a file
                bool isFolder = Directory.Exists(inputPath);
                string volumePath, labelsPath;

                if (isFolder)
                {
                    volumePath = Path.Combine(inputPath, "volume.bin");
                    labelsPath = Path.Combine(inputPath, "labels.bin");
                }
                else
                {
                    // If it's a file, assume it's volume.bin and look for labels.bin in same directory
                    volumePath = inputPath;
                    labelsPath = Path.Combine(Path.GetDirectoryName(inputPath), "labels.bin");
                }

                if (!File.Exists(volumePath))
                {
                    throw new FileNotFoundException($"Volume file not found: {volumePath}");
                }

                // Read header from volume file to get dimensions
                VolumeHeader header;
                using (var fs = new FileStream(volumePath, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs))
                {
                    header = ReadVolumeHeader(br);
                }

                // Calculate total chunks
                _totalChunks = header.ChunkCountX * header.ChunkCountY * header.ChunkCountZ;
                if (File.Exists(labelsPath))
                {
                    _totalChunks *= 2; // Double for labels
                }
                _processedChunks = 0;

                Logger.Log($"[ChunkedVolumeCompressor] Compressing {header.Width}x{header.Height}x{header.Depth} volume with {_totalChunks} chunks");

                using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(outputStream))
                {
                    // Write compressed file header
                    WriteCompressedHeader(writer, header, File.Exists(labelsPath));

                    // Compress volume data
                    await CompressVolumeFileAsync(volumePath, writer, header);

                    // Compress labels if they exist
                    if (File.Exists(labelsPath))
                    {
                        Logger.Log("[ChunkedVolumeCompressor] Compressing label data...");
                        await CompressLabelsFileAsync(labelsPath, writer);
                    }
                }

                Logger.Log($"[ChunkedVolumeCompressor] Compression completed. Output: {outputPath}");
                progress?.Report(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChunkedVolumeCompressor] Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Decompress a CTS compressed file back to volume files
        /// </summary>
        public async Task DecompressAsync(string inputPath, string outputPath, IProgress<int> progress)
        {
            _progress = progress;
            Logger.Log($"[ChunkedVolumeCompressor] Starting decompression of {inputPath} to {outputPath}");

            try
            {
                // Create output directory if it doesn't exist
                Directory.CreateDirectory(outputPath);

                using (var inputStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(inputStream))
                {
                    // Read and validate header
                    var header = ReadCompressedHeader(reader);

                    string volumePath = Path.Combine(outputPath, "volume.bin");
                    string labelsPath = Path.Combine(outputPath, "labels.bin");

                    // Calculate total chunks for progress
                    _totalChunks = header.ChunkCountX * header.ChunkCountY * header.ChunkCountZ;
                    if (header.HasLabels)
                    {
                        _totalChunks *= 2;
                    }
                    _processedChunks = 0;

                    Logger.Log($"[ChunkedVolumeCompressor] Decompressing {header.Width}x{header.Height}x{header.Depth} volume");

                    // Decompress volume data
                    await DecompressVolumeFileAsync(reader, volumePath, header);

                    // Decompress labels if present
                    if (header.HasLabels)
                    {
                        Logger.Log("[ChunkedVolumeCompressor] Decompressing label data...");
                        await DecompressLabelsFileAsync(reader, labelsPath, header);
                    }

                    // Create volume.chk for compatibility
                    FileOperations.CreateVolumeChk(outputPath, header.Width, header.Height, header.Depth,
                        header.ChunkDim, header.PixelSize);
                }

                Logger.Log($"[ChunkedVolumeCompressor] Decompression completed. Output: {outputPath}");
                progress?.Report(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChunkedVolumeCompressor] Error: {ex.Message}");
                throw;
            }
        }

        #region Compression Methods

        private async Task CompressVolumeFileAsync(string volumePath, BinaryWriter writer, VolumeHeader header)
        {
            int chunkSize = header.ChunkDim * header.ChunkDim * header.ChunkDim;
            int totalChunks = header.ChunkCountX * header.ChunkCountY * header.ChunkCountZ;

            // Process chunks in parallel
            var compressedChunks = new ConcurrentDictionary<int, byte[]>();
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount);

            using (var volumeFile = MemoryMappedFile.CreateFromFile(volumePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
            using (var accessor = volumeFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            {
                var tasks = new List<Task>();

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    int index = chunkIndex; // Capture for closure

                    var task = Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            // Read chunk data
                            byte[] chunkData = new byte[chunkSize];
                            long offset = 36 + (long)index * chunkSize; // 36 bytes header
                            accessor.ReadArray(offset, chunkData, 0, chunkSize);

                            // Compress chunk
                            byte[] compressed = Compress3DChunk(chunkData, header.ChunkDim);
                            compressedChunks[index] = compressed;

                            Interlocked.Increment(ref _processedChunks);
                            _progress?.Report((int)(_processedChunks * 100 / _totalChunks));
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    tasks.Add(task);
                }

                await Task.WhenAll(tasks);
            }

            // Write compressed chunks in order
            writer.Write(totalChunks);
            for (int i = 0; i < totalChunks; i++)
            {
                var compressed = compressedChunks[i];
                writer.Write(compressed.Length);
                writer.Write(compressed);
            }
        }
        private CompressionLevel GetCompressionLevel()
        {
            if (_compressionLevel <= 3)
                return CompressionLevel.Fastest;
            else if (_compressionLevel >= 7)
                return CompressionLevel.Optimal;
            else
                return CompressionLevel.NoCompression;
        }
        private byte[] Compress3DChunk(byte[] chunkData, int chunkDim)
        {
            try
            {
                // Apply predictive coding if enabled
                if (_usePredictiveCoding)
                {
                    chunkData = ApplyPredictiveCoding3D(chunkData, chunkDim);
                }

                // Apply run-length encoding if enabled
                if (_useRunLengthEncoding)
                {
                    chunkData = ApplyRunLengthEncoding(chunkData);
                }

                // Apply final compression (DEFLATE)
                using (var output = new MemoryStream())
                {
                    using (var deflate = new DeflateStream(output, GetCompressionLevel(), leaveOpen: true))
                    {
                        deflate.Write(chunkData, 0, chunkData.Length);
                    }
                    return output.ToArray();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Compress3DChunk] Error: {ex.Message}");
                throw;
            }
        }

        private byte[] ApplyPredictiveCoding3D(byte[] data, int dim)
        {
            byte[] result = new byte[data.Length];
            int dimSq = dim * dim;

            // First voxel is stored as-is
            result[0] = data[0];

            // Apply 3D predictive coding
            for (int z = 0; z < dim; z++)
            {
                for (int y = 0; y < dim; y++)
                {
                    for (int x = 0; x < dim; x++)
                    {
                        int idx = z * dimSq + y * dim + x;

                        if (idx == 0) continue; // Skip first voxel

                        // Predict based on neighbors
                        int prediction = 0;
                        int count = 0;

                        // Previous in X
                        if (x > 0)
                        {
                            prediction += data[idx - 1];
                            count++;
                        }

                        // Previous in Y
                        if (y > 0)
                        {
                            prediction += data[idx - dim];
                            count++;
                        }

                        // Previous in Z
                        if (z > 0)
                        {
                            prediction += data[idx - dimSq];
                            count++;
                        }

                        // Average prediction
                        prediction = count > 0 ? prediction / count : 128;

                        // Store difference
                        int diff = data[idx] - prediction;
                        result[idx] = (byte)(diff + 128); // Offset to make positive
                    }
                }
            }

            return result;
        }

        private byte[] ApplyRunLengthEncoding(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                int i = 0;
                while (i < data.Length)
                {
                    byte value = data[i];
                    int count = 1;

                    // Count consecutive same values
                    while (i + count < data.Length && data[i + count] == value && count < 255)
                    {
                        count++;
                    }

                    // Write count and value
                    output.WriteByte((byte)count);
                    output.WriteByte(value);

                    i += count;
                }

                return output.ToArray();
            }
        }

        #endregion

        #region Decompression Methods

        private async Task DecompressVolumeFileAsync(BinaryReader reader, string outputPath, VolumeHeader header)
        {
            int chunkSize = header.ChunkDim * header.ChunkDim * header.ChunkDim;
            int totalChunks = reader.ReadInt32();

            // Create output file
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(fs))
            {
                // Write volume header
                WriteVolumeHeader(writer, header);

                // Pre-allocate file
                long totalSize = 36 + (long)totalChunks * chunkSize;
                fs.SetLength(totalSize);

                // Decompress chunks
                for (int i = 0; i < totalChunks; i++)
                {
                    int compressedSize = reader.ReadInt32();
                    byte[] compressedData = reader.ReadBytes(compressedSize);

                    byte[] decompressed = Decompress3DChunk(compressedData, header.ChunkDim);

                    // Write to correct position
                    long offset = 36 + (long)i * chunkSize;
                    fs.Seek(offset, SeekOrigin.Begin);
                    writer.Write(decompressed);

                    _processedChunks++;
                    _progress?.Report((int)(_processedChunks * 100 / _totalChunks));
                }
            }
        }

        private byte[] Decompress3DChunk(byte[] compressedData, int chunkDim)
        {
            try
            {
                // Decompress with DEFLATE
                byte[] decompressed;
                using (var input = new MemoryStream(compressedData))
                using (var output = new MemoryStream())
                {
                    using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                    {
                        deflate.CopyTo(output);
                    }
                    decompressed = output.ToArray();
                }

                // Reverse run-length encoding if it was applied
                if (_useRunLengthEncoding)
                {
                    decompressed = ReverseRunLengthEncoding(decompressed);
                }

                // Reverse predictive coding if it was applied
                if (_usePredictiveCoding)
                {
                    decompressed = ReversePredictiveCoding3D(decompressed, chunkDim);
                }

                return decompressed;
            }
            catch (Exception ex)
            {
                Logger.Log($"[Decompress3DChunk] Error: {ex.Message}");
                throw;
            }
        }

        private byte[] ReverseRunLengthEncoding(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                int i = 0;
                while (i < data.Length - 1)
                {
                    byte count = data[i];
                    byte value = data[i + 1];

                    for (int j = 0; j < count; j++)
                    {
                        output.WriteByte(value);
                    }

                    i += 2;
                }

                return output.ToArray();
            }
        }

        private byte[] ReversePredictiveCoding3D(byte[] data, int dim)
        {
            byte[] result = new byte[data.Length];
            int dimSq = dim * dim;

            // First voxel is stored as-is
            result[0] = data[0];

            // Reverse predictive coding
            for (int z = 0; z < dim; z++)
            {
                for (int y = 0; y < dim; y++)
                {
                    for (int x = 0; x < dim; x++)
                    {
                        int idx = z * dimSq + y * dim + x;

                        if (idx == 0) continue;

                        // Reconstruct prediction
                        int prediction = 0;
                        int count = 0;

                        if (x > 0)
                        {
                            prediction += result[idx - 1];
                            count++;
                        }

                        if (y > 0)
                        {
                            prediction += result[idx - dim];
                            count++;
                        }

                        if (z > 0)
                        {
                            prediction += result[idx - dimSq];
                            count++;
                        }

                        prediction = count > 0 ? prediction / count : 128;

                        // Reconstruct original value
                        int diff = data[idx] - 128;
                        result[idx] = (byte)Math.Max(0, Math.Min(255, prediction + diff));
                    }
                }
            }

            return result;
        }

        #endregion

        #region Header Management

        private class VolumeHeader
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public int Depth { get; set; }
            public int ChunkDim { get; set; }
            public int BitsPerPixel { get; set; }
            public double PixelSize { get; set; }
            public int ChunkCountX { get; set; }
            public int ChunkCountY { get; set; }
            public int ChunkCountZ { get; set; }
            public bool HasLabels { get; set; }
        }

        private VolumeHeader ReadVolumeHeader(BinaryReader reader)
        {
            return new VolumeHeader
            {
                Width = reader.ReadInt32(),
                Height = reader.ReadInt32(),
                Depth = reader.ReadInt32(),
                ChunkDim = reader.ReadInt32(),
                BitsPerPixel = reader.ReadInt32(),
                PixelSize = reader.ReadDouble(),
                ChunkCountX = reader.ReadInt32(),
                ChunkCountY = reader.ReadInt32(),
                ChunkCountZ = reader.ReadInt32()
            };
        }

        private void WriteVolumeHeader(BinaryWriter writer, VolumeHeader header)
        {
            writer.Write(header.Width);
            writer.Write(header.Height);
            writer.Write(header.Depth);
            writer.Write(header.ChunkDim);
            writer.Write(header.BitsPerPixel);
            writer.Write(header.PixelSize);
            writer.Write(header.ChunkCountX);
            writer.Write(header.ChunkCountY);
            writer.Write(header.ChunkCountZ);
        }

        private void WriteCompressedHeader(BinaryWriter writer, VolumeHeader header, bool hasLabels)
        {
            try
            {
                writer.Write(SIGNATURE.ToCharArray());
                writer.Write(VERSION);
                writer.Write(header.Width);
                writer.Write(header.Height);
                writer.Write(header.Depth);
                writer.Write(header.ChunkDim);
                writer.Write(header.PixelSize);
                writer.Write(hasLabels);
                writer.Write(_compressionLevel); // Write int directly
                writer.Write(_usePredictiveCoding);
                writer.Write(_useRunLengthEncoding);
            }
            catch (Exception ex)
            {
                Logger.Log($"[WriteCompressedHeader] Error: {ex.Message}");
                throw;
            }
        }

        private VolumeHeader ReadCompressedHeader(BinaryReader reader)
        {
            try
            {
                char[] signature = reader.ReadChars(5);
                if (new string(signature) != SIGNATURE)
                    throw new InvalidDataException("Invalid file signature");

                int version = reader.ReadInt32();
                if (version != VERSION)
                    throw new InvalidDataException($"Unsupported version: {version}");

                var header = new VolumeHeader
                {
                    Width = reader.ReadInt32(),
                    Height = reader.ReadInt32(),
                    Depth = reader.ReadInt32(),
                    ChunkDim = reader.ReadInt32(),
                    PixelSize = reader.ReadDouble(),
                    HasLabels = reader.ReadBoolean()
                };

                // Read compression settings as int
                _compressionLevel = reader.ReadInt32();
                _usePredictiveCoding = reader.ReadBoolean();
                _useRunLengthEncoding = reader.ReadBoolean();

                // Calculate chunk counts
                header.ChunkCountX = (header.Width + header.ChunkDim - 1) / header.ChunkDim;
                header.ChunkCountY = (header.Height + header.ChunkDim - 1) / header.ChunkDim;
                header.ChunkCountZ = (header.Depth + header.ChunkDim - 1) / header.ChunkDim;

                return header;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ReadCompressedHeader] Error: {ex.Message}");
                throw;
            }
        }

        private async Task CompressLabelsFileAsync(string labelsPath, BinaryWriter writer)
        {
            // Read labels header
            int chunkDim, cntX, cntY, cntZ;
            using (var fs = new FileStream(labelsPath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                chunkDim = br.ReadInt32();
                cntX = br.ReadInt32();
                cntY = br.ReadInt32();
                cntZ = br.ReadInt32();
            }

            int totalChunks = cntX * cntY * cntZ;
            int chunkSize = chunkDim * chunkDim * chunkDim;

            writer.Write(chunkDim);
            writer.Write(cntX);
            writer.Write(cntY);
            writer.Write(cntZ);

            using (var labelsFile = MemoryMappedFile.CreateFromFile(labelsPath, FileMode.Open))
            using (var accessor = labelsFile.CreateViewAccessor())
            {
                for (int i = 0; i < totalChunks; i++)
                {
                    byte[] chunkData = new byte[chunkSize];
                    long offset = 16 + (long)i * chunkSize; // 16 bytes header
                    accessor.ReadArray(offset, chunkData, 0, chunkSize);

                    // Compress label chunk (labels compress better with RLE)
                    byte[] compressed = CompressLabelChunk(chunkData);

                    writer.Write(compressed.Length);
                    writer.Write(compressed);

                    _processedChunks++;
                    _progress?.Report((int)(_processedChunks * 100 / _totalChunks));
                }
            }
        }

        private byte[] CompressLabelChunk(byte[] data)
        {
            // For labels, RLE is usually very effective
            var rle = ApplyRunLengthEncoding(data);

            // Then apply DEFLATE
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, GetCompressionLevel()))
                {
                    deflate.Write(rle, 0, rle.Length);
                }
                return output.ToArray();
            }
        }

        private async Task DecompressLabelsFileAsync(BinaryReader reader, string outputPath, VolumeHeader header)
        {
            int chunkDim = reader.ReadInt32();
            int cntX = reader.ReadInt32();
            int cntY = reader.ReadInt32();
            int cntZ = reader.ReadInt32();

            int totalChunks = cntX * cntY * cntZ;
            int chunkSize = chunkDim * chunkDim * chunkDim;

            using (var fs = new FileStream(outputPath, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                // Write labels header
                writer.Write(chunkDim);
                writer.Write(cntX);
                writer.Write(cntY);
                writer.Write(cntZ);

                // Decompress chunks
                for (int i = 0; i < totalChunks; i++)
                {
                    int compressedSize = reader.ReadInt32();
                    byte[] compressed = reader.ReadBytes(compressedSize);

                    byte[] decompressed = DecompressLabelChunk(compressed);
                    writer.Write(decompressed);

                    _processedChunks++;
                    _progress?.Report((int)(_processedChunks * 100 / _totalChunks));
                }
            }
        }

        private byte[] DecompressLabelChunk(byte[] compressed)
        {
            // Decompress with DEFLATE
            byte[] decompressed;
            using (var input = new MemoryStream(compressed))
            using (var output = new MemoryStream())
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                deflate.CopyTo(output);
                decompressed = output.ToArray();
            }

            // Reverse RLE
            return ReverseRunLengthEncoding(decompressed);
        }

        #endregion
    }
}