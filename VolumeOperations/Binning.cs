//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CTS
{
    public static class Binning
    {
        /// <summary>
        /// Process true 3D binning that reduces all three dimensions
        /// </summary>
        /// <param name="folderPath">Path to the dataset folder</param>
        /// <param name="binFactor">Binning factor to apply (e.g., 2, 4, 8)</param>
        /// <param name="pixelSizeOverride">If > 0, use this exact value instead of calculated one (for pre-scaled values)</param>
        /// <param name="useMemoryMapping">Whether to use memory mapping for large datasets</param>
        public static async Task ProcessBinningAsync(string folderPath, int binFactor, float basePixelSize, bool useMemoryMapping)
        {
            Logger.Log($"[Binning] Starting true 3D binning with factor {binFactor}");

            try
            {
                // Read the current volume dimensions
                var (oldWidth, oldHeight, oldDepth, chunkDim, oldPixelSize) = FileOperations.ReadVolumeChk(folderPath);

                // Calculate new dimensions - ALL THREE DIMENSIONS REDUCED
                int newWidth = Math.Max(1, oldWidth / binFactor);
                int newHeight = Math.Max(1, oldHeight / binFactor);
                int newDepth = Math.Max(1, oldDepth / binFactor);

                // FIXED: Pixel size calculation - properly handle binning factor
                double newPixelSize;

                if (basePixelSize > 0)
                {
                    // Apply binning factor to the base pixel size correctly here
                    // This is where the scaling should happen - not in AskUserPixelSize
                    newPixelSize = basePixelSize * binFactor;
                    Logger.Log($"[Binning] Calculated new pixel size from input: {basePixelSize} × {binFactor} = {newPixelSize}");
                }
                else
                {
                    // When not provided, scale the original pixel size from the volume
                    newPixelSize = oldPixelSize * binFactor;
                    Logger.Log($"[Binning] Calculated new pixel size from original: {oldPixelSize} × {binFactor} = {newPixelSize}");
                }

                Logger.Log($"[Binning] Dimensions: {oldWidth}x{oldHeight}x{oldDepth} -> {newWidth}x{newHeight}x{newDepth}");
                Logger.Log($"[Binning] Depth reduction: {oldDepth} -> {newDepth}");
                Logger.Log($"[Binning] Pixel size: {oldPixelSize} → {newPixelSize}");

                // Load the existing volume
                string volumeBinPath = Path.Combine(folderPath, "volume.bin");
                var sourceVolume = FileOperations.LoadVolumeBin(volumeBinPath, false);

                // Create new binned volume with reduced depth
                ChunkedVolume binnedVolume = new ChunkedVolume(newWidth, newHeight, newDepth, chunkDim);

                // Perform the actual 3D binning
                await Task.Run(() =>
                {
                    // For each voxel in the binned volume
                    Parallel.For(0, newDepth, binnedZ =>
                    {
                        for (int binnedY = 0; binnedY < newHeight; binnedY++)
                        {
                            for (int binnedX = 0; binnedX < newWidth; binnedX++)
                            {
                                // Calculate the corresponding region in the source volume
                                int srcXStart = binnedX * binFactor;
                                int srcYStart = binnedY * binFactor;
                                int srcZStart = binnedZ * binFactor;

                                int srcXEnd = Math.Min(srcXStart + binFactor, oldWidth);
                                int srcYEnd = Math.Min(srcYStart + binFactor, oldHeight);
                                int srcZEnd = Math.Min(srcZStart + binFactor, oldDepth);

                                // Average all voxels in the bin (3D box)
                                long sum = 0;
                                int count = 0;

                                for (int srcZ = srcZStart; srcZ < srcZEnd; srcZ++)
                                {
                                    for (int srcY = srcYStart; srcY < srcYEnd; srcY++)
                                    {
                                        for (int srcX = srcXStart; srcX < srcXEnd; srcX++)
                                        {
                                            sum += sourceVolume[srcX, srcY, srcZ];
                                            count++;
                                        }
                                    }
                                }

                                // Store the average
                                binnedVolume[binnedX, binnedY, binnedZ] = (byte)(sum / Math.Max(1, count));
                            }
                        }

                        if ((binnedZ + 1) % 10 == 0)
                        {
                            Logger.Log($"[Binning] Processed {binnedZ + 1}/{newDepth} binned slices");
                        }
                    });
                });

                Logger.Log("[Binning] 3D binning complete, saving results");

                // Save the binned volume
                string tempVolumePath = Path.Combine(folderPath, "volume_binned.tmp");
                binnedVolume.SaveAsBin(tempVolumePath);

                // Create a new empty labels file
                string tempLabelsPath = Path.Combine(folderPath, "labels_binned.tmp");
                FileOperations.CreateBlankLabelsFile(tempLabelsPath, newWidth, newHeight, newDepth, chunkDim);

                // Clean up
                sourceVolume.Dispose();
                binnedVolume.Dispose();

                // Move temporary files to final locations
                string labelsBinPath = Path.Combine(folderPath, "labels.bin");

                // Delete old files
                File.Delete(volumeBinPath);
                if (File.Exists(labelsBinPath))
                    File.Delete(labelsBinPath);

                // Rename temp files
                File.Move(tempVolumePath, volumeBinPath);
                File.Move(tempLabelsPath, labelsBinPath);

                // Update the header file with new dimensions
                FileOperations.CreateVolumeChk(folderPath, newWidth, newHeight, newDepth, chunkDim, newPixelSize);

                // Preserve materials if they exist
                string labelsChkPath = Path.Combine(folderPath, "labels.chk");
                if (File.Exists(labelsChkPath))
                {
                    var materials = FileOperations.ReadLabelsChk(folderPath);
                    FileOperations.CreateLabelsChk(folderPath, materials);
                }

                Logger.Log($"[Binning] Success! New volume: {newWidth}x{newHeight}x{newDepth}");

                // Create a marker file to indicate binning was done
                string markerPath = Path.Combine(folderPath, $"binned_{binFactor}x3d.txt");
                File.WriteAllText(markerPath, $"3D binned with factor {binFactor} at {DateTime.Now}\nOriginal: {oldWidth}x{oldHeight}x{oldDepth}\nNew: {newWidth}x{newHeight}x{newDepth}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[Binning] Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Alternative method to create a properly 3D-binned volume from images
        /// </summary>
        public static async Task Create3DBinnedVolumeFromImages(string folderPath, int binFactor)
        {
            Logger.Log($"[Binning] Creating 3D binned volume from images with factor {binFactor}");

            try
            {
                // Find image files
                var imageFiles = Directory.GetFiles(folderPath)
                    .Where(f => IsImageFile(f))
                    .OrderBy(f => GetImageNumber(f))
                    .ToList();

                if (imageFiles.Count == 0)
                    throw new Exception("No image files found");

                // Get dimensions from first image
                int origWidth, origHeight;
                using (var firstImage = new Bitmap(imageFiles[0]))
                {
                    origWidth = firstImage.Width;
                    origHeight = firstImage.Height;
                }
                int origDepth = imageFiles.Count;

                // Calculate new dimensions including depth
                int newWidth = Math.Max(1, origWidth / binFactor);
                int newHeight = Math.Max(1, origHeight / binFactor);
                int newDepth = Math.Max(1, origDepth / binFactor);

                Logger.Log($"[Binning] Original: {origWidth}x{origHeight}x{origDepth}");
                Logger.Log($"[Binning] New: {newWidth}x{newHeight}x{newDepth}");

                // Create output folder
                string outputFolder = Path.Combine(folderPath, $"binned_{binFactor}x3d");
                if (Directory.Exists(outputFolder))
                    Directory.Delete(outputFolder, true);
                Directory.CreateDirectory(outputFolder);

                // Process slices in groups
                await Task.Run(() =>
                {
                    Parallel.For(0, newDepth, newZ =>
                    {
                        // Create binned slice by combining multiple source slices
                        using (Bitmap binnedSlice = new Bitmap(newWidth, newHeight))
                        {
                            // Accumulator for averaging
                            float[,] accumulator = new float[newWidth, newHeight];
                            int[,] counts = new int[newWidth, newHeight];

                            // Process each source slice in this depth bin
                            int srcZStart = newZ * binFactor;
                            int srcZEnd = Math.Min(srcZStart + binFactor, origDepth);

                            for (int srcZ = srcZStart; srcZ < srcZEnd; srcZ++)
                            {
                                if (srcZ >= imageFiles.Count) break;

                                using (Bitmap sourceImage = new Bitmap(imageFiles[srcZ]))
                                {
                                    // Process each pixel in the slice
                                    for (int newY = 0; newY < newHeight; newY++)
                                    {
                                        for (int newX = 0; newX < newWidth; newX++)
                                        {
                                            // Average pixels in this bin
                                            int srcXStart = newX * binFactor;
                                            int srcYStart = newY * binFactor;
                                            int srcXEnd = Math.Min(srcXStart + binFactor, origWidth);
                                            int srcYEnd = Math.Min(srcYStart + binFactor, origHeight);

                                            float sum = 0;
                                            int pixelCount = 0;

                                            for (int srcY = srcYStart; srcY < srcYEnd; srcY++)
                                            {
                                                for (int srcX = srcXStart; srcX < srcXEnd; srcX++)
                                                {
                                                    Color pixel = sourceImage.GetPixel(srcX, srcY);
                                                    float gray = 0.299f * pixel.R + 0.587f * pixel.G + 0.114f * pixel.B;
                                                    sum += gray;
                                                    pixelCount++;
                                                }
                                            }

                                            if (pixelCount > 0)
                                            {
                                                accumulator[newX, newY] += sum / pixelCount;
                                                counts[newX, newY]++;
                                            }
                                        }
                                    }
                                }
                            }

                            // Create final binned slice
                            for (int y = 0; y < newHeight; y++)
                            {
                                for (int x = 0; x < newWidth; x++)
                                {
                                    int value = counts[x, y] > 0 ? (int)(accumulator[x, y] / counts[x, y]) : 0;
                                    value = Math.Max(0, Math.Min(255, value));
                                    binnedSlice.SetPixel(x, y, Color.FromArgb(value, value, value));
                                }
                            }

                            // Save as grayscale
                            Bitmap grayscale = ConvertToGrayscale(binnedSlice);
                            string outputPath = Path.Combine(outputFolder, $"{newZ:D5}.bmp");
                            grayscale.Save(outputPath, ImageFormat.Bmp);
                            grayscale.Dispose();
                        }

                        if ((newZ + 1) % 10 == 0)
                        {
                            Logger.Log($"[Binning] Created {newZ + 1}/{newDepth} binned slices");
                        }
                    });
                });

                Logger.Log($"[Binning] Created {newDepth} binned slices in {outputFolder}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[Binning] Error: {ex.Message}");
                throw;
            }
        }

        private static Bitmap ConvertToGrayscale(Bitmap source)
        {
            Bitmap grayscale = new Bitmap(source.Width, source.Height, PixelFormat.Format8bppIndexed);

            // Set palette
            ColorPalette palette = grayscale.Palette;
            for (int i = 0; i < 256; i++)
            {
                palette.Entries[i] = Color.FromArgb(i, i, i);
            }
            grayscale.Palette = palette;

            // Convert pixels
            BitmapData sourceData = source.LockBits(
                new Rectangle(0, 0, source.Width, source.Height),
                ImageLockMode.ReadOnly,
                source.PixelFormat);

            BitmapData grayscaleData = grayscale.LockBits(
                new Rectangle(0, 0, grayscale.Width, grayscale.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format8bppIndexed);

            try
            {
                unsafe
                {
                    int srcBytesPerPixel = Image.GetPixelFormatSize(source.PixelFormat) / 8;

                    for (int y = 0; y < source.Height; y++)
                    {
                        byte* sourceRow = (byte*)sourceData.Scan0 + (y * sourceData.Stride);
                        byte* grayscaleRow = (byte*)grayscaleData.Scan0 + (y * grayscaleData.Stride);

                        for (int x = 0; x < source.Width; x++)
                        {
                            int offset = x * srcBytesPerPixel;
                            byte b = sourceRow[offset];
                            byte g = srcBytesPerPixel > 1 ? sourceRow[offset + 1] : b;
                            byte r = srcBytesPerPixel > 2 ? sourceRow[offset + 2] : b;

                            byte gray = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
                            grayscaleRow[x] = gray;
                        }
                    }
                }
            }
            finally
            {
                source.UnlockBits(sourceData);
                grayscale.UnlockBits(grayscaleData);
            }

            return grayscale;
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            return ext == ".bmp" || ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".tif" || ext == ".tiff";
        }

        private static int GetImageNumber(string path)
        {
            string filename = Path.GetFileNameWithoutExtension(path);
            string digits = new string(filename.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out int number) ? number : 0;
        }
    }
}