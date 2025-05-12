using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;

namespace CTS.Compression
{
    /// <summary>
    /// Represents a node in the octree structure for 3D compression
    /// </summary>
    public class OctreeNode
    {
        public byte Value { get; set; }
        public bool IsLeaf { get; set; }
        public OctreeNode[] Children { get; set; } // 8 children for octree
        public byte MinValue { get; set; }
        public byte MaxValue { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public int Size { get; set; }

        public OctreeNode()
        {
            Children = new OctreeNode[8];
            IsLeaf = true;
        }
    }

    /// <summary>
    /// Main 3D volumetric compressor using adaptive octree compression
    /// </summary>
    public class VolumetricCompressor
    {
        private const int BLOCK_SIZE = 64; // Process volume in 64³ blocks
        private const int MIN_NODE_SIZE = 2; // Minimum octree node size
        private const byte VARIANCE_THRESHOLD = 5; // Threshold for subdividing nodes

        // Compression settings
        private readonly int _blockSize;
        private readonly int _minNodeSize;
        private readonly byte _varianceThreshold;

        // Progress reporting
        private IProgress<int> _progress;
        private long _totalBlocks;
        private long _processedBlocks;

        public VolumetricCompressor(int blockSize = BLOCK_SIZE, int minNodeSize = MIN_NODE_SIZE, byte varianceThreshold = VARIANCE_THRESHOLD)
        {
            _blockSize = blockSize;
            _minNodeSize = minNodeSize;
            _varianceThreshold = varianceThreshold;
        }

