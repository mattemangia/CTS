using System;
using System.Drawing;
using System.Threading.Tasks;

namespace CTS
{
    /// <summary>
    /// Adapter that integrates ILabelVolumeData with TriaxialVisualizationExtension for triaxial visualization
    /// </summary>
    public class TriaxialVisualizationAdapter : IDisposable
    {
        private TriaxialVisualizationExtension _visualizer;
        private byte[,,] _labelVolumeArray;
        private bool _disposed = false;

        /// <summary>
        /// Initialize a new instance of the TriaxialVisualizationAdapter class
        /// </summary>
        /// <param name="width">Width of the volume</param>
        /// <param name="height">Height of the volume</param>
        /// <param name="depth">Depth of the volume</param>
        /// <param name="pixelSize">Pixel size in meters</param>
        /// <param name="labelVolume">Label volume data</param>
        /// <param name="densityVolume">Density volume data</param>
        /// <param name="materialID">Material ID to visualize</param>
        public TriaxialVisualizationAdapter(
     int width, int height, int depth, float pixelSize,
     ILabelVolumeData labelVolume, float[,,] densityVolume, byte materialID)
        {
            try
            {
                // For very large volumes, we may need to downsample to avoid array size limitations
                int downsample = CalculateDownsampleFactor(width, height, depth);

                // Create the label volume array with appropriate size
                int visWidth = Math.Max(1, width / downsample);
                int visHeight = Math.Max(1, height / downsample);
                int visDepth = Math.Max(1, depth / downsample);

                Logger.Log($"[TriaxialVisualizationAdapter] Creating volume size {visWidth}x{visHeight}x{visDepth} with downsample factor {downsample}");

                _labelVolumeArray = new byte[visWidth, visHeight, visDepth];

                // Copy data from the ILabelVolumeData to our array - do this in a fast way
                CopyVolumeData(labelVolume, width, height, depth, downsample);

                // Create a downsampled density volume if needed
                float[,,] useDensityVolume = densityVolume;
                if (downsample > 1)
                {
                    useDensityVolume = DownsampleDensityVolume(densityVolume, width, height, depth, downsample);
                }

                // Create the visualizer with our array
                _visualizer = new TriaxialVisualizationExtension(
                    visWidth, visHeight, visDepth,
                    pixelSize * downsample, // Adjust pixel size for downsampling
                    _labelVolumeArray,
                    useDensityVolume, // Use downsampled density volume if needed
                    materialID);

                // Set initial slice positions at the center of the volume
                int centerX = visWidth / 2;
                int centerY = visHeight / 2;
                int centerZ = visDepth / 2;
                SetSlicePositions(centerX, centerY, centerZ);

                Logger.Log($"[TriaxialVisualizationAdapter] Successfully initialized visualization");
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialVisualizationAdapter] Error initializing adapter: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }
        /// <summary>
        /// Set the failure point for visualization
        /// </summary>
        public void SetFailurePoint(bool detected, int x, int y, int z)
        {
            _visualizer?.SetFailurePoint(detected, x, y, z);
        }

        /// <summary>
        /// Set the slice positions for visualization
        /// </summary>
        public void SetSlicePositions(int x, int y, int z)
        {
            _visualizer?.SetSlicePositions(x, y, z);
        }
        /// <summary>
        /// Calculate an appropriate downsample factor based on volume size
        /// </summary>
        private int CalculateDownsampleFactor(int width, int height, int depth)
        {
            // Check if the volume is too large for a single array in .NET 4.8 (~2GB limit)
            long totalVoxels = (long)width * height * depth;
            int downsample = 1;

            // If greater than ~100 million voxels, we need to downsample
            if (totalVoxels > 100_000_000)
            {
                // Calculate downsample factor to get below threshold
                downsample = 2;
                while ((long)(width / downsample) * (height / downsample) * (depth / downsample) > 100_000_000)
                {
                    downsample *= 2;
                }

                Logger.Log($"[TriaxialVisualizationAdapter] Downsampling by factor of {downsample} for volume of {totalVoxels} voxels");
            }

            return downsample;
        }

        /// <summary>
        /// Copy data from the ILabelVolumeData to our array with optional downsampling
        /// </summary>
        private void CopyVolumeData(ILabelVolumeData labelVolume, int width, int height, int depth, int downsample)
        {
            int visWidth = _labelVolumeArray.GetLength(0);
            int visHeight = _labelVolumeArray.GetLength(1);
            int visDepth = _labelVolumeArray.GetLength(2);

            // Copy data with downsampling if needed
            for (int z = 0; z < visDepth; z++)
            {
                int srcZ = Math.Min(z * downsample, depth - 1);
                for (int y = 0; y < visHeight; y++)
                {
                    int srcY = Math.Min(y * downsample, height - 1);
                    for (int x = 0; x < visWidth; x++)
                    {
                        int srcX = Math.Min(x * downsample, width - 1);
                        _labelVolumeArray[x, y, z] = labelVolume[srcX, srcY, srcZ];
                    }
                }
            }
        }

        /// <summary>
        /// Create a downsampled version of the density volume
        /// </summary>
        private float[,,] DownsampleDensityVolume(float[,,] densityVolume, int width, int height, int depth, int downsample)
        {
            int visWidth = Math.Max(1, width / downsample);
            int visHeight = Math.Max(1, height / downsample);
            int visDepth = Math.Max(1, depth / downsample);

            float[,,] result = new float[visWidth, visHeight, visDepth];

            // Copy data with downsampling using averaging for better quality
            Parallel.For(0, visDepth, z =>
            {
                int startZ = z * downsample;
                int endZ = Math.Min(startZ + downsample, depth);

                for (int y = 0; y < visHeight; y++)
                {
                    int startY = y * downsample;
                    int endY = Math.Min(startY + downsample, height);

                    for (int x = 0; x < visWidth; x++)
                    {
                        int startX = x * downsample;
                        int endX = Math.Min(startX + downsample, width);

                        // Calculate average density in this block
                        float sum = 0;
                        int count = 0;

                        for (int sz = startZ; sz < endZ; sz++)
                        {
                            for (int sy = startY; sy < endY; sy++)
                            {
                                for (int sx = startX; sx < endX; sx++)
                                {
                                    sum += densityVolume[sx, sy, sz];
                                    count++;
                                }
                            }
                        }

                        result[x, y, z] = count > 0 ? sum / count : 0;
                    }
                }
            });

            return result;
        }

        /// <summary>
        /// Set the pressure parameters for visualization
        /// </summary>
        public void SetPressureParameters(double confiningPressure, double axialPressure, StressAxis axis)
        {
            _visualizer?.SetPressureParameters(confiningPressure, axialPressure, axis);
        }

        /// <summary>
        /// Set the view transformation parameters
        /// </summary>
        public void SetViewTransformation(float rotationX, float rotationY, float zoom, PointF pan)
        {
            _visualizer?.SetViewTransformation(rotationX, rotationY, zoom, pan);
        }

        /// <summary>
        /// Render the visualization to the specified graphics context
        /// </summary>
        public void Render(Graphics g, int width, int height)
        {
            _visualizer?.Render(g, width, height);
        }

        /// <summary>
        /// Force regeneration of visualizations
        /// </summary>
        public void Invalidate()
        {
            _visualizer?.Invalidate();
        }

        /// <summary>
        /// Dispose of resources
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _visualizer?.Dispose();
                _visualizer = null;
                _labelVolumeArray = null;
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}