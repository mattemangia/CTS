//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace CTS
{
    /// <summary>
    /// Base interface for all volume data types
    /// </summary>
    public interface IVolumeData : IDisposable
    {
        int Width { get; }
        int Height { get; }
        int Depth { get; }
        int ChunkDim { get; }
        int ChunkCountX { get; }
        int ChunkCountY { get; }
        int ChunkCountZ { get; }

        // Indexer for accessing voxel data
        byte this[int x, int y, int z] { get; set; }

        // Methods for accessing chunk data
        byte[] GetChunkBytes(int chunkIndex);

        int GetChunkIndex(int cx, int cy, int cz);
    }

    /// <summary>
    /// Interface for grayscale volume data
    /// </summary>
    public interface IGrayscaleVolumeData : IVolumeData
    {
        void WriteChunks(BinaryWriter bw);

        void ReadChunks(BinaryReader br);
    }

    /// <summary>
    /// Interface for label volume data
    /// </summary>
    public interface ILabelVolumeData : IVolumeData
    {
        void WriteChunks(BinaryWriter w);

        void ReadChunksHeaderAndData(BinaryReader br, MemoryMappedFile mmfIfAny = null, long offsetAfterHeader = 0);

        void ReleaseFileLock();

    }
}