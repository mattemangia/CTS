using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

namespace CTSegmenter
{
    /// <summary>
    /// Implementation of IMaterialOperations that provides efficient and parallel
    /// operations for material and voxel management in 3D volumes.
    /// </summary>
    public class MaterialOperations : IMaterialOperations
    {
        private readonly ILabelVolumeData _volumeLabels;
        private readonly List<Material> _materials;
        private readonly int _width, _height, _depth;
        private readonly object _materialsLock = new object();

        /// <summary>
        /// Initializes a new instance of the MaterialOperations class.
        /// </summary>
        /// <param name="volumeLabels">The 3D volume containing material labels</param>
        /// <param name="materials">The list of materials</param>
        /// <param name="width">Width of the volume (X dimension)</param>
        /// <param name="height">Height of the volume (Y dimension)</param>
        /// <param name="depth">Depth of the volume (Z dimension)</param>
        public MaterialOperations(ILabelVolumeData volumeLabels, List<Material> materials, int width, int height, int depth)
        {
            _volumeLabels = volumeLabels;
            _materials = materials;
            _width = width;
            _height = height;
            _depth = depth;
        }

        /// <summary>
        /// Gets the next available material ID.
        /// </summary>
        /// <returns>The next available material ID</returns>
        public byte GetNextMaterialID()
        {
            lock (_materialsLock)
            {
                // Find the smallest available ID starting from 1
                for (byte candidate = 1; candidate < byte.MaxValue; candidate++)
                {
                    if (!_materials.Any(m => m.ID == candidate))
                    {
                        return candidate;
                    }
                }
                throw new InvalidOperationException("No available material IDs.");
            }
        }

        /// <summary>
        /// Removes a material and clears all voxels assigned to it in the volume.
        /// </summary>
        /// <param name="materialID">ID of the material to remove</param>
        public void RemoveMaterial(byte materialID)
        {
            // Do not allow deletion of the Exterior material (ID 0).
            if (materialID == 0)
                return;

            // Check if volume labels exist
            if (_volumeLabels == null)
                return;

            // Clear segmentation voxels assigned to this material across the entire volume.
            Parallel.For(0, _depth, z =>
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (_volumeLabels[x, y, z] == materialID)
                            _volumeLabels[x, y, z] = 0;
                    }
                }
            });

            // Remove the material from the list by its unique ID.
            lock (_materialsLock)
            {
                Material toRemove = _materials.FirstOrDefault(m => m.ID == materialID);
                if (toRemove != null)
                {
                    _materials.Remove(toRemove);
                }
            }

            Logger.Log($"[MaterialOperations] Removed material with ID {materialID} and cleared associated voxels");
        }

        /// <summary>
        /// Adds voxels to a material based on a grayscale threshold across the entire volume.
        /// </summary>
        /// <param name="volumeData">The grayscale volume data</param>
        /// <param name="materialID">ID of the material to add voxels to</param>
        /// <param name="minVal">Minimum grayscale value</param>
        /// <param name="maxVal">Maximum grayscale value</param>
        public void AddVoxelsByThreshold(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal)
        {
            if (volumeData == null || _volumeLabels == null) return;

            Parallel.For(0, _depth, z =>
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        byte g = volumeData[x, y, z];
                        if (g >= minVal && g <= maxVal)
                            _volumeLabels[x, y, z] = materialID;
                    }
                }
            });

            Logger.Log($"[MaterialOperations] Added voxels to material {materialID} using threshold {minVal}-{maxVal}");
        }

        /// <summary>
        /// Adds voxels to a material based on a grayscale threshold for a specific slice.
        /// </summary>
        /// <param name="volumeData">The grayscale volume data</param>
        /// <param name="materialID">ID of the material to add voxels to</param>
        /// <param name="minVal">Minimum grayscale value</param>
        /// <param name="maxVal">Maximum grayscale value</param>
        /// <param name="slice">The slice index</param>
        public void AddVoxelsByThresholdForSlice(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal, int slice)
        {
            if (volumeData == null || _volumeLabels == null) return;
            if (slice < 0 || slice >= _depth) return;

            // First pass: Clear existing material labels that are outside the new range
            Parallel.For(0, _height, y =>
            {
                for (int x = 0; x < _width; x++)
                {
                    if (_volumeLabels[x, y, slice] == materialID)
                    {
                        byte g = volumeData[x, y, slice];
                        if (g < minVal || g > maxVal)
                        {
                            _volumeLabels[x, y, slice] = 0;
                        }
                    }
                }
            });

            // Second pass: Set new labels for voxels in range
            Parallel.For(0, _height, y =>
            {
                for (int x = 0; x < _width; x++)
                {
                    byte g = volumeData[x, y, slice];
                    if (g >= minVal && g <= maxVal)
                    {
                        _volumeLabels[x, y, slice] = materialID;
                    }
                }
            });

            Logger.Log($"[MaterialOperations] Added voxels to material {materialID} in slice {slice} using threshold {minVal}-{maxVal}");
        }

        /// <summary>
        /// Removes voxels from a material based on a grayscale threshold across the entire volume.
        /// </summary>
        /// <param name="volumeData">The grayscale volume data</param>
        /// <param name="materialID">ID of the material to remove voxels from</param>
        /// <param name="minVal">Minimum grayscale value</param>
        /// <param name="maxVal">Maximum grayscale value</param>
        public void RemoveVoxelsByThreshold(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal)
        {
            if (volumeData == null || _volumeLabels == null) return;

            Parallel.For(0, _depth, z =>
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (_volumeLabels[x, y, z] == materialID)
                        {
                            byte g = volumeData[x, y, z];
                            if (g >= minVal && g <= maxVal)
                                _volumeLabels[x, y, z] = 0;
                        }
                    }
                }
            });

            Logger.Log($"[MaterialOperations] Removed voxels from material {materialID} using threshold {minVal}-{maxVal}");
        }

        /// <summary>
        /// Removes voxels from a material based on a grayscale threshold for a specific slice.
        /// </summary>
        /// <param name="volumeData">The grayscale volume data</param>
        /// <param name="materialID">ID of the material to remove voxels from</param>
        /// <param name="minVal">Minimum grayscale value</param>
        /// <param name="maxVal">Maximum grayscale value</param>
        /// <param name="slice">The slice index</param>
        public void RemoveVoxelsByThresholdForSlice(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal, int slice)
        {
            if (volumeData == null || _volumeLabels == null) return;
            if (slice < 0 || slice >= _depth) return;

            Parallel.For(0, _height, y =>
            {
                for (int x = 0; x < _width; x++)
                {
                    if (_volumeLabels[x, y, slice] == materialID)
                    {
                        byte g = volumeData[x, y, slice];
                        if (g >= minVal && g <= maxVal)
                            _volumeLabels[x, y, slice] = 0;
                    }
                }
            });

            Logger.Log($"[MaterialOperations] Removed voxels from material {materialID} in slice {slice} using threshold {minVal}-{maxVal}");
        }

        /// <summary>
        /// Applies a 2D selection mask to a specific slice.
        /// </summary>
        /// <param name="selection">The 2D selection mask</param>
        /// <param name="slice">The slice index</param>
        public void ApplySelection(byte[,] selection, int slice)
        {
            if (selection == null || _volumeLabels == null) return;
            if (slice < 0 || slice >= _depth) return;

            // Check dimensions
            int selectionWidth = selection.GetLength(0);
            int selectionHeight = selection.GetLength(1);
            if (selectionWidth > _width || selectionHeight > _height)
            {
                Logger.Log("[MaterialOperations] Selection dimensions exceed volume dimensions");
                return;
            }

            // Apply the selection to the current slice
            Parallel.For(0, selectionHeight, y =>
            {
                for (int x = 0; x < selectionWidth; x++)
                {
                    byte sel = selection[x, y];
                    if (sel != 0)
                        _volumeLabels[x, y, slice] = sel;
                }
            });

            Logger.Log($"[MaterialOperations] Applied selection to slice {slice}");
        }

        /// <summary>
        /// Subtracts a 2D selection mask from a specific slice.
        /// </summary>
        /// <param name="selection">The 2D selection mask</param>
        /// <param name="slice">The slice index</param>
        public void SubtractSelection(byte[,] selection, int slice)
        {
            if (selection == null || _volumeLabels == null) return;
            if (slice < 0 || slice >= _depth) return;

            // Check dimensions
            int selectionWidth = selection.GetLength(0);
            int selectionHeight = selection.GetLength(1);
            if (selectionWidth > _width || selectionHeight > _height)
            {
                Logger.Log("[MaterialOperations] Selection dimensions exceed volume dimensions");
                return;
            }

            // Subtract the selection from the current slice
            Parallel.For(0, selectionHeight, y =>
            {
                for (int x = 0; x < selectionWidth; x++)
                {
                    byte sel = selection[x, y];
                    if (sel != 0 && _volumeLabels[x, y, slice] == sel)
                        _volumeLabels[x, y, slice] = 0;
                }
            });

            Logger.Log($"[MaterialOperations] Subtracted selection from slice {slice}");
        }

        /// <summary>
        /// Applies a 2D selection mask to an orthogonal view (XZ or YZ).
        /// </summary>
        /// <param name="selection">The 2D selection mask</param>
        /// <param name="fixedIndex">The fixed axis index (Y for XZ view, X for YZ view)</param>
        /// <param name="view">The orthogonal view (XZ or YZ)</param>
        public void ApplyOrthogonalSelection(byte[,] selection, int fixedIndex, OrthogonalView view)
        {
            if (selection == null || _volumeLabels == null) return;

            switch (view)
            {
                case OrthogonalView.XZ:
                    if (fixedIndex < 0 || fixedIndex >= _height) return;

                    Parallel.For(0, _depth, z =>
                    {
                        for (int x = 0; x < _width; x++)
                        {
                            if (x < selection.GetLength(0) && z < selection.GetLength(1))
                            {
                                byte sel = selection[x, z];
                                if (sel != 0)
                                    _volumeLabels[x, fixedIndex, z] = sel;
                            }
                        }
                    });
                    Logger.Log($"[MaterialOperations] Applied XZ selection at Y={fixedIndex}");
                    break;

                case OrthogonalView.YZ:
                    if (fixedIndex < 0 || fixedIndex >= _width) return;

                    Parallel.For(0, _depth, z =>
                    {
                        for (int y = 0; y < _height; y++)
                        {
                            if (z < selection.GetLength(0) && y < selection.GetLength(1))
                            {
                                byte sel = selection[z, y];
                                if (sel != 0)
                                    _volumeLabels[fixedIndex, y, z] = sel;
                            }
                        }
                    });
                    Logger.Log($"[MaterialOperations] Applied YZ selection at X={fixedIndex}");
                    break;

                default:
                    Logger.Log("[MaterialOperations] Unsupported orthogonal view");
                    break;
            }
        }

        /// <summary>
        /// Subtracts a 2D selection mask from an orthogonal view (XZ or YZ).
        /// </summary>
        /// <param name="selection">The 2D selection mask</param>
        /// <param name="fixedIndex">The fixed axis index (Y for XZ view, X for YZ view)</param>
        /// <param name="view">The orthogonal view (XZ or YZ)</param>
        public void SubtractOrthogonalSelection(byte[,] selection, int fixedIndex, OrthogonalView view)
        {
            if (selection == null || _volumeLabels == null) return;

            switch (view)
            {
                case OrthogonalView.XZ:
                    if (fixedIndex < 0 || fixedIndex >= _height) return;

                    Parallel.For(0, _depth, z =>
                    {
                        for (int x = 0; x < _width; x++)
                        {
                            if (x < selection.GetLength(0) && z < selection.GetLength(1))
                            {
                                byte sel = selection[x, z];
                                if (sel != 0 && _volumeLabels[x, fixedIndex, z] == sel)
                                    _volumeLabels[x, fixedIndex, z] = 0;
                            }
                        }
                    });
                    Logger.Log($"[MaterialOperations] Subtracted XZ selection at Y={fixedIndex}");
                    break;

                case OrthogonalView.YZ:
                    if (fixedIndex < 0 || fixedIndex >= _width) return;

                    Parallel.For(0, _depth, z =>
                    {
                        for (int y = 0; y < _height; y++)
                        {
                            if (z < selection.GetLength(0) && y < selection.GetLength(1))
                            {
                                byte sel = selection[z, y];
                                if (sel != 0 && _volumeLabels[fixedIndex, y, z] == sel)
                                    _volumeLabels[fixedIndex, y, z] = 0;
                            }
                        }
                    });
                    Logger.Log($"[MaterialOperations] Subtracted YZ selection at X={fixedIndex}");
                    break;

                default:
                    Logger.Log("[MaterialOperations] Unsupported orthogonal view");
                    break;
            }
        }
    }
}