        /// <summary>
        /// Compress a volume to a file
        /// </summary>
        public async Task CompressVolumeAsync(string inputPath, string outputPath, IProgress<int> progress)
        {
            _progress = progress;
            Logger.Log($"[VolumetricCompressor] Starting compression of {inputPath} to {outputPath}");

            try
            {
                // Load volume metadata
                var (volumeData, volumeLabels, width, height, depth, pixelSize) =
                    await FileOperations.LoadDatasetAsync(inputPath, true, 1e-6, 1, null);

                if (volumeData == null)
                {
                    throw new InvalidOperationException("No volume data found to compress");
                }

                // Calculate total blocks
                int blocksX = (width + _blockSize - 1) / _blockSize;
                int blocksY = (height + _blockSize - 1) / _blockSize;
                int blocksZ = (depth + _blockSize - 1) / _blockSize;
                _totalBlocks = blocksX * blocksY * blocksZ;
                _processedBlocks = 0;

                Logger.Log($"[VolumetricCompressor] Volume dimensions: {width}x{height}x{depth}, Blocks: {blocksX}x{blocksY}x{blocksZ}");

                using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(outputStream))
                {
                    // Write header
                    WriteHeader(writer, width, height, depth, pixelSize, volumeLabels != null);

                    // Compress grayscale data
                    Logger.Log("[VolumetricCompressor] Compressing grayscale data...");
                    await CompressGrayscaleDataAsync(writer, volumeData, width, height, depth);

                    // Compress labels if present
                    if (volumeLabels != null)
                    {
                        Logger.Log("[VolumetricCompressor] Compressing label data...");
                        await CompressLabelDataAsync(writer, volumeLabels, width, height, depth);
                    }
                }

                // Clean up
                volumeData?.Dispose();
                volumeLabels?.Dispose();

                Logger.Log($"[VolumetricCompressor] Compression completed. Output file: {outputPath}");
                progress?.Report(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumetricCompressor] Error during compression: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Decompress a volume from file
        /// </summary>
        public async Task DecompressVolumeAsync(string inputPath, string outputPath, IProgress<int> progress)
        {
            _progress = progress;
            Logger.Log($"[VolumetricCompressor] Starting decompression of {inputPath} to {outputPath}");

            try
            {
                using (var inputStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(inputStream))
                {
                    // Read header
                    var header = ReadHeader(reader);
                    Logger.Log($"[VolumetricCompressor] Decompressing volume: {header.Width}x{header.Height}x{header.Depth}");

                    // Create output volume
                    var volumeData = new ChunkedVolume(header.Width, header.Height, header.Depth, FileOperations.CHUNK_DIM);
                    ChunkedLabelVolume volumeLabels = null;

                    if (header.HasLabels)
                    {
                        volumeLabels = new ChunkedLabelVolume(header.Width, header.Height, header.Depth,
                            FileOperations.CHUNK_DIM, false, null);
                    }

                    // Decompress grayscale data
                    Logger.Log("[VolumetricCompressor] Decompressing grayscale data...");
                    await DecompressGrayscaleDataAsync(reader, volumeData, header.Width, header.Height, header.Depth);

                    // Decompress labels if present
                    if (header.HasLabels)
                    {
                        Logger.Log("[VolumetricCompressor] Decompressing label data...");
                        await DecompressLabelDataAsync(reader, volumeLabels, header.Width, header.Height, header.Depth);
                    }

                    // Save decompressed volume
                    string volumePath = Path.Combine(outputPath, "volume.bin");
                    volumeData.SaveAsBin(volumePath);

                    if (volumeLabels != null)
                    {
                        string labelsPath = Path.Combine(outputPath, "labels.bin");
                        using (var fs = new FileStream(labelsPath, FileMode.Create))
                        using (var bw = new BinaryWriter(fs))
                        {
                            volumeLabels.WriteChunks(bw);
                        }
                    }

                    // Create header files
                    FileOperations.CreateVolumeChk(outputPath, header.Width, header.Height, header.Depth,
                        FileOperations.CHUNK_DIM, header.PixelSize);

                    // Clean up
                    volumeData.Dispose();
                    volumeLabels?.Dispose();
                }

                Logger.Log($"[VolumetricCompressor] Decompression completed. Output path: {outputPath}");
                progress?.Report(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumetricCompressor] Error during decompression: {ex.Message}");
                throw;
            }
        }

        #region Compression Methods

        private async Task CompressGrayscaleDataAsync(BinaryWriter writer, IGrayscaleVolumeData volumeData,
            int width, int height, int depth)
        {
            int blocksX = (width + _blockSize - 1) / _blockSize;
            int blocksY = (height + _blockSize - 1) / _blockSize;
            int blocksZ = (depth + _blockSize - 1) / _blockSize;

            // Process blocks in parallel
            var blockQueue = new ConcurrentQueue<CompressedBlock>();
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2); // Limit concurrent blocks

            var tasks = new List<Task>();

            for (int bz = 0; bz < blocksZ; bz++)
            {
                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        int blockX = bx, blockY = by, blockZ = bz;

                        await semaphore.WaitAsync();
                        var task = Task.Run(() =>
                        {
                            try
                            {
                                var block = ExtractBlock(volumeData, blockX, blockY, blockZ, width, height, depth);
                                var compressedBlock = CompressBlock(block, blockX, blockY, blockZ);
                                blockQueue.Enqueue(compressedBlock);

                                Interlocked.Increment(ref _processedBlocks);
                                _progress?.Report((int)(_processedBlocks * 100 / _totalBlocks));
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        });

                        tasks.Add(task);
                    }
                }
            }

            await Task.WhenAll(tasks);

            // Write compressed blocks
            Logger.Log($"[VolumetricCompressor] Writing {blockQueue.Count} compressed blocks");
            writer.Write(blockQueue.Count);

            foreach (var block in blockQueue.OrderBy(b => b.Z).ThenBy(b => b.Y).ThenBy(b => b.X))
            {
                WriteCompressedBlock(writer, block);
            }
        }

        private byte[,,] ExtractBlock(IGrayscaleVolumeData volumeData, int blockX, int blockY, int blockZ,
            int volumeWidth, int volumeHeight, int volumeDepth)
        {
            int startX = blockX * _blockSize;
            int startY = blockY * _blockSize;
            int startZ = blockZ * _blockSize;

            int sizeX = Math.Min(_blockSize, volumeWidth - startX);
            int sizeY = Math.Min(_blockSize, volumeHeight - startY);
            int sizeZ = Math.Min(_blockSize, volumeDepth - startZ);

            byte[,,] block = new byte[sizeX, sizeY, sizeZ];

            for (int z = 0; z < sizeZ; z++)
            {
                for (int y = 0; y < sizeY; y++)
                {
                    for (int x = 0; x < sizeX; x++)
                    {
                        block[x, y, z] = volumeData[startX + x, startY + y, startZ + z];
                    }
                }
            }

            return block;
        }

        private CompressedBlock CompressBlock(byte[,,] block, int blockX, int blockY, int blockZ)
        {
            var compressedBlock = new CompressedBlock
            {
                X = blockX,
                Y = blockY,
                Z = blockZ,
                SizeX = block.GetLength(0),
                SizeY = block.GetLength(1),
                SizeZ = block.GetLength(2)
            };

            // Build octree representation
            var octree = BuildOctree(block, 0, 0, 0, Math.Max(Math.Max(block.GetLength(0), block.GetLength(1)), block.GetLength(2)));

            // Serialize octree to compressed format
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                SerializeOctree(writer, octree);
                compressedBlock.Data = stream.ToArray();
            }

            return compressedBlock;
        }

        private OctreeNode BuildOctree(byte[,,] data, int x, int y, int z, int size)
        {
            var node = new OctreeNode
            {
                X = x,
                Y = y,
                Z = z,
                Size = size
            };

            // Check if we're within bounds
            if (x >= data.GetLength(0) || y >= data.GetLength(1) || z >= data.GetLength(2))
            {
                node.Value = 0;
                node.IsLeaf = true;
                return node;
            }

            // Calculate min/max values and variance in this region
            byte minVal = 255, maxVal = 0;
            long sum = 0, sumSquares = 0;
            int count = 0;

            for (int dz = 0; dz < size && z + dz < data.GetLength(2); dz++)
            {
                for (int dy = 0; dy < size && y + dy < data.GetLength(1); dy++)
                {
                    for (int dx = 0; dx < size && x + dx < data.GetLength(0); dx++)
                    {
                        byte val = data[x + dx, y + dy, z + dz];
                        minVal = Math.Min(minVal, val);
                        maxVal = Math.Max(maxVal, val);
                        sum += val;
                        sumSquares += val * val;
                        count++;
                    }
                }
            }

            node.MinValue = minVal;
            node.MaxValue = maxVal;

            // Calculate variance
            double mean = (double)sum / count;
            double variance = (sumSquares / count) - (mean * mean);

            // Decide if we should subdivide
            if (size <= _minNodeSize || variance < _varianceThreshold * _varianceThreshold)
            {
                // Make this a leaf node with average value
                node.IsLeaf = true;
                node.Value = (byte)Math.Round(mean);
            }
            else
            {
                // Subdivide into 8 children
                node.IsLeaf = false;
                int halfSize = size / 2;

                for (int i = 0; i < 8; i++)
                {
                    int childX = x + (i & 1) * halfSize;
                    int childY = y + ((i >> 1) & 1) * halfSize;
                    int childZ = z + ((i >> 2) & 1) * halfSize;

                    node.Children[i] = BuildOctree(data, childX, childY, childZ, halfSize);
                }
            }

            return node;
        }

        private void SerializeOctree(BinaryWriter writer, OctreeNode node)
        {
            // Write node type flag
            writer.Write(node.IsLeaf);

            if (node.IsLeaf)
            {
                // Write leaf value
                writer.Write(node.Value);
            }
            else
            {
                // Write child presence mask
                byte mask = 0;
                for (int i = 0; i < 8; i++)
                {
                    if (node.Children[i] != null)
                        mask |= (byte)(1 << i);
                }
                writer.Write(mask);

                // Recursively write children
                for (int i = 0; i < 8; i++)
                {
                    if (node.Children[i] != null)
                        SerializeOctree(writer, node.Children[i]);
                }
            }
        }

        #endregion

        #region Decompression Methods

        private async Task DecompressGrayscaleDataAsync(BinaryReader reader, ChunkedVolume volumeData,
            int width, int height, int depth)
        {
            int blockCount = reader.ReadInt32();
            Logger.Log($"[VolumetricCompressor] Decompressing {blockCount} blocks");

            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
            var tasks = new List<Task>();

            for (int i = 0; i < blockCount; i++)
            {
                var compressedBlock = ReadCompressedBlock(reader);

                await semaphore.WaitAsync();
                var task = Task.Run(() =>
                {
                    try
                    {
                        var block = DecompressBlock(compressedBlock);
                        WriteBlockToVolume(volumeData, block, compressedBlock.X, compressedBlock.Y, compressedBlock.Z);

                        _progress?.Report((i + 1) * 100 / blockCount);
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

        private byte[,,] DecompressBlock(CompressedBlock compressedBlock)
        {
            byte[,,] block = new byte[compressedBlock.SizeX, compressedBlock.SizeY, compressedBlock.SizeZ];

            using (var stream = new MemoryStream(compressedBlock.Data))
            using (var reader = new BinaryReader(stream))
            {
                var octree = DeserializeOctree(reader);
                FillBlockFromOctree(block, octree, 0, 0, 0,
                    Math.Max(Math.Max(compressedBlock.SizeX, compressedBlock.SizeY), compressedBlock.SizeZ));
            }

            return block;
        }

        private OctreeNode DeserializeOctree(BinaryReader reader)
        {
            var node = new OctreeNode();
            node.IsLeaf = reader.ReadBoolean();

            if (node.IsLeaf)
            {
                node.Value = reader.ReadByte();
            }
            else
            {
                byte mask = reader.ReadByte();

                for (int i = 0; i < 8; i++)
                {
                    if ((mask & (1 << i)) != 0)
                        node.Children[i] = DeserializeOctree(reader);
                }
            }

            return node;
        }

        private void FillBlockFromOctree(byte[,,] block, OctreeNode node, int x, int y, int z, int size)
        {
            if (node == null) return;

            if (node.IsLeaf)
            {
                // Fill the region with the leaf value
                for (int dz = 0; dz < size && z + dz < block.GetLength(2); dz++)
                {
                    for (int dy = 0; dy < size && y + dy < block.GetLength(1); dy++)
                    {
                        for (int dx = 0; dx < size && x + dx < block.GetLength(0); dx++)
                        {
                            block[x + dx, y + dy, z + dz] = node.Value;
                        }
                    }
                }
            }
            else
            {
                // Recursively fill children
                int halfSize = size / 2;

                for (int i = 0; i < 8; i++)
                {
                    if (node.Children[i] != null)
                    {
                        int childX = x + (i & 1) * halfSize;
                        int childY = y + ((i >> 1) & 1) * halfSize;
                        int childZ = z + ((i >> 2) & 1) * halfSize;

                        FillBlockFromOctree(block, node.Children[i], childX, childY, childZ, halfSize);
                    }
                }
            }
        }

        private void WriteBlockToVolume(ChunkedVolume volumeData, byte[,,] block, int blockX, int blockY, int blockZ)
        {
            int startX = blockX * _blockSize;
            int startY = blockY * _blockSize;
            int startZ = blockZ * _blockSize;

            for (int z = 0; z < block.GetLength(2); z++)
            {
                for (int y = 0; y < block.GetLength(1); y++)
                {
                    for (int x = 0; x < block.GetLength(0); x++)
                    {
                        volumeData[startX + x, startY + y, startZ + z] = block[x, y, z];
                    }
                }
            }
        }

        #endregion

        #region Label Compression

        private async Task CompressLabelDataAsync(BinaryWriter writer, ILabelVolumeData volumeLabels,
            int width, int height, int depth)
        {
            Logger.Log("[VolumetricCompressor] Using sparse representation for label compression");

            // Count non-zero voxels
            long nonZeroCount = 0;
            var nonZeroVoxels = new ConcurrentBag<SparseVoxel>();

            int slicesPerThread = depth / Environment.ProcessorCount;
            var tasks = new List<Task>();

            for (int t = 0; t < Environment.ProcessorCount; t++)
            {
                int startZ = t * slicesPerThread;
                int endZ = (t == Environment.ProcessorCount - 1) ? depth : (t + 1) * slicesPerThread;

                tasks.Add(Task.Run(() =>
                {
                    for (int z = startZ; z < endZ; z++)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                byte label = volumeLabels[x, y, z];
                                if (label != 0)
                                {
                                    nonZeroVoxels.Add(new SparseVoxel
                                    {
                                        X = (ushort)x,
                                        Y = (ushort)y,
                                        Z = (ushort)z,
                                        Value = label
                                    });
                                    Interlocked.Increment(ref nonZeroCount);
                                }
                            }
                        }

                        _progress?.Report(50 + (z - startZ) * 50 / (endZ - startZ));
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Write sparse representation
            writer.Write(nonZeroCount);
            Logger.Log($"[VolumetricCompressor] Writing {nonZeroCount} non-zero label voxels");

            foreach (var voxel in nonZeroVoxels.OrderBy(v => v.Z).ThenBy(v => v.Y).ThenBy(v => v.X))
            {
                writer.Write(voxel.X);
                writer.Write(voxel.Y);
                writer.Write(voxel.Z);
                writer.Write(voxel.Value);
            }
        }

        private async Task DecompressLabelDataAsync(BinaryReader reader, ChunkedLabelVolume volumeLabels,
            int width, int height, int depth)
        {
            long nonZeroCount = reader.ReadInt64();
            Logger.Log($"[VolumetricCompressor] Decompressing {nonZeroCount} non-zero label voxels");

            // Read sparse voxels
            for (long i = 0; i < nonZeroCount; i++)
            {
                ushort x = reader.ReadUInt16();
                ushort y = reader.ReadUInt16();
                ushort z = reader.ReadUInt16();
                byte value = reader.ReadByte();

                volumeLabels[x, y, z] = value;

                if (i % 100000 == 0)
                {
                    _progress?.Report(50 + (int)(i * 50 / nonZeroCount));
                }
            }
        }

        #endregion

        #region Helper Classes and Methods

        private class CompressedBlock
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
            public int SizeX { get; set; }
            public int SizeY { get; set; }
            public int SizeZ { get; set; }
            public byte[] Data { get; set; }
        }

        private class SparseVoxel
        {
            public ushort X { get; set; }
            public ushort Y { get; set; }
            public ushort Z { get; set; }
            public byte Value { get; set; }
        }

        private class CompressionHeader
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public int Depth { get; set; }
            public double PixelSize { get; set; }
            public bool HasLabels { get; set; }
            public int Version { get; set; }
        }

        private void WriteHeader(BinaryWriter writer, int width, int height, int depth, double pixelSize, bool hasLabels)
        {
            writer.Write("CTS3D"); // Magic signature
            writer.Write(1); // Version
            writer.Write(width);
            writer.Write(height);
            writer.Write(depth);
            writer.Write(pixelSize);
            writer.Write(hasLabels);
            writer.Write(_blockSize);
            writer.Write(_minNodeSize);
            writer.Write(_varianceThreshold);
        }

        private CompressionHeader ReadHeader(BinaryReader reader)
        {
            string signature = new string(reader.ReadChars(5));
            if (signature != "CTS3D")
                throw new InvalidDataException("Invalid file signature");

            return new CompressionHeader
            {
                Version = reader.ReadInt32(),
                Width = reader.ReadInt32(),
                Height = reader.ReadInt32(),
                Depth = reader.ReadInt32(),
                PixelSize = reader.ReadDouble(),
                HasLabels = reader.ReadBoolean()
            };
        }

        private void WriteCompressedBlock(BinaryWriter writer, CompressedBlock block)
        {
            writer.Write(block.X);
            writer.Write(block.Y);
            writer.Write(block.Z);
            writer.Write(block.SizeX);
            writer.Write(block.SizeY);
            writer.Write(block.SizeZ);
            writer.Write(block.Data.Length);
            writer.Write(block.Data);
        }

        private CompressedBlock ReadCompressedBlock(BinaryReader reader)
        {
            return new CompressedBlock
            {
                X = reader.ReadInt32(),
                Y = reader.ReadInt32(),
                Z = reader.ReadInt32(),
                SizeX = reader.ReadInt32(),
                SizeY = reader.ReadInt32(),
                SizeZ = reader.ReadInt32(),
                Data = reader.ReadBytes(reader.ReadInt32())
            };
        }

        #endregion
    }
}