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
            Index2D index,         // 2D processing grid (x, y)
            ArrayView<byte> voxels, // Input voxels
            int width,             // Width
            int height,            // Height
            int depth,             // Depth
            int sliceCount,        // Slice count
            int startSlice,        // Starting slice
            ArrayView<float> variances) // Output variances
        {
            int x = index.X;
            int y = index.Y;

            // If we're out of bounds, return
            if (x >= width || y >= height)
                return;

            // Calculate the 1D index for this pixel in the result
            int resultIdx = y * width + x;

            // Calculate sum and sum of squares for variance
            float sum = 0.0f;
            float sumSquared = 0.0f;
            int validSlices = 0;

            // Iterate through the slices to calculate variance
            for (int z = 0; z < sliceCount; z++)
            {
                int sliceIdx = startSlice + z;

                // Skip if out of bounds
                if (sliceIdx >= depth)
                    continue;

                // Calculate flattened index for this voxel
                int idx = sliceIdx * width * height + y * width + x;

                // Get the voxel value and normalize to 0-1
                float val = voxels[idx] / 255.0f;

                // Update sums
                sum += val;
                sumSquared += val * val;
                validSlices++;
            }

            // Calculate variance only if we have at least 2 slices
            if (validSlices >= 2)
            {
                float mean = sum / validSlices;
                float variance = (sumSquared / validSlices) - (mean * mean);

                // Store the result
                variances[resultIdx] = variance;
            }
            else
            {
                // Not enough slices for variance
                variances[resultIdx] = 0.0f;
            }
        }
    }
}