using System;
using ILGPU;

namespace CTSegmenter
{
    // Define the kernel for variance calculation
    public static class VarianceKernels
    {
        /// <summary>
        /// Kernel to calculate variance across multiple slices for each pixel
        /// </summary>
        public static void CalculateVarianceKernel(
            Index1D index,
            ArrayView<byte> volumeData,
            ArrayView<float> varianceMap,
            int width,
            int height,
            int depth,
            int sliceStart,
            int sliceEnd)
        {
            // Calculate x, y coordinates from 1D index
            int x = index % width;
            int y = index / width;

            // Skip if out of bounds
            if (x >= width || y >= height)
                return;

            // Calculate variance for the current position
            float sum = 0.0f;
            float sumSquared = 0.0f;
            int validSlices = 0;

            // For each slice in the range
            for (int z = sliceStart; z <= sliceEnd; z++)
            {
                // Skip if out of bounds
                if (z < 0 || z >= depth)
                    continue;

                // Get voxel value and normalize to 0-1
                int offset = z * width * height + y * width + x;
                if (offset >= 0 && offset < volumeData.Length)
                {
                    byte voxelValue = volumeData[offset];
                    float val = voxelValue / 255.0f;

                    // Update sums
                    sum += val;
                    sumSquared += val * val;
                    validSlices++;
                }
            }

            // Calculate variance only if we have at least 2 slices
            if (validSlices >= 2)
            {
                float mean = sum / validSlices;
                float variance = (sumSquared / validSlices) - (mean * mean);

                // Store variance in the output map - ensure positive value
                varianceMap[y * width + x] = Math.Max(0.000001f, variance);
            }
            else
            {
                // Default small value to avoid division by zero later
                varianceMap[y * width + x] = 0.000001f;
            }
        }
    }
}