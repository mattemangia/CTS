//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace CTS
{
    /// <summary>
    /// In-memory implementation of ILabelVolumeData using a contiguous byte array.
    /// This treats the entire volume as a single chunk, suitable for moderate-sized volumes.
    /// </summary>
    public class LabelVolumeDataArray : ILabelVolumeData
    {
        // Volume dimensions and chunking
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public int ChunkDim { get; }
        public int ChunkCountX { get; }
        public int ChunkCountY { get; }
        public int ChunkCountZ { get; }

        // Internal voxel storage (contiguous array of size Width*Height*Depth)
        private byte[] data;
        private bool disposed = false;

        public LabelVolumeDataArray(int width, int height, int depth)
        {
            if (width <= 0 || height <= 0 || depth <= 0)
                throw new ArgumentException("Volume dimensions must be positive.");
            Width = width;
            Height = height;
            Depth = depth;
            // Use a single chunk covering the entire volume
            ChunkDim = Math.Max(width, Math.Max(height, depth));
            ChunkCountX = (width + ChunkDim - 1) / ChunkDim;   // likely 1
            ChunkCountY = (height + ChunkDim - 1) / ChunkDim;  // likely 1
            ChunkCountZ = (depth + ChunkDim - 1) / ChunkDim;   // likely 1
            data = new byte[width * height * depth];
        }

        /// <summary>Indexer to get or set a voxel label value.</summary>
        public byte this[int x, int y, int z]
        {
            get
            {
                CheckBounds(x, y, z);
                long index = x + (long)Width * (y + (long)Height * z);
                return data[index];
            }
            set
            {
                CheckBounds(x, y, z);
                long index = x + (long)Width * (y + (long)Height * z);
                data[index] = value;
            }
        }

        /// <summary>
        /// Returns the flat byte array of a given chunk. For this class, the entire volume 
        /// is one chunk (index 0).
        /// </summary>
        public byte[] GetChunkBytes(int chunkIndex)
        {
            if (chunkIndex != 0)
                throw new IndexOutOfRangeException("Only one chunk (index 0) exists in LabelVolumeDataArray.");
            // Return a copy of the internal data to maintain encapsulation
            return (byte[])data.Clone();
        }

        /// <summary>
        /// Computes the linear chunk index from chunk coordinates. In this implementation, 
        /// returns 0 for the single chunk.
        /// </summary>
        public int GetChunkIndex(int cx, int cy, int cz)
        {
            if (cx < 0 || cy < 0 || cz < 0 || cx >= ChunkCountX || cy >= ChunkCountY || cz >= ChunkCountZ)
                throw new IndexOutOfRangeException("Chunk coordinates out of range.");
            return 0;
        }

        /// <summary>
        /// Write the volume data chunks to a binary writer (with a 16-byte header: ChunkDim and chunk counts).
        /// </summary>
        public void WriteChunks(BinaryWriter w)
        {
            // Write 4 header ints: ChunkDim, ChunkCountX, ChunkCountY, ChunkCountZ
            w.Write(ChunkDim);
            w.Write(ChunkCountX);
            w.Write(ChunkCountY);
            w.Write(ChunkCountZ);
            // Write all voxel data
            w.Write(data);
        }

        /// <summary>
        /// Read volume data from a binary reader. Expects that volume dimensions were known at construction.
        /// This will read a 16-byte header and then the chunk data.
        /// </summary>
        public void ReadChunksHeaderAndData(BinaryReader br, MemoryMappedFile mmfIfAny = null, long offsetAfterHeader = 0)
        {
            // Read chunk header (4 ints)
            int fileChunkDim = br.ReadInt32();
            int fileChunkCountX = br.ReadInt32();
            int fileChunkCountY = br.ReadInt32();
            int fileChunkCountZ = br.ReadInt32();
            // Validate against our volume dimensions (the chunk counts should match our single-chunk assumption or overall size)
            long totalChunks = (long)fileChunkCountX * fileChunkCountY * fileChunkCountZ;
            long chunkSize = (long)fileChunkDim * fileChunkDim * fileChunkDim;
            long totalVoxels = Width * Height * Depth;
            if (totalChunks <= 0)
                throw new InvalidDataException("Invalid chunk counts in label volume file.");
            // Allocate data array if not yet initialized or size changed
            if (data == null || data.Length != totalVoxels)
            {
                data = new byte[totalVoxels];
            }
            // If there's only one chunk, read directly into data
            if (totalChunks == 1)
            {
                int bytesToRead = (int)totalVoxels;
                byte[] bytes = br.ReadBytes(bytesToRead);
                if (bytes.Length < bytesToRead)
                {
                    // If file is truncated, pad remaining with zeros
                    byte[] padded = new byte[bytesToRead];
                    Array.Copy(bytes, padded, bytes.Length);
                    bytes = padded;
                }
                data = bytes;
                // If file had extra padding beyond volume (unlikely for single chunk), skip it
                long remaining = fileChunkCountX * fileChunkCountY * fileChunkCountZ * chunkSize - bytesToRead;
                if (remaining > 0)
                {
                    br.BaseStream.Seek(remaining, SeekOrigin.Current);
                }
            }
            else
            {
                // Multiple chunks: read all chunk bytes then assemble into data array
                byte[] allBytes = br.ReadBytes((int)(totalChunks * chunkSize));
                if (allBytes.Length < totalChunks * chunkSize)
                {
                    // pad incomplete file data
                    byte[] newAll = new byte[totalChunks * chunkSize];
                    Array.Copy(allBytes, newAll, allBytes.Length);
                    allBytes = newAll;
                }
                // Iterate chunks and copy data into correct position in the volume array
                int chunkIndex = 0;
                for (int cz = 0; cz < fileChunkCountZ; cz++)
                {
                    for (int cy = 0; cy < fileChunkCountY; cy++)
                    {
                        for (int cx = 0; cx < fileChunkCountX; cx++)
                        {
                            long chunkOffset = chunkIndex * chunkSize;
                            // For each chunk, iterate within chunk dimensions
                            int xStart = cx * fileChunkDim;
                            int yStart = cy * fileChunkDim;
                            int zStart = cz * fileChunkDim;
                            for (int dz = 0; dz < fileChunkDim; dz++)
                            {
                                int zIdx = zStart + dz;
                                if (zIdx >= Depth) break;
                                for (int dy = 0; dy < fileChunkDim; dy++)
                                {
                                    int yIdx = yStart + dy;
                                    if (yIdx >= Height) break;
                                    for (int dx = 0; dx < fileChunkDim; dx++)
                                    {
                                        int xIdx = xStart + dx;
                                        if (xIdx >= Width) break;
                                        long voxelIndex = xIdx + (long)Width * (yIdx + (long)Height * zIdx);
                                        long chunkByteIndex = chunkOffset + (dz * fileChunkDim * fileChunkDim) + (dy * fileChunkDim) + dx;
                                        data[voxelIndex] = allBytes[chunkByteIndex];
                                    }
                                }
                            }
                            chunkIndex++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Release any file locks or memory-mapped resources (not used in this implementation).
        /// </summary>
        public void ReleaseFileLock()
        {
            // No file locking in use for pure in-memory data
        }

        private void CheckBounds(int x, int y, int z)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth)
                throw new IndexOutOfRangeException($"Coordinates ({x},{y},{z}) are out of volume bounds.");
        }

        public void Dispose()
        {
            data = null;
            disposed = true;
        }
    }
}
