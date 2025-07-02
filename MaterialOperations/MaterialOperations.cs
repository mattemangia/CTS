using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CTS
{
    /// <summary>
    /// High-performance implementation of IMaterialOperations that provides
    /// efficient and parallel operations for material and voxel management in 3D volumes.
    /// </summary>
    public class MaterialOperations : IMaterialOperations
    {
        // Constants for optimization
        private const int OPTIMAL_CHUNK_SIZE = 64;

        // Volume data and dimensions
        private readonly ILabelVolumeData _volumeLabels;
        private readonly List<Material> _materials;
        private readonly int _width, _height, _depth;
        private readonly object _materialsLock = new object();

        // Processing optimization flags and state
        private readonly bool _isChunkedLabels;
        private readonly int _chunkDim;
        private readonly int _chunkCountX, _chunkCountY, _chunkCountZ;
        private readonly int _optimalThreadCount;

        // Volume data type information for optimized processing
        private Type _labelVolumeType;
        private Type _grayscaleVolumeType;

        /// <summary>
        /// Initializes a new instance of the MaterialOperations class.
        /// </summary>
        public MaterialOperations(ILabelVolumeData volumeLabels, List<Material> materials, int width, int height, int depth)
        {
            _volumeLabels = volumeLabels ?? throw new ArgumentNullException(nameof(volumeLabels));
            _materials = materials ?? throw new ArgumentNullException(nameof(materials));

            _width = width > 0 ? width : throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive");
            _height = height > 0 ? height : throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive");
            _depth = depth > 0 ? depth : throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be positive");

            // Calculate optimal number of threads based on CPU cores
            _optimalThreadCount = Math.Max(1, Environment.ProcessorCount - 1);

            // Determine if we're using chunked volumes and get chunk information
            _labelVolumeType = _volumeLabels.GetType();
            _isChunkedLabels = _labelVolumeType == typeof(ChunkedLabelVolume) ||
                              _labelVolumeType == typeof(LabelVolumeDataArray);

            // Set chunking dimensions, either from volume properties or calculated
            if (_volumeLabels is ChunkedLabelVolume chunkedLabels)
            {
                _chunkDim = chunkedLabels.ChunkDim;
                _chunkCountX = chunkedLabels.ChunkCountX;
                _chunkCountY = chunkedLabels.ChunkCountY;
                _chunkCountZ = chunkedLabels.ChunkCountZ;
            }
            else if (_volumeLabels is LabelVolumeDataArray arrayLabels)
            {
                _chunkDim = arrayLabels.ChunkDim;
                _chunkCountX = arrayLabels.ChunkCountX;
                _chunkCountY = arrayLabels.ChunkCountY;
                _chunkCountZ = arrayLabels.ChunkCountZ;
            }
            else
            {
                // Default chunking for non-chunked volumes
                _chunkDim = OPTIMAL_CHUNK_SIZE;
                _chunkCountX = (_width + _chunkDim - 1) / _chunkDim;
                _chunkCountY = (_height + _chunkDim - 1) / _chunkDim;
                _chunkCountZ = (_depth + _chunkDim - 1) / _chunkDim;
            }

            Logger.Log($"[MaterialOperations] Initialized with dimensions {width}x{height}x{depth}");
            Logger.Log($"[MaterialOperations] Using {_optimalThreadCount} threads for parallel operations");
            Logger.Log($"[MaterialOperations] Chunk dimensions: {_chunkDim}, Count: {_chunkCountX}x{_chunkCountY}x{_chunkCountZ}");
        }

        #region IMaterialOperations Interface Implementation

        /// <summary>
        /// Gets the next available material ID.
        /// </summary>
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
        public void RemoveMaterial(byte materialID)
        {
            // Do not allow deletion of the Exterior material (ID 0).
            if (materialID == 0)
                return;

            // Check if volume labels exist
            if (_volumeLabels == null)
                return;

            Logger.Log($"[MaterialOperations] Starting removal of material {materialID}...");

            // Process all slices in parallel
            Parallel.For(0, _depth, new ParallelOptions { MaxDegreeOfParallelism = _optimalThreadCount }, z =>
            {
                // Process one slice at a time
                RemoveMaterialFromSlice(materialID, z);
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
        /// Label every voxel whose grayscale value is in [minVal,maxVal] with materialID.
        /// Highly optimized implementation for performance.
        /// </summary>
        public void AddVoxelsByThreshold(IGrayscaleVolumeData volumeData,
                                       byte materialID,
                                       byte minVal, byte maxVal)
        {
            if (volumeData == null || _volumeLabels == null) return;

            // Log the operation start
            Logger.Log($"[MaterialOperations] Starting to add voxels to material {materialID} (threshold {minVal}-{maxVal})");

            // Store the grayscale volume type for optimized processing
            _grayscaleVolumeType = volumeData.GetType();

            // Use specialized implementations based on volume types for maximum performance
            if (IsBestForChunkProcessing(volumeData))
            {
                AddVoxelsByThresholdChunked(volumeData, materialID, minVal, maxVal);
            }
            else
            {
                AddVoxelsByThresholdSliced(volumeData, materialID, minVal, maxVal);
            }

            Logger.Log($"[MaterialOperations] Completed adding voxels to material {materialID}");
        }

        /// <summary>
        /// Clear voxels that currently belong to materialID and whose grayscale lies in the threshold interval.
        /// Highly optimized implementation for performance.
        /// </summary>
        public void RemoveVoxelsByThreshold(IGrayscaleVolumeData volumeData,
                                          byte materialID,
                                          byte minVal, byte maxVal)
        {
            if (volumeData == null || _volumeLabels == null) return;

            // Log the operation start
            Logger.Log($"[MaterialOperations] Starting to remove voxels from material {materialID} (threshold {minVal}-{maxVal})");

            // Store the grayscale volume type for optimized processing
            _grayscaleVolumeType = volumeData.GetType();

            // Use specialized implementations based on volume types for maximum performance
            if (IsBestForChunkProcessing(volumeData))
            {
                RemoveVoxelsByThresholdChunked(volumeData, materialID, minVal, maxVal);
            }
            else
            {
                RemoveVoxelsByThresholdSliced(volumeData, materialID, minVal, maxVal);
            }

            Logger.Log($"[MaterialOperations] Completed removing voxels from material {materialID}");
        }

        /// <summary>
        /// Adds voxels to a material based on a grayscale threshold for a specific slice.
        /// Optimized implementation.
        /// </summary>
        public void AddVoxelsByThresholdForSlice(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal, int slice)
        {
            if (volumeData == null || _volumeLabels == null) return;
            if (slice < 0 || slice >= _depth) return;

            // Process a single slice with the optimized implementation
            AddVoxelsByThresholdInSlice(volumeData, materialID, minVal, maxVal, slice);

            Logger.Log($"[MaterialOperations] Added voxels to material {materialID} in slice {slice} using threshold {minVal}-{maxVal}");
        }

        /// <summary>
        /// Removes voxels from a material based on a grayscale threshold for a specific slice.
        /// Optimized implementation.
        /// </summary>
        public void RemoveVoxelsByThresholdForSlice(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal, int slice)
        {
            if (volumeData == null || _volumeLabels == null) return;
            if (slice < 0 || slice >= _depth) return;

            // Process a single slice with the optimized implementation
            RemoveVoxelsByThresholdInSlice(volumeData, materialID, minVal, maxVal, slice);

            Logger.Log($"[MaterialOperations] Removed voxels from material {materialID} in slice {slice} using threshold {minVal}-{maxVal}");
        }

        /// <summary>
        /// Applies a 2D selection mask to a specific slice.
        /// </summary>
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

            // Fast path for full selection
            if (selectionWidth == _width && selectionHeight == _height)
            {
                ApplyFullSliceSelection(selection, slice);
                return;
            }

            // Apply the selection to the current slice with optimal parallelism
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

            // Fast path for full selection
            if (selectionWidth == _width && selectionHeight == _height)
            {
                SubtractFullSliceSelection(selection, slice);
                return;
            }

            // Subtract the selection from the current slice with optimal parallelism
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
        public void ApplyOrthogonalSelection(byte[,] selection, int fixedIndex, OrthogonalView view)
        {
            if (selection == null || _volumeLabels == null) return;

            switch (view)
            {
                case OrthogonalView.XZ:
                    if (fixedIndex < 0 || fixedIndex >= _height) return;

                    ApplyOrthogonalSelectionXZ(selection, fixedIndex);
                    Logger.Log($"[MaterialOperations] Applied XZ selection at Y={fixedIndex}");
                    break;

                case OrthogonalView.YZ:
                    if (fixedIndex < 0 || fixedIndex >= _width) return;

                    ApplyOrthogonalSelectionYZ(selection, fixedIndex);
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
        public void SubtractOrthogonalSelection(byte[,] selection, int fixedIndex, OrthogonalView view)
        {
            if (selection == null || _volumeLabels == null) return;

            switch (view)
            {
                case OrthogonalView.XZ:
                    if (fixedIndex < 0 || fixedIndex >= _height) return;

                    SubtractOrthogonalSelectionXZ(selection, fixedIndex);
                    Logger.Log($"[MaterialOperations] Subtracted XZ selection at Y={fixedIndex}");
                    break;

                case OrthogonalView.YZ:
                    if (fixedIndex < 0 || fixedIndex >= _width) return;

                    SubtractOrthogonalSelectionYZ(selection, fixedIndex);
                    Logger.Log($"[MaterialOperations] Subtracted YZ selection at X={fixedIndex}");
                    break;

                default:
                    Logger.Log("[MaterialOperations] Unsupported orthogonal view");
                    break;
            }
        }

        #endregion

        #region Bridge Methods for Compatibility

        /// <summary>
        /// Compatibility method that bridges to the new implementation
        /// </summary>
        public void AddVoxelsByThresholdOptimized(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal)
        {
            // Simply call the standard method which is now optimized
            AddVoxelsByThreshold(volumeData, materialID, minVal, maxVal);
        }

        /// <summary>
        /// Compatibility method that bridges to the new implementation
        /// </summary>
        public void RemoveVoxelsByThresholdOptimized(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal)
        {
            // Simply call the standard method which is now optimized
            RemoveVoxelsByThreshold(volumeData, materialID, minVal, maxVal);
        }

        /// <summary>
        /// Compatibility method that bridges to the new implementation
        /// </summary>
        public void AddVoxelsByThresholdAdvanced(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal)
        {
            // Simply call the standard method which is now optimized
            AddVoxelsByThreshold(volumeData, materialID, minVal, maxVal);
        }

        /// <summary>
        /// Compatibility method that bridges to the new implementation
        /// </summary>
        public void RemoveVoxelsByThresholdAdvanced(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal)
        {
            // Simply call the standard method which is now optimized
            RemoveVoxelsByThreshold(volumeData, materialID, minVal, maxVal);
        }

        #endregion

        #region Optimized Implementation Methods

        /// <summary>
        /// Determines if the volumes are best processed with a chunk-based approach.
        /// </summary>
        private bool IsBestForChunkProcessing(IGrayscaleVolumeData volumeData)
        {
            // Chunk processing is best when both volumes are chunked with the same dimensions
            bool bothChunked = _isChunkedLabels && (volumeData is ChunkedVolume || volumeData is IVolumeData);

            // For very large volumes, chunk processing is almost always better
            bool isVeryLarge = _width * _height * _depth > 100_000_000; // ~100M voxels

            return bothChunked || isVeryLarge;
        }

        private void RemoveMaterialFromSlice(byte materialID, int z)
        {
            // Fast inner loop for removing a material from a slice
            byte[] sliceBuffer = new byte[_width * _height];
            bool modified = false;

            // Check if the label volume is a type we can optimize.
            if (_volumeLabels is ChunkedLabelVolume fastVolume)
            {
                // --- OPTIMIZED PATH ---
                try
                {
                    fastVolume.ReadSliceZ(z, sliceBuffer);

                    // Process the entire slice buffer at once
                    for (int i = 0; i < sliceBuffer.Length; i++)
                    {
                        if (sliceBuffer[i] == materialID)
                        {
                            sliceBuffer[i] = 0; // Set to Exterior
                            modified = true;
                        }
                    }

                    // Write back only if modified using the new, fast method
                    if (modified)
                    {
                        fastVolume.WriteSliceZ(z, sliceBuffer);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[MaterialOperations] Optimized slice operation failed for slice {z}. Falling back. Error: {ex.Message}");
                    modified = false; // Reset flag to trigger fallback
                }
            }

            // --- FALLBACK PATH ---
            // Use this if the volume is not a 'ChunkedLabelVolume' or if the optimized path failed.
            if (!(_volumeLabels is ChunkedLabelVolume) || modified == false)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (_volumeLabels[x, y, z] == materialID)
                        {
                            _volumeLabels[x, y, z] = 0; // Set to Exterior
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Adds voxels by threshold using a slice-based approach.
        /// This is optimal for non-chunked volumes or when memory mapping is not used.
        /// </summary>
        private void AddVoxelsByThresholdSliced(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal)
        {
            // Simple counter for progress reporting
            int processedSlices = 0;
            int totalSlices = _depth;
            int logInterval = Math.Max(1, totalSlices / 10);  // Log progress every 10%

            Logger.Log($"[MaterialOperations] Processing volume using slice-based approach (total slices: {totalSlices})");

            // Process all slices in parallel
            Parallel.For(0, _depth, new ParallelOptions { MaxDegreeOfParallelism = _optimalThreadCount }, z =>
            {
                // Process this slice
                AddVoxelsByThresholdInSlice(volumeData, materialID, minVal, maxVal, z);

                // Update progress (atomic increment)
                int completed = Interlocked.Increment(ref processedSlices);

                // Log progress at appropriate intervals
                if (completed % logInterval == 0 || completed == totalSlices)
                {
                    int percentComplete = (completed * 100) / totalSlices;
                    Logger.Log($"[MaterialOperations] Progress: {completed}/{totalSlices} slices processed ({percentComplete}%)");
                }
            });
        }

        /// <summary>
        /// Removes voxels by threshold using a slice-based approach.
        /// </summary>
        private void RemoveVoxelsByThresholdSliced(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal)
        {
            // Simple counter for progress reporting
            int processedSlices = 0;
            int totalSlices = _depth;
            int logInterval = Math.Max(1, totalSlices / 10);  // Log progress every 10%

            Logger.Log($"[MaterialOperations] Processing volume using slice-based approach (total slices: {totalSlices})");

            // Process all slices in parallel
            Parallel.For(0, _depth, new ParallelOptions { MaxDegreeOfParallelism = _optimalThreadCount }, z =>
            {
                // Process this slice
                RemoveVoxelsByThresholdInSlice(volumeData, materialID, minVal, maxVal, z);

                // Update progress (atomic increment)
                int completed = Interlocked.Increment(ref processedSlices);

                // Log progress at appropriate intervals
                if (completed % logInterval == 0 || completed == totalSlices)
                {
                    int percentComplete = (completed * 100) / totalSlices;
                    Logger.Log($"[MaterialOperations] Progress: {completed}/{totalSlices} slices processed ({percentComplete}%)");
                }
            });
        }

        /// <summary>
        /// Adds voxels by threshold using a chunk-based approach.
        /// This is optimal for chunked volumes, especially with memory mapping.
        /// </summary>
        private void AddVoxelsByThresholdChunked(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal)
        {
            // Process all chunks in parallel
            int totalChunks = _chunkCountX * _chunkCountY * _chunkCountZ;
            int processedChunks = 0;
            int logInterval = Math.Max(1, totalChunks / 10);  // Log progress every 10%

            Logger.Log($"[MaterialOperations] Processing volume using chunk-based approach (total chunks: {totalChunks})");

            // Process chunks in parallel
            Parallel.For(0, totalChunks, new ParallelOptions { MaxDegreeOfParallelism = _optimalThreadCount }, chunkIndex =>
            {
                // Get chunk coordinates
                int cz = chunkIndex / (_chunkCountX * _chunkCountY);
                int remainder = chunkIndex % (_chunkCountX * _chunkCountY);
                int cy = remainder / _chunkCountX;
                int cx = remainder % _chunkCountX;

                // Process this chunk
                ProcessChunkWithThreshold(volumeData, cx, cy, cz, materialID, minVal, maxVal, true);

                // Update progress (atomic increment)
                int completed = Interlocked.Increment(ref processedChunks);

                // Log progress at appropriate intervals
                if (completed % logInterval == 0 || completed == totalChunks)
                {
                    int percentComplete = (completed * 100) / totalChunks;
                    Logger.Log($"[MaterialOperations] Progress: {completed}/{totalChunks} chunks processed ({percentComplete}%)");
                }
            });
        }

        /// <summary>
        /// Removes voxels by threshold using a chunk-based approach.
        /// </summary>
        private void RemoveVoxelsByThresholdChunked(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal)
        {
            // Process all chunks in parallel
            int totalChunks = _chunkCountX * _chunkCountY * _chunkCountZ;
            int processedChunks = 0;
            int logInterval = Math.Max(1, totalChunks / 10);  // Log progress every 10%

            Logger.Log($"[MaterialOperations] Processing volume using chunk-based approach (total chunks: {totalChunks})");

            // Process chunks in parallel
            Parallel.For(0, totalChunks, new ParallelOptions { MaxDegreeOfParallelism = _optimalThreadCount }, chunkIndex =>
            {
                // Get chunk coordinates
                int cz = chunkIndex / (_chunkCountX * _chunkCountY);
                int remainder = chunkIndex % (_chunkCountX * _chunkCountY);
                int cy = remainder / _chunkCountX;
                int cx = remainder % _chunkCountX;

                // Process this chunk
                ProcessChunkWithThreshold(volumeData, cx, cy, cz, materialID, minVal, maxVal, false);

                // Update progress (atomic increment)
                int completed = Interlocked.Increment(ref processedChunks);

                // Log progress at appropriate intervals
                if (completed % logInterval == 0 || completed == totalChunks)
                {
                    int percentComplete = (completed * 100) / totalChunks;
                    Logger.Log($"[MaterialOperations] Progress: {completed}/{totalChunks} chunks processed ({percentComplete}%)");
                }
            });
        }

        /// <summary>
        /// Processes a volume chunk with threshold operation (add or remove).
        /// </summary>
        private void ProcessChunkWithThreshold(
            IGrayscaleVolumeData volumeData,
            int cx, int cy, int cz,
            byte materialID, byte minVal, byte maxVal,
            bool isAddOperation)
        {
            // Calculate chunk boundaries
            int xStart = cx * _chunkDim;
            int yStart = cy * _chunkDim;
            int zStart = cz * _chunkDim;

            int xEnd = Math.Min(xStart + _chunkDim, _width);
            int yEnd = Math.Min(yStart + _chunkDim, _height);
            int zEnd = Math.Min(zStart + _chunkDim, _depth);

            // Process the chunk slice by slice for better cache locality
            byte[] graySlice = new byte[(xEnd - xStart) * (yEnd - yStart)];
            byte[] labelSlice = new byte[(xEnd - xStart) * (yEnd - yStart)];

            for (int z = zStart; z < zEnd; z++)
            {
                // Read the slice data
                ReadPartialSlice(volumeData, z, xStart, yStart, xEnd - xStart, yEnd - yStart, graySlice);
                ReadPartialLabelSlice(_volumeLabels, z, xStart, yStart, xEnd - xStart, yEnd - yStart, labelSlice);

                // Process the slice data
                bool modified = ProcessSliceWithThreshold(
                    graySlice, labelSlice,
                    (xEnd - xStart) * (yEnd - yStart),
                    materialID, minVal, maxVal, isAddOperation);

                // Write back only if modified
                if (modified)
                {
                    WritePartialLabelSlice(_volumeLabels, z, xStart, yStart, xEnd - xStart, yEnd - yStart, labelSlice);
                }
            }
        }

        /// <summary>
        /// Adds voxels by threshold in a specific slice.
        /// </summary>
        private void AddVoxelsByThresholdInSlice(
            IGrayscaleVolumeData volumeData,
            byte materialID, byte minVal, byte maxVal,
            int slice)
        {
            // Read entire slices for better performance
            byte[] graySlice = new byte[_width * _height];
            byte[] labelSlice = new byte[_width * _height];

            // Read the full slice data
            ReadFullSlice(volumeData, slice, graySlice);
            ReadFullLabelSlice(_volumeLabels, slice, labelSlice);

            // Process the slice with threshold (add operation)
            bool modified = ProcessSliceWithThreshold(
                graySlice, labelSlice, _width * _height,
                materialID, minVal, maxVal, true);

            // Write back only if modified
            if (modified)
            {
                WriteFullLabelSlice(_volumeLabels, slice, labelSlice);
            }
        }

        /// <summary>
        /// Removes voxels by threshold in a specific slice.
        /// </summary>
        private void RemoveVoxelsByThresholdInSlice(
            IGrayscaleVolumeData volumeData,
            byte materialID, byte minVal, byte maxVal,
            int slice)
        {
            // Read entire slices for better performance
            byte[] graySlice = new byte[_width * _height];
            byte[] labelSlice = new byte[_width * _height];

            // Read the full slice data
            ReadFullSlice(volumeData, slice, graySlice);
            ReadFullLabelSlice(_volumeLabels, slice, labelSlice);

            // Process the slice with threshold (remove operation)
            bool modified = ProcessSliceWithThreshold(
                graySlice, labelSlice, _width * _height,
                materialID, minVal, maxVal, false);

            // Write back only if modified
            if (modified)
            {
                WriteFullLabelSlice(_volumeLabels, slice, labelSlice);
            }
        }

        /// <summary>
        /// Optimized processing of a slice with threshold.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ProcessSliceWithThreshold(
            byte[] graySlice, byte[] labelSlice, int length,
            byte materialID, byte minVal, byte maxVal,
            bool isAddOperation)
        {
            bool modified = false;

            if (isAddOperation)
            {
                // Add voxels by threshold
                for (int i = 0; i < length; i++)
                {
                    byte gray = graySlice[i];
                    if (gray >= minVal && gray <= maxVal && labelSlice[i] != materialID)
                    {
                        labelSlice[i] = materialID;
                        modified = true;
                    }
                }
            }
            else
            {
                // Remove voxels by threshold
                for (int i = 0; i < length; i++)
                {
                    byte gray = graySlice[i];
                    if (labelSlice[i] == materialID && gray >= minVal && gray <= maxVal)
                    {
                        labelSlice[i] = 0;
                        modified = true;
                    }
                }
            }

            return modified;
        }

        /// <summary>
        /// Optimized method to apply a full slice selection.
        /// </summary>
        private void ApplyFullSliceSelection(byte[,] selection, int slice)
        {
            // Create a flat buffer for the selection and labels
            byte[] selBuffer = new byte[_width * _height];
            byte[] labelBuffer = new byte[_width * _height];

            // Copy the 2D selection to a flat buffer
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    selBuffer[y * _width + x] = selection[x, y];
                }
            }

            // Read the current labels
            ReadFullLabelSlice(_volumeLabels, slice, labelBuffer);

            // Apply the selection
            bool modified = false;
            for (int i = 0; i < selBuffer.Length; i++)
            {
                if (selBuffer[i] != 0 && labelBuffer[i] != selBuffer[i])
                {
                    labelBuffer[i] = selBuffer[i];
                    modified = true;
                }
            }

            // Write back only if modified
            if (modified)
            {
                WriteFullLabelSlice(_volumeLabels, slice, labelBuffer);
            }
        }

        /// <summary>
        /// Optimized method to subtract a full slice selection.
        /// </summary>
        private void SubtractFullSliceSelection(byte[,] selection, int slice)
        {
            // Create a flat buffer for the selection and labels
            byte[] selBuffer = new byte[_width * _height];
            byte[] labelBuffer = new byte[_width * _height];

            // Copy the 2D selection to a flat buffer
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    selBuffer[y * _width + x] = selection[x, y];
                }
            }

            // Read the current labels
            ReadFullLabelSlice(_volumeLabels, slice, labelBuffer);

            // Subtract the selection
            bool modified = false;
            for (int i = 0; i < selBuffer.Length; i++)
            {
                if (selBuffer[i] != 0 && labelBuffer[i] == selBuffer[i])
                {
                    labelBuffer[i] = 0;
                    modified = true;
                }
            }

            // Write back only if modified
            if (modified)
            {
                WriteFullLabelSlice(_volumeLabels, slice, labelBuffer);
            }
        }

        /// <summary>
        /// Optimized method to apply an orthogonal selection in the XZ plane.
        /// </summary>
        private void ApplyOrthogonalSelectionXZ(byte[,] selection, int yFixed)
        {
            int selectionWidth = Math.Min(selection.GetLength(0), _width);
            int selectionDepth = Math.Min(selection.GetLength(1), _depth);

            // Prepare a flat buffer for faster access
            byte[] selBuffer = new byte[selectionWidth * selectionDepth];
            for (int z = 0; z < selectionDepth; z++)
            {
                for (int x = 0; x < selectionWidth; x++)
                {
                    selBuffer[z * selectionWidth + x] = selection[x, z];
                }
            }

            // Apply in parallel across Z slices
            Parallel.For(0, selectionDepth, z =>
            {
                for (int x = 0; x < selectionWidth; x++)
                {
                    byte sel = selBuffer[z * selectionWidth + x];
                    if (sel != 0)
                    {
                        _volumeLabels[x, yFixed, z] = sel;
                    }
                }
            });
        }

        /// <summary>
        /// Optimized method to apply an orthogonal selection in the YZ plane.
        /// </summary>
        private void ApplyOrthogonalSelectionYZ(byte[,] selection, int xFixed)
        {
            int selectionDepth = Math.Min(selection.GetLength(0), _depth);
            int selectionHeight = Math.Min(selection.GetLength(1), _height);

            // Prepare a flat buffer for faster access
            byte[] selBuffer = new byte[selectionDepth * selectionHeight];
            for (int z = 0; z < selectionDepth; z++)
            {
                for (int y = 0; y < selectionHeight; y++)
                {
                    selBuffer[z * selectionHeight + y] = selection[z, y];
                }
            }

            // Apply in parallel across Z slices
            Parallel.For(0, selectionDepth, z =>
            {
                for (int y = 0; y < selectionHeight; y++)
                {
                    byte sel = selBuffer[z * selectionHeight + y];
                    if (sel != 0)
                    {
                        _volumeLabels[xFixed, y, z] = sel;
                    }
                }
            });
        }

        /// <summary>
        /// Optimized method to subtract an orthogonal selection in the XZ plane.
        /// </summary>
        private void SubtractOrthogonalSelectionXZ(byte[,] selection, int yFixed)
        {
            int selectionWidth = Math.Min(selection.GetLength(0), _width);
            int selectionDepth = Math.Min(selection.GetLength(1), _depth);

            // Prepare a flat buffer for faster access
            byte[] selBuffer = new byte[selectionWidth * selectionDepth];
            for (int z = 0; z < selectionDepth; z++)
            {
                for (int x = 0; x < selectionWidth; x++)
                {
                    selBuffer[z * selectionWidth + x] = selection[x, z];
                }
            }

            // Apply in parallel across Z slices
            Parallel.For(0, selectionDepth, z =>
            {
                for (int x = 0; x < selectionWidth; x++)
                {
                    byte sel = selBuffer[z * selectionWidth + x];
                    if (sel != 0 && _volumeLabels[x, yFixed, z] == sel)
                    {
                        _volumeLabels[x, yFixed, z] = 0;
                    }
                }
            });
        }

        /// <summary>
        /// Optimized method to subtract an orthogonal selection in the YZ plane.
        /// </summary>
        private void SubtractOrthogonalSelectionYZ(byte[,] selection, int xFixed)
        {
            int selectionDepth = Math.Min(selection.GetLength(0), _depth);
            int selectionHeight = Math.Min(selection.GetLength(1), _height);

            // Prepare a flat buffer for faster access
            byte[] selBuffer = new byte[selectionDepth * selectionHeight];
            for (int z = 0; z < selectionDepth; z++)
            {
                for (int y = 0; y < selectionHeight; y++)
                {
                    selBuffer[z * selectionHeight + y] = selection[z, y];
                }
            }

            // Apply in parallel across Z slices
            Parallel.For(0, selectionDepth, z =>
            {
                for (int y = 0; y < selectionHeight; y++)
                {
                    byte sel = selBuffer[z * selectionHeight + y];
                    if (sel != 0 && _volumeLabels[xFixed, y, z] == sel)
                    {
                        _volumeLabels[xFixed, y, z] = 0;
                    }
                }
            });
        }

        #endregion

        #region Low-Level Data Access Methods

        /// <summary>
        /// Reads a full slice from a grayscale volume.
        /// </summary>
        private void ReadFullSlice(IGrayscaleVolumeData volume, int z, byte[] buffer)
        {
            try
            {
                // For volumes supporting direct slice reading
                if (volume is IVolumeData volumeWithSlices)
                {
                    byte[] slice = volumeWithSlices.GetSliceXY(z);
                    if (slice.Length == buffer.Length)
                    {
                        Buffer.BlockCopy(slice, 0, buffer, 0, slice.Length);
                        return;
                    }
                }

                // Fallback to voxel-by-voxel reading
                int index = 0;
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        buffer[index++] = volume[x, y, z];
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[MaterialOperations] Error reading slice: {ex.Message}");
                // Fallback to voxel-by-voxel reading
                int index = 0;
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        buffer[index++] = volume[x, y, z];
                    }
                }
            }
        }

        /// <summary>
        /// Reads a full slice from a label volume.
        /// </summary>
        private void ReadFullLabelSlice(ILabelVolumeData volume, int z, byte[] buffer)
        {
            try
            {
                // Try to use the direct slice reading method
                volume.ReadSliceZ(z, buffer);
            }
            catch (Exception ex)
            {
                Logger.Log($"[MaterialOperations] Error reading label slice: {ex.Message}");
                // Fallback to voxel-by-voxel reading
                int index = 0;
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        buffer[index++] = volume[x, y, z];
                    }
                }
            }
        }

        /// <summary>
        /// Writes a full slice to a label volume.
        /// </summary>
        private void WriteFullLabelSlice(ILabelVolumeData volume, int z, byte[] buffer)
        {
            // Write voxel-by-voxel (no efficient bulk writing method available)
            int index = 0;
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    volume[x, y, z] = buffer[index++];
                }
            }
        }

        /// <summary>
        /// Writes a slice to a label volume.
        /// </summary>
        private void WriteSliceToVolume(ILabelVolumeData volume, int z, byte[] buffer)
        {
            // Write back voxel-by-voxel (no efficient bulk write method is available)
            int index = 0;
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    volume[x, y, z] = buffer[index++];
                }
            }
        }

        /// <summary>
        /// Reads a partial slice from a grayscale volume.
        /// </summary>
        private void ReadPartialSlice(
            IGrayscaleVolumeData volume, int z,
            int xStart, int yStart, int width, int height,
            byte[] buffer)
        {
            // Read the partial slice
            int index = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    buffer[index++] = volume[xStart + x, yStart + y, z];
                }
            }
        }

        /// <summary>
        /// Reads a partial slice from a label volume.
        /// </summary>
        private void ReadPartialLabelSlice(
            ILabelVolumeData volume, int z,
            int xStart, int yStart, int width, int height,
            byte[] buffer)
        {
            // Read the partial slice
            int index = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    buffer[index++] = volume[xStart + x, yStart + y, z];
                }
            }
        }

        /// <summary>
        /// Writes a partial slice to a label volume.
        /// </summary>
        private void WritePartialLabelSlice(
            ILabelVolumeData volume, int z,
            int xStart, int yStart, int width, int height,
            byte[] buffer)
        {
            // Write the partial slice
            int index = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    volume[xStart + x, yStart + y, z] = buffer[index++];
                }
            }
        }

        #endregion
    }
}