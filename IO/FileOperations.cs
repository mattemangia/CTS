using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading.Tasks;

namespace CTS
{
    /// <summary>
    /// Provides file operations for CT datasets including loading, saving, and exporting volumes
    /// </summary>
    public static class FileOperations
    {
        // Constants
        public const int CHUNK_DIM = 256;

        #region Volume and Label Loading
        /// <summary>
        /// Loads a dataset from a folder or file path
        /// </summary>
        public static async Task<(IGrayscaleVolumeData volumeData, ILabelVolumeData volumeLabels, int width, int height, int depth, double pixelSize)>
    LoadDatasetAsync(string path, bool useMemoryMapping, double pixelSize, int binningFactor, IProgress<int> progress)
        {
            Logger.Log($"[FileOperations] Loading dataset from path: {path}");
            string absolutePath = Path.GetFullPath(path);
            Logger.Log($"[FileOperations] Absolute path resolved to: {absolutePath}");
            Logger.Log($"[FileOperations] Current working directory: {Directory.GetCurrentDirectory()}");

            path = absolutePath;

            IGrayscaleVolumeData volumeData = null;
            ILabelVolumeData volumeLabels = null;
            int width = 0, height = 0, depth = 0;

            bool folderMode = Directory.Exists(path);

            if (folderMode)
            {
                Logger.Log("[FileOperations] Detected folder mode.");

                string volumeBinPath = Path.Combine(path, "volume.bin");
                string labelsBinPath = Path.Combine(path, "labels.bin");
                string volumeChkPath = Path.Combine(path, "volume.chk");

                // Check if we need to apply binning FIRST
                if (binningFactor > 1)
                {
                    Logger.Log($"[FileOperations] Binning requested with factor {binningFactor}");

                    // Check if we already have binned data
                    string binnedMarker = Path.Combine(path, $"binned_{binningFactor}.txt");

                    if (!File.Exists(binnedMarker))
                    {
                        Logger.Log("[FileOperations] No binned data found, processing images first");

                        // Find source images in the folder
                        var imageFiles = Directory.GetFiles(path)
                            .Where(f => IsImageFile(f))
                            .OrderBy(f => GetImageNumber(f))
                            .ToList();

                        if (imageFiles.Count() > 0)
                        {
                            Logger.Log($"[FileOperations] Found {imageFiles.Count()} images to bin");

                            // Declare dimensions outside the using block
                            int origWidth, origHeight, origDepth;

                            // Get dimensions from first image
                            using (var firstImage = new Bitmap(imageFiles[0]))
                            {
                                origWidth = firstImage.Width;
                                origHeight = firstImage.Height;
                            }
                            origDepth = imageFiles.Count();

                            // Calculate binned dimensions - INCLUDING DEPTH!
                            width = Math.Max(1, origWidth / binningFactor);
                            height = Math.Max(1, origHeight / binningFactor);
                            depth = Math.Max(1, origDepth / binningFactor);  // THIS IS THE KEY CHANGE!

                            Logger.Log($"[FileOperations] 3D Binning {origWidth}x{origHeight}x{origDepth} -> {width}x{height}x{depth}");

                            // Create binned folder
                            string binnedFolder = Path.Combine(path, "binned_temp");
                            if (Directory.Exists(binnedFolder))
                                Directory.Delete(binnedFolder, true);
                            Directory.CreateDirectory(binnedFolder);

                            // Process images with 3D binning
                            await ProcessImagesWithBinning(imageFiles, binnedFolder, binningFactor, width, height, progress);

                            // Create volume from binned images
                            ChunkedVolume tempVolume = null;
                            try
                            {
                                Logger.Log("[FileOperations] Creating volume from binned images");
                                tempVolume = (ChunkedVolume)ChunkedVolume.FromFolder(binnedFolder, CHUNK_DIM, (ProgressForm)progress, false);

                                width = tempVolume.Width;
                                height = tempVolume.Height;
                                depth = tempVolume.Depth;

                                Logger.Log($"[FileOperations] Volume created: {width}x{height}x{depth}");

                                // Save the volume to the main folder
                                Logger.Log("[FileOperations] Saving volume to main folder");
                                tempVolume.SaveAsBin(volumeBinPath);

                                // Update pixel size
                                if (pixelSize <= 0 && File.Exists(volumeChkPath))
                                {
                                    var header = ReadVolumeChk(path);
                                    pixelSize = header.pixelSize * binningFactor;
                                }
                                else if (pixelSize <= 0)
                                {
                                    pixelSize = 1e-6 * binningFactor;
                                }
                                else
                                {
                                    pixelSize = pixelSize * binningFactor;
                                }

                                // Create volume.chk
                                CreateVolumeChk(path, width, height, depth, CHUNK_DIM, pixelSize);

                                // Dispose the temp volume to release file locks
                                tempVolume.Dispose();
                                tempVolume = null;

                                Logger.Log("[FileOperations] Volume saved, cleaning up temp folder");
                            }
                            finally
                            {
                                if (tempVolume != null)
                                {
                                    tempVolume.Dispose();
                                    tempVolume = null;
                                }
                            }

                            // Force garbage collection
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();

                            // Wait for file locks to be released
                            await Task.Delay(500);

                            // Now delete the temp folder
                            try
                            {
                                Directory.Delete(binnedFolder, true);
                                Logger.Log("[FileOperations] Temp folder deleted");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[FileOperations] Warning: Could not delete temp folder: {ex.Message}");
                            }

                            // Create binned marker
                            File.WriteAllText(binnedMarker, $"3D Binned with factor {binningFactor} at {DateTime.Now}\n{origWidth}x{origHeight}x{origDepth} -> {width}x{height}x{depth}");

                            // Load the saved volume
                            Logger.Log("[FileOperations] Loading saved volume");
                            volumeData = LoadVolumeBin(volumeBinPath, useMemoryMapping);

                            // Create empty labels
                            CreateBlankLabelsFile(labelsBinPath, width, height, depth, CHUNK_DIM);
                            volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);

                            Logger.Log("[FileOperations] 3D binning completed, volume loaded");
                        }
                        else
                        {
                            throw new Exception("No images found in folder for binning");
                        }
                    }
                    else
                    {
                        Logger.Log($"[FileOperations] Already binned with factor {binningFactor}");
                        // Load the already binned volume
                        volumeData = LoadVolumeBin(volumeBinPath, useMemoryMapping);
                        width = volumeData.Width;
                        height = volumeData.Height;
                        depth = volumeData.Depth;

                        if (File.Exists(labelsBinPath))
                        {
                            volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);
                        }
                        else
                        {
                            // Create and load labels
                            CreateBlankLabelsFile(labelsBinPath, width, height, depth, CHUNK_DIM);
                            volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);
                        }
                    }
                }
                else
                {
                    // No binning requested, proceed with original code
                    bool volumeExists = File.Exists(volumeBinPath);
                    bool labelsExist = File.Exists(labelsBinPath);
                    bool chkExists = File.Exists(volumeChkPath);

                    bool needToGenerateVolume = !volumeExists || !chkExists;
                    bool needToGenerateLabels = !labelsExist;

                    if (needToGenerateVolume)
                    {
                        Logger.Log("[FileOperations] Binary files missing. Generating volume.bin from folder images.");

                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        try
                        {
                            volumeData = ChunkedVolume.FromFolder(path, CHUNK_DIM, (ProgressForm)progress, useMemoryMapping);
                            width = volumeData.Width;
                            height = volumeData.Height;
                            depth = volumeData.Depth;

                            Logger.Log($"[FileOperations] Created original volume: {width}x{height}x{depth}");

                            if (!chkExists)
                            {
                                CreateVolumeChk(path, width, height, depth, CHUNK_DIM, pixelSize);
                            }

                            if (useMemoryMapping)
                            {
                                volumeData.Dispose();
                                volumeData = null;
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                                await Task.Delay(500);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[FileOperations] Error generating volume: {ex.Message}");
                            if (volumeData != null)
                            {
                                volumeData.Dispose();
                                volumeData = null;
                            }
                            throw;
                        }
                    }
                    else if (chkExists && !volumeExists)
                    {
                        Logger.Log("[FileOperations] Found volume.chk but volume.bin is missing. Reading dimensions from header.");
                        var header = ReadVolumeChk(path);
                        width = header.volWidth;
                        height = header.volHeight;
                        depth = header.volDepth;
                        pixelSize = header.pixelSize;
                    }

                    if (needToGenerateLabels && width > 0 && height > 0 && depth > 0)
                    {
                        Logger.Log("[FileOperations] Labels file not found. Creating blank labels file.");
                        CreateBlankLabelsFile(labelsBinPath, width, height, depth, CHUNK_DIM);
                    }

                    // Now load the volume and labels files
                    if (File.Exists(volumeBinPath))
                    {
                        try
                        {
                            Logger.Log("[FileOperations] Loading volume.bin...");
                            volumeData = LoadVolumeBin(volumeBinPath, useMemoryMapping);
                            width = volumeData.Width;
                            height = volumeData.Height;
                            depth = volumeData.Depth;
                            Logger.Log($"[FileOperations] Loaded volume: {width}x{height}x{depth}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[FileOperations] Error loading volume.bin: {ex.Message}");
                            await Task.Delay(1000);
                            Logger.Log("[FileOperations] Retrying after short delay...");

                            try
                            {
                                volumeData = LoadVolumeBin(volumeBinPath, useMemoryMapping);
                                width = volumeData.Width;
                                height = volumeData.Height;
                                depth = volumeData.Depth;
                                Logger.Log($"[FileOperations] Retry succeeded, loaded volume: {width}x{height}x{depth}");
                            }
                            catch (Exception retryEx)
                            {
                                Logger.Log($"[FileOperations] Retry failed: {retryEx.Message}");
                                throw;
                            }
                        }
                    }

                    if (File.Exists(labelsBinPath))
                    {
                        Logger.Log($"[FileOperations] Found labels file at: {labelsBinPath}");
                        try
                        {
                            volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);
                            if (volumeLabels != null)
                            {
                                Logger.Log($"[FileOperations] Loaded labels: {volumeLabels.Width}x{volumeLabels.Height}x{volumeLabels.Depth}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log("[FileOperations] Error loading labels.bin: " + ex.Message + ". Recreating blank labels file.");

                            if (volumeLabels != null)
                            {
                                volumeLabels.Dispose();
                                volumeLabels = null;
                            }

                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            await Task.Delay(500);

                            try
                            {
                                File.Delete(labelsBinPath);
                                CreateBlankLabelsFile(labelsBinPath, width, height, depth, CHUNK_DIM);
                                volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);
                                Logger.Log("[FileOperations] Reloaded new labels.bin.");
                            }
                            catch (Exception retryEx)
                            {
                                Logger.Log($"[FileOperations] Failed to create new labels file: {retryEx.Message}");
                                throw;
                            }
                        }
                    }
                    else if (width > 0 && height > 0 && depth > 0)
                    {
                        Logger.Log("[FileOperations] labels.bin not found. Creating and loading blank labels file.");
                        CreateBlankLabelsFile(labelsBinPath, width, height, depth, CHUNK_DIM);

                        await Task.Delay(500);

                        try
                        {
                            volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);
                            Logger.Log("[FileOperations] Successfully loaded new labels file.");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[FileOperations] Error loading newly created labels file: {ex.Message}");
                            throw;
                        }
                    }
                }
            }
            else if (File.Exists(path))
            {
                // File mode handling - unchanged from original
                string fileName = Path.GetFileName(path).ToLower();
                string baseFolder = Path.GetDirectoryName(path);

                Logger.Log($"[FileOperations] File mode detected. Loading file: {fileName} from folder: {baseFolder}");

                if (fileName.Contains("volume") && !fileName.Contains("labels"))
                {
                    string volChk = Path.Combine(baseFolder, "volume.chk");
                    if (File.Exists(volChk))
                    {
                        var header = ReadVolumeChk(baseFolder);
                        int volWidth = header.volWidth;
                        int volHeight = header.volHeight;
                        int volDepth = header.volDepth;
                        int chkChunkDim = header.chunkDim;
                        double chkPixelSize = header.pixelSize;
                        pixelSize = chkPixelSize;

                        Logger.Log($"[FileOperations] Loaded header from volume.chk: {volWidth}x{volHeight}x{volDepth}, chunkDim={chkChunkDim}, pixelSize={pixelSize}");

                        volumeData = LoadVolumeBinRaw(path, useMemoryMapping, volWidth, volHeight, volDepth, chkChunkDim);
                        width = volumeData.Width;
                        height = volumeData.Height;
                        depth = volumeData.Depth;

                        string labelsPath = Path.Combine(baseFolder, "labels.bin");
                        if (File.Exists(labelsPath))
                        {
                            volumeLabels = LoadLabelsBin(labelsPath, useMemoryMapping);
                        }
                        else
                        {
                            CreateBlankLabelsFile(labelsPath, width, height, depth, CHUNK_DIM);
                            await Task.Delay(500);
                            volumeLabels = LoadLabelsBin(labelsPath, useMemoryMapping);
                        }
                    }
                    else
                    {
                        Logger.Log("[FileOperations] volume.chk not found. Assuming combined file mode.");
                        (volumeData, volumeLabels, width, height, depth, pixelSize) = LoadCombinedBinary(path);
                    }
                }
                else if (fileName.Contains("labels"))
                {
                    string labChk = Path.Combine(baseFolder, "labels.chk");
                    if (!File.Exists(labChk))
                        throw new Exception("Labels header file (labels.chk) not found.");

                    volumeLabels = LoadLabelsBin(path, useMemoryMapping);
                    volumeData = null;
                    width = volumeLabels.Width;
                    height = volumeLabels.Height;
                    depth = volumeLabels.Depth;
                }
                else
                {
                    Logger.Log("[FileOperations] File mode: assuming combined file mode.");
                    (volumeData, volumeLabels, width, height, depth, pixelSize) = LoadCombinedBinary(path);
                }
            }
            else
            {
                Logger.Log("[FileOperations] Provided path is neither a folder nor an existing file.");
                throw new FileNotFoundException("Path is neither a folder nor a file", path);
            }

            return (volumeData, volumeLabels, width, height, depth, pixelSize);
        }
        private static async Task ProcessImagesWithBinning(List<string> imageFiles, string outputFolder,
    int binFactor, int newWidth, int newHeight, IProgress<int> progress)
        {
            int processedCount = 0;
            int origDepth = imageFiles.Count;
            int newDepth = Math.Max(1, origDepth / binFactor);  // PROPER 3D BINNING!

            Logger.Log($"[ProcessImagesWithBinning] 3D Binning: {imageFiles.Count} images -> {newDepth} slices");
            Logger.Log($"[ProcessImagesWithBinning] Depth reduction: {origDepth} -> {newDepth}");

            await Task.Run(() =>
            {
                // Process slices in groups for Z-dimension binning
                Parallel.For(0, newDepth, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                newZ =>
                {
                    try
                    {
                        // Create binned slice by combining multiple source slices
                        Bitmap binnedSlice = new Bitmap(newWidth, newHeight);

                        // Accumulator for averaging multiple slices
                        float[,] accumulator = new float[newWidth, newHeight];
                        int[,] counts = new int[newWidth, newHeight];

                        // Process each source slice that contributes to this binned slice
                        int srcZStart = newZ * binFactor;
                        int srcZEnd = Math.Min(srcZStart + binFactor, origDepth);

                        for (int srcZ = srcZStart; srcZ < srcZEnd; srcZ++)
                        {
                            if (srcZ >= imageFiles.Count) break;

                            using (Bitmap sourceImage = new Bitmap(imageFiles[srcZ]))
                            {
                                // Scale this slice to the new width/height
                                using (Bitmap scaledSlice = new Bitmap(newWidth, newHeight))
                                {
                                    using (Graphics g = Graphics.FromImage(scaledSlice))
                                    {
                                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                                        g.DrawImage(sourceImage, 0, 0, newWidth, newHeight);
                                    }

                                    // Accumulate pixel values from this slice
                                    for (int y = 0; y < newHeight; y++)
                                    {
                                        for (int x = 0; x < newWidth; x++)
                                        {
                                            Color pixel = scaledSlice.GetPixel(x, y);
                                            float gray = 0.299f * pixel.R + 0.587f * pixel.G + 0.114f * pixel.B;
                                            accumulator[x, y] += gray;
                                            counts[x, y]++;
                                        }
                                    }
                                }
                            }
                        }

                        // Create final binned slice by averaging
                        for (int y = 0; y < newHeight; y++)
                        {
                            for (int x = 0; x < newWidth; x++)
                            {
                                int value = counts[x, y] > 0 ? (int)(accumulator[x, y] / counts[x, y]) : 0;
                                value = Math.Max(0, Math.Min(255, value));
                                binnedSlice.SetPixel(x, y, Color.FromArgb(value, value, value));
                            }
                        }

                        // Convert to grayscale and save
                        Bitmap grayscaleSlice = ConvertToGrayscale(binnedSlice);
                        string outputPath = Path.Combine(outputFolder, $"{newZ:D5}.bmp");
                        grayscaleSlice.Save(outputPath, ImageFormat.Bmp);

                        // Clean up
                        grayscaleSlice.Dispose();
                        binnedSlice.Dispose();

                        int count = System.Threading.Interlocked.Add(ref processedCount, srcZEnd - srcZStart);
                        progress?.Report((count * 100) / origDepth);

                        Logger.Log($"[ProcessImagesWithBinning] Created binned slice {newZ}/{newDepth}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ProcessImagesWithBinning] Error creating slice {newZ}: {ex.Message}");
                        throw;
                    }
                });
            });

            Logger.Log($"[ProcessImagesWithBinning] 3D binning completed: {newDepth} slices created");
        }
        private static void ProcessSingleImage(string imagePath, string outputFolder, int binFactor, int newWidth, int newHeight)
        {
            Bitmap source = null;
            Bitmap binned = null;
            Bitmap grayscale = null;

            try
            {
                // Load source image
                source = new Bitmap(imagePath);

                // Create binned image
                binned = new Bitmap(newWidth, newHeight);

                using (Graphics g = Graphics.FromImage(binned))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.DrawImage(source, 0, 0, newWidth, newHeight);
                }

                // Convert to grayscale
                grayscale = ConvertToGrayscale(binned);

                // Save with the same filename
                string filename = Path.GetFileName(imagePath);
                string outputPath = Path.Combine(outputFolder, filename);
                grayscale.Save(outputPath, ImageFormat.Bmp);
            }
            finally
            {
                // Ensure all bitmaps are disposed
                source?.Dispose();
                binned?.Dispose();
                grayscale?.Dispose();
            }
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

            if (int.TryParse(digits, out int number))
                return number;

            return 0;
        }
        private static Bitmap ConvertToGrayscale(Bitmap source)
        {
            Bitmap grayscale = null;
            BitmapData sourceData = null;
            BitmapData grayscaleData = null;

            try
            {
                grayscale = new Bitmap(source.Width, source.Height, PixelFormat.Format8bppIndexed);

                // Set grayscale palette
                ColorPalette palette = grayscale.Palette;
                for (int i = 0; i < 256; i++)
                {
                    palette.Entries[i] = Color.FromArgb(i, i, i);
                }
                grayscale.Palette = palette;

                // Lock bits for both bitmaps
                sourceData = source.LockBits(
                    new Rectangle(0, 0, source.Width, source.Height),
                    ImageLockMode.ReadOnly,
                    source.PixelFormat);

                grayscaleData = grayscale.LockBits(
                    new Rectangle(0, 0, grayscale.Width, grayscale.Height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format8bppIndexed);

                unsafe
                {
                    int bytesPerPixel = Image.GetPixelFormatSize(source.PixelFormat) / 8;

                    for (int y = 0; y < source.Height; y++)
                    {
                        byte* sourceRow = (byte*)sourceData.Scan0 + (y * sourceData.Stride);
                        byte* grayscaleRow = (byte*)grayscaleData.Scan0 + (y * grayscaleData.Stride);

                        for (int x = 0; x < source.Width; x++)
                        {
                            int offset = x * bytesPerPixel;

                            byte b = sourceRow[offset];
                            byte g = bytesPerPixel > 1 ? sourceRow[offset + 1] : b;
                            byte r = bytesPerPixel > 2 ? sourceRow[offset + 2] : b;

                            // Convert to grayscale
                            byte gray = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
                            grayscaleRow[x] = gray;
                        }
                    }
                }

                return grayscale;
            }
            catch
            {
                grayscale?.Dispose();
                throw;
            }
            finally
            {
                // Always unlock bits
                if (sourceData != null)
                    source.UnlockBits(sourceData);

                if (grayscaleData != null && grayscale != null)
                    grayscale.UnlockBits(grayscaleData);
            }
        }

        /// <summary>
        /// Loads a volume from a binary file with robust header validation and corruption handling
        /// </summary>
        public static IGrayscaleVolumeData LoadVolumeBin(string path, bool useMM)
        {
            // Log the absolute path to identify the exact file being loaded
            string absolutePath = Path.GetFullPath(path);
            Logger.Log($"[LoadVolumeBin] Loading volume from absolute path: {absolutePath}");

            // First, check if we have a volume.chk file in the same directory
            string folderPath = Path.GetDirectoryName(absolutePath);
            string chkPath = Path.Combine(folderPath, "volume.chk");

            int volWidth = 0, volHeight = 0, volDepth = 0, chunkDim = 0;
            bool useBackupDimensions = false;

            // Try to read dimensions from volume.chk as a backup
            if (File.Exists(chkPath))
            {
                try
                {
                    var header = ReadVolumeChk(folderPath);
                    Logger.Log($"[LoadVolumeBin] Found volume.chk with dimensions: {header.volWidth}x{header.volHeight}x{header.volDepth}, chunkDim={header.chunkDim}");

                    // Store these in case we need to fall back to them
                    volWidth = header.volWidth;
                    volHeight = header.volHeight;
                    volDepth = header.volDepth;
                    chunkDim = header.chunkDim;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[LoadVolumeBin] Warning: Could not read volume.chk: {ex.Message}");
                }
            }

            // Try to read the dimensions from the bin file
            int binWidth = 0, binHeight = 0, binDepth = 0, binChunkDim = 0;
            int bitsPerPixel = 8;
            double pixelSize = 1e-6;
            int headerSize = 36; // 9 ints (36 bytes)

            try
            {
                // Read header with FileShare.ReadWrite to avoid file locking issues
                using (FileStream fs = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    // Verify file is at least large enough to contain a header
                    if (fs.Length < 7 * sizeof(int))
                    {
                        Logger.Log("[LoadVolumeBin] File too small for header, using backup dimensions");
                        useBackupDimensions = true;
                    }
                    else
                    {
                        // Read header values
                        binWidth = br.ReadInt32();
                        binHeight = br.ReadInt32();
                        binDepth = br.ReadInt32();
                        binChunkDim = br.ReadInt32();

                        // Try to read bits per pixel and pixel size if available
                        try
                        {
                            bitsPerPixel = br.ReadInt32();
                            pixelSize = br.ReadDouble();
                        }
                        catch
                        {
                            // Use defaults if not available
                            Logger.Log("[LoadVolumeBin] Header doesn't include bitsPerPixel/pixelSize. Using defaults.");
                        }

                        // Skip over the (potentially corrupt) chunk counts
                        try { br.ReadInt32(); br.ReadInt32(); br.ReadInt32(); } catch { }

                        // Check if dimensions seem reasonable (between 1 and 10000)
                        const int MAX_DIM = 10000;
                        if (binWidth <= 0 || binWidth > MAX_DIM ||
                            binHeight <= 0 || binHeight > MAX_DIM ||
                            binDepth <= 0 || binDepth > MAX_DIM ||
                            binChunkDim <= 0 || binChunkDim > 1024)
                        {
                            Logger.Log($"[LoadVolumeBin] Invalid dimensions detected in bin file: {binWidth}x{binHeight}x{binDepth}, chunkDim={binChunkDim}");
                            useBackupDimensions = true;
                        }
                        else
                        {
                            // Bin file dimensions look good
                            Logger.Log($"[LoadVolumeBin] Valid dimensions from bin file: {binWidth}x{binHeight}x{binDepth}, chunkDim={binChunkDim}");

                            // Use the bin file dimensions
                            volWidth = binWidth;
                            volHeight = binHeight;
                            volDepth = binDepth;
                            chunkDim = binChunkDim;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[LoadVolumeBin] Error reading header from bin file: {ex.Message}");
                useBackupDimensions = true;
            }

            // If we couldn't get valid dimensions from the bin file and have no backup, fail
            if (useBackupDimensions && (volWidth <= 0 || volHeight <= 0 || volDepth <= 0 || chunkDim <= 0))
            {
                throw new InvalidDataException("Could not determine volume dimensions from either bin file or chk file");
            }

            // Calculate chunk counts correctly
            int cntX = (volWidth + chunkDim - 1) / chunkDim;
            int cntY = (volHeight + chunkDim - 1) / chunkDim;
            int cntZ = (volDepth + chunkDim - 1) / chunkDim;
            int totalChunks = cntX * cntY * cntZ;

            Logger.Log($"[LoadVolumeBin] Using dimensions: {volWidth}x{volHeight}x{volDepth}, chunkDim={chunkDim}, chunks={cntX}x{cntY}x{cntZ}");

            // Now that we have valid dimensions, create the appropriate volume
            if (!useMM)
            {
                // Create in-memory volume
                ChunkedVolume volume = new ChunkedVolume(volWidth, volHeight, volDepth, chunkDim);

                // Load data from file into memory chunks
                try
                {
                    using (FileStream fs = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        // Skip header
                        fs.Seek(headerSize, SeekOrigin.Begin);

                        // Read chunk by chunk
                        long chunkSize = (long)chunkDim * chunkDim * chunkDim;
                        byte[] buffer = new byte[chunkSize];

                        for (int i = 0; i < totalChunks; i++)
                        {
                            int bytesRead = fs.Read(buffer, 0, (int)chunkSize);
                            if (bytesRead == chunkSize)
                            {
                                volume.Chunks[i] = (byte[])buffer.Clone();
                            }
                            else
                            {
                                // Handle partial or no read by creating a blank chunk
                                byte[] chunkData = new byte[chunkSize];
                                Array.Copy(buffer, chunkData, Math.Max(0, bytesRead));
                                volume.Chunks[i] = chunkData;
                                Logger.Log($"[LoadVolumeBin] Warning: Chunk {i} incomplete, read {bytesRead}/{chunkSize} bytes");
                            }
                        }
                    }

                    return volume;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[LoadVolumeBin] Error loading volume data: {ex.Message}");
                    throw;
                }
            }
            else
            {
                // For memory mapping, implement a robust retry mechanism
                MemoryMappedFile mmf = null;
                MemoryMappedViewAccessor viewAccessor = null;

                try
                {
                    // Close any existing file handles and force GC
                    GC.Collect();
                    GC.WaitForPendingFinalizers();

                    // Generate a unique mapping name
                    string mapName = $"CTSegmenter_Volume_{Guid.NewGuid()}";

                    // Implement retry logic with exponential backoff
                    int maxRetries = 5;
                    for (int retry = 0; retry < maxRetries; retry++)
                    {
                        try
                        {
                            Logger.Log($"[LoadVolumeBin] Attempt {retry + 1} to create memory-mapped file");

                            // Wait between retries with increasing delay
                            if (retry > 0)
                            {
                                int delay = 200 * (int)Math.Pow(2, retry - 1);
                                System.Threading.Thread.Sleep(delay);

                                // Force GC again
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                            }

                            mmf = MemoryMappedFile.CreateFromFile(
                                absolutePath,
                                FileMode.Open,
                                mapName,
                                0, // Use file size
                                MemoryMappedFileAccess.ReadWrite);

                            viewAccessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                            Logger.Log("[LoadVolumeBin] Successfully created memory-mapped file");
                            break;
                        }
                        catch (Exception ex)
                        {
                            // Clean up resources from failed attempt
                            viewAccessor?.Dispose();
                            viewAccessor = null;
                            mmf?.Dispose();
                            mmf = null;

                            Logger.Log($"[LoadVolumeBin] Attempt {retry + 1} failed: {ex.Message}");

                            // Throw on last attempt
                            if (retry == maxRetries - 1)
                                throw;
                        }
                    }

                    // Create the chunked volume
                    Logger.Log($"[LoadVolumeBin] Creating memory-mapped volume with {totalChunks} chunks");
                    return new ChunkedVolume(volWidth, volHeight, volDepth, chunkDim, mmf, viewAccessor, headerSize);
                }
                catch (Exception ex)
                {
                    // Clean up resources
                    viewAccessor?.Dispose();
                    mmf?.Dispose();

                    Logger.Log($"[LoadVolumeBin] Memory mapping failed: {ex.Message}");
                    throw;
                }
            }
        }
        /// <summary>
        /// Loads a volume from a raw binary file without header
        /// </summary>
       
public static IGrayscaleVolumeData LoadVolumeBinRaw(string path, bool useMM, int volWidth, int volHeight, int volDepth, int chunkDim)
        {
            try
            {
                // Get file size and validate it
                int cntX = (volWidth + chunkDim - 1) / chunkDim;
                int cntY = (volHeight + chunkDim - 1) / chunkDim;
                int cntZ = (volDepth + chunkDim - 1) / chunkDim;
                int totalChunks = cntX * cntY * cntZ;
                long chunkSize = (long)chunkDim * chunkDim * chunkDim;
                long dataSize = totalChunks * chunkSize;

                // Possible header sizes:
                // Old format: 9 ints = 36 bytes
                // New format: 4 ints + 1 int + 1 double + 3 ints = 5 ints + 1 double + 3 ints = 40 bytes
                int headerSize36 = 36; // 9 ints
                int headerSize40 = 40; // 5 ints + 1 double + 3 ints

                // Get file information
                FileInfo fileInfo = new FileInfo(path);
                long fileSize = fileInfo.Length;

                // Determine if file has a header and set the data offset
                bool hasHeader = false;
                long dataOffset = 0;
                int actualHeaderSize = 0;

                if (fileSize == dataSize)
                {
                    Logger.Log("[LoadVolumeBinRaw] File appears to be raw data with no header");
                    hasHeader = false;
                    dataOffset = 0;
                }
                else if (fileSize == dataSize + headerSize36)
                {
                    Logger.Log("[LoadVolumeBinRaw] File appears to include a 36-byte header (old format)");
                    hasHeader = true;
                    dataOffset = headerSize36;
                    actualHeaderSize = headerSize36;
                }
                else if (fileSize == dataSize + headerSize40)
                {
                    Logger.Log("[LoadVolumeBinRaw] File appears to include a 40-byte header (new format with pixelSize)");
                    hasHeader = true;
                    dataOffset = headerSize40;
                    actualHeaderSize = headerSize40;
                }
                else
                {
                    // Try to detect header size by reading first few bytes
                    using (FileStream detectFs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (BinaryReader detectBr = new BinaryReader(detectFs))
                    {
                        try
                        {
                            int testWidth = detectBr.ReadInt32();
                            int testHeight = detectBr.ReadInt32();
                            int testDepth = detectBr.ReadInt32();
                            int testChunkDim = detectBr.ReadInt32();

                            // Check if these values match expected dimensions
                            if (testWidth == volWidth && testHeight == volHeight &&
                                testDepth == volDepth && testChunkDim == chunkDim)
                            {
                                // Header detected, determine size
                                long remainingData = fileSize - 16; // Already read 4 ints

                                if (remainingData == dataSize + 20) // 5 more ints (old format)
                                {
                                    Logger.Log("[LoadVolumeBinRaw] Detected 36-byte header format");
                                    hasHeader = true;
                                    dataOffset = headerSize36;
                                    actualHeaderSize = headerSize36;
                                }
                                else if (remainingData == dataSize + 24) // 1 int + 1 double + 3 ints (new format)
                                {
                                    Logger.Log("[LoadVolumeBinRaw] Detected 40-byte header format");
                                    hasHeader = true;
                                    dataOffset = headerSize40;
                                    actualHeaderSize = headerSize40;
                                }
                                else
                                {
                                    throw new Exception($"Volume file size mismatch: expected {dataSize:N0}, {dataSize + headerSize36:N0}, or {dataSize + headerSize40:N0} bytes but got {fileSize:N0} bytes");
                                }
                            }
                            else
                            {
                                throw new Exception($"Invalid header values or file size mismatch");
                            }
                        }
                        catch
                        {
                            throw new Exception($"Volume file size mismatch: expected {dataSize:N0}, {dataSize + headerSize36:N0}, or {dataSize + headerSize40:N0} bytes but got {fileSize:N0} bytes");
                        }
                    }
                }

                // Log what we're doing
                Logger.Log($"[LoadVolumeBinRaw] Loading volume: {volWidth}x{volHeight}x{volDepth}, chunkDim={chunkDim}");
                Logger.Log($"[LoadVolumeBinRaw] File: {path}, size: {fileSize:N0} bytes, {(hasHeader ? $"with {actualHeaderSize}-byte header" : "no header")}");

                // Implement retry mechanism with exponential back-off
                MemoryMappedFile mmf = null;
                MemoryMappedViewAccessor viewAccessor = null;

                int maxRetries = 5;
                for (int retry = 0; retry < maxRetries; retry++)
                {
                    try
                    {
                        // Generate a unique mapping name to avoid conflicts
                        string mapName = $"CTSegmenter_RawVolume_{Guid.NewGuid()}";

                        // Force GC to release any lingering handles
                        if (retry > 0)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }

                        // Try to open the memory-mapped file
                        mmf = MemoryMappedFile.CreateFromFile(
                            path,
                            FileMode.Open,
                            mapName,
                            0,  // Use file size
                            MemoryMappedFileAccess.ReadWrite);

                        // Create a single view accessor for the entire file
                        viewAccessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                        Logger.Log($"[LoadVolumeBinRaw] Successfully created memory-mapped file on attempt {retry + 1}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Clean up any partial resources
                        if (viewAccessor != null)
                        {
                            viewAccessor.Dispose();
                            viewAccessor = null;
                        }

                        if (mmf != null)
                        {
                            mmf.Dispose();
                            mmf = null;
                        }

                        // Log the error
                        Logger.Log($"[LoadVolumeBinRaw] Attempt {retry + 1} failed: {ex.Message}");

                        // On last retry, throw the exception
                        if (retry == maxRetries - 1)
                        {
                            throw new IOException($"Failed to create memory-mapped file after {maxRetries} attempts: {ex.Message}", ex);
                        }

                        // Wait before retrying with exponential back-off
                        int delay = (int)Math.Pow(2, retry) * 100;
                        System.Threading.Thread.Sleep(delay);
                    }
                }

                // Create and return the ChunkedVolume with the correct data offset
                return new ChunkedVolume(volWidth, volHeight, volDepth, chunkDim, mmf, viewAccessor, dataOffset);
            }
            catch (Exception ex)
            {
                Logger.Log($"[LoadVolumeBinRaw] Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Loads labels from a binary file
        /// </summary>
        public static ILabelVolumeData LoadLabelsBin(string path, bool useMM)
        {
            if (!File.Exists(path))
            {
                Logger.Log("[FileOperations] File not found; creating blank labels volume.");
                if (useMM)
                    throw new FileNotFoundException("labels.bin not found and memory mapping is enabled.");

                // We don't know the dimensions here, so we'll return null and let the caller handle it
                return null;
            }

            if (useMM)
            {
                MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
                int headerSize = sizeof(int) * 4;
                int chunkDim, cntX, cntY, cntZ;

                using (var headerStream = mmf.CreateViewStream(0, headerSize, MemoryMappedFileAccess.Read))
                using (BinaryReader br = new BinaryReader(headerStream))
                {
                    chunkDim = br.ReadInt32();
                    cntX = br.ReadInt32();
                    cntY = br.ReadInt32();
                    cntZ = br.ReadInt32();
                }

                int labWidth = cntX * chunkDim;
                int labHeight = cntY * chunkDim;
                int labDepth = cntZ * chunkDim;
                ChunkedLabelVolume labVol = (ChunkedLabelVolume)(ILabelVolumeData)new ChunkedLabelVolume(labWidth, labHeight, labDepth, chunkDim, mmf);

                using (var dataStream = mmf.CreateViewStream(headerSize, 0, MemoryMappedFileAccess.ReadWrite))
                using (BinaryReader dataReader = new BinaryReader(dataStream))
                {
                    labVol.ReadChunksHeaderAndData(dataReader, mmf, headerSize);
                }

                return labVol;
            }
            else
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    int chunkDim = br.ReadInt32();
                    int cntX = br.ReadInt32();
                    int cntY = br.ReadInt32();
                    int cntZ = br.ReadInt32();

                    if (chunkDim <= 0)
                        throw new Exception("Invalid header in labels file: chunkDim is zero.");

                    int labWidth = cntX * chunkDim;
                    int labHeight = cntY * chunkDim;
                    int labDepth = cntZ * chunkDim;
                    ChunkedLabelVolume labVol = (ChunkedLabelVolume)(ILabelVolumeData)new ChunkedLabelVolume(labWidth, labHeight, labDepth, chunkDim, false, path);
                    labVol.ReadChunksHeaderAndData(br);
                    return labVol;
                }
            }
        }

        /// <summary>
        /// Loads a combined binary file containing volume, labels, and materials
        /// </summary>
        public static (IGrayscaleVolumeData volumeData, ILabelVolumeData volumeLabels, int width, int height, int depth, double pixelSize)
            LoadCombinedBinary(string path)
        {
            // This implementation loads everything into RAM.
            ChunkedVolume volumeData = null;
            ChunkedLabelVolume volumeLabels = null;
            List<Material> materials = new List<Material>();
            int width, height, depth;
            double pixelSize;

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                // Read volume header
                width = br.ReadInt32();
                height = br.ReadInt32();
                depth = br.ReadInt32();
                pixelSize = br.ReadDouble();

                // Read materials
                int materialCount = br.ReadInt32();
                for (int i = 0; i < materialCount; i++)
                {
                    string name = br.ReadString();
                    int argb = br.ReadInt32();
                    byte min = br.ReadByte();
                    byte max = br.ReadByte();
                    bool isExterior = br.ReadBoolean();
                    byte id = br.ReadByte();
                    materials.Add(new Material(name, Color.FromArgb(argb), min, max, id) { IsExterior = isExterior });
                }

                // Read a flag that indicates whether volume data (grayscale) is present.
                bool hasGrayscale = br.ReadBoolean();
                if (hasGrayscale)
                {
                    // Create the volume in RAM
                    volumeData = new ChunkedVolume(width, height, depth, CHUNK_DIM);
                    volumeData.ReadChunks(br);
                }

                // Now load the labels.
                // Create a new ChunkedLabelVolume (RAM mode) and read its chunks.
                volumeLabels = new ChunkedLabelVolume(width, height, depth, CHUNK_DIM, false, null);
                volumeLabels.ReadChunksHeaderAndData(br);
            }

            return (volumeData, volumeLabels, width, height, depth, pixelSize);
        }

        /// <summary>
        /// Process binning on a volume to reduce its size
        /// </summary>
        public static async Task ProcessBinningAsync(string path, int binFactor, float pixelSize, bool useMemoryMapping)
        {
            try
            {
                // Load the source volume 
                string volumeBinPath = Path.Combine(path, "volume.bin");
                if (!File.Exists(volumeBinPath))
                    throw new FileNotFoundException("Cannot find volume.bin for binning");

                // Read size info from the header only
                int sourceWidth, sourceHeight, sourceDepth, chunkDim;

                using (FileStream fs = new FileStream(volumeBinPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    sourceWidth = br.ReadInt32();
                    sourceHeight = br.ReadInt32();
                    sourceDepth = br.ReadInt32();
                    chunkDim = br.ReadInt32();
                }

                // Calculate new dimensions
                int newWidth = sourceWidth / binFactor;
                int newHeight = sourceHeight / binFactor;
                int newDepth = sourceDepth / binFactor;

                Logger.Log($"[ProcessBinningAsync] Binning {sourceWidth}x{sourceHeight}x{sourceDepth} " +
                          $"to {newWidth}x{newHeight}x{newDepth} with factor {binFactor}");

                // Ensure minimum size
                if (newWidth <= 0 || newHeight <= 0 || newDepth <= 0)
                    throw new ArgumentException("Binning factor too large for volume dimensions");

                // Create a new volume for the binned data
                string binnedPath = Path.Combine(path, "binned_volume.bin");

                // Create the binned volume and save it
                await Task.Run(() =>
                {
                    // Load the source volume (must be in memory for efficient processing)
                    IGrayscaleVolumeData sourceVolume = LoadVolumeBin(volumeBinPath, false);

                    // Create a new volume for the binned data
                    ChunkedVolume binnedVolume = new ChunkedVolume(newWidth, newHeight, newDepth, chunkDim);

                    // Process the binning in chunks to avoid excessive memory usage
                    Parallel.For(0, newDepth, z =>
                    {
                        for (int y = 0; y < newHeight; y++)
                        {
                            for (int x = 0; x < newWidth; x++)
                            {
                                long sum = 0;
                                int count = 0;

                                // Sum the values in the bin
                                for (int bz = 0; bz < binFactor; bz++)
                                {
                                    int srcZ = z * binFactor + bz;
                                    if (srcZ >= sourceDepth) continue;

                                    for (int by = 0; by < binFactor; by++)
                                    {
                                        int srcY = y * binFactor + by;
                                        if (srcY >= sourceHeight) continue;

                                        for (int bx = 0; bx < binFactor; bx++)
                                        {
                                            int srcX = x * binFactor + bx;
                                            if (srcX >= sourceWidth) continue;

                                            sum += sourceVolume[srcX, srcY, srcZ];
                                            count++;
                                        }
                                    }
                                }

                                // Calculate average
                                byte value = count > 0 ? (byte)(sum / count) : (byte)0;
                                binnedVolume[x, y, z] = value;
                            }
                        }
                    });

                    // Save the binned volume
                    binnedVolume.SaveAsBin(binnedPath);

                    // Clean up
                    sourceVolume.Dispose();
                    binnedVolume.Dispose();
                });

                // Replace the original volume with the binned one
                File.Delete(volumeBinPath);
                File.Move(binnedPath, volumeBinPath);

                // Update the header file
                CreateVolumeChk(path, newWidth, newHeight, newDepth, chunkDim, pixelSize * binFactor);

                Logger.Log("[ProcessBinningAsync] Binning completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessBinningAsync] Error: {ex.Message}");
                throw;
            }
        }
        #endregion

        #region Header Files Operations
        /// <summary>
        /// Creates a volume.chk file containing metadata about the volume
        /// </summary>
        public static void CreateVolumeChk(string folder, int volWidth, int volHeight, int volDepth, int chunkDim, double pixelSize)
        {
            // Validate input parameters
            if (string.IsNullOrEmpty(folder))
                throw new ArgumentException("Folder path cannot be null or empty", nameof(folder));

            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException($"Directory not found: {folder}");

            if (volWidth <= 0 || volHeight <= 0 || volDepth <= 0)
                throw new ArgumentException($"Invalid volume dimensions: {volWidth}x{volHeight}x{volDepth}");

            if (chunkDim <= 0)
                throw new ArgumentException($"Invalid chunk dimension: {chunkDim}");

            if (pixelSize <= 0)
                throw new ArgumentException($"Invalid pixel size: {pixelSize}");

            string chkPath = Path.Combine(folder, "volume.chk");
            string absolutePath = Path.GetFullPath(chkPath);

            Logger.Log($"[CreateVolumeChk] Creating header file at {absolutePath}");
            Logger.Log($"[CreateVolumeChk] Parameters: {volWidth}x{volHeight}x{volDepth}, chunkDim={chunkDim}, pixelSize={pixelSize}");

            try
            {
                // Ensure the directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));

                using (FileStream fs = new FileStream(absolutePath, FileMode.Create, FileAccess.Write))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(volWidth);
                    bw.Write(volHeight);
                    bw.Write(volDepth);
                    bw.Write(chunkDim);
                    bw.Write(pixelSize); // Store pixel size

                    // Ensure all data is written to disk
                    fs.Flush(true);
                }

                // Verify the file was created successfully
                FileInfo fileInfo = new FileInfo(absolutePath);
                if (!fileInfo.Exists || fileInfo.Length < sizeof(int) * 4 + sizeof(double))
                {
                    throw new IOException($"Failed to create valid header file: {absolutePath}");
                }

                Logger.Log($"[CreateVolumeChk] Header file created successfully: {fileInfo.Length} bytes");
            }
            catch (Exception ex)
            {
                Logger.Log($"[CreateVolumeChk] Error creating header file: {ex.Message}");
                throw new IOException($"Failed to create volume.chk file: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Reads metadata from a volume.chk file
        /// </summary>
        public static (int volWidth, int volHeight, int volDepth, int chunkDim, double pixelSize) ReadVolumeChk(string folder)
        {
            string chkPath = Path.Combine(folder, "volume.chk");
            string absolutePath = Path.GetFullPath(chkPath);

            Logger.Log($"[ReadVolumeChk] Reading volume header from: {absolutePath}");

            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException($"Volume header file not found: {absolutePath}");
            }

            FileInfo fileInfo = new FileInfo(absolutePath);
            Logger.Log($"[ReadVolumeChk] File size: {fileInfo.Length} bytes, Last modified: {fileInfo.LastWriteTime}");

            // Minimum size for a valid header
            int minHeaderSize = sizeof(int) * 4 + sizeof(double);

            if (fileInfo.Length < minHeaderSize)
            {
                throw new InvalidDataException($"Header file too small: {fileInfo.Length} bytes (needs at least {minHeaderSize} bytes)");
            }

            try
            {
                using (FileStream fs = new FileStream(absolutePath, FileMode.Open, FileAccess.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    int volWidth = br.ReadInt32();
                    int volHeight = br.ReadInt32();
                    int volDepth = br.ReadInt32();
                    int chunkDim = br.ReadInt32();

                    // The pixelSize field might be missing in older files, use a default in that case
                    double pixelSize = 1e-6; // Default to 1 µm

                    if (fs.Position < fs.Length)
                    {
                        try
                        {
                            pixelSize = br.ReadDouble();
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[ReadVolumeChk] Warning: Could not read pixelSize field, using default: {ex.Message}");
                        }
                    }
                    else
                    {
                        Logger.Log("[ReadVolumeChk] Warning: volume.chk file does not contain pixelSize field, using default value");
                    }

                    // Validate the values are reasonable
                    const int MAX_DIM = 10000;
                    const int MIN_CHUNK = 16;
                    const int MAX_CHUNK = 512;

                    if (volWidth <= 0 || volWidth > MAX_DIM)
                        throw new InvalidDataException($"Invalid volume width in header: {volWidth}");

                    if (volHeight <= 0 || volHeight > MAX_DIM)
                        throw new InvalidDataException($"Invalid volume height in header: {volHeight}");

                    if (volDepth <= 0 || volDepth > MAX_DIM)
                        throw new InvalidDataException($"Invalid volume depth in header: {volDepth}");

                    if (chunkDim < MIN_CHUNK || chunkDim > MAX_CHUNK)
                        throw new InvalidDataException($"Invalid chunk dimension in header: {chunkDim}");

                    if (pixelSize <= 0 || pixelSize > 1.0) // 1.0 meters would be huge for CT voxels
                        throw new InvalidDataException($"Invalid pixel size in header: {pixelSize}");

                    Logger.Log($"[ReadVolumeChk] Successfully read header: {volWidth}x{volHeight}x{volDepth}, chunkDim={chunkDim}, pixelSize={pixelSize}");
                    return (volWidth, volHeight, volDepth, chunkDim, pixelSize);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ReadVolumeChk] Error reading volume.chk: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Creates a labels.chk file containing material data
        /// </summary>
        public static void CreateLabelsChk(string folder, List<Material> materials)
        {
            string chkPath = Path.Combine(folder, "labels.chk");
            using (FileStream fs = new FileStream(chkPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(materials.Count);
                foreach (var mat in materials)
                {
                    bw.Write(mat.Name);
                    bw.Write(mat.Color.ToArgb());
                    bw.Write(mat.Min);
                    bw.Write(mat.Max);
                    bw.Write(mat.IsExterior);
                    // Write the material's unique ID.
                    bw.Write(mat.ID);
                }
            }
            Logger.Log($"[FileOperations] Created labels.chk at {chkPath}");
        }

        /// <summary>
        /// Reads material data from a labels.chk file
        /// </summary>
        public static List<Material> ReadLabelsChk(string folder)
        {
            string chkPath = Path.Combine(folder, "labels.chk");
            List<Material> mats = new List<Material>();
            using (FileStream fs = new FileStream(chkPath, FileMode.Open, FileAccess.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string name = br.ReadString();
                    int argb = br.ReadInt32();
                    byte min = br.ReadByte();
                    byte max = br.ReadByte();
                    bool isExterior = br.ReadBoolean();
                    // Read the material ID from the file.
                    byte id = br.ReadByte();
                    mats.Add(new Material(name, Color.FromArgb(argb), min, max, id) { IsExterior = isExterior });
                }
            }
            Logger.Log($"[FileOperations] Loaded {mats.Count} materials from labels.chk");
            return mats;
        }
        #endregion

        #region File Creation and Export
        /// <summary>
        /// Creates a blank labels.bin file with the specified dimensions
        /// </summary>
        public static void CreateBlankLabelsFile(string path, int volWidth, int volHeight, int volDepth, int chunkDim)
        {
            int cntX = (volWidth + chunkDim - 1) / chunkDim;
            int cntY = (volHeight + chunkDim - 1) / chunkDim;
            int cntZ = (volDepth + chunkDim - 1) / chunkDim;
            int totalChunks = cntX * cntY * cntZ;
            long chunkSize = (long)chunkDim * chunkDim * chunkDim;

            Logger.Log($"[CreateBlankLabelsFile] Creating file: {path} with dimensions {volWidth}x{volHeight}x{volDepth}");

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(chunkDim);
                bw.Write(cntX);
                bw.Write(cntY);
                bw.Write(cntZ);

                byte[] emptyChunk = new byte[chunkSize];
                for (int i = 0; i < totalChunks; i++)
                {
                    bw.Write(emptyChunk, 0, emptyChunk.Length);
                }
            }
            Logger.Log($"[CreateBlankLabelsFile] Created blank labels file: {totalChunks} chunks, {totalChunks * chunkSize:N0} bytes");
        }

        /// <summary>
        /// Saves volume and label data to a combined binary file
        /// </summary>
        public static void SaveBinary(string path, IGrayscaleVolumeData volumeData, ILabelVolumeData volumeLabels,
                                    List<Material> materials, int width, int height, int depth, double pixelSize)
        {
            if (volumeLabels == null)
            {
                throw new ArgumentNullException(nameof(volumeLabels), "No label volume to save.");
            }

            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(width);
                    bw.Write(height);
                    bw.Write(depth);
                    bw.Write(pixelSize);

                    bw.Write(materials.Count);
                    foreach (var mat in materials)
                    {
                        bw.Write(mat.Name);
                        bw.Write(mat.Color.ToArgb());
                        bw.Write(mat.Min);
                        bw.Write(mat.Max);
                        bw.Write(mat.IsExterior);
                        bw.Write(mat.ID);
                    }

                    bool hasGrayscale = (volumeData != null);
                    bw.Write(hasGrayscale);
                    if (hasGrayscale)
                        volumeData.WriteChunks(bw);
                    volumeLabels.WriteChunks(bw);
                }
                Logger.Log($"[FileOperations] Volume saved successfully to {path}");
            }
            catch (Exception ex)
            {
                Logger.Log("[FileOperations] Error saving binary: " + ex);
                throw;
            }
        }

        /// <summary>
        /// Exports the dataset as image files
        /// </summary>
        public static void ExportImages(string outputPath, IGrayscaleVolumeData volumeData, ILabelVolumeData volumeLabels,
                                      List<Material> materials, int width, int height, int depth)
        {
            if (volumeLabels == null)
            {
                throw new ArgumentNullException(nameof(volumeLabels), "No label volume loaded.");
            }

            Parallel.For(0, depth, z =>
            {
                using (Bitmap bmp = CreateBitmapFromData(z, volumeData, volumeLabels, materials, width, height))
                {
                    string filePath = Path.Combine(outputPath, $"{z:00000}.png");
                    bmp.Save(filePath, ImageFormat.Png);
                }
            });

            Logger.Log("[FileOperations] Exported label images to " + outputPath);
        }

        /// <summary>
        /// Creates a bitmap for a specific slice
        /// </summary>
        private static Bitmap CreateBitmapFromData(int sliceIndex, IGrayscaleVolumeData volumeData, ILabelVolumeData volumeLabels,
                                                 List<Material> materials, int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            // Lock bits for faster access
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            try
            {
                unsafe
                {
                    byte* ptr = (byte*)bmpData.Scan0.ToPointer();
                    int stride = bmpData.Stride;

                    for (int y = 0; y < height; y++)
                    {
                        byte* row = ptr + (y * stride);
                        for (int x = 0; x < width; x++)
                        {
                            int offset = x * 3;

                            // Get grayscale value
                            byte gVal = volumeData != null ? volumeData[x, y, sliceIndex] : (byte)128;

                            // Get label value
                            byte label = volumeLabels[x, y, sliceIndex];

                            // Start with grayscale
                            byte r = gVal, g = gVal, b = gVal;

                            // Apply label color if present
                            if (label != 0)
                            {
                                Material mat = materials.FirstOrDefault(m => m.ID == label);
                                if (mat != null)
                                {
                                    // Blend with material color
                                    r = (byte)((r + mat.Color.R) / 2);
                                    g = (byte)((g + mat.Color.G) / 2);
                                    b = (byte)((b + mat.Color.B) / 2);
                                }
                            }

                            // Write RGB values (BGR order for bitmap)
                            row[offset] = b;
                            row[offset + 1] = g;
                            row[offset + 2] = r;
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return bmp;
        }
        #endregion
    }
}