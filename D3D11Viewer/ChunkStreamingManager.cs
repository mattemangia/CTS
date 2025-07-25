// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace CTS.D3D11
{
    public class ChunkStreamingManager : IDisposable
    {
        private class GpuCacheSlot
        {
            public int ChunkIndex { get; set; } = -1;
            public long LastAccessTime { get; set; }
            public bool IsLoading { get; set; } = false;
        }

        private readonly ID3D11Device device;
        private readonly ID3D11DeviceContext context;
        private readonly IGrayscaleVolumeData volumeData;
        private readonly ILabelVolumeData labelData;

        private readonly ID3D11Texture2D grayscaleCache;
        private readonly ID3D11Texture2D labelCache;

        private readonly GpuCacheSlot[] cacheSlots;
        private readonly Dictionary<int, int> chunkToGpuSlotMap;
        private readonly ChunkInfoGPU[] chunkInfoGpuData;

        private readonly Task streamingTask;
        private readonly CancellationTokenSource cancellationTokenSource;
        private Camera lastCameraState;
        private bool isDirty = true;
        private readonly object _lock = new object();
        private readonly int totalChunks;

        private readonly ID3D11Texture2D stagingGrayscaleTexture;
        private readonly ID3D11Texture2D stagingLabelTexture;

        // Queue for asynchronous chunk uploads
        private readonly Queue<(int chunkIndex, int slotIndex)> uploadQueue = new Queue<(int, int)>();
        private readonly object uploadQueueLock = new object();

        public ChunkStreamingManager(ID3D11Device device, ID3D11DeviceContext context, IGrayscaleVolumeData volumeData, ILabelVolumeData labelData, ID3D11Texture2D grayscaleCache, ID3D11Texture2D labelCache)
        {
            this.device = device;
            this.context = context;
            this.volumeData = volumeData;
            this.labelData = labelData;
            this.grayscaleCache = grayscaleCache;
            this.labelCache = labelCache;

            totalChunks = volumeData.ChunkCountX * volumeData.ChunkCountY * volumeData.ChunkCountZ;

            // Calculate cache size from texture array size
            int arraySize = grayscaleCache.Description.ArraySize;
            int chunkDim = volumeData.ChunkDim;
            int numCacheSlots = arraySize / chunkDim;

            cacheSlots = new GpuCacheSlot[numCacheSlots];
            for (int i = 0; i < cacheSlots.Length; i++)
            {
                cacheSlots[i] = new GpuCacheSlot();
            }

            chunkToGpuSlotMap = new Dictionary<int, int>();
            chunkInfoGpuData = new ChunkInfoGPU[totalChunks];
            for (int i = 0; i < chunkInfoGpuData.Length; i++)
                chunkInfoGpuData[i].GpuSlotIndex = -1;

            // Create staging textures for efficient GPU upload
            var stagingDesc = new Texture2DDescription
            {
                Width = volumeData.ChunkDim,
                Height = volumeData.ChunkDim,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Write
            };
            stagingGrayscaleTexture = device.CreateTexture2D(stagingDesc);

            stagingDesc.Format = Format.R8_UInt;
            stagingLabelTexture = device.CreateTexture2D(stagingDesc);

            Logger.Log($"[ChunkStreamingManager] Initialized with {numCacheSlots} cache slots for {totalChunks} total chunks");

            cancellationTokenSource = new CancellationTokenSource();
            streamingTask = Task.Run(StreamingLoop, cancellationTokenSource.Token);

            // Start upload processing task
            Task.Run(ProcessUploadQueue, cancellationTokenSource.Token);
        }

        public void Update(Camera camera)
        {
            lock (_lock)
            {
                lastCameraState = camera;
                isDirty = true;
            }
        }

        public bool IsDirty() => isDirty;
        public ChunkInfoGPU[] GetGpuChunkInfo() => chunkInfoGpuData;

        private async Task StreamingLoop()
        {
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                Camera currentCamera = null;
                lock (_lock)
                {
                    if (isDirty && lastCameraState != null)
                    {
                        currentCamera = lastCameraState;
                        isDirty = false;
                    }
                }

                if (currentCamera != null)
                {
                    var requiredChunks = DetermineRequiredChunks(currentCamera);
                    ProcessRequiredChunks(requiredChunks);
                }

                await Task.Delay(50); // Faster update rate
            }
        }

        private async Task ProcessUploadQueue()
        {
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                (int chunkIndex, int slotIndex) uploadItem = (-1, -1);

                lock (uploadQueueLock)
                {
                    if (uploadQueue.Count > 0)
                    {
                        uploadItem = uploadQueue.Dequeue();
                    }
                }

                if (uploadItem.chunkIndex >= 0)
                {
                    UploadChunkToGpu(uploadItem.chunkIndex, uploadItem.slotIndex);

                    // Mark upload complete
                    lock (_lock)
                    {
                        cacheSlots[uploadItem.slotIndex].IsLoading = false;
                    }
                }
                else
                {
                    await Task.Delay(10); // Small delay when queue is empty
                }
            }
        }

        private HashSet<int> DetermineRequiredChunks(Camera camera)
        {
            var required = new HashSet<int>();
            var camPos = camera.Position;
            var chunkDistances = new List<(int index, float distance)>();

            int countX = volumeData.ChunkCountX;
            int countY = volumeData.ChunkCountY;
            int countZ = volumeData.ChunkCountZ;
            int chunkDim = volumeData.ChunkDim;

            // Calculate distances to all chunks
            for (int cz = 0; cz < countZ; cz++)
            {
                for (int cy = 0; cy < countY; cy++)
                {
                    for (int cx = 0; cx < countX; cx++)
                    {
                        var chunkCenter = new Vector3(
                            (cx + 0.5f) * chunkDim,
                            (cy + 0.5f) * chunkDim,
                            (cz + 0.5f) * chunkDim);

                        float distance = Vector3.DistanceSquared(camPos, chunkCenter);
                        int chunkIndex = cz * (countX * countY) + cy * countX + cx;
                        chunkDistances.Add((chunkIndex, distance));
                    }
                }
            }

            // Sort by distance and take the closest chunks that fit in cache
            var orderedChunks = chunkDistances.OrderBy(c => c.distance).ToList();
            int maxChunks = cacheSlots.Length;

            foreach (var (chunkIndex, _) in orderedChunks.Take(maxChunks))
            {
                required.Add(chunkIndex);
            }

            return required;
        }

        private void ProcessRequiredChunks(HashSet<int> requiredChunks)
        {
            // First pass: mark chunks that are already loaded
            foreach (var chunkIndex in requiredChunks)
            {
                if (chunkToGpuSlotMap.ContainsKey(chunkIndex))
                {
                    int slotIndex = chunkToGpuSlotMap[chunkIndex];
                    cacheSlots[slotIndex].LastAccessTime = DateTime.Now.Ticks;
                }
            }

            // Second pass: load missing chunks
            foreach (var chunkIndex in requiredChunks)
            {
                if (!chunkToGpuSlotMap.ContainsKey(chunkIndex))
                {
                    int slotIndex = FindGpuSlot(requiredChunks);
                    if (slotIndex >= 0)
                    {
                        lock (_lock)
                        {
                            // Check if slot is already being loaded
                            if (cacheSlots[slotIndex].IsLoading)
                                continue;

                            // Clean up old chunk in this slot
                            if (cacheSlots[slotIndex].ChunkIndex != -1)
                            {
                                int oldChunkIndex = cacheSlots[slotIndex].ChunkIndex;
                                chunkToGpuSlotMap.Remove(oldChunkIndex);
                                chunkInfoGpuData[oldChunkIndex].GpuSlotIndex = -1;
                            }

                            // Mark as loading and assign chunk
                            cacheSlots[slotIndex].IsLoading = true;
                            cacheSlots[slotIndex].ChunkIndex = chunkIndex;
                            cacheSlots[slotIndex].LastAccessTime = DateTime.Now.Ticks;
                            chunkToGpuSlotMap[chunkIndex] = slotIndex;
                            chunkInfoGpuData[chunkIndex].GpuSlotIndex = slotIndex;
                        }

                        // Queue for async upload
                        lock (uploadQueueLock)
                        {
                            uploadQueue.Enqueue((chunkIndex, slotIndex));
                        }
                    }
                }
            }
        }

        private int FindGpuSlot(HashSet<int> requiredChunks)
        {
            lock (_lock)
            {
                // First, try to find an empty slot
                for (int i = 0; i < cacheSlots.Length; i++)
                {
                    if (cacheSlots[i].ChunkIndex == -1 && !cacheSlots[i].IsLoading)
                        return i;
                }

                // Find the least recently used slot that's not currently loading
                // and not in the required set
                long minTime = long.MaxValue;
                int lruIndex = -1;

                for (int i = 0; i < cacheSlots.Length; i++)
                {
                    if (!cacheSlots[i].IsLoading &&
                        cacheSlots[i].ChunkIndex != -1 &&
                        !requiredChunks.Contains(cacheSlots[i].ChunkIndex) &&
                        cacheSlots[i].LastAccessTime < minTime)
                    {
                        minTime = cacheSlots[i].LastAccessTime;
                        lruIndex = i;
                    }
                }

                return lruIndex;
            }
        }

        private void UploadChunkToGpu(int chunkIndex, int slotIndex)
        {
            try
            {
                var grayData = volumeData.GetChunkBytes(chunkIndex);
                var labelDataBytes = labelData.GetChunkBytes(chunkIndex);

                int chunkDim = volumeData.ChunkDim;
                int sliceByteSize = chunkDim * chunkDim;
                int startSliceIndexInAtlas = slotIndex * chunkDim;

                // Upload each slice
                lock (context)
                {
                    for (int i = 0; i < chunkDim; i++)
                    {
                        // Upload grayscale slice
                        var mappedGray = context.Map(stagingGrayscaleTexture, 0, MapMode.Write, MapFlags.None);
                        if (mappedGray.DataPointer != IntPtr.Zero)
                        {
                            // Calculate proper stride and copy
                            int srcOffset = i * sliceByteSize;
                            if (srcOffset + sliceByteSize <= grayData.Length)
                            {
                                Marshal.Copy(grayData, srcOffset, mappedGray.DataPointer, sliceByteSize);
                            }
                            context.Unmap(stagingGrayscaleTexture, 0);

                            int destGraySliceSubresource = startSliceIndexInAtlas + i;

                            // Use explicit source box
                            var srcBox = new Box(0, 0, 0, chunkDim, chunkDim, 1);
                            context.CopySubresourceRegion(
                                grayscaleCache,
                                destGraySliceSubresource,
                                0, 0, 0,
                                stagingGrayscaleTexture,
                                0,
                                srcBox);
                        }

                        // Upload label slice
                        var mappedLabel = context.Map(stagingLabelTexture, 0, MapMode.Write, MapFlags.None);
                        if (mappedLabel.DataPointer != IntPtr.Zero)
                        {
                            int srcOffset = i * sliceByteSize;
                            if (srcOffset + sliceByteSize <= labelDataBytes.Length)
                            {
                                Marshal.Copy(labelDataBytes, srcOffset, mappedLabel.DataPointer, sliceByteSize);
                            }
                            context.Unmap(stagingLabelTexture, 0);

                            int destLabelSliceSubresource = startSliceIndexInAtlas + i;

                            // Use explicit source box
                            var srcBox = new Box(0, 0, 0, chunkDim, chunkDim, 1);
                            context.CopySubresourceRegion(
                                labelCache,
                                destLabelSliceSubresource,
                                0, 0, 0,
                                stagingLabelTexture,
                                0,
                                srcBox);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChunkStreamingManager] Error uploading chunk {chunkIndex}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            cancellationTokenSource.Cancel();
            try
            {
                streamingTask.Wait(1000);
            }
            catch { }

            cancellationTokenSource.Dispose();
            stagingGrayscaleTexture?.Dispose();
            stagingLabelTexture?.Dispose();
        }
    }
}