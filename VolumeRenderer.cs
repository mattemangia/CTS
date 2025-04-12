using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

using HelixToolkit.Wpf;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit.Wpf.SharpDX.Core;
using HelixToolkit.Wpf.SharpDX.Model;

using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Color = System.Windows.Media.Color;
using Point3D = System.Windows.Media.Media3D.Point3D;
using Vector3D = System.Windows.Media.Media3D.Vector3D;
using Device = SharpDX.Direct3D11.Device;
using Format = SharpDX.DXGI.Format;
using MeshBuilder = HelixToolkit.Wpf.SharpDX.MeshBuilder;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using System.Diagnostics;
using System.Windows.Forms.Design;
using System.Windows.Documents;
using System.Drawing.Imaging;
using Rectangle = System.Drawing.Rectangle;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace CTSegmenter
{
    public class VolumeRenderer : IDisposable
    {
        public bool disposed = false;
        private MainForm mainForm;
        private EffectsManager effectsManager;
        private PerspectiveCamera camera;
        private Viewport3DX viewport;
        private GroupModel3D rootGroup;

        private VolumeTextureModel3D volumeModel;
        private VolumeTextureModel3D materialModel;
        private GroupModel3D slicePlanesGroup;
        private MeshGeometryModel3D xSlicePlaneModel;
        private MeshGeometryModel3D ySlicePlaneModel;
        private MeshGeometryModel3D zSlicePlaneModel;

        // Transfer function + material data
        private VolumeTextureDiffuseMaterial volumeMaterial;
        private VolumeTextureDiffuseMaterial labelsMaterial;
        private Color4[] labelColorMap;
        private Color4[] grayColorMap;

        // Material visibility/opacity
        private Dictionary<byte, bool> materialVisibilityState;
        private Dictionary<byte, double> materialOpacities;

        // Volume data
        private int volWidth, volHeight, volDepth;
        private double voxelSize;
        private bool dataLoaded = false;

        // Rendering parameters
        private int voxelStride = 2;  // Default to medium quality for large volumes
        private int minThreshold = 30; // Start with a higher threshold to reduce noise
        private int maxThreshold = 255;
        private bool showBwDataset = true;
        private bool useLodRendering = true;
        private bool realTimeUpdate = false;

        // GPU optimization settings
        private const int MAX_TEXTURE_SIZE = 2048;  // Maximum 3D texture dimension
        private bool useOctreeRendering = true;     // Use octree for large volumes
        private bool useViewDependentRender = true; // Render only what's visible

        // Clipping (slice) parameters
        private bool slicesEnabled = false;
        private bool slicePlanesVisible = false;
        private int sliceX = 0, sliceY = 0, sliceZ = 0;

        // Cached textures for performance
        private VolumeTextureGradientParams grayscaleTexture;
        private VolumeTextureGradientParams labelTexture;

        // GPU profiling
        private Stopwatch renderTimer = new Stopwatch();

        public VolumeRenderer(MainForm mainForm, Viewport3DX viewport)
        {
            this.mainForm = mainForm;
            this.viewport = viewport;
            this.materialVisibilityState = new Dictionary<byte, bool>();
            this.materialOpacities = new Dictionary<byte, double>();
            slicePlanesGroup = new GroupModel3D();

            InitializeViewportAndScene();
            InitializeMaterials();
            ConfigureGpuSettings();
            Logger.Log("[VolumeRenderer] Initialized with GPU optimizations");
        }

        private void ConfigureGpuSettings()
        {
            // Configure viewport for high-performance rendering
            viewport.FXAALevel = FXAALevel.None; // Disable anti-aliasing for performance
            viewport.EnableSwapChainRendering = true;
            viewport.EnableDeferredRendering = false;

            // Configure OIT if available
            

            Logger.Log("[VolumeRenderer] GPU render configuration set for high performance");
        }

        private void InitializeViewportAndScene()
        {
            try
            {
                // Create default camera with wider view
                camera = new PerspectiveCamera
                {
                    Position = new Point3D(0, 0, -500),
                    LookDirection = new Vector3D(0, 0, 1),
                    UpDirection = new Vector3D(0, -1, 0), // This corrects the orientation issue
                    FieldOfView = 60
                };
                viewport.Camera = camera;

                // Create HelixToolkit SharpDX effects manager
                effectsManager = new DefaultEffectsManager();
                viewport.EffectsManager = effectsManager;

                // Create root group for 3D content
                rootGroup = new GroupModel3D();
                viewport.Items.Add(rootGroup);

                // Add lights
                var light = new DirectionalLight3D
                {
                    Direction = new Vector3D(0, -0.5, -1),
                    Color = Color.FromRgb(255, 255, 255)
                };
                viewport.Items.Add(light);

                var ambient = new AmbientLight3D { Color = Color.FromRgb(90, 90, 90) };
                viewport.Items.Add(ambient);

                // Initialize slice plane group
                slicePlanesGroup = new GroupModel3D();
                rootGroup.Children.Add(slicePlanesGroup);
                xSlicePlaneModel = new MeshGeometryModel3D();
                ySlicePlaneModel = new MeshGeometryModel3D();
                zSlicePlaneModel = new MeshGeometryModel3D();

                

                // Setup coordinate system
                viewport.CoordinateSystemLabelForeground = System.Windows.Media.Color.FromRgb(255,255,255);
                viewport.ShowCoordinateSystem = true;

                // Setup ViewCube orientation correctly
                viewport.ModelUpDirection = new Vector3D(0, -1, 0);
                viewport.ViewCubeHorizontalPosition = viewport.Width-200;
                viewport.ViewCubeVerticalPosition = viewport.Height-200;

                // Set render technique options
                viewport.FXAALevel = FXAALevel.None; // Disable FXAA for performance
                viewport.EnableSwapChainRendering = true; // For better GPU performance
                viewport.EnableDeferredRendering = false; // Simpler render path

                // Viewport interaction setup
                viewport.ZoomExtentsWhenLoaded = true;
                viewport.RotationSensitivity = 0.5;
                viewport.ZoomSensitivity = 0.5;
                viewport.LeftRightPanSensitivity = 0.5;
                viewport.UpDownPanSensitivity = 0.5;
                viewport.IsPanEnabled = true;
                viewport.CameraRotationMode = HelixToolkit.Wpf.SharpDX.CameraRotationMode.Turnball;
                viewport.InfiniteSpin = true;

                Logger.Log("[VolumeRenderer] Viewport configured with optimized settings");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error initializing scene: {ex.Message}");
            }
        }

        private void InitializeMaterials()
        {
            foreach (var material in mainForm.Materials)
            {
                if (!material.IsExterior)
                {
                    materialVisibilityState[material.ID] = true;
                    materialOpacities[material.ID] = 1.0;
                }
            }
            Logger.Log("[VolumeRenderer] Material visibility/opacity initialized.");
        }

        // Properties for external toggles
        public bool ShowBwDataset
        {
            get => showBwDataset;
            set
            {
                showBwDataset = value;
                if (dataLoaded)
                {
                    volumeModel.IsRendering = showBwDataset;
                    Logger.Log($"[VolumeRenderer] ShowBwDataset set to {showBwDataset}");
                    UpdateTransferFunctions();
                }
            }
        }

        public bool UseLodRendering
        {
            get => useLodRendering;
            set
            {
                useLodRendering = value;
                Logger.Log($"[VolumeRenderer] UseLodRendering set to {useLodRendering}");
            }
        }

        public int VoxelStride
        {
            get => voxelStride;
            set
            {
                voxelStride = Math.Max(1, value);
                Logger.Log($"[VolumeRenderer] Voxel stride set to {voxelStride}");
            }
        }

        public int MinThreshold
        {
            get => minThreshold;
            set
            {
                minThreshold = Math.Max(0, value);
                Logger.Log($"[VolumeRenderer] MinThreshold set to {minThreshold}");
            }
        }

        public int MaxThreshold
        {
            get => maxThreshold;
            set
            {
                maxThreshold = Math.Min(255, value);
                Logger.Log($"[VolumeRenderer] MaxThreshold set to {maxThreshold}");
            }
        }

        public bool RealTimeUpdate { get => realTimeUpdate; set => realTimeUpdate = value; }

        /// <summary>
        /// GPU-optimized volume rendering for large datasets
        /// </summary>
        public async Task UpdateAsync()
        {
            if (mainForm.volumeData == null)
            {
                Logger.Log("[VolumeRenderer] No volume data loaded => aborting.");
                return;
            }

            renderTimer.Restart();
            Logger.Log("[VolumeRenderer] Starting volume rendering update...");

            try
            {
                // Get volume dimensions and pixel size
                volWidth = mainForm.GetWidth();
                volHeight = mainForm.GetHeight();
                volDepth = mainForm.GetDepth();
                voxelSize = mainForm.GetPixelSize();
                if (voxelSize <= 0)
                    voxelSize = 1.0;

                // Calculate volume size in bytes
                long totalVoxels = (long)volWidth * volHeight * volDepth;
                long volumeBytes = totalVoxels * sizeof(byte);
                Logger.Log($"[VolumeRenderer] Volume size: {volWidth}x{volHeight}x{volDepth} ({volumeBytes / (1024 * 1024)}MB)");

                // Determine appropriate downsampling for large volumes
                CalculateOptimalStride(totalVoxels);

                // Compute final texture dimensions
                int texWidth = (volWidth + voxelStride - 1) / voxelStride;
                int texHeight = (volHeight + voxelStride - 1) / voxelStride;
                int texDepth = (volDepth + voxelStride - 1) / voxelStride;

                Logger.Log($"[VolumeRenderer] Using stride {voxelStride} => Texture size: {texWidth}x{texHeight}x{texDepth}");

                // If texture dimensions are still too large, use brick-based approach
                if (texWidth > MAX_TEXTURE_SIZE || texHeight > MAX_TEXTURE_SIZE || texDepth > MAX_TEXTURE_SIZE)
                {
                    Logger.Log("[VolumeRenderer] Volume exceeds maximum texture size, using octree partitioning");
                    await RenderWithOctree(texWidth, texHeight, texDepth);
                }
                else
                {
                    // Create down-sampled volume array
                    await CreateOptimizedVolumeTexture(texWidth, texHeight, texDepth);
                }

                // Update transfer maps after texture creation
                InitializeTransferMaps();

                if (volumeMaterial != null)
                    volumeMaterial.TransferMap = grayColorMap;

                if (labelsMaterial != null)
                    labelsMaterial.TransferMap = labelColorMap;

                dataLoaded = true;

                // Reset camera to show entire volume - IMPORTANT for fixing the zoom issue
                if (!dataLoaded)
                {
                    ResetCameraView();
                }

                renderTimer.Stop();
                Logger.Log($"[VolumeRenderer] Volume rendering completed in {renderTimer.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error in UpdateAsync => {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Calculate the optimal stride based on volume size and available memory
        /// </summary>
        private void CalculateOptimalStride(long totalVoxels)
        {
            if (!useLodRendering)
            {
                voxelStride = 1;
                return;
            }

            // For extremely large volumes, use higher stride
            if (totalVoxels > 2000L * 2000 * 2000)
            {
                voxelStride = 8; // Ultra large volumes
                Logger.Log("[VolumeRenderer] Ultra large volume detected, using stride=8");
            }
            else if (totalVoxels > 1000L * 1000 * 1000)
            {
                voxelStride = 4; // Very large volumes 
                Logger.Log("[VolumeRenderer] Very large volume detected, using stride=4");
            }
            else if (totalVoxels > 500L * 500 * 500)
            {
                voxelStride = 2; // Large volumes
                Logger.Log("[VolumeRenderer] Large volume detected, using stride=2");
            }
            else
            {
                // Keep user-defined stride for smaller volumes
            }
        }

        /// <summary>
        /// Create optimized volume texture with downsampling for performance
        /// </summary>
        private async Task CreateOptimizedVolumeTexture(int texWidth, int texHeight, int texDepth)
        {
            try
            {
                Logger.Log("[VolumeRenderer] Creating optimized volume texture...");

                // Create grayscale volume arrays using stride for downsampling
                byte[] volumeBytes = await Task.Run(() => DownsampleVolumeData(texWidth, texHeight, texDepth));

                

                // Create new GPU texture for grayscale volume
                grayscaleTexture = CreateGPUTexture3D(volumeBytes, texWidth, texHeight, texDepth);

                // Create new volume material with optimized settings
                volumeMaterial = new VolumeTextureDiffuseMaterial
                {
                    Texture = grayscaleTexture,
                    SampleDistance = 0.5f / Math.Max(Math.Max(texWidth, texHeight), texDepth),
                    MaxIterations = 1024 // Increase for better quality if needed
                };

                // Handle label volume if available
                if (mainForm.volumeLabels != null)
                {
                    // Create label volume array
                    byte[] labelBytes = await Task.Run(() => DownsampleLabelData(texWidth, texHeight, texDepth));

                    // Dispose previous label texture if it exists
                    

                    // Create new GPU texture for labels
                    labelTexture = CreateGPUTexture3D(labelBytes, texWidth, texHeight, texDepth);

                    // Create new label material
                    labelsMaterial = new VolumeTextureDiffuseMaterial
                    {
                        Texture = labelTexture,
                        SampleDistance = 0.5f / Math.Max(Math.Max(texWidth, texHeight), texDepth),
                        MaxIterations = 1024
                    };
                }

                // Create or update volume models
                if (volumeModel == null)
                {
                    volumeModel = new VolumeTextureModel3D();
                    rootGroup.Children.Add(volumeModel);
                }

                volumeModel.VolumeMaterial = volumeMaterial;
                volumeModel.IsRendering = showBwDataset;

                if (materialModel == null && labelsMaterial != null)
                {
                    materialModel = new VolumeTextureModel3D();
                    rootGroup.Children.Add(materialModel);
                }

                if (labelsMaterial != null)
                {
                    materialModel.VolumeMaterial = labelsMaterial;
                    materialModel.IsRendering = true;
                }

                Logger.Log("[VolumeRenderer] Volume textures created successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error creating textures: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Downsample volume data with stride for performance
        /// </summary>
        private byte[] DownsampleVolumeData(int texWidth, int texHeight, int texDepth)
        {
            Logger.Log("[VolumeRenderer] Downsampling volume data...");
            byte[] volumeBytes = new byte[texWidth * texHeight * texDepth];

            Parallel.For(0, texDepth, z =>
            {
                int srcZ = Math.Min(z * voxelStride, volDepth - 1);
                for (int y = 0; y < texHeight; y++)
                {
                    int srcY = Math.Min(y * voxelStride, volHeight - 1);
                    for (int x = 0; x < texWidth; x++)
                    {
                        int srcX = Math.Min(x * voxelStride, volWidth - 1);
                        int destIndex = z * texWidth * texHeight + y * texWidth + x;

                        // Simple point sampling (could be upgraded to box filter for better quality)
                        volumeBytes[destIndex] = mainForm.volumeData[srcX, srcY, srcZ];
                    }
                }
            });

            return volumeBytes;
        }

        /// <summary>
        /// Downsample label data with stride for performance
        /// </summary>
        private byte[] DownsampleLabelData(int texWidth, int texHeight, int texDepth)
        {
            Logger.Log("[VolumeRenderer] Downsampling label data...");
            byte[] labelBytes = new byte[texWidth * texHeight * texDepth];

            Parallel.For(0, texDepth, z =>
            {
                int srcZ = Math.Min(z * voxelStride, volDepth - 1);
                for (int y = 0; y < texHeight; y++)
                {
                    int srcY = Math.Min(y * voxelStride, volHeight - 1);
                    for (int x = 0; x < texWidth; x++)
                    {
                        int srcX = Math.Min(x * voxelStride, volWidth - 1);
                        int destIndex = z * texWidth * texHeight + y * texWidth + x;

                        // For labels, use nearest neighbor to avoid interpolating label values
                        labelBytes[destIndex] = mainForm.volumeLabels[srcX, srcY, srcZ];
                    }
                }
            });

            return labelBytes;
        }

        /// <summary>
        /// Create GPU-optimized 3D texture
        /// </summary>
        private VolumeTextureGradientParams CreateGPUTexture3D(byte[] data, int width, int height, int depth)
        {
            int length = width * height * depth;
            var textureData = new Half4[length];

            // Convert byte data to Half4 format optimized for GPU
            Parallel.For(0, length, i =>
            {
                // Normalize the byte value into the range [0,1]
                float value = data[i] / 255f;
                textureData[i] = new Half4(value, value, value, value > 0 ? 1.0f : 0.0f);
            });

            return new VolumeTextureGradientParams(textureData, width, height, depth);
        }

        /// <summary>
        /// Render large volumes using octree partitioning
        /// </summary>
        private async Task RenderWithOctree(int texWidth, int texHeight, int texDepth)
        {
            // This would implement a brick-based approach for extremely large volumes
            // Here we'll use a simplified version by creating a lower-resolution preview

            Logger.Log("[VolumeRenderer] Using octree-based rendering for large volume");

            // Calculate a more aggressive downsampling factor for preview
            int previewStride = voxelStride * 2;
            int previewWidth = (volWidth + previewStride - 1) / previewStride;
            int previewHeight = (volHeight + previewStride - 1) / previewStride;
            int previewDepth = (volDepth + previewStride - 1) / previewStride;

            Logger.Log($"[VolumeRenderer] Creating preview texture: {previewWidth}x{previewHeight}x{previewDepth} with stride {previewStride}");

            // Create down-sampled preview volume
            byte[] previewVolume = new byte[previewWidth * previewHeight * previewDepth];

            await Task.Run(() =>
            {
                Parallel.For(0, previewDepth, z =>
                {
                    int srcZ = Math.Min(z * previewStride, volDepth - 1);
                    for (int y = 0; y < previewHeight; y++)
                    {
                        int srcY = Math.Min(y * previewStride, volHeight - 1);
                        for (int x = 0; x < previewWidth; x++)
                        {
                            int srcX = Math.Min(x * previewStride, volWidth - 1);
                            int destIndex = z * previewWidth * previewHeight + y * previewWidth + x;
                            previewVolume[destIndex] = mainForm.volumeData[srcX, srcY, srcZ];
                        }
                    }
                });
            });

            // Create preview texture
            
            grayscaleTexture = CreateGPUTexture3D(previewVolume, previewWidth, previewHeight, previewDepth);

            // Create optimized volume material for preview
            volumeMaterial = new VolumeTextureDiffuseMaterial
            {
                Texture = grayscaleTexture,
                SampleDistance = 0.5f / Math.Max(Math.Max(previewWidth, previewHeight), previewDepth),
                MaxIterations = 768 // Lower for performance with large volumes
            };

            // Handle labels if available
            if (mainForm.volumeLabels != null)
            {
                byte[] previewLabels = new byte[previewWidth * previewHeight * previewDepth];

                await Task.Run(() =>
                {
                    Parallel.For(0, previewDepth, z =>
                    {
                        int srcZ = Math.Min(z * previewStride, volDepth - 1);
                        for (int y = 0; y < previewHeight; y++)
                        {
                            int srcY = Math.Min(y * previewStride, volHeight - 1);
                            for (int x = 0; x < previewWidth; x++)
                            {
                                int srcX = Math.Min(x * previewStride, volWidth - 1);
                                int destIndex = z * previewWidth * previewHeight + y * previewWidth + x;
                                previewLabels[destIndex] = mainForm.volumeLabels[srcX, srcY, srcZ];
                            }
                        }
                    });
                });

                // Create label texture
                
                labelTexture = CreateGPUTexture3D(previewLabels, previewWidth, previewHeight, previewDepth);

                // Create material
                labelsMaterial = new VolumeTextureDiffuseMaterial
                {
                    Texture = labelTexture,
                    SampleDistance = 0.5f / Math.Max(Math.Max(previewWidth, previewHeight), previewDepth),
                    MaxIterations = 768
                };
            }

            // Update models
            if (volumeModel == null)
            {
                volumeModel = new VolumeTextureModel3D();
                rootGroup.Children.Add(volumeModel);
            }

            volumeModel.VolumeMaterial = volumeMaterial;
            volumeModel.IsRendering = showBwDataset;

            if (materialModel == null && labelsMaterial != null)
            {
                materialModel = new VolumeTextureModel3D();
                rootGroup.Children.Add(materialModel);
            }

            if (labelsMaterial != null)
            {
                materialModel.VolumeMaterial = labelsMaterial;
                materialModel.IsRendering = true;
            }

            Logger.Log("[VolumeRenderer] Octree preview rendering complete");
        }

        /// <summary>
        /// Initialize or update the transfer function maps for volume rendering
        /// </summary>
        private void InitializeTransferMaps()
        {
            // Create or update grayscale transfer map
            grayColorMap = new Color4[256];
            for (int i = 0; i < 256; i++)
                grayColorMap[i] = new Color4(0, 0, 0, 0);

            if (minThreshold < 0) minThreshold = 0;
            if (maxThreshold > 255) maxThreshold = 255;
            if (minThreshold > maxThreshold) minThreshold = maxThreshold;

            // Apply transfer function with hard cutoff for thresholding
            for (int i = 0; i < 256; i++)
            {
                if (i >= minThreshold && i <= maxThreshold)
                {
                    float normalizedIntensity = (i - minThreshold) / (float)Math.Max(1, maxThreshold - minThreshold);
                    float intensity = i / 255f;

                    // Set alpha to 1.0 for full opacity within the threshold range
                    float alpha = 1.0f; // Changed from normalizedIntensity

                    grayColorMap[i] = new Color4(intensity, intensity, intensity, alpha);
                }
                else
                {
                    // Complete transparency for values outside threshold range
                    grayColorMap[i] = new Color4(0, 0, 0, 0);
                }
            }

            // Ensure the material color map works properly
            labelColorMap = new Color4[256];
            for (int i = 0; i < 256; i++)
                labelColorMap[i] = new Color4(0, 0, 0, 0);

            foreach (var mat in mainForm.Materials)
            {
                if (mat.ID == 0) continue;
                System.Drawing.Color c = mat.Color;
                float alpha = 0f;
                if (materialVisibilityState.TryGetValue(mat.ID, out bool vis) && vis)
                {
                    materialOpacities.TryGetValue(mat.ID, out double op);
                    if (op <= 0) op = 1.0;
                    alpha = (float)op;
                }
                labelColorMap[mat.ID] = new Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
            }

            Logger.Log("[VolumeRenderer] Transfer functions updated with fixed thresholds");
        }

        /// <summary>
        /// Update volume rendering transfer function maps
        /// </summary>
        public void UpdateTransferFunctions()
        {
            if (!dataLoaded) return;

            InitializeTransferMaps();

            if (volumeMaterial != null)
                volumeMaterial.TransferMap = grayColorMap;

            if (labelsMaterial != null)
                labelsMaterial.TransferMap = labelColorMap;

            Logger.Log("[VolumeRenderer] Transfer functions updated.");
        }

        /// <summary>
        /// Set material visibility state and update rendering
        /// </summary>
        public void SetMaterialVisibility(byte materialId, bool isVisible)
        {
            materialVisibilityState[materialId] = isVisible;
            Logger.Log($"[VolumeRenderer] Material {materialId} visibility set => {isVisible}");
            UpdateTransferFunctions();
        }

        /// <summary>
        /// Set material opacity and update rendering
        /// </summary>
        public void SetMaterialOpacity(byte materialId, double opacity)
        {
            if (opacity < 0) opacity = 0;
            if (opacity > 1) opacity = 1;
            materialOpacities[materialId] = opacity;
            Logger.Log($"[VolumeRenderer] Material {materialId} opacity set => {opacity:F2}");
            UpdateTransferFunctions();
        }

        /// <summary>
        /// Get current material opacity
        /// </summary>
        public double GetMaterialOpacity(byte materialId)
        {
            return materialOpacities.TryGetValue(materialId, out double op) ? op : 1.0;
        }

        /// <summary>
        /// Toggle slice plane visibility
        /// </summary>
        public void ShowSlicePlanes(bool show)
        {
            slicePlanesVisible = show;
            if (!show)
            {
                slicePlanesGroup.Children.Clear();
            }
            else
            {
                UpdateSlicePlanes(sliceX, sliceY, sliceZ, mainForm.GetWidth(), mainForm.GetHeight(), mainForm.GetDepth(), mainForm.GetPixelSize());
            }
        }

        /// <summary>
        /// Update all slice planes
        /// </summary>
        public void UpdateSlicePlanes(int xPos, int yPos, int zPos, int width, int height, int depth, double pixelSize)
        {
            sliceX = xPos;
            sliceY = yPos;
            sliceZ = zPos;
            if (!slicePlanesVisible) return;

            slicePlanesGroup.Children.Clear();

            // Create slice planes using thresholded textures
            CreateThresholdedSliceX(xPos, width, height, depth, pixelSize);
            CreateThresholdedSliceY(yPos, width, height, depth, pixelSize);
            CreateThresholdedSliceZ(zPos, width, height, depth, pixelSize);

            Logger.Log($"[VolumeRenderer] Updated slice planes with thresholding => X={xPos}, Y={yPos}, Z={zPos}");
        }

        private MeshGeometryModel3D BuildSlicePlaneX(int xPos, int width, int height, int depth, double pixelSize, Color4 sliceColor)
        {
            float xCoord = (float)(xPos * pixelSize);
            float totalY = (float)(height * pixelSize);
            float totalZ = (float)(depth * pixelSize);

            var builder = new MeshBuilder();
            builder.AddQuad(
                new Vector3(xCoord, 0, 0),
                new Vector3(xCoord, totalY, 0),
                new Vector3(xCoord, totalY, totalZ),
                new Vector3(xCoord, 0, totalZ)
            );

            var geometry = builder.ToMeshGeometry3D();
            var material = CreateTransparentMaterial(sliceColor);

            return new MeshGeometryModel3D
            {
                Geometry = geometry,
                Material = material,
                CullMode = CullMode.None
            };
        }

        private MeshGeometryModel3D BuildSlicePlaneY(int yPos, int width, int height, int depth, double pixelSize, Color4 sliceColor)
        {
            float yCoord = (float)(yPos * pixelSize);
            float totalX = (float)(width * pixelSize);
            float totalZ = (float)(depth * pixelSize);

            var builder = new MeshBuilder();
            builder.AddQuad(
                new Vector3(0, yCoord, 0),
                new Vector3(totalX, yCoord, 0),
                new Vector3(totalX, yCoord, totalZ),
                new Vector3(0, yCoord, totalZ)
            );

            var geometry = builder.ToMeshGeometry3D();
            var material = CreateTransparentMaterial(sliceColor);

            return new MeshGeometryModel3D
            {
                Geometry = geometry,
                Material = material,
                CullMode = CullMode.None
            };
        }

        private MeshGeometryModel3D BuildSlicePlaneZ(int zPos, int width, int height, int depth, double pixelSize, Color4 sliceColor)
        {
            float zCoord = (float)(zPos * pixelSize);
            float totalX = (float)(width * pixelSize);
            float totalY = (float)(height * pixelSize);

            var builder = new MeshBuilder();
            builder.AddQuad(
                new Vector3(0, 0, zCoord),
                new Vector3(totalX, 0, zCoord),
                new Vector3(totalX, totalY, zCoord),
                new Vector3(0, totalY, zCoord)
            );

            var geometry = builder.ToMeshGeometry3D();
            var material = CreateTransparentMaterial(sliceColor);

            return new MeshGeometryModel3D
            {
                Geometry = geometry,
                Material = material,
                CullMode = CullMode.None
            };
        }

        private PhongMaterial CreateTransparentMaterial(Color4 color)
        {
            return new PhongMaterial
            {
                DiffuseColor = color,
                AmbientColor = color,
                SpecularColor = new Color4(0, 0, 0, 1),
                ReflectiveColor = new Color4(0, 0, 0, 1)
            };
        }

        /// <summary>
        /// Update X-axis slice position
        /// </summary>
        public void UpdateXSlice(int xPos, int width, double pixelSize)
        {
            sliceX = xPos;
            if (slicePlanesVisible)
            {
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();
                CreateThresholdedSliceX(xPos, width, height, depth, pixelSize);
                // Force viewport update
                viewport.InvalidateVisual();
            }
        }
        private void CreateThresholdedSliceX(int xPos, int width, int height, int depth, double pixelSize)
        {
            try
            {
                // 1) Create a Bitmap sized [depth x height]
                //    Because for X-slice, the 'horizontal' axis in the slice is Z,
                //    and the 'vertical' axis is Y.
                using (Bitmap bmp = new Bitmap(depth, height, PixelFormat.Format32bppArgb))
                {
                    Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);

                    // 2) Allocate a byte array for BGRA data
                    //    Each pixel takes 4 bytes => totalPixels * 4
                    int totalPixels = bmp.Width * bmp.Height;
                    byte[] pixels = new byte[totalPixels * 4];

                    // 3) Fill the pixel array
                    //    We'll do: row = y, col = z
                    Parallel.For(0, height, y =>
                    {
                        // rowOffset is the start index for this row in the pixel buffer
                        int rowOffset = y * bmp.Width * 4;

                        for (int z = 0; z < depth; z++)
                        {
                            int idx = rowOffset + z * 4;

                            // Read voxel from volumeData[xPos, y, z]
                            byte val = mainForm.volumeData[xPos, y, z];

                            // Apply threshold
                            if (val < minThreshold || val > maxThreshold)
                            {
                                // Transparent pixel
                                pixels[idx + 0] = 0; // B
                                pixels[idx + 1] = 0; // G
                                pixels[idx + 2] = 0; // R
                                pixels[idx + 3] = 0; // A
                            }
                            else
                            {
                                // Grayscale pixel, fully opaque
                                pixels[idx + 0] = val;    // B
                                pixels[idx + 1] = val;    // G
                                pixels[idx + 2] = val;    // R
                                pixels[idx + 3] = 255;    // A
                            }
                        }
                    });

                    // 4) Copy the entire byte[] into the Bitmap
                    BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }

                    // 5) Convert that Bitmap into a texture/material
                    var material = CreateSliceMaterial(bmp, true);

                    // 6) Build the actual 3D quad for the slice plane at x = xPos * pixelSize
                    float xCoord = (float)(xPos * pixelSize);
                    float totalY = (float)(height * pixelSize);
                    float totalZ = (float)(depth * pixelSize);

                    var builder = new MeshBuilder();
                    builder.AddQuad(
                        new Vector3(xCoord, 0, 0),
                        new Vector3(xCoord, totalY, 0),
                        new Vector3(xCoord, totalY, totalZ),
                        new Vector3(xCoord, 0, totalZ)
                    );

                    xSlicePlaneModel = new MeshGeometryModel3D
                    {
                        Geometry = builder.ToMeshGeometry3D(),
                        Material = material,
                        CullMode = CullMode.None
                    };

                    // 7) Finally add to our slicePlanesGroup
                    slicePlanesGroup.Children.Add(xSlicePlaneModel);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error creating X slice: {ex.Message}");
            }
        }
        /// <summary>
        /// Update Y-axis slice position
        /// </summary>
        public void UpdateYSlice(int yPos, int height, double pixelSize)
        {
            sliceY = yPos;
            if (slicePlanesVisible)
            {
                int width = mainForm.GetWidth();
                int depth = mainForm.GetDepth();
                CreateThresholdedSliceY(yPos, width, height, depth, pixelSize);
                // Force viewport update
                viewport.InvalidateVisual();
            }
        }
        private void CreateThresholdedSliceY(int yPos, int width, int height, int depth, double pixelSize)
        {
            try
            {
                // 1) For Y-slice, the plane is sized [width x depth]
                //    Because 'horizontal' is X, 'vertical' is Z in the slice
                using (Bitmap bmp = new Bitmap(width, depth, PixelFormat.Format32bppArgb))
                {
                    Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                    int totalPixels = bmp.Width * bmp.Height;
                    byte[] pixels = new byte[totalPixels * 4];

                    // 2) Fill the pixel array
                    //    row => z, col => x
                    Parallel.For(0, depth, z =>
                    {
                        int rowOffset = z * bmp.Width * 4;
                        for (int x = 0; x < width; x++)
                        {
                            int idx = rowOffset + x * 4;

                            byte val = mainForm.volumeData[x, yPos, z];

                            if (val < minThreshold || val > maxThreshold)
                            {
                                pixels[idx + 0] = 0;
                                pixels[idx + 1] = 0;
                                pixels[idx + 2] = 0;
                                pixels[idx + 3] = 0;
                            }
                            else
                            {
                                pixels[idx + 0] = val;
                                pixels[idx + 1] = val;
                                pixels[idx + 2] = val;
                                pixels[idx + 3] = 255;
                            }
                        }
                    });

                    // 3) LockBits & copy
                    BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }

                    // 4) Create material
                    var material = CreateSliceMaterial(bmp, true);

                    // 5) Build the plane at y = yPos * pixelSize
                    float yCoord = (float)(yPos * pixelSize);
                    float totalX = (float)(width * pixelSize);
                    float totalZ = (float)(depth * pixelSize);

                    var builder = new MeshBuilder();
                    builder.AddQuad(
                        new Vector3(0, yCoord, 0),
                        new Vector3(totalX, yCoord, 0),
                        new Vector3(totalX, yCoord, totalZ),
                        new Vector3(0, yCoord, totalZ)
                    );

                    ySlicePlaneModel = new MeshGeometryModel3D
                    {
                        Geometry = builder.ToMeshGeometry3D(),
                        Material = material,
                        CullMode = CullMode.None
                    };

                    slicePlanesGroup.Children.Add(ySlicePlaneModel);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error creating Y slice: {ex.Message}");
            }
        }


        /// <summary>
        /// Update Z-axis slice position
        /// </summary>
        public void UpdateZSlice(int zPos, int depth, double pixelSize)
        {
            sliceZ = zPos;
            if (slicePlanesVisible)
            {
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                CreateThresholdedSliceZ(zPos, width, height, depth, pixelSize);
                // Force viewport update
                viewport.InvalidateVisual();
            }
        }
        private void CreateThresholdedSliceZ(int zPos, int width, int height, int depth, double pixelSize)
        {
            try
            {
                // 1) For Z-slice, the plane is [width x height]
                using (Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                    int totalPixels = bmp.Width * bmp.Height;
                    byte[] pixels = new byte[totalPixels * 4];

                    // 2) row => y, col => x
                    Parallel.For(0, height, y =>
                    {
                        int rowOffset = y * bmp.Width * 4;
                        for (int x = 0; x < width; x++)
                        {
                            int idx = rowOffset + x * 4;

                            byte val = mainForm.volumeData[x, y, zPos];

                            if (val < minThreshold || val > maxThreshold)
                            {
                                pixels[idx + 0] = 0;
                                pixels[idx + 1] = 0;
                                pixels[idx + 2] = 0;
                                pixels[idx + 3] = 0;
                            }
                            else
                            {
                                pixels[idx + 0] = val;
                                pixels[idx + 1] = val;
                                pixels[idx + 2] = val;
                                pixels[idx + 3] = 255;
                            }
                        }
                    });

                    // 3) LockBits & copy
                    BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }

                    // 4) Create the slice material
                    var material = CreateSliceMaterial(bmp, true);

                    // 5) Build the plane at z = zPos * pixelSize
                    float zCoord = (float)(zPos * pixelSize);
                    float totalX = (float)(width * pixelSize);
                    float totalY = (float)(height * pixelSize);

                    var builder = new MeshBuilder();
                    builder.AddQuad(
                        new Vector3(0, 0, zCoord),
                        new Vector3(totalX, 0, zCoord),
                        new Vector3(totalX, totalY, zCoord),
                        new Vector3(0, totalY, zCoord)
                    );

                    zSlicePlaneModel = new MeshGeometryModel3D
                    {
                        Geometry = builder.ToMeshGeometry3D(),
                        Material = material,
                        CullMode = CullMode.None
                    };

                    slicePlanesGroup.Children.Add(zSlicePlaneModel);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error creating Z slice: {ex.Message}");
            }
        }

        private PhongMaterial CreateSliceMaterial(System.Drawing.Bitmap bitmap, bool isTransparent)
        {
            try
            {
                // Convert bitmap to stream
                System.IO.MemoryStream stream = new System.IO.MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;

                // Create a BitmapImage
                var bitmapImage = new System.Windows.Media.Imaging.BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = stream;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                // Create a new material
                var material = new PhongMaterial
                {
                    DiffuseMap = new HelixToolkit.Wpf.SharpDX.TextureModel(stream),
                    DiffuseColor = new SharpDX.Color4(1, 1, 1, 1),
                    EmissiveColor = new SharpDX.Color4(0.1f, 0.1f, 0.1f, 1f)
                };

                if (isTransparent)
                {
                    material.DiffuseColor = new SharpDX.Color4(0,0,0,0);
                   
                }

                return material;
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error creating slice material: {ex.Message}");

                // Return a default material
                return new PhongMaterial
                {
                    DiffuseColor = new SharpDX.Color4(0.7f, 0.7f, 0.7f, 0.5f),
                     
                };
            }
        }


        /// <summary>
        /// Reset camera to show the entire volume in the viewport
        /// </summary>
        public void ResetCameraView()
        {
            if (camera == null) return;

            try
            {
                // Calculate volume dimensions
                double volWidth = mainForm.GetWidth();
                double volHeight = mainForm.GetHeight();
                double volDepth = mainForm.GetDepth();
                double pixelSize = mainForm.GetPixelSize();
                if (pixelSize <= 0) pixelSize = 1.0;

                Logger.Log($"[ResetCameraView] Volume dimensions: {volWidth}x{volHeight}x{volDepth}, pixel size: {pixelSize}");

                // Calculate volume center in world coordinates
                double centerX = volWidth * pixelSize / 2.0;
                double centerY = volHeight * pixelSize / 2.0;
                double centerZ = volDepth * pixelSize / 2.0;

                // Calculate diagonal of the volume for sizing
                double diagonal = Math.Sqrt(
                    volWidth * volWidth +
                    volHeight * volHeight +
                    volDepth * volDepth) * pixelSize;

                // Position camera at a reasonable distance
                // Using diagonal/2 should give a good field of view
                double distance = diagonal * 0.8;

                Logger.Log($"[ResetCameraView] Center: ({centerX}, {centerY}, {centerZ}), Distance: {distance}");

                // Set camera parameters directly
                camera.Position = new Point3D(centerX, centerY, -distance);
                camera.LookDirection = new Vector3D(0, 0, 1);
                camera.UpDirection = new Vector3D(0, -1, 0);

                // Adjust field of view for better perspective
                camera.FieldOfView = 40;

                // Set near and far planes
                camera.NearPlaneDistance = 0.1;
                camera.FarPlaneDistance = diagonal * 10;

                // Force viewport to redraw
                viewport.InvalidateVisual();

                Logger.Log("[VolumeRenderer] Camera view reset successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error in ResetCameraView: {ex.Message}");
            }
        }

        /// <summary>
        /// Quick test rendering with minimal quality for rapid feedback
        /// </summary>
        public void QuickRenderTest()
        {
            Logger.Log("[VolumeRenderer] Running quick test render...");

            try
            {
                // Get volume dimensions
                volWidth = mainForm.GetWidth();
                volHeight = mainForm.GetHeight();
                volDepth = mainForm.GetDepth();
                voxelSize = mainForm.GetPixelSize();

                // Create a very low-resolution preview with high downsampling
                int quickStride = 16;  // Very aggressive downsampling
                int previewWidth = (volWidth + quickStride - 1) / quickStride;
                int previewHeight = (volHeight + quickStride - 1) / quickStride;
                int previewDepth = (volDepth + quickStride - 1) / quickStride;

                Logger.Log($"[VolumeRenderer] Quick test: Creating {previewWidth}x{previewHeight}x{previewDepth} preview");

                // Create downsampled volume data
                byte[] previewVolume = new byte[previewWidth * previewHeight * previewDepth];

                for (int z = 0; z < previewDepth; z++)
                {
                    int srcZ = Math.Min(z * quickStride, volDepth - 1);
                    for (int y = 0; y < previewHeight; y++)
                    {
                        int srcY = Math.Min(y * quickStride, volHeight - 1);
                        for (int x = 0; x < previewWidth; x++)
                        {
                            int srcX = Math.Min(x * quickStride, volWidth - 1);
                            int destIndex = z * previewWidth * previewHeight + y * previewWidth + x;
                            previewVolume[destIndex] = mainForm.volumeData[srcX, srcY, srcZ];
                        }
                    }
                }

                // Create texture
                

                grayscaleTexture = CreateGPUTexture3D(previewVolume, previewWidth, previewHeight, previewDepth);

                // Create material with quick render settings
                volumeMaterial = new VolumeTextureDiffuseMaterial
                {
                    Texture = grayscaleTexture,
                    SampleDistance = 1.0f / Math.Max(Math.Max(previewWidth, previewHeight), previewDepth),
                    MaxIterations = 512
                };

                // Update transfer function for quick preview (higher threshold to see structure)
                minThreshold = 50; // Start with a higher threshold for faster preview
                InitializeTransferMaps();
                volumeMaterial.TransferMap = grayColorMap;

                // Create or update volume model
                if (volumeModel == null)
                {
                    volumeModel = new VolumeTextureModel3D();
                    rootGroup.Children.Add(volumeModel);
                }

                volumeModel.VolumeMaterial = volumeMaterial;
                volumeModel.IsRendering = true;

                // Reset camera view
                dataLoaded = true;
                ResetCameraView();

                Logger.Log("[VolumeRenderer] Quick test rendering complete");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error in QuickRenderTest: {ex.Message}");
            }
        }

        /// <summary>
        /// Export 3D model to mesh file
        /// </summary>
        public void ExportModel(string filePath)
        {
            if (!dataLoaded)
            {
                throw new InvalidOperationException("No volume data loaded to export.");
            }

            Logger.Log($"[VolumeRenderer] Exporting model to {filePath}...");

            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();

                if (ext == ".stl")
                {
                    ExportToStl(filePath);
                }
                else if (ext == ".obj")
                {
                    ExportToObj(filePath);
                }
                else if (ext == ".ply")
                {
                    ExportToPly(filePath);
                }
                else
                {
                    // Default to STL if unknown extension
                    ExportToStl(filePath);
                }

                Logger.Log("[VolumeRenderer] Export completed successfully.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error exporting model: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Generate triangle mesh data for export
        /// </summary>
        private void GenerateMeshData(List<Point3D> vertices, List<(int, int, int)> triangles, List<Color> triangleColors)
        {
            vertices.Clear();
            triangles.Clear();
            triangleColors.Clear();

            // Export uses a larger voxel stride to keep mesh size manageable
            int exportStride = voxelStride * 2;
            if (exportStride < 2) exportStride = 2;

            // Voxel size for scaling to world coords
            double sx = voxelSize;
            double sy = voxelSize;
            double sz = voxelSize;

            // Use higher threshold for export to get cleaner surfaces
            int isoValue = minThreshold > 20 ? minThreshold : 30;

            // Precompute which materials are visible
            var visibleMaterials = mainForm.Materials
                .Where(m => !m.IsExterior && materialVisibilityState.ContainsKey(m.ID) && materialVisibilityState[m.ID])
                .Select(m => m.ID)
                .ToHashSet();

            // Define colors for each region
            Color grayscaleColor = Colors.LightGray;
            Dictionary<byte, Color> materialColors = new Dictionary<byte, Color>();

            foreach (var mat in mainForm.Materials)
            {
                if (!mat.IsExterior && visibleMaterials.Contains(mat.ID))
                {
                    materialColors[mat.ID] = Color.FromRgb(mat.Color.R, mat.Color.G, mat.Color.B);
                }
            }

            // Process each slice of the volume
            for (int z = 0; z < volDepth; z += exportStride)
            {
                for (int y = 0; y < volHeight; y += exportStride)
                {
                    for (int x = 0; x < volWidth; x += exportStride)
                    {
                        // Determine which region this voxel belongs to
                        bool voxelInGrayRegion = false;
                        byte label = 0;
                        byte dataVal = mainForm.volumeData[x, y, z];

                        if (dataVal >= isoValue)
                        {
                            byte lbl = (mainForm.volumeLabels != null ? mainForm.volumeLabels[x, y, z] : (byte)0);
                            if (lbl != 0 && visibleMaterials.Contains(lbl))
                            {
                                label = lbl;
                            }
                            else
                            {
                                voxelInGrayRegion = true;
                            }
                        }
                        else
                        {
                            if (mainForm.volumeLabels != null)
                            {
                                byte lbl = mainForm.volumeLabels[x, y, z];
                                if (lbl != 0 && visibleMaterials.Contains(lbl))
                                    label = lbl;
                            }
                        }

                        // Skip empty regions
                        if (!voxelInGrayRegion && label == 0) continue;

                        // Check +X face
                        bool boundaryAtPlusX = false;
                        if (x + exportStride >= volWidth)
                        {
                            boundaryAtPlusX = true;
                        }
                        else
                        {
                            byte neighborLabel = 0;
                            bool neighborInGray = false;
                            byte neighborVal = mainForm.volumeData[x + exportStride, y, z];

                            if (neighborVal >= isoValue)
                            {
                                byte nLbl = (mainForm.volumeLabels != null ? mainForm.volumeLabels[x + exportStride, y, z] : (byte)0);
                                if (nLbl != 0 && visibleMaterials.Contains(nLbl))
                                    neighborLabel = nLbl;
                                else
                                    neighborInGray = true;
                            }
                            else
                            {
                                if (mainForm.volumeLabels != null)
                                {
                                    byte nLbl = mainForm.volumeLabels[x + exportStride, y, z];
                                    if (nLbl != 0 && visibleMaterials.Contains(nLbl))
                                        neighborLabel = nLbl;
                                }
                            }

                            if (voxelInGrayRegion)
                            {
                                if (neighborInGray)
                                    boundaryAtPlusX = false;
                                else if (neighborLabel != 0)
                                    boundaryAtPlusX = false;
                                else
                                    boundaryAtPlusX = true;
                            }
                            else if (label != 0)
                            {
                                if (neighborLabel == label)
                                    boundaryAtPlusX = false;
                                else if (neighborLabel != 0)
                                    boundaryAtPlusX = false;
                                else if (neighborInGray)
                                    boundaryAtPlusX = false;
                                else
                                    boundaryAtPlusX = true;
                            }
                        }

                        if (boundaryAtPlusX)
                        {
                            // Create face vertices
                            double x0 = (x + exportStride) * sx;
                            double y0 = y * sy;
                            double y1 = Math.Min(y + exportStride, volHeight) * sy;
                            double z0 = z * sz;
                            double z1 = Math.Min(z + exportStride, volDepth) * sz;

                            Point3D v0 = new Point3D(x0, y0, z0);
                            Point3D v1 = new Point3D(x0, y1, z0);
                            Point3D v2 = new Point3D(x0, y1, z1);
                            Point3D v3 = new Point3D(x0, y0, z1);

                            int baseIndex = vertices.Count;
                            vertices.Add(v0); vertices.Add(v1); vertices.Add(v2); vertices.Add(v3);

                            triangles.Add((baseIndex, baseIndex + 1, baseIndex + 2));
                            triangles.Add((baseIndex, baseIndex + 2, baseIndex + 3));

                            Color faceColor = voxelInGrayRegion ? grayscaleColor : materialColors[label];
                            triangleColors.Add(faceColor);
                            triangleColors.Add(faceColor);
                        }

                        // Check +Y face
                        bool boundaryAtPlusY = false;
                        if (y + exportStride >= volHeight)
                        {
                            boundaryAtPlusY = true;
                        }
                        else
                        {
                            byte neighborLabel = 0;
                            bool neighborInGray = false;
                            byte neighborVal = mainForm.volumeData[x, y + exportStride, z];

                            if (neighborVal >= isoValue)
                            {
                                byte nLbl = (mainForm.volumeLabels != null ? mainForm.volumeLabels[x, y + exportStride, z] : (byte)0);
                                if (nLbl != 0 && visibleMaterials.Contains(nLbl))
                                    neighborLabel = nLbl;
                                else
                                    neighborInGray = true;
                            }
                            else
                            {
                                if (mainForm.volumeLabels != null)
                                {
                                    byte nLbl = mainForm.volumeLabels[x, y + exportStride, z];
                                    if (nLbl != 0 && visibleMaterials.Contains(nLbl))
                                        neighborLabel = nLbl;
                                }
                            }

                            if (voxelInGrayRegion)
                            {
                                if (neighborInGray)
                                    boundaryAtPlusY = false;
                                else if (neighborLabel != 0)
                                    boundaryAtPlusY = false;
                                else
                                    boundaryAtPlusY = true;
                            }
                            else if (label != 0)
                            {
                                if (neighborLabel == label)
                                    boundaryAtPlusY = false;
                                else if (neighborLabel != 0)
                                    boundaryAtPlusY = false;
                                else if (neighborInGray)
                                    boundaryAtPlusY = false;
                                else
                                    boundaryAtPlusY = true;
                            }
                        }

                        if (boundaryAtPlusY)
                        {
                            double x0 = x * sx;
                            double x1 = Math.Min(x + exportStride, volWidth) * sx;
                            double y1 = (y + exportStride) * sy;
                            double z0 = z * sz;
                            double z1 = Math.Min(z + exportStride, volDepth) * sz;

                            Point3D v0 = new Point3D(x0, y1, z0);
                            Point3D v1 = new Point3D(x1, y1, z0);
                            Point3D v2 = new Point3D(x1, y1, z1);
                            Point3D v3 = new Point3D(x0, y1, z1);

                            int baseIndex = vertices.Count;
                            vertices.Add(v0); vertices.Add(v1); vertices.Add(v2); vertices.Add(v3);

                            triangles.Add((baseIndex, baseIndex + 1, baseIndex + 2));
                            triangles.Add((baseIndex, baseIndex + 2, baseIndex + 3));

                            Color faceColor = voxelInGrayRegion ? grayscaleColor : materialColors[label];
                            triangleColors.Add(faceColor);
                            triangleColors.Add(faceColor);
                        }

                        // Check +Z face
                        bool boundaryAtPlusZ = false;
                        if (z + exportStride >= volDepth)
                        {
                            boundaryAtPlusZ = true;
                        }
                        else
                        {
                            byte neighborLabel = 0;
                            bool neighborInGray = false;
                            byte neighborVal = mainForm.volumeData[x, y, z + exportStride];

                            if (neighborVal >= isoValue)
                            {
                                byte nLbl = (mainForm.volumeLabels != null ? mainForm.volumeLabels[x, y, z + exportStride] : (byte)0);
                                if (nLbl != 0 && visibleMaterials.Contains(nLbl))
                                    neighborLabel = nLbl;
                                else
                                    neighborInGray = true;
                            }
                            else
                            {
                                if (mainForm.volumeLabels != null)
                                {
                                    byte nLbl = mainForm.volumeLabels[x, y, z + exportStride];
                                    if (nLbl != 0 && visibleMaterials.Contains(nLbl))
                                        neighborLabel = nLbl;
                                }
                            }

                            if (voxelInGrayRegion)
                            {
                                if (neighborInGray)
                                    boundaryAtPlusZ = false;
                                else if (neighborLabel != 0)
                                    boundaryAtPlusZ = false;
                                else
                                    boundaryAtPlusZ = true;
                            }
                            else if (label != 0)
                            {
                                if (neighborLabel == label)
                                    boundaryAtPlusZ = false;
                                else if (neighborLabel != 0)
                                    boundaryAtPlusZ = false;
                                else if (neighborInGray)
                                    boundaryAtPlusZ = false;
                                else
                                    boundaryAtPlusZ = true;
                            }
                        }

                        if (boundaryAtPlusZ)
                        {
                            double x0 = x * sx;
                            double x1 = Math.Min(x + exportStride, volWidth) * sx;
                            double y0 = y * sy;
                            double y1 = Math.Min(y + exportStride, volHeight) * sy;
                            double z1 = (z + exportStride) * sz;

                            Point3D v0 = new Point3D(x0, y0, z1);
                            Point3D v1 = new Point3D(x1, y0, z1);
                            Point3D v2 = new Point3D(x1, y1, z1);
                            Point3D v3 = new Point3D(x0, y1, z1);

                            int baseIndex = vertices.Count;
                            vertices.Add(v0); vertices.Add(v1); vertices.Add(v2); vertices.Add(v3);

                            triangles.Add((baseIndex, baseIndex + 1, baseIndex + 2));
                            triangles.Add((baseIndex, baseIndex + 2, baseIndex + 3));

                            Color faceColor = voxelInGrayRegion ? grayscaleColor : materialColors[label];
                            triangleColors.Add(faceColor);
                            triangleColors.Add(faceColor);
                        }
                    }
                }
            }

            Logger.Log($"[VolumeRenderer] Mesh generation complete: {vertices.Count} vertices, {triangles.Count} triangles");
        }

        /// <summary>
        /// Export to OBJ format with material definitions
        /// </summary>
        private void ExportToObj(string filePath)
        {
            List<Point3D> vertices = new List<Point3D>();
            List<(int, int, int)> triangles = new List<(int, int, int)>();
            List<Color> triangleColors = new List<Color>();

            GenerateMeshData(vertices, triangles, triangleColors);

            string mtlFilePath = Path.ChangeExtension(filePath, ".mtl");
            string objFileName = Path.GetFileName(mtlFilePath);

            using (StreamWriter objWriter = new StreamWriter(filePath))
            {
                objWriter.WriteLine("# Exported from CT Segmenter VolumeRenderer");
                objWriter.WriteLine($"mtllib {Path.GetFileName(mtlFilePath)}");

                // Write vertices
                foreach (var v in vertices)
                {
                    objWriter.WriteLine($"v {v.X:F4} {v.Y:F4} {v.Z:F4}");
                }

                // Determine distinct materials
                Dictionary<Color, string> colorToMatName = new Dictionary<Color, string>();
                List<Color> uniqueColors = triangleColors.Distinct().ToList();

                // Assign material names
                foreach (Color col in uniqueColors)
                {
                    string matName;
                    if (col == Colors.LightGray)
                    {
                        matName = "Grayscale";
                    }
                    else
                    {
                        var mat = mainForm.Materials.FirstOrDefault(m =>
                            !m.IsExterior &&
                            m.Color.R == col.R &&
                            m.Color.G == col.G &&
                            m.Color.B == col.B
                        );
                        matName = mat != null ? mat.Name.Replace(" ", "_") : $"Color_{col.R}_{col.G}_{col.B}";
                    }
                    colorToMatName[col] = matName;
                }

                // Write faces grouped by material
                Color currentColor = Colors.Transparent;
                for (int t = 0; t < triangles.Count; ++t)
                {
                    Color faceColor = triangleColors[t];
                    if (faceColor != currentColor)
                    {
                        // Switch material
                        currentColor = faceColor;
                        string matName = colorToMatName[faceColor];
                        objWriter.WriteLine($"usemtl {matName}");
                    }

                    // OBJ indices are 1-based
                    var (ia, ib, ic) = triangles[t];
                    objWriter.WriteLine($"f {ia + 1} {ib + 1} {ic + 1}");
                }
            }

            // Write the material library
            using (StreamWriter mtlWriter = new StreamWriter(mtlFilePath))
            {
                mtlWriter.WriteLine("# Material definitions for exported volume");

                // Write materials
                foreach (var mat in mainForm.Materials)
                {
                    if (mat.IsExterior) continue;
                    mtlWriter.WriteLine($"newmtl {mat.Name.Replace(" ", "_")}");
                    mtlWriter.WriteLine($"Kd {mat.Color.R / 255f:F3} {mat.Color.G / 255f:F3} {mat.Color.B / 255f:F3}");
                    mtlWriter.WriteLine("illum 1");
                    mtlWriter.WriteLine();
                }

                // Add grayscale material
                mtlWriter.WriteLine("newmtl Grayscale");
                mtlWriter.WriteLine($"Kd {0.75:F3} {0.75:F3} {0.75:F3}");
                mtlWriter.WriteLine("illum 1");
            }
        }

        /// <summary>
        /// Export to PLY format with color information
        /// </summary>
        private void ExportToPly(string filePath)
        {
            List<Point3D> vertices = new List<Point3D>();
            List<(int, int, int)> triangles = new List<(int, int, int)>();
            List<Color> triangleColors = new List<Color>();

            GenerateMeshData(vertices, triangles, triangleColors);

            using (StreamWriter writer = new StreamWriter(filePath))
            {
                // Write PLY header
                writer.WriteLine("ply");
                writer.WriteLine("format ascii 1.0");
                writer.WriteLine($"element vertex {vertices.Count}");
                writer.WriteLine("property float x");
                writer.WriteLine("property float y");
                writer.WriteLine("property float z");
                writer.WriteLine($"element face {triangles.Count}");
                writer.WriteLine("property list uchar int vertex_index");
                writer.WriteLine("property uchar red");
                writer.WriteLine("property uchar green");
                writer.WriteLine("property uchar blue");
                writer.WriteLine("end_header");

                // Write vertex list
                foreach (var v in vertices)
                {
                    writer.WriteLine($"{v.X:F4} {v.Y:F4} {v.Z:F4}");
                }

                // Write faces with color
                for (int i = 0; i < triangles.Count; ++i)
                {
                    var (ia, ib, ic) = triangles[i];
                    Color col = triangleColors[i];
                    writer.WriteLine($"3 {ia} {ib} {ic} {col.R} {col.G} {col.B}");
                }
            }
        }

        /// <summary>
        /// Export to STL format
        /// </summary>
        private void ExportToStl(string filePath)
        {
            List<Point3D> vertices = new List<Point3D>();
            List<(int, int, int)> triangles = new List<(int, int, int)>();
            List<Color> triangleColors = new List<Color>();

            GenerateMeshData(vertices, triangles, triangleColors);

            using (StreamWriter writer = new StreamWriter(filePath))
            {
                writer.WriteLine("solid volume");

                for (int i = 0; i < triangles.Count; ++i)
                {
                    var (ia, ib, ic) = triangles[i];

                    // Compute facet normal
                    var p1 = vertices[ia];
                    var p2 = vertices[ib];
                    var p3 = vertices[ic];
                    Vector3D u = new Vector3D(p2.X - p1.X, p2.Y - p1.Y, p2.Z - p1.Z);
                    Vector3D v = new Vector3D(p3.X - p1.X, p3.Y - p1.Y, p3.Z - p1.Z);
                    Vector3D normal = Vector3D.CrossProduct(u, v);

                    // Normalize normal vector
                    try { normal.Normalize(); } catch { }

                    writer.WriteLine($"  facet normal {normal.X:F6} {normal.Y:F6} {normal.Z:F6}");
                    writer.WriteLine("    outer loop");
                    writer.WriteLine($"      vertex {p1.X:F6} {p1.Y:F6} {p1.Z:F6}");
                    writer.WriteLine($"      vertex {p2.X:F6} {p2.Y:F6} {p2.Z:F6}");
                    writer.WriteLine($"      vertex {p3.X:F6} {p3.Y:F6} {p3.Z:F6}");
                    writer.WriteLine("    endloop");
                    writer.WriteLine("  endfacet");
                }

                writer.WriteLine("endsolid volume");
            }
        }

        /// <summary>
        /// Clean up resources
        /// </summary>
        public virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources in correct order
                   
                    volumeMaterial = null;
                    labelsMaterial = null;
                    volumeModel?.Dispose();
                    materialModel?.Dispose();
                    effectsManager?.Dispose();

                    Logger.Log("[VolumeRenderer] Resources disposed");
                }

                disposed = true;
            }
        }

        void IDisposable.Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}