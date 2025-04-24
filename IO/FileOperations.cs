using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading.Tasks;

namespace CTSegmenter
{
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

            IGrayscaleVolumeData volumeData = null;
            ILabelVolumeData volumeLabels = null;
            int width = 0, height = 0, depth = 0;

            bool folderMode = Directory.Exists(path);

            // --- FOLDER MODE ---
            if (folderMode)
            {
                Logger.Log("[FileOperations] Detected folder mode.");

                string volumeBinPath = Path.Combine(path, "volume.bin");
                string labelsBinPath = Path.Combine(path, "labels.bin");
                string volumeChkPath = Path.Combine(path, "volume.chk");

                // Check if binary files need to be generated from folder images
                if (!File.Exists(volumeBinPath) || !File.Exists(labelsBinPath) || !File.Exists(volumeChkPath))
                {
                    Logger.Log("[FileOperations] Binary files missing. Generating volume.bin from folder images.");
                    volumeData = ChunkedVolume.FromFolder(path, CHUNK_DIM, (ProgressForm)progress, useMemoryMapping);
                    width = volumeData.Width;
                    height = volumeData.Height;
                    depth = volumeData.Depth;

                    Logger.Log($"[FileOperations] Created original volume: {width}x{height}x{depth}");
                    CreateVolumeChk(path, width, height, depth, CHUNK_DIM, pixelSize);

                    if (!File.Exists(labelsBinPath))
                    {
                        CreateBlankLabelsFile(labelsBinPath, width, height, depth, CHUNK_DIM);
                    }

                    volumeData.Dispose();
                    volumeData = null;
                }

                // --- BINNING Branch ---
                if (binningFactor > 1)
                {
                    string backupVolPath = Path.Combine(path, "temp_volume.bin");
                    Logger.Log($"[FileOperations] Creating backup copy of volume.bin: {backupVolPath}");
                    File.Copy(volumeBinPath, backupVolPath, true);

                    Logger.Log($"[FileOperations] Running binning process with factor {binningFactor}...");
                    await Binning.ProcessBinningAsync(path, binningFactor, (float)pixelSize, useMemoryMapping);
                    Logger.Log("[FileOperations] Binning complete. Loading binned volume.");
                }

                // Load volume.bin
                if (File.Exists(volumeBinPath))
                {
                    volumeData = LoadVolumeBin(volumeBinPath, useMemoryMapping);
                    width = volumeData.Width;
                    height = volumeData.Height;
                    depth = volumeData.Depth;
                    Logger.Log($"[FileOperations] Loaded volume: {width}x{height}x{depth}");
                }

                // Load labels.bin
                if (File.Exists(labelsBinPath))
                {
                    Logger.Log($"[FileOperations] Found labels file at: {labelsBinPath}");
                    try
                    {
                        volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("[FileOperations] Error loading labels.bin: " + ex.Message + ". Recreating blank labels file.");
                        CreateBlankLabelsFile(labelsBinPath, width, height, depth, CHUNK_DIM);
                        volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);
                        Logger.Log("[FileOperations] Reloaded new labels.bin.");
                    }
                }
                else
                {
                    Logger.Log("[FileOperations] labels.bin not found. Creating blank labels file.");
                    CreateBlankLabelsFile(labelsBinPath, width, height, depth, CHUNK_DIM);
                    volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);
                }
            }
            // --- FILE MODE ---
            else if (File.Exists(path))
            {
                string fileName = Path.GetFileName(path).ToLower();
                string baseFolder = Path.GetDirectoryName(path);

                // Case: Volume file (volume.bin) only
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

                        // Use raw-loading since the volume.bin in file mode does not include a header
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
                            volumeLabels = LoadLabelsBin(labelsPath, useMemoryMapping);
                        }
                    }
                    else
                    {
                        Logger.Log("[FileOperations] volume.chk not found. Assuming combined file mode.");
                        (volumeData, volumeLabels, width, height, depth, pixelSize) = LoadCombinedBinary(path);
                    }
                }
                // Case: Labels file only
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
                // Otherwise, assume Combined File mode
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

        /// <summary>
        /// Loads a volume from a binary file with header
        /// </summary>
        public static IGrayscaleVolumeData LoadVolumeBin(string path, bool useMM)
        {
            int volWidth, volHeight, volDepth, chunkDim, cntX, cntY, cntZ;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (BinaryReader br = new BinaryReader(fs))
            {
                volWidth = br.ReadInt32();
                volHeight = br.ReadInt32();
                volDepth = br.ReadInt32();
                chunkDim = br.ReadInt32();
                cntX = br.ReadInt32();
                cntY = br.ReadInt32();
                cntZ = br.ReadInt32();
            }

            int headerSize = 7 * sizeof(int);
            long chunkSize = (long)chunkDim * chunkDim * chunkDim;
            int totalChunks = cntX * cntY * cntZ;
            long expectedSize = headerSize + totalChunks * chunkSize;
            long fileSize = new FileInfo(path).Length;

            if (fileSize < expectedSize)
                throw new Exception($"volume.bin file is incomplete: expected {expectedSize} bytes but got {fileSize} bytes.");

            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            MemoryMappedViewAccessor[] accessors = new MemoryMappedViewAccessor[totalChunks];

            for (int i = 0; i < totalChunks; i++)
            {
                long offset = headerSize + i * chunkSize;
                accessors[i] = mmf.CreateViewAccessor(offset, chunkSize, MemoryMappedFileAccess.Read);
            }

            return new ChunkedVolume(volWidth, volHeight, volDepth, chunkDim, mmf, accessors);
        }

        /// <summary>
        /// Loads a volume from a raw binary file without header
        /// </summary>
        public static IGrayscaleVolumeData LoadVolumeBinRaw(string path, bool useMM, int volWidth, int volHeight, int volDepth, int chunkDim)
        {
            int cntX = (volWidth + chunkDim - 1) / chunkDim;
            int cntY = (volHeight + chunkDim - 1) / chunkDim;
            int cntZ = (volDepth + chunkDim - 1) / chunkDim;
            int totalChunks = cntX * cntY * cntZ;
            long chunkSize = (long)chunkDim * chunkDim * chunkDim;
            long expectedSize = totalChunks * chunkSize;
            long fileSize = new FileInfo(path).Length;

            if (fileSize != expectedSize)
                throw new Exception($"Raw volume.bin file size mismatch: expected {expectedSize} bytes but got {fileSize} bytes.");

            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            MemoryMappedViewAccessor[] accessors = new MemoryMappedViewAccessor[totalChunks];

            for (int i = 0; i < totalChunks; i++)
            {
                long offset = i * chunkSize;
                accessors[i] = mmf.CreateViewAccessor(offset, chunkSize, MemoryMappedFileAccess.Read);
            }

            return new ChunkedVolume(volWidth, volHeight, volDepth, chunkDim, mmf, accessors);
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

        #endregion Volume and Label Loading

        #region Header Files Operations

        /// <summary>
        /// Creates a volume.chk file containing metadata about the volume
        /// </summary>
        public static void CreateVolumeChk(string folder, int volWidth, int volHeight, int volDepth, int chunkDim, double pixelSize)
        {
            string chkPath = Path.Combine(folder, "volume.chk");
            using (FileStream fs = new FileStream(chkPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(volWidth);
                bw.Write(volHeight);
                bw.Write(volDepth);
                bw.Write(chunkDim);
                bw.Write(pixelSize); // Store pixel size
            }
            Logger.Log($"[FileOperations] Created header file at {chkPath} with pixel size {pixelSize}");
        }

        /// <summary>
        /// Reads metadata from a volume.chk file
        /// </summary>
        public static (int volWidth, int volHeight, int volDepth, int chunkDim, double pixelSize) ReadVolumeChk(string folder)
        {
            string chkPath = Path.Combine(folder, "volume.chk");
            if (!File.Exists(chkPath))
                throw new Exception("Volume header file (volume.chk) not found.");

            using (FileStream fs = new FileStream(chkPath, FileMode.Open, FileAccess.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                int volWidth = br.ReadInt32();
                int volHeight = br.ReadInt32();
                int volDepth = br.ReadInt32();
                int chunkDim = br.ReadInt32();
                double pixelSize = br.ReadDouble();
                return (volWidth, volHeight, volDepth, chunkDim, pixelSize);
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

        #endregion Header Files Operations

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
            Logger.Log($"[FileOperations] Created blank labels.bin at {path}");
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
                using (Bitmap bmp = CreateBitmapFromData(z, (ChunkedVolume)volumeData, (ChunkedLabelVolume)volumeLabels, materials, width, height))
                {
                    string filePath = Path.Combine(outputPath, $"{z:00000}.bmp");
                    bmp.Save(filePath);
                }
            });

            Logger.Log("[FileOperations] Exported label images to " + outputPath);
        }

        /// <summary>
        /// Creates a bitmap for a specific slice
        /// </summary>
        private static Bitmap CreateBitmapFromData(int sliceIndex, ChunkedVolume volumeData, ChunkedLabelVolume volumeLabels,
                                                 List<Material> materials, int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Start with the grayscale value
                    byte gVal = volumeData?[x, y, sliceIndex] ?? 128;
                    Color finalColor = Color.FromArgb(gVal, gVal, gVal);

                    // If a segmentation has been applied, use it
                    byte appliedLabel = volumeLabels?[x, y, sliceIndex] ?? 0;
                    if (appliedLabel != 0)
                    {
                        Material mat = materials.FirstOrDefault(m => m.ID == appliedLabel);
                        if (mat != null)
                        {
                            finalColor = mat.Color;
                        }
                    }

                    bmp.SetPixel(x, y, finalColor);
                }
            }

            return bmp;
        }

        #endregion File Creation and Export
    }
}