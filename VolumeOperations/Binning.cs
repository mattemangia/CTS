using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace CTSegmenter
{
    public static class Binning
    {
        /// <summary>
        /// Entry point for binning. 
        /// 1) Load old chunked volume from volume.bin/.chk in folderPath.
        /// 2) Bin it by binFactor (2,4,8,16,...).
        /// 3) Overwrite the old volume.bin/.chk with the new binned data.
        /// 4) Create a new blank labels.bin/.chk (all zero => "exterior").
        /// </summary>
        public static async Task ProcessBinningAsync(string folderPath, int binFactor, float pixelSize, bool useMM)
        {
            Log($"[ProcessBinningAsync] Binning factor={binFactor}, folder={folderPath}, pixelSize={pixelSize}", "Binning");

            // 1) Read old chunkDim + old pixelSize from volume.chk. If pixelSize <= 0, we keep the old one from .chk
            string volumeChkPath = Path.Combine(folderPath, "volume.chk");
            if (!File.Exists(volumeChkPath))
                throw new FileNotFoundException($"No volume.chk found in {folderPath}");

            int oldChunkDim = 256;
            float oldPixelSize = 1f;
            ParseVolumeChk(volumeChkPath, ref oldChunkDim, ref oldPixelSize);
            if (pixelSize > 0f)  // If user explicitly provided a pixel size, override
                oldPixelSize = pixelSize;

            // 2) Load the old volume using ChunkedVolume.FromFolder, so it’s not locked to "volume.bin"
            //    However, if "FromFolder" itself creates "volume.bin", we can still do that. 
            //    Or can just open memory-mapped from "volume.bin" if it already exists. 

            //    Here, we assume it's the same approach code is using. 


            ChunkedVolume oldVol = null;
            ChunkedVolume newVol = null;
            try
            {
                oldVol = ChunkedVolume.FromFolder(folderPath, oldChunkDim, null, useMM);
                int oldW = oldVol.Width;
                int oldH = oldVol.Height;
                int oldD = oldVol.Depth;
                Log($"[ProcessBinningAsync] Loaded old volume: {oldW}x{oldH}x{oldD}, chunkDim={oldChunkDim}", "Binning");

                // 3) Compute new dims
                int newW = (oldW + binFactor - 1) / binFactor;
                int newH = (oldH + binFactor - 1) / binFactor;
                int newD = (oldD + binFactor - 1) / binFactor;
                if (newW < 1) newW = 1;
                if (newH < 1) newH = 1;
                if (newD < 1) newD = 1;

                int newChunkDim = Math.Min(oldChunkDim, newW);
                if (newChunkDim < 1) newChunkDim = 1;
                float newPixelSize = oldPixelSize * binFactor;

                Log($"[ProcessBinningAsync] Binned volume => {newW}x{newH}x{newD}, chunkDim={newChunkDim}, px={newPixelSize}", "Binning");

                // 4) Create the new chunked volume in memory
                newVol = new ChunkedVolume(newW, newH, newD, newChunkDim);

                // 5) Bin the old volume => new volume
                BinVolume(oldVol, newVol, binFactor);

                // 6) Dispose old volume to release any lock on volume.bin
                oldVol.Dispose();
                oldVol = null;

                // 7) Overwrite volume.bin + volume.chk
                OverwriteVolume(newVol, folderPath, newPixelSize, useMM);

                // 8) Always create a blank label file 
                //    with the same new dimension + chunkDim => label=0 => "exterior"
                CreateBlankLabels(folderPath, newW, newH, newD, newChunkDim, newPixelSize, useMM);
            }
            finally
            {
                // Cleanup if something fails
                if (oldVol != null)
                    oldVol.Dispose();
                if (newVol != null)
                    newVol.Dispose();
            }

            Log("[ProcessBinningAsync] Binning complete. Overwrote volume.bin/.chk + labels.bin/.chk with binned data.", "Binning");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Bins the volume chunk-by-chunk:
        /// For each voxel in newVol, we average binFactor^3 region from oldVol. 
        /// </summary>
        private static void BinVolume(ChunkedVolume oldVol, ChunkedVolume newVol, int binFactor)
        {
            Log("[BinVolume] Start binning...", "Binning");
            int oldW = oldVol.Width, oldH = oldVol.Height, oldD = oldVol.Depth;
            int newW = newVol.Width, newH = newVol.Height, newD = newVol.Depth;

            for (int z = 0; z < newD; z++)
            {
                for (int y = 0; y < newH; y++)
                {
                    for (int x = 0; x < newW; x++)
                    {
                        long sum = 0;
                        int count = 0;
                        for (int dz = 0; dz < binFactor; dz++)
                        {
                            int srcZ = z * binFactor + dz;
                            if (srcZ >= oldD) break;
                            for (int dy = 0; dy < binFactor; dy++)
                            {
                                int srcY = y * binFactor + dy;
                                if (srcY >= oldH) break;
                                for (int dx = 0; dx < binFactor; dx++)
                                {
                                    int srcX = x * binFactor + dx;
                                    if (srcX >= oldW) break;
                                    byte val = oldVol[srcX, srcY, srcZ];
                                    sum += val;
                                    count++;
                                }
                            }
                        }
                        byte avg = (count > 0) ? (byte)(sum / count) : (byte)0;
                        newVol[x, y, z] = avg;
                    }
                }
            }
            Log("[BinVolume] Done binning oldVol => newVol", "Binning");
        }

        /// <summary>
        /// Overwrites volume.bin + volume.chk with the new volume data + updated pixel size.
        /// Disposal of the old volume must happen first, or the file lock remains.
        /// </summary>
        private static void OverwriteVolume(ChunkedVolume vol, string folderPath, float newPixelSize, bool useMM)
        {
            // 1) Delete old volume.bin/.chk
            string binPath = Path.Combine(folderPath, "volume.bin");
            string chkPath = Path.Combine(folderPath, "volume.chk");
            DeleteIfExists(binPath);
            DeleteIfExists(chkPath);

            // 2) SaveAsBin => writes the 28-byte header + chunk data
            vol.SaveAsBin(binPath);

            // 3) Recompute chunk counts
            int cntX = (vol.Width + GetPrivateField<int>(vol, "_chunkDim") - 1) / GetPrivateField<int>(vol, "_chunkDim");
            int cntY = (vol.Height + GetPrivateField<int>(vol, "_chunkDim") - 1) / GetPrivateField<int>(vol, "_chunkDim");
            int cntZ = (vol.Depth + GetPrivateField<int>(vol, "_chunkDim") - 1) / GetPrivateField<int>(vol, "_chunkDim");

            // 4) Write volume.chk
            using (var sw = new StreamWriter(chkPath))
            {
                sw.WriteLine($"Width={vol.Width}");
                sw.WriteLine($"Height={vol.Height}");
                sw.WriteLine($"Depth={vol.Depth}");
                sw.WriteLine($"ChunkDim={GetPrivateField<int>(vol, "_chunkDim")}");
                sw.WriteLine($"CntX={cntX}");
                sw.WriteLine($"CntY={cntY}");
                sw.WriteLine($"CntZ={cntZ}");
                sw.WriteLine($"PixelSize={newPixelSize.ToString(CultureInfo.InvariantCulture)}");
                sw.WriteLine("RawFile=volume.bin");
            }
            Log("[OverwriteVolume] Wrote new volume.bin/.chk", " Binning");
        }

        /// <summary>
        /// Creates a new blank label volume with the same new dimension/chunkDim => label=0 => "Exterior",
        /// and overwrites labels.bin/.chk. 
        /// </summary>
        private static void CreateBlankLabels(
    string folderPath,
    int w, int h, int d,
    int chunkDim, float px,
    bool useMM)
        {
            Logger.Log("[CreateBlankLabels] Creating empty label volume => all zero => exterior"+ " Binning");

            // 1) Delete old labels.bin/.chk
            string labelsBin = Path.Combine(folderPath, "labels.bin");
            string labelsChk = Path.Combine(folderPath, "labels.chk");
            DeleteIfExists(labelsBin);
            DeleteIfExists(labelsChk);

            // 2) Create new ChunkedLabelVolume 
            //    The constructor will create labels.bin at the needed size
            var labelVol = new ChunkedLabelVolume(w, h, d, chunkDim, useMM, labelsBin);


            // 3) If we want to be sure everything is zero, 

           

            //    do a triple nested loop writing labelVol[x,y,z] = 0.
            //    Usually new file is zeroed, but let's do it explicitly:
            for (int z = 0; z < d; z++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        labelVol[x, y, z] = 0; // writes to memory-mapped chunk
                    }
                }
            }

            // 4) Dispose => flush data to disk + close the memory map
            labelVol.Dispose();

            // 5) Write labels.chk (text file). 
            int cntX = (w + chunkDim - 1) / chunkDim;
            int cntY = (h + chunkDim - 1) / chunkDim;
            int cntZ = (d + chunkDim - 1) / chunkDim;
            using (var sw = new StreamWriter(labelsChk))
            {
                sw.WriteLine($"Width={w}");
                sw.WriteLine($"Height={h}");
                sw.WriteLine($"Depth={d}");
                sw.WriteLine($"ChunkDim={chunkDim}");
                sw.WriteLine($"CntX={cntX}");
                sw.WriteLine($"CntY={cntY}");
                sw.WriteLine($"CntZ={cntZ}");
                sw.WriteLine($"PixelSize={px.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                sw.WriteLine("MaterialCount=2");
                sw.WriteLine("Material0=Exterior");
                sw.WriteLine("Material1=Material1");
                sw.WriteLine($"RawFile={Path.GetFileName(labelsBin)}");
            }
            Logger.Log("[CreateBlankLabels] Created new labels.bin/.chk with all-zero data => exterior only."+ "Binning");
        }


        // --------------------------------------------------------------------
        //  HELPER METHODS
        // --------------------------------------------------------------------
        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    Log($"[DeleteIfExists] Can't delete {path}: {ex.Message}", "Binning");
                }
            }
        }

        /// <summary>

        ///   .chk: 

        /// In .chk: 

        ///   Width=...
        ///   Height=...
        ///   Depth=...
        ///   ChunkDim=256
        ///   PixelSize=...

        ///   only parse ChunkDim & PixelSize here to replicate the main usage.

        

        /// </summary>
        private static void ParseVolumeChk(string chkPath, ref int chunkDim, ref float px)
        {
            var lines = File.ReadAllLines(chkPath);
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                    continue;
                string[] parts = trimmed.Split('=');
                if (parts.Length != 2) continue;
                string key = parts[0].Trim().ToLowerInvariant();
                string val = parts[1].Trim();

                if (key == "chunkdim") chunkDim = int.Parse(val);
                else if (key == "pixelsize") px = float.Parse(val, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Accesses private fields in ChunkedVolume/ChunkedLabelVolume (e.g. _chunkDim).
        /// We do this so we can compute correct .chk fields without modifying the miracle classes.
        /// </summary>
        private static T GetPrivateField<T>(object obj, string fieldName)
        {
            var fi = obj.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (fi == null) return default;
            return (T)fi.GetValue(obj);
        }

        private static void Log(string msg, string cat)
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - [{cat}] {msg}");
        }
    }
}
