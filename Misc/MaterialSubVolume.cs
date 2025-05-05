using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading.Tasks;

namespace CTS
{
    /// <summary>
    /// A class that extracts just the material of interest (plus a small margin) into a smaller volume
    /// to ensure the simulation only processes the relevant material
    /// </summary>
    public class MaterialSubVolume : ILabelVolumeData, IDisposable
    {
        private readonly int _originalWidth;
        private readonly int _originalHeight;
        private readonly int _originalDepth;

        // Sub-volume arrays
        private readonly byte[,,] _subLabels;
        private readonly float[,,] _subDensity;

        // Reference to original volume for chunk operations
        private readonly ILabelVolumeData _originalVolume;

        // Bounds of material in original volume
        public int MinX { get; }
        public int MaxX { get; }
        public int MinY { get; }
        public int MaxY { get; }
        public int MinZ { get; }
        public int MaxZ { get; }

        // Sub-volume dimensions
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
        public int Depth => MaxZ - MinZ + 1;

        // Mapping arrays to convert between sub and original coordinates
        private readonly int[,,] _originalToSubMap;
        private readonly List<(int x, int y, int z)> _subToOriginalMap;

        // Selected material ID
        public byte MaterialID { get; }

        // IVolumeData implementation for chunk support
        public int ChunkDim => _originalVolume.ChunkDim;
        public int ChunkCountX => _originalVolume.ChunkCountX;
        public int ChunkCountY => _originalVolume.ChunkCountY;
        public int ChunkCountZ => _originalVolume.ChunkCountZ;

        public MaterialSubVolume(ILabelVolumeData originalLabels, float[,,] originalDensity, byte materialID)
        {
            // Store original volume for pass-through methods
            _originalVolume = originalLabels;

            // Store original dimensions
            _originalWidth = originalDensity.GetLength(0);
            _originalHeight = originalDensity.GetLength(1);
            _originalDepth = originalDensity.GetLength(2);

            MaterialID = materialID;

            // Find bounds of the material
            FindMaterialBounds(originalLabels, materialID, out int minX, out int maxX,
                              out int minY, out int maxY, out int minZ, out int maxZ);

            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
            MinZ = minZ;
            MaxZ = maxZ;

            // Create the mapping array
            _originalToSubMap = new int[_originalWidth, _originalHeight, _originalDepth];
            for (int i = 0; i < _originalWidth; i++)
                for (int j = 0; j < _originalHeight; j++)
                    for (int k = 0; k < _originalDepth; k++)
                        _originalToSubMap[i, j, k] = -1; // Initialize to invalid

            // Create the sub-volume arrays
            _subLabels = new byte[Width, Height, Depth];
            _subDensity = new float[Width, Height, Depth];
            _subToOriginalMap = new List<(int x, int y, int z)>();

            // Fill the sub-volume and build mappings
            ExtractMaterialVolume(originalLabels, originalDensity, materialID);

            Logger.Log($"[MaterialSubVolume] Created sub-volume for material {materialID}: " +
                      $"Bounds: X:[{MinX}-{MaxX}], Y:[{MinY}-{MaxY}], Z:[{MinZ}-{MaxZ}], " +
                      $"Size: {Width}x{Height}x{Depth}, " +
                      $"Material voxels: {_subToOriginalMap.Count}");
        }

        private void FindMaterialBounds(ILabelVolumeData labels, byte materialID,
                                       out int minX, out int maxX, out int minY,
                                       out int maxY, out int minZ, out int maxZ)
        {
            minX = _originalWidth - 1;
            maxX = 0;
            minY = _originalHeight - 1;
            maxY = 0;
            minZ = _originalDepth - 1;
            maxZ = 0;

            for (int z = 0; z < _originalDepth; z++)
            {
                for (int y = 0; y < _originalHeight; y++)
                {
                    for (int x = 0; x < _originalWidth; x++)
                    {
                        if (labels[x, y, z] == materialID)
                        {
                            minX = Math.Min(minX, x);
                            maxX = Math.Max(maxX, x);
                            minY = Math.Min(minY, y);
                            maxY = Math.Max(maxY, y);
                            minZ = Math.Min(minZ, z);
                            maxZ = Math.Max(maxZ, z);
                        }
                    }
                }
            }

            // Add a margin of 1 voxel for safety with boundary conditions
            minX = Math.Max(0, minX - 1);
            minY = Math.Max(0, minY - 1);
            minZ = Math.Max(0, minZ - 1);
            maxX = Math.Min(_originalWidth - 1, maxX + 1);
            maxY = Math.Min(_originalHeight - 1, maxY + 1);
            maxZ = Math.Min(_originalDepth - 1, maxZ + 1);
        }

        private void ExtractMaterialVolume(ILabelVolumeData originalLabels, float[,,] originalDensity, byte materialID)
        {
            int subVoxelCount = 0;

            // Fill the sub-volume arrays
            for (int z = MinZ; z <= MaxZ; z++)
            {
                for (int y = MinY; y <= MaxY; y++)
                {
                    for (int x = MinX; x <= MaxX; x++)
                    {
                        // Calculate sub-volume coordinates
                        int sx = x - MinX;
                        int sy = y - MinY;
                        int sz = z - MinZ;

                        byte label = originalLabels[x, y, z];

                        // Copy only if it's the selected material or part of the added margin
                        if (label == materialID)
                        {
                            _subLabels[sx, sy, sz] = materialID;
                            _subDensity[sx, sy, sz] = originalDensity[x, y, z];

                            // Store index mapping
                            int index = subVoxelCount++;
                            _originalToSubMap[x, y, z] = index;
                            _subToOriginalMap.Add((x, y, z));
                        }
                        else
                        {
                            // For margin voxels, assign a distinct label (0 for exterior)
                            _subLabels[sx, sy, sz] = 0;
                            _subDensity[sx, sy, sz] = 0;
                        }
                    }
                }
            }
        }

        // Implementation of ILabelVolumeData/IVolumeData interfaces

        // Getter (required)
        public byte this[int x, int y, int z]
        {
            get
            {
                // Check bounds for sub-volume
                if (x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth)
                    return 0;

                return _subLabels[x, y, z];
            }
            // Setter (also required by the interface)
            set
            {
                // Only update if within bounds
                if (x >= 0 && x < Width && y >= 0 && y < Height && z >= 0 && z < Depth)
                {
                    _subLabels[x, y, z] = value;
                }
            }
        }

        // Get the density value from the sub-volume
        public float GetDensity(int x, int y, int z)
        {
            // Check bounds for sub-volume
            if (x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth)
                return 0;

            return _subDensity[x, y, z];
        }

        // Convert sub-volume coordinates to original coordinates
        public (int x, int y, int z) ConvertToOriginal(int subX, int subY, int subZ)
        {
            return (subX + MinX, subY + MinY, subZ + MinZ);
        }

        // Convert original coordinates to sub-volume coordinates
        public (int x, int y, int z) ConvertToSub(int origX, int origY, int origZ)
        {
            return (origX - MinX, origY - MinY, origZ - MinZ);
        }

        // Check if original coordinates map to a sub-volume voxel
        public bool IsInSubVolume(int origX, int origY, int origZ)
        {
            return origX >= MinX && origX <= MaxX &&
                   origY >= MinY && origY <= MaxY &&
                   origZ >= MinZ && origZ <= MaxZ &&
                   _originalToSubMap[origX, origY, origZ] >= 0;
        }

        // Get index in the mapping array for a original coordinate
        public int GetMapIndex(int origX, int origY, int origZ)
        {
            if (!IsInSubVolume(origX, origY, origZ))
                return -1;

            return _originalToSubMap[origX, origY, origZ];
        }

        // Get original coordinates for a map index
        public (int x, int y, int z) GetOriginalFromIndex(int index)
        {
            if (index < 0 || index >= _subToOriginalMap.Count)
                return (-1, -1, -1);

            return _subToOriginalMap[index];
        }

        // Copy damage data from sub-volume to original volume array
        public void CopyDamageToOriginal(double[,,] subDamage, double[,,] origDamage)
        {
            // First zero out the original damage array
            Array.Clear(origDamage, 0, origDamage.Length);

            // Copy damage values from sub-volume back to original coordinates
            for (int sz = 0; sz < Depth; sz++)
            {
                for (int sy = 0; sy < Height; sy++)
                {
                    for (int sx = 0; sx < Width; sx++)
                    {
                        // Only copy material voxels (not margin)
                        if (_subLabels[sx, sy, sz] == MaterialID)
                        {
                            (int ox, int oy, int oz) = ConvertToOriginal(sx, sy, sz);
                            origDamage[ox, oy, oz] = subDamage[sx, sy, sz];
                        }
                    }
                }
            }
        }

        // IVolumeData chunk-related implementations
        public int GetChunkIndex(int x, int y, int z)
        {
            // Pass through to original volume
            return _originalVolume.GetChunkIndex(x, y, z);
        }

        public byte[] GetChunkBytes(int chunkIndex)
        {
            // Pass through to original volume
            return _originalVolume.GetChunkBytes(chunkIndex);
        }

        // ILabelVolumeData specific methods
        public void WriteChunks(BinaryWriter writer)
        {
            // Pass through to original volume
            _originalVolume.WriteChunks(writer);
        }

        public void ReleaseFileLock()
        {
            // Pass through to original volume
            _originalVolume.ReleaseFileLock();
        }

        public void ReadChunksHeaderAndData(BinaryReader reader, MemoryMappedFile mmf, long offset)
        {
            // Pass through to original volume
            _originalVolume.ReadChunksHeaderAndData(reader, mmf, offset);
        }

        public void Dispose()
        {
            // No resources to dispose yet - the arrays will be garbage collected
        }
    }
}