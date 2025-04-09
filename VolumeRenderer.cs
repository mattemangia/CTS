using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using System.IO;
using System.Windows.Threading;
using System.Linq;
using System.Diagnostics;

namespace CTSegmenter
{
    public class VolumeRenderer
    {
        private MainForm mainForm;
        private ModelVisual3D rootModel;
        private ModelVisual3D volumeModel;
        private ModelVisual3D materialModel;
        private Dictionary<byte, ModelVisual3D> materialModels;
        private ModelVisual3D slicePlanesModel;

        // Custom implementations instead of PlaneVisual3D
        private ModelVisual3D xSlicePlaneModel;
        private ModelVisual3D ySlicePlaneModel;
        private ModelVisual3D zSlicePlaneModel;

        private Dictionary<byte, double> materialOpacities;

        private int voxelStride = 4; // Default medium quality
        private int minThreshold = 0;
        private int maxThreshold = 255;
        private bool updateRequired = true;
        private CancellationTokenSource renderCancellation;

        // Chunk size for volume rendering (in voxels)
        private int chunkSize = 64;

        // Cache for rendered meshes to avoid redundant work
        private Dictionary<string, MeshGeometry3D> meshCache = new Dictionary<string, MeshGeometry3D>();

        // Surface extraction parameters
        private double isoValue = 128.0;
        private bool showIsosurface = true;

        // Optimization fields
        private bool realTimeUpdate = false;
        private Dispatcher uiDispatcher;

        private Dictionary<byte, bool> materialVisibilityState = new Dictionary<byte, bool>();

        // Memory optimization - use LOD (Level of Detail) based on distance
        private bool useLodRendering = true;

        // Performance metrics
        private Stopwatch renderTimer = new Stopwatch();

        // BW Dataset visibility
        private bool showBwDataset = true;

        private SynchronizationContext syncContext;

        public VolumeRenderer(MainForm mainForm)
        {
            this.mainForm = mainForm;
            this.syncContext = SynchronizationContext.Current;
            this.uiDispatcher = Dispatcher.CurrentDispatcher;

            // Initialize models
            this.rootModel = new ModelVisual3D();
            this.volumeModel = new ModelVisual3D();
            this.materialModel = new ModelVisual3D();
            this.slicePlanesModel = new ModelVisual3D();

            this.materialModels = new Dictionary<byte, ModelVisual3D>();
            this.materialOpacities = new Dictionary<byte, double>();

            // Initialize orthographic slice planes
            this.xSlicePlaneModel = new ModelVisual3D();
            this.ySlicePlaneModel = new ModelVisual3D();
            this.zSlicePlaneModel = new ModelVisual3D();

            // Add models to hierarchy
            this.rootModel.Children.Add(volumeModel);
            this.rootModel.Children.Add(materialModel);
            this.rootModel.Children.Add(slicePlanesModel);
            this.materialVisibilityState = new Dictionary<byte, bool>();

            // Initialize material opacities
            foreach (var material in mainForm.Materials)
            {
                if (!material.IsExterior)
                {
                    materialOpacities[material.ID] = 1.0; // Start fully opaque
                    materialVisibilityState[material.ID] = true; // Default to visible
                }
            }
        }

        #region Properties

        public ModelVisual3D RootModel => rootModel;

        public int VoxelStride
        {
            get => voxelStride;
            set
            {
                if (value != voxelStride && value > 0)
                {
                    voxelStride = value;
                    updateRequired = true;
                    // Clear cache when stride changes
                    meshCache.Clear();
                }
            }
        }

        public int MinThreshold
        {
            get => minThreshold;
            set
            {
                if (value != minThreshold)
                {
                    minThreshold = value;
                    updateRequired = true;
                }
            }
        }

        public int MaxThreshold
        {
            get => maxThreshold;
            set
            {
                if (value != maxThreshold)
                {
                    maxThreshold = value;
                    updateRequired = true;
                }
            }
        }

        public bool RealTimeUpdate
        {
            get => realTimeUpdate;
            set => realTimeUpdate = value;
        }

        public bool UseLodRendering
        {
            get => useLodRendering;
            set => useLodRendering = value;
        }

        public bool ShowBwDataset
        {
            get => showBwDataset;
            set
            {
                showBwDataset = value;
                RunOnDispatcher(() =>
                {
                    if (showBwDataset)
                    {
                        // Only add if it isn't already in the parent's children collection.
                        if (!rootModel.Children.Contains(volumeModel))
                            rootModel.Children.Add(volumeModel);
                    }
                    else
                    {
                        // Remove the model to hide it.
                        rootModel.Children.Remove(volumeModel);
                    }
                });
            }
        }

        #endregion

        #region Public Methods

        public void SetDispatcher(Dispatcher dispatcher)
        {
            this.uiDispatcher = dispatcher;
        }

        public void SetMaterialVisibility(byte materialId, bool visible)
        {
            // Update our internal state
            materialVisibilityState[materialId] = visible;

            // Use the UI dispatcher to update UI elements
            RunOnDispatcher(() =>
            {
                if (materialModels.TryGetValue(materialId, out var model))
                {
                    // Remove the model from its parent regardless of current state
                    materialModel.Children.Remove(model);

                    // Add it back only if it should be visible
                    if (visible)
                    {
                        materialModel.Children.Add(model);
                    }
                }
            });
        }

        public double GetMaterialOpacity(byte materialId)
        {
            if (materialOpacities.TryGetValue(materialId, out double opacity))
            {
                return opacity;
            }
            return 1.0; // Default opacity
        }

        public void SetMaterialOpacity(byte materialId, double opacity)
        {
            materialOpacities[materialId] = opacity;

            // Use UI dispatcher to update UI
            RunOnDispatcher(() =>
            {
                if (materialModels.TryGetValue(materialId, out var model))
                {
                    UpdateModelOpacity(model, opacity);
                }
            });
        }

        public async Task UpdateAsync()
        {
            if (!updateRequired) return;
            updateRequired = false;

            // Cancel any ongoing rendering
            renderCancellation?.Cancel();
            renderCancellation = new CancellationTokenSource();
            var cancellationToken = renderCancellation.Token;

            try
            {
                // Use the UI dispatcher to clear previous models first
                await RunOnDispatcherAsync(() =>
                {
                    volumeModel.Children.Clear();
                    materialModel.Children.Clear();
                    materialModels.Clear();
                });

                // Start performance timer
                renderTimer.Reset();
                renderTimer.Start();

                await RenderVolumeMeshesAsync(cancellationToken);

                renderTimer.Stop();
                Logger.Log($"[VolumeRenderer] Rendering completed in {renderTimer.ElapsedMilliseconds}ms");

                // Force camera update to see the model
                await RunOnDispatcherAsync(() => {
                    // Add a very small box at 0,0,0 to ensure the bounds are calculated properly
                    MeshBuilder anchorMesh = new MeshBuilder(false, false);
                    anchorMesh.AddBox(new Point3D(0, 0, 0), 0.001, 0.001, 0.001);
                    GeometryModel3D anchorModel = new GeometryModel3D
                    {
                        Geometry = anchorMesh.ToMesh(),
                        Material = new DiffuseMaterial(Brushes.Transparent),
                        BackMaterial = new DiffuseMaterial(Brushes.Transparent)
                    };
                    ModelVisual3D anchor = new ModelVisual3D { Content = anchorModel };
                    rootModel.Children.Add(anchor);
                });
            }
            catch (OperationCanceledException)
            {
                // Rendering was canceled, log it
                Logger.Log("[VolumeRenderer] Rendering was canceled");
            }
            catch (Exception ex)
            {
                // Log detailed error
                Logger.Log($"[VolumeRenderer] Error during rendering: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        public void UpdateThreshold()
        {
            // This method updates materials in real-time based on threshold changes
            RunOnDispatcher(() =>
            {
                foreach (var child in volumeModel.Children)
                {
                    if (child is ModelVisual3D modelVis && modelVis.Content is GeometryModel3D gm)
                    {
                        // Create a semi-transparent material for threshold visualization
                        var color = Colors.LightGray;
                        color.A = (byte)(128); // Semi-transparent

                        var material = new DiffuseMaterial(new SolidColorBrush(color));
                        gm.Material = material;
                        gm.BackMaterial = material;
                    }
                }
            });
        }

        public void ShowSlicePlanes(bool show)
        {
            RunOnDispatcher(() =>
            {
                slicePlanesModel.Children.Clear();

                if (show)
                {
                    // Initialize slice planes if not already created
                    slicePlanesModel.Children.Add(xSlicePlaneModel);
                    slicePlanesModel.Children.Add(ySlicePlaneModel);
                    slicePlanesModel.Children.Add(zSlicePlaneModel);
                }
            });
        }

        public void UpdateSlicePlanes(int xPos, int yPos, int zPos, int width, int height, int depth, double pixelSize)
        {
            RunOnDispatcher(() =>
            {
                slicePlanesModel.Children.Clear();
                slicePlanesModel.Children.Add(xSlicePlaneModel);
                slicePlanesModel.Children.Add(ySlicePlaneModel);
                slicePlanesModel.Children.Add(zSlicePlaneModel);

                UpdateXSlice(xPos, width, pixelSize);
                UpdateYSlice(yPos, height, pixelSize);
                UpdateZSlice(zPos, depth, pixelSize);
            });
        }

        public void UpdateXSlice(int xPos, int width, double pixelSize)
        {
            double totalWidth = width * pixelSize;
            double totalHeight = mainForm.GetHeight() * pixelSize;
            double totalDepth = mainForm.GetDepth() * pixelSize;
            double realX = xPos * pixelSize;

            MeshBuilder meshBuilder = new MeshBuilder(false, false);
            meshBuilder.AddQuad(
                new Point3D(realX, 0, 0),
                new Point3D(realX, totalHeight, 0),
                new Point3D(realX, totalHeight, totalDepth),
                new Point3D(realX, 0, totalDepth));

            GeometryModel3D model = new GeometryModel3D
            {
                Geometry = meshBuilder.ToMesh(),
                Material = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(127, 255, 0, 0))),
                BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(127, 255, 0, 0)))
            };

            RunOnDispatcher(() =>
            {
                xSlicePlaneModel.Content = model;
            });
        }

        public void UpdateYSlice(int yPos, int height, double pixelSize)
        {
            double totalWidth = mainForm.GetWidth() * pixelSize;
            double totalHeight = height * pixelSize;
            double totalDepth = mainForm.GetDepth() * pixelSize;
            double realY = yPos * pixelSize;

            MeshBuilder meshBuilder = new MeshBuilder(false, false);
            meshBuilder.AddQuad(
                new Point3D(0, realY, 0),
                new Point3D(totalWidth, realY, 0),
                new Point3D(totalWidth, realY, totalDepth),
                new Point3D(0, realY, totalDepth));

            GeometryModel3D model = new GeometryModel3D
            {
                Geometry = meshBuilder.ToMesh(),
                Material = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(127, 0, 255, 0))),
                BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(127, 0, 255, 0)))
            };

            RunOnDispatcher(() =>
            {
                ySlicePlaneModel.Content = model;
            });
        }

        public void UpdateZSlice(int zPos, int depth, double pixelSize)
        {
            double totalWidth = mainForm.GetWidth() * pixelSize;
            double totalHeight = mainForm.GetHeight() * pixelSize;
            double totalDepth = depth * pixelSize;
            double realZ = zPos * pixelSize;

            MeshBuilder meshBuilder = new MeshBuilder(false, false);
            meshBuilder.AddQuad(
                new Point3D(0, 0, realZ),
                new Point3D(totalWidth, 0, realZ),
                new Point3D(totalWidth, totalHeight, realZ),
                new Point3D(0, totalHeight, realZ));

            GeometryModel3D model = new GeometryModel3D
            {
                Geometry = meshBuilder.ToMesh(),
                Material = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(127, 0, 0, 255))),
                BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(127, 0, 0, 255)))
            };

            RunOnDispatcher(() =>
            {
                zSlicePlaneModel.Content = model;
            });
        }

        public void QuickRenderTest()
        {
            try
            {
                // Create a bounding box representation of the volume
                MeshBuilder meshBuilder = new MeshBuilder(false, false);

                // Calculate real-world dimensions
                double width = mainForm.GetWidth() * mainForm.GetPixelSize();
                double height = mainForm.GetHeight() * mainForm.GetPixelSize();
                double depth = mainForm.GetDepth() * mainForm.GetPixelSize();

                // Create a wireframe box
                meshBuilder.AddBoundingBox(new Rect3D(0, 0, 0, width, height, depth), Math.Min(width, Math.Min(height, depth)) * 0.01);

                var mesh = meshBuilder.ToMesh();
                var wireframe = new GeometryModel3D
                {
                    Geometry = mesh,
                    Material = new DiffuseMaterial(Brushes.Red),
                    BackMaterial = new DiffuseMaterial(Brushes.Red)
                };

                RunOnDispatcher(() =>
                {
                    volumeModel.Children.Clear();
                    volumeModel.Children.Add(new ModelVisual3D { Content = wireframe });
                    Logger.Log("[3D Viewer] Added volume bounding box");
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[3D Viewer] Quick test error: {ex.Message}");
            }
        }

        // Export model implementation
        public void ExportModel(string filePath)
        {
            try
            {
                if (filePath.EndsWith(".obj"))
                {
                    ExportToObj(filePath);
                }
                else if (filePath.EndsWith(".stl"))
                {
                    ExportToStl(filePath);
                }
                Logger.Log($"[VolumeRenderer] Model exported successfully to {filePath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Export failed: {ex.Message}");
                throw new Exception($"Export failed: {ex.Message}", ex);
            }
        }

        #endregion

        #region Private Methods

        private void RunOnDispatcher(Action action)
        {
            try
            {
                if (uiDispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    uiDispatcher.Invoke(action);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] UI operation error: {ex.Message}");
            }
        }

        private async Task RunOnDispatcherAsync(Action action)
        {
            try
            {
                if (uiDispatcher.CheckAccess())
                {
                    action();
                    await Task.CompletedTask;
                }
                else
                {
                    await uiDispatcher.InvokeAsync(action);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Async UI operation error: {ex.Message}");
            }
        }

        private void ExportToObj(string filePath)
        {
            using (StreamWriter objWriter = new StreamWriter(filePath))
            {
                objWriter.WriteLine("# Exported from CT Segmenter");

                int vertexCount = 0;
                int normalCount = 0;

                // Export materials
                string mtlFilePath = Path.ChangeExtension(filePath, ".mtl");
                using (StreamWriter mtlWriter = new StreamWriter(mtlFilePath))
                {
                    mtlWriter.WriteLine("# Materials");

                    // Default material for volume data
                    mtlWriter.WriteLine("newmtl volumeMaterial");
                    mtlWriter.WriteLine("Ka 0.2 0.2 0.2");
                    mtlWriter.WriteLine("Kd 0.8 0.8 0.8");
                    mtlWriter.WriteLine("Ks 0.1 0.1 0.1");
                    mtlWriter.WriteLine("Ns 32");
                    mtlWriter.WriteLine("d 0.5");

                    // Material for each segmented material
                    foreach (var material in mainForm.Materials)
                    {
                        if (!material.IsExterior)
                        {
                            mtlWriter.WriteLine($"newmtl material{material.ID}");
                            mtlWriter.WriteLine($"Ka 0.2 0.2 0.2");
                            mtlWriter.WriteLine($"Kd {material.Color.R / 255.0} {material.Color.G / 255.0} {material.Color.B / 255.0}");
                            mtlWriter.WriteLine($"Ks 0.1 0.1 0.1");
                            mtlWriter.WriteLine($"Ns 32");
                            mtlWriter.WriteLine($"d {(materialOpacities.ContainsKey(material.ID) ? materialOpacities[material.ID] : 1.0)}");
                        }
                    }
                }

                // Reference the material library
                objWriter.WriteLine($"mtllib {Path.GetFileName(mtlFilePath)}");

                // Export geometry for volume
                ExportGeometryToObj(objWriter, volumeModel, "volumeMaterial", ref vertexCount, ref normalCount);

                // Export geometry for each material
                foreach (var material in mainForm.Materials)
                {
                    if (!material.IsExterior && materialModels.TryGetValue(material.ID, out var model))
                    {
                        ExportGeometryToObj(objWriter, model, $"material{material.ID}", ref vertexCount, ref normalCount);
                    }
                }
            }
        }

        private void ExportGeometryToObj(StreamWriter writer, ModelVisual3D model, string materialName, ref int vertexCount, ref int normalCount)
        {
            // Start a new object
            writer.WriteLine($"o {materialName}");
            writer.WriteLine($"usemtl {materialName}");

            // Export vertices, normals, and faces recursively for this model
            ExportModelVisual3DToObj(writer, model, ref vertexCount, ref normalCount);
        }

        private void ExportModelVisual3DToObj(StreamWriter writer, ModelVisual3D visual, ref int vertexCount, ref int normalCount)
        {
            // Export geometry for this model
            if (visual.Content is GeometryModel3D geometryModel)
            {
                if (geometryModel.Geometry is MeshGeometry3D mesh)
                {
                    // Export vertices
                    foreach (var vertex in mesh.Positions)
                    {
                        writer.WriteLine($"v {vertex.X} {vertex.Y} {vertex.Z}");
                    }

                    // Export normals if available
                    if (mesh.Normals != null && mesh.Normals.Count > 0)
                    {
                        foreach (var normal in mesh.Normals)
                        {
                            writer.WriteLine($"vn {normal.X} {normal.Y} {normal.Z}");
                        }
                    }

                    // Export faces (triangles)
                    for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
                    {
                        if (i + 2 < mesh.TriangleIndices.Count)
                        {
                            int v1 = mesh.TriangleIndices[i] + 1 + vertexCount;
                            int v2 = mesh.TriangleIndices[i + 1] + 1 + vertexCount;
                            int v3 = mesh.TriangleIndices[i + 2] + 1 + vertexCount;

                            if (mesh.Normals != null && mesh.Normals.Count > 0)
                            {
                                int n1 = mesh.TriangleIndices[i] + 1 + normalCount;
                                int n2 = mesh.TriangleIndices[i + 1] + 1 + normalCount;
                                int n3 = mesh.TriangleIndices[i + 2] + 1 + normalCount;
                                writer.WriteLine($"f {v1}//{n1} {v2}//{n2} {v3}//{n3}");
                            }
                            else
                            {
                                writer.WriteLine($"f {v1} {v2} {v3}");
                            }
                        }
                    }

                    vertexCount += mesh.Positions.Count;
                    normalCount += mesh.Normals.Count;
                }
            }

            // Recursively export children
            foreach (var child in visual.Children)
            {
                if (child is ModelVisual3D childVisual)
                {
                    ExportModelVisual3DToObj(writer, childVisual, ref vertexCount, ref normalCount);
                }
            }
        }

        private void ExportToStl(string filePath)
        {
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                writer.WriteLine("solid CTSegmenter");

                // Export all geometry
                ExportModelVisual3DToStl(writer, rootModel);

                writer.WriteLine("endsolid CTSegmenter");
            }
        }

        private void ExportModelVisual3DToStl(StreamWriter writer, ModelVisual3D visual)
        {
            // Export geometry for this model
            if (visual.Content is GeometryModel3D geometryModel)
            {
                if (geometryModel.Geometry is MeshGeometry3D mesh)
                {
                    // Export triangles
                    for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
                    {
                        if (i + 2 < mesh.TriangleIndices.Count)
                        {
                            Point3D v1 = mesh.Positions[mesh.TriangleIndices[i]];
                            Point3D v2 = mesh.Positions[mesh.TriangleIndices[i + 1]];
                            Point3D v3 = mesh.Positions[mesh.TriangleIndices[i + 2]];

                            // Calculate normal
                            Vector3D edge1 = v2 - v1;
                            Vector3D edge2 = v3 - v1;
                            Vector3D normal = Vector3D.CrossProduct(edge1, edge2);
                            normal.Normalize();

                            writer.WriteLine("  facet normal " + normal.X + " " + normal.Y + " " + normal.Z);
                            writer.WriteLine("    outer loop");
                            writer.WriteLine("      vertex " + v1.X + " " + v1.Y + " " + v1.Z);
                            writer.WriteLine("      vertex " + v2.X + " " + v2.Y + " " + v2.Z);
                            writer.WriteLine("      vertex " + v3.X + " " + v3.Y + " " + v3.Z);
                            writer.WriteLine("    endloop");
                            writer.WriteLine("  endfacet");
                        }
                    }
                }
            }

            // Recursively export children
            foreach (var child in visual.Children)
            {
                if (child is ModelVisual3D childVisual)
                {
                    ExportModelVisual3DToStl(writer, childVisual);
                }
            }
        }

        private void UpdateModelOpacity(ModelVisual3D model, double opacity)
        {
            if (model.Content is GeometryModel3D gm)
            {
                if (gm.Material is DiffuseMaterial dm)
                {
                    // Clone the brush and set its opacity
                    SolidColorBrush originalBrush = dm.Brush as SolidColorBrush;
                    if (originalBrush != null)
                    {
                        Color color = originalBrush.Color;
                        color.A = (byte)(opacity * 255);
                        dm.Brush = new SolidColorBrush(color);
                    }
                }

                if (gm.BackMaterial is DiffuseMaterial bm)
                {
                    // Clone the brush and set its opacity
                    SolidColorBrush originalBrush = bm.Brush as SolidColorBrush;
                    if (originalBrush != null)
                    {
                        Color color = originalBrush.Color;
                        color.A = (byte)(opacity * 255);
                        bm.Brush = new SolidColorBrush(color);
                    }
                }
            }

            // Apply to all children as well
            foreach (var child in model.Children)
            {
                if (child is ModelVisual3D childModel)
                {
                    UpdateModelOpacity(childModel, opacity);
                }
            }
        }

        private async Task RenderVolumeMeshesAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Create models for grayscale volume if available
                if (mainForm.volumeData != null && showIsosurface)
                {
                    await RenderGrayscaleVolumeAsync(cancellationToken);
                }

                // Create models for segmented materials if available
                if (mainForm.volumeLabels != null)
                {
                    await RenderMaterialVolumesAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error in RenderVolumeMeshesAsync: {ex.Message}");
                throw;
            }
        }

        private async Task RenderGrayscaleVolumeAsync(CancellationToken cancellationToken)
        {
            var width = mainForm.GetWidth();
            var height = mainForm.GetHeight();
            var depth = mainForm.GetDepth();

            // Calculate total volume size
            long totalVoxels = (long)width * height * depth;

            // For extremely large volumes, use a more aggressive strategy
            int adaptiveChunkSize;
            if (totalVoxels > 1_000_000_000) // > 1 billion voxels
            {
                adaptiveChunkSize = 256;
                voxelStride = Math.Max(voxelStride, 8); // Force coarser stride
            }
            else if (totalVoxels > 500_000_000) // > 500 million voxels
            {
                adaptiveChunkSize = 192;
                voxelStride = Math.Max(voxelStride, 4);
            }
            else if (totalVoxels > 100_000_000) // > 100 million voxels
            {
                adaptiveChunkSize = 128;
            }
            else
            {
                adaptiveChunkSize = 64;
            }

            Logger.Log($"[VolumeRenderer] Volume size: {width}x{height}x{depth} = {totalVoxels:N0} voxels");
            Logger.Log($"[VolumeRenderer] Using chunk size: {adaptiveChunkSize}, stride: {voxelStride}");

            int numChunksX = (width + adaptiveChunkSize - 1) / adaptiveChunkSize;
            int numChunksY = (height + adaptiveChunkSize - 1) / adaptiveChunkSize;
            int numChunksZ = (depth + adaptiveChunkSize - 1) / adaptiveChunkSize;

            // Scale to real-world coordinates
            double pixelSize = mainForm.GetPixelSize();

            // Compute the iso value from the thresholds 
            isoValue = (minThreshold + maxThreshold) / 2.0;

            // Set material for isosurface
            var material = new DiffuseMaterial(new SolidColorBrush(Colors.LightGray));

            Logger.Log($"[VolumeRenderer] Starting grayscale volume rendering with {numChunksX}x{numChunksY}x{numChunksZ} chunks");

            // Process chunks
            int batchSize = 5; // Number of chunks to process in each batch
            int chunkCount = 0;
            int completedChunks = 0;
            ModelVisual3D container = new ModelVisual3D();

            try
            {
                // Sample to check if we have any data within threshold range
                bool hasDataInRange = await SampleVolumeForThresholdRangeAsync(cancellationToken);
                if (!hasDataInRange)
                {
                    Logger.Log($"[VolumeRenderer] No data within threshold range ({minThreshold}-{maxThreshold}). Skipping grayscale rendering.");
                    return;
                }

                for (int cz = 0; cz < numChunksZ; cz++)
                {
                    for (int cy = 0; cy < numChunksY; cy++)
                    {
                        for (int cx = 0; cx < numChunksX; cx++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            // Process this chunk
                            int startX = cx * adaptiveChunkSize;
                            int startY = cy * adaptiveChunkSize;
                            int startZ = cz * adaptiveChunkSize;

                            int endX = Math.Min(startX + adaptiveChunkSize, width);
                            int endY = Math.Min(startY + adaptiveChunkSize, height);
                            int endZ = Math.Min(startZ + adaptiveChunkSize, depth);

                            // Skip processing if the entire chunk is outside the threshold range
                            if (await IsPotentiallyEmptyChunkAsync(startX, startY, startZ, endX, endY, endZ, cancellationToken))
                                continue;

                            // Process this chunk
                            MeshGeometry3D mesh = await Task.Run(() =>
                            {
                                string cacheKey = $"iso_{startX}_{startY}_{startZ}_{voxelStride}_{minThreshold}_{maxThreshold}";

                                if (meshCache.TryGetValue(cacheKey, out var cachedMesh))
                                {
                                    return cachedMesh;
                                }
                                else
                                {
                                    var newMesh = ExtractIsosurfaceForChunk(
                                        startX, startY, startZ,
                                        endX, endY, endZ,
                                        pixelSize,
                                        cancellationToken);

                                    if (newMesh != null && newMesh.Positions.Count > 0)
                                    {
                                        meshCache[cacheKey] = newMesh;
                                    }

                                    return newMesh;
                                }
                            }, cancellationToken);

                            if (mesh != null && mesh.Positions.Count > 0)
                            {
                                // Create a model on the UI thread
                                await RunOnDispatcherAsync(() =>
                                {
                                    var model = new GeometryModel3D
                                    {
                                        Geometry = mesh,
                                        Material = material.Clone(),
                                        BackMaterial = material.Clone()
                                    };

                                    var visual = new ModelVisual3D { Content = model };
                                    container.Children.Add(visual);
                                });
                            }

                            chunkCount++;
                            completedChunks++;

                            // Every batch chunks, update the UI
                            if (completedChunks >= batchSize)
                            {
                                await UpdateVolumeModelAsync(container);
                                container = new ModelVisual3D();
                                completedChunks = 0;

                                // Force GC to clean up memory
                                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                            }
                        }
                    }
                }

                // Add any remaining chunks
                if (container.Children.Count > 0)
                {
                    await UpdateVolumeModelAsync(container);
                }

                Logger.Log($"[VolumeRenderer] Completed grayscale rendering, processed {chunkCount} chunks");
            }
            catch (OperationCanceledException)
            {
                Logger.Log("[VolumeRenderer] Grayscale rendering was canceled");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error in grayscale rendering: {ex.Message}");
                throw;
            }
        }

        private async Task<bool> SampleVolumeForThresholdRangeAsync(CancellationToken cancellationToken)
        {
            // Check a sample of voxels to see if any are within threshold range
            var volume = mainForm.volumeData;
            if (volume == null) return false;

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Determine a reasonable sampling rate based on volume size
            int stride = Math.Max(1, Math.Min(32, (int)Math.Ceiling(Math.Pow(width * height * depth, 1.0 / 3) / 20)));

            return await Task.Run(() =>
            {
                for (int z = 0; z < depth; z += stride)
                {
                    for (int y = 0; y < height; y += stride)
                    {
                        for (int x = 0; x < width; x += stride)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                return false;

                            byte voxel = volume[x, y, z];
                            if (voxel >= minThreshold && voxel <= maxThreshold)
                                return true;
                        }
                    }
                }
                return false;
            }, cancellationToken);
        }

        private async Task UpdateVolumeModelAsync(ModelVisual3D container)
        {
            if (container.Children.Count == 0) return;

            await RunOnDispatcherAsync(() =>
            {
                volumeModel.Children.Add(container);
            });
        }

        private async Task<bool> IsPotentiallyEmptyChunkAsync(
            int startX, int startY, int startZ,
            int endX, int endY, int endZ,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Sample a few points to determine if there's anything in this chunk
                    // Adjust stride based on chunk size for better performance
                    int dim = Math.Max(endX - startX, Math.Max(endY - startY, endZ - startZ));
                    int sampleStride = Math.Max(4, dim / 8);

                    for (int z = startZ; z < endZ; z += sampleStride)
                    {
                        for (int y = startY; y < endY; y += sampleStride)
                        {
                            for (int x = startX; x < endX; x += sampleStride)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                if (x < mainForm.GetWidth() && y < mainForm.GetHeight() && z < mainForm.GetDepth())
                                {
                                    byte voxel = mainForm.volumeData[x, y, z];
                                    if (voxel >= minThreshold && voxel <= maxThreshold)
                                    {
                                        return false; // Not empty
                                    }
                                }
                            }
                        }
                    }

                    return true; // Potentially empty
                }
                catch (Exception ex)
                {
                    Logger.Log($"[VolumeRenderer] Error checking chunk: {ex.Message}");
                    return true; // Skip on error
                }
            }, cancellationToken);
        }

        private MeshGeometry3D ExtractIsosurfaceForChunk(
            int startX, int startY, int startZ,
            int endX, int endY, int endZ,
            double pixelSize,
            CancellationToken cancellationToken)
        {
            try
            {
                // Optimization for large datasets
                if (useLodRendering && voxelStride < 4)
                {
                    // Use lower detail for distant chunks (based on chunk size)
                    double chunkSize = Math.Max(endX - startX, Math.Max(endY - startY, endZ - startZ));
                    double scale = chunkSize / 128.0; // Base reference size

                    if (scale > 3.0) voxelStride = Math.Max(voxelStride, 4);
                    else if (scale > 2.0) voxelStride = Math.Max(voxelStride, 3);
                    else if (scale > 1.0) voxelStride = Math.Max(voxelStride, 2);
                }

                // Allow for one voxel overlap to avoid seams between chunks
                int sizeX = endX - startX + 1;
                int sizeY = endY - startY + 1;
                int sizeZ = endZ - startZ + 1;

                // Create scalar field for isosurface extraction
                double[,,] field = new double[sizeX, sizeY, sizeZ];

                // Fill the scalar field from the volume data
                for (int z = 0; z < sizeZ; z++)
                {
                    int volumeZ = startZ + z;
                    if (volumeZ >= mainForm.GetDepth()) continue;

                    for (int y = 0; y < sizeY; y++)
                    {
                        int volumeY = startY + y;
                        if (volumeY >= mainForm.GetHeight()) continue;

                        for (int x = 0; x < sizeX; x++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            int volumeX = startX + x;
                            if (volumeX >= mainForm.GetWidth()) continue;

                            field[x, y, z] = mainForm.volumeData[volumeX, volumeY, volumeZ];
                        }
                    }
                }

                // Create mesh geometry for this chunk using the modified Marching Cubes algorithm
                MeshBuilder meshBuilder = new MeshBuilder(false, false);

                // Process the scalar field with the desired stride
                for (int z = 0; z < sizeZ - 1; z += voxelStride)
                {
                    for (int y = 0; y < sizeY - 1; y += voxelStride)
                    {
                        for (int x = 0; x < sizeX - 1; x += voxelStride)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            // Skip processing if outside bounds
                            if (startX + x >= mainForm.GetWidth() ||
                                startY + y >= mainForm.GetHeight() ||
                                startZ + z >= mainForm.GetDepth())
                                continue;

                            // Check if this cell crosses the isosurface
                            bool hasCrossing = IsCrossingIsosurface(field, x, y, z, isoValue);

                            if (hasCrossing)
                            {
                                // Create a box at this position
                                double realX = (startX + x) * pixelSize;
                                double realY = (startY + y) * pixelSize;
                                double realZ = (startZ + z) * pixelSize;
                                double size = voxelStride * pixelSize * 0.95; // Slightly smaller to avoid z-fighting

                                meshBuilder.AddBox(
                                    new Point3D(realX + size / 2, realY + size / 2, realZ + size / 2),
                                    size, size, size);
                            }
                        }
                    }
                }

                MeshGeometry3D mesh = meshBuilder.ToMesh();
                if (mesh.CanFreeze)
                {
                    mesh.Freeze();
                }
                return mesh;
            }
            catch (OperationCanceledException)
            {
                throw; // Re-throw cancellation
            }
            catch (Exception ex)
            {
                // Log the error but don't crash the whole app
                Logger.Log($"[VolumeRenderer] Error extracting isosurface: {ex.Message}");
                return null;
            }
        }

        private bool IsCrossingIsosurface(double[,,] field, int x, int y, int z, double isoValue)
        {
            // Get the eight corners of this cell
            double v000 = field[x, y, z];
            double v100 = field[x + 1, y, z];
            double v010 = field[x, y + 1, z];
            double v110 = field[x + 1, y + 1, z];
            double v001 = field[x, y, z + 1];
            double v101 = field[x + 1, y, z + 1];
            double v011 = field[x, y + 1, z + 1];
            double v111 = field[x + 1, y + 1, z + 1];

            // Check if some corners are above the iso value and some are below
            bool hasAbove = v000 >= isoValue || v100 >= isoValue || v010 >= isoValue || v110 >= isoValue ||
                           v001 >= isoValue || v101 >= isoValue || v011 >= isoValue || v111 >= isoValue;

            bool hasBelow = v000 < isoValue || v100 < isoValue || v010 < isoValue || v110 < isoValue ||
                           v001 < isoValue || v101 < isoValue || v011 < isoValue || v111 < isoValue;

            return hasAbove && hasBelow;
        }

        private async Task RenderMaterialVolumesAsync(CancellationToken cancellationToken)
        {
            // Create a separate model for each material
            foreach (var material in mainForm.Materials)
            {
                if (material.IsExterior)
                    continue;

                cancellationToken.ThrowIfCancellationRequested();

                // Create a model for this material
                var materialModel = new ModelVisual3D();
                materialModels[material.ID] = materialModel;

                // Get opacity for this material
                double opacity = 1.0;
                materialOpacities.TryGetValue(material.ID, out opacity);

                // Render the material mesh
                await RenderMaterialVolumeAsync(material, materialModel, opacity, cancellationToken);

                // Only add to scene if material is visible
                bool isVisible = true;
                materialVisibilityState.TryGetValue(material.ID, out isVisible);

                if (isVisible)
                {
                    await RunOnDispatcherAsync(() =>
                    {
                        this.materialModel.Children.Add(materialModel);
                    });
                }
            }
        }

        private async Task RenderMaterialVolumeAsync(
            Material material,
            ModelVisual3D materialModel,
            double opacity,
            CancellationToken cancellationToken)
        {
            var width = mainForm.GetWidth();
            var height = mainForm.GetHeight();
            var depth = mainForm.GetDepth();

            // Calculate total volume size
            long totalVoxels = (long)width * height * depth;

            // For extremely large volumes, use a more aggressive strategy
            int adaptiveChunkSize;
            if (totalVoxels > 1_000_000_000) // > 1 billion voxels
            {
                adaptiveChunkSize = 256;
                voxelStride = Math.Max(voxelStride, 8); // Force coarser stride
            }
            else if (totalVoxels > 500_000_000) // > 500 million voxels
            {
                adaptiveChunkSize = 192;
                voxelStride = Math.Max(voxelStride, 4);
            }
            else if (totalVoxels > 100_000_000) // > 100 million voxels
            {
                adaptiveChunkSize = 128;
            }
            else
            {
                adaptiveChunkSize = 64;
            }

            int numChunksX = (width + adaptiveChunkSize - 1) / adaptiveChunkSize;
            int numChunksY = (height + adaptiveChunkSize - 1) / adaptiveChunkSize;
            int numChunksZ = (depth + adaptiveChunkSize - 1) / adaptiveChunkSize;

            // Scale to real-world coordinates
            double pixelSize = mainForm.GetPixelSize();

            // Convert material color to WPF color
            var wpfColor = Color.FromArgb(
                (byte)(opacity * 255),
                material.Color.R,
                material.Color.G,
                material.Color.B);

            var materialBrush = new SolidColorBrush(wpfColor);
            var diffuseMaterial = new DiffuseMaterial(materialBrush);

            // Process chunks
            int batchSize = 5; // Number of chunks to process in each batch
            int chunkCount = 0;
            int completedChunks = 0;
            ModelVisual3D container = new ModelVisual3D();

            try
            {
                // Check if this material exists in the volume at all
                bool materialExists = await CheckMaterialExistsAsync(material.ID, cancellationToken);
                if (!materialExists)
                {
                    Logger.Log($"[VolumeRenderer] Material {material.ID} not found in volume. Skipping.");
                    return;
                }

                for (int cz = 0; cz < numChunksZ; cz++)
                {
                    for (int cy = 0; cy < numChunksY; cy++)
                    {
                        for (int cx = 0; cx < numChunksX; cx++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            int startX = cx * adaptiveChunkSize;
                            int startY = cy * adaptiveChunkSize;
                            int startZ = cz * adaptiveChunkSize;

                            int endX = Math.Min(startX + adaptiveChunkSize, width);
                            int endY = Math.Min(startY + adaptiveChunkSize, height);
                            int endZ = Math.Min(startZ + adaptiveChunkSize, depth);

                            // Check if this chunk contains the material
                            if (!await ContainsMaterialAsync(material.ID, startX, startY, startZ, endX, endY, endZ, cancellationToken))
                                continue;

                            // Process the chunk
                            MeshGeometry3D mesh = await Task.Run(() =>
                            {
                                // Check for cached mesh first
                                string cacheKey = $"mat_{material.ID}_{startX}_{startY}_{startZ}_{voxelStride}";

                                if (meshCache.TryGetValue(cacheKey, out var cachedMesh))
                                {
                                    return cachedMesh;
                                }
                                else
                                {
                                    // Extract surface for this material in this chunk
                                    var newMesh = ExtractMaterialSurfaceForChunk(
                                        material.ID,
                                        startX, startY, startZ,
                                        endX, endY, endZ,
                                        pixelSize,
                                        cancellationToken);

                                    // Cache the mesh if it's not empty
                                    if (newMesh != null && newMesh.Positions.Count > 0)
                                    {
                                        meshCache[cacheKey] = newMesh;
                                    }

                                    return newMesh;
                                }
                            }, cancellationToken);

                            if (mesh != null && mesh.Positions.Count > 0)
                            {
                                // Create a model on the UI thread
                                await RunOnDispatcherAsync(() =>
                                {
                                    var model = new GeometryModel3D
                                    {
                                        Geometry = mesh,
                                        Material = diffuseMaterial.Clone(),
                                        BackMaterial = diffuseMaterial.Clone()
                                    };

                                    var visual = new ModelVisual3D { Content = model };
                                    container.Children.Add(visual);
                                });
                            }

                            chunkCount++;
                            completedChunks++;

                            // Every batch chunks, update the UI
                            if (completedChunks >= batchSize)
                            {
                                await UpdateMaterialModelAsync(materialModel, container);
                                container = new ModelVisual3D();
                                completedChunks = 0;

                                // Force GC to clean up memory
                                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                            }
                        }
                    }
                }

                // Add any remaining chunks
                if (container.Children.Count > 0)
                {
                    await UpdateMaterialModelAsync(materialModel, container);
                }

                Logger.Log($"[VolumeRenderer] Completed material {material.ID} rendering, processed {chunkCount} chunks");
            }
            catch (OperationCanceledException)
            {
                Logger.Log($"[VolumeRenderer] Material {material.ID} rendering was canceled");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeRenderer] Error in material {material.ID} rendering: {ex.Message}");
                throw;
            }
        }

        private async Task<bool> CheckMaterialExistsAsync(byte materialId, CancellationToken cancellationToken)
        {
            var volume = mainForm.volumeLabels;
            if (volume == null) return false;

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Determine a reasonable sampling rate based on volume size
            int stride = Math.Max(1, Math.Min(32, (int)Math.Ceiling(Math.Pow(width * height * depth, 1.0 / 3) / 20)));

            return await Task.Run(() =>
            {
                for (int z = 0; z < depth; z += stride)
                {
                    for (int y = 0; y < height; y += stride)
                    {
                        for (int x = 0; x < width; x += stride)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                return false;

                            byte voxel = volume[x, y, z];
                            if (voxel == materialId)
                                return true;
                        }
                    }
                }
                return false;
            }, cancellationToken);
        }

        private async Task UpdateMaterialModelAsync(ModelVisual3D parentModel, ModelVisual3D container)
        {
            if (container.Children.Count == 0) return;

            await RunOnDispatcherAsync(() =>
            {
                parentModel.Children.Add(container);
            });
        }

        private async Task<bool> ContainsMaterialAsync(
            byte materialId,
            int startX, int startY, int startZ,
            int endX, int endY, int endZ,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Sample the chunk to determine if it contains this material
                    // Adjust stride based on chunk size for better performance
                    int dim = Math.Max(endX - startX, Math.Max(endY - startY, endZ - startZ));
                    int sampleStride = Math.Max(4, dim / 8);

                    for (int z = startZ; z < endZ; z += sampleStride)
                    {
                        for (int y = startY; y < endY; y += sampleStride)
                        {
                            for (int x = startX; x < endX; x += sampleStride)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                if (x < mainForm.GetWidth() && y < mainForm.GetHeight() && z < mainForm.GetDepth())
                                {
                                    byte label = mainForm.volumeLabels[x, y, z];
                                    if (label == materialId)
                                    {
                                        return true; // Contains the material
                                    }
                                }
                            }
                        }
                    }

                    return false; // Does not contain the material
                }
                catch (Exception ex)
                {
                    Logger.Log($"[VolumeRenderer] Error checking material: {ex.Message}");
                    return false; // Skip on error
                }
            }, cancellationToken);
        }

        private MeshGeometry3D ExtractMaterialSurfaceForChunk(
            byte materialId,
            int startX, int startY, int startZ,
            int endX, int endY, int endZ,
            double pixelSize,
            CancellationToken cancellationToken)
        {
            try
            {
                // For materials, we create a binary field where 1 indicates voxels 
                // belonging to this material and 0 elsewhere

                // Allow for one voxel overlap to avoid seams between chunks
                int sizeX = endX - startX + 1;
                int sizeY = endY - startY + 1;
                int sizeZ = endZ - startZ + 1;

                // Create binary field - use sparse storage for large datasets
                bool[,,] field = new bool[sizeX, sizeY, sizeZ];

                // Fill the field from the label volume
                for (int z = 0; z < sizeZ; z++)
                {
                    int volumeZ = startZ + z;
                    if (volumeZ >= mainForm.GetDepth()) continue;

                    for (int y = 0; y < sizeY; y++)
                    {
                        int volumeY = startY + y;
                        if (volumeY >= mainForm.GetHeight()) continue;

                        for (int x = 0; x < sizeX; x++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            int volumeX = startX + x;
                            if (volumeX >= mainForm.GetWidth()) continue;

                            field[x, y, z] = mainForm.volumeLabels[volumeX, volumeY, volumeZ] == materialId;
                        }
                    }
                }

                // Create mesh geometry for this chunk
                MeshBuilder meshBuilder = new MeshBuilder(false, false);

                // Process the binary field with the desired stride
                for (int z = 0; z < sizeZ - 1; z += voxelStride)
                {
                    for (int y = 0; y < sizeY - 1; y += voxelStride)
                    {
                        for (int x = 0; x < sizeX - 1; x += voxelStride)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            // Skip processing if outside bounds
                            if (startX + x >= mainForm.GetWidth() ||
                                startY + y >= mainForm.GetHeight() ||
                                startZ + z >= mainForm.GetDepth())
                                continue;

                            // Check if this cell is at the boundary of the material
                            bool isAtBoundary = IsAtMaterialBoundary(field, x, y, z);

                            if (isAtBoundary)
                            {
                                // Create a box at this position if the central voxel is the material
                                if (field[x, y, z])
                                {
                                    double realX = (startX + x) * pixelSize;
                                    double realY = (startY + y) * pixelSize;
                                    double realZ = (startZ + z) * pixelSize;
                                    double size = voxelStride * pixelSize * 0.9; // Slightly smaller

                                    meshBuilder.AddBox(
                                        new Point3D(realX + size / 2, realY + size / 2, realZ + size / 2),
                                        size, size, size);
                                }
                            }
                        }
                    }
                }

                MeshGeometry3D mesh = meshBuilder.ToMesh();
                if (mesh.CanFreeze)
                {
                    mesh.Freeze();
                }
                return mesh;
            }
            catch (OperationCanceledException)
            {
                throw; // Re-throw cancellation
            }
            catch (Exception ex)
            {
                // Log the error but don't crash the whole app
                Logger.Log($"[VolumeRenderer] Error extracting material surface: {ex.Message}");
                return null;
            }
        }

        private bool IsAtMaterialBoundary(bool[,,] field, int x, int y, int z)
        {
            // A voxel is at the boundary if it's different from any of its neighbors
            bool center = field[x, y, z];

            // Check the 6-connected neighbors
            // We need bounds checking to avoid array out of bounds
            bool left = x > 0 ? field[x - 1, y, z] : false;
            bool right = x < field.GetLength(0) - 1 ? field[x + 1, y, z] : false;
            bool bottom = y > 0 ? field[x, y - 1, z] : false;
            bool top = y < field.GetLength(1) - 1 ? field[x, y + 1, z] : false;
            bool back = z > 0 ? field[x, y, z - 1] : false;
            bool front = z < field.GetLength(2) - 1 ? field[x, y, z + 1] : false;

            // If any neighbor differs from the center, this voxel is at a boundary
            return center != left || center != right ||
                   center != bottom || center != top ||
                   center != back || center != front;
        }

        #endregion
    }
}