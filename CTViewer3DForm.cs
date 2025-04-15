using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpDX; // for Vector3, etc.
using CTSegmenter;
using Point = System.Drawing.Point;
using Color = System.Drawing.Color;
using System.Collections.Generic;
using System.IO;

namespace CTSegmenter.SharpDXIntegration
{
    public partial class SharpDXViewerForm : Form
    {
        private MainForm mainForm;
        private Panel renderPanel;
        private SharpDXVolumeRenderer volumeRenderer;
        private SharpDXControlPanel controlPanel;
        private bool formLoaded = false;
        private Timer renderTimer;
        private bool isRendering = false;
        private DateTime lastRenderTime = DateTime.MinValue;
        private int renderFailCount = 0;
        private const int MAX_FAILURES = 3;

        private bool measurementMode = false;
        
        private bool isDrawingMeasurement = false;
        private SharpDX.Vector3 measureStartPoint;
        private SharpDX.Vector3 measureEndPoint;

        //Slice controls
        private bool showXSlice = false;
        private bool showYSlice = false;
        private bool showZSlice = false;

        // Modified method to set individual slice visibility
        public void SetSliceXEnabled(bool enabled)
        {
            showXSlice = enabled;
            volumeRenderer.ShowXSlice = enabled;
            volumeRenderer.NeedsRender = true;
        }

        public void SetSliceYEnabled(bool enabled)
        {
            showYSlice = enabled;
            volumeRenderer.ShowYSlice = enabled;
            volumeRenderer.NeedsRender = true;
        }

        public void SetSliceZEnabled(bool enabled)
        {
            showZSlice = enabled;
            volumeRenderer.ShowZSlice = enabled;
            volumeRenderer.NeedsRender = true;
        }

        // Method to get visibility state for UI sync
        public bool GetSliceXEnabled() => showXSlice;
        public bool GetSliceYEnabled() => showYSlice;
        public bool GetSliceZEnabled() => showZSlice;

        // Modified method to handle global slice toggle
        public void SetSlicesEnabled(bool enabled)
        {
            if (volumeRenderer != null)
            {
                showXSlice = enabled;
                showYSlice = enabled;
                showZSlice = enabled;

                volumeRenderer.ShowXSlice = enabled;
                volumeRenderer.ShowYSlice = enabled;
                volumeRenderer.ShowZSlice = enabled;
                volumeRenderer.NeedsRender = true;
            }
        }

        public SharpDXViewerForm(MainForm main)
        {
            mainForm = main;
            InitializeComponent();
            Logger.Log("[SharpDXViewerForm] Constructor finished.");
        }

        private void SetupRenderTimer()
        {
            renderTimer = new Timer();
            renderTimer.Interval = 33; // Increase responsiveness to ~30fps (from 100ms/10fps)

            renderTimer.Tick += (s, e) => {
                if (volumeRenderer != null && !isRendering)
                {
                    try
                    {
                        isRendering = true;
                        lastRenderTime = DateTime.Now;

                        // Force render at least once per second even if nothing is changing
                        // This ensures the volume stays visible
                        bool forceRender = (DateTime.Now - lastRenderTime).TotalSeconds > 1.0;
                        bool needsRender = volumeRenderer.NeedsRender || forceRender;

                        if (needsRender)
                        {
                            volumeRenderer.Render();
                            renderFailCount = 0; // Reset failure counter on success
                        }
                    }
                    catch (Exception ex)
                    {
                        renderFailCount++;
                        Logger.Log($"[SharpDXViewerForm] Render failed ({renderFailCount}/{MAX_FAILURES}): {ex.Message}");

                        // After several consecutive failures, try to recreate the renderer
                        if (renderFailCount >= MAX_FAILURES)
                        {
                            try
                            {
                                RecreateRenderer();
                                renderFailCount = 0;
                            }
                            catch (Exception recreateEx)
                            {
                                renderTimer.Stop();
                                MessageBox.Show("3D rendering disabled due to failures. Please reopen the viewer.",
                                             "Rendering Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }
                    finally
                    {
                        isRendering = false;
                    }
                }
            };
        }

        private void RecreateRenderer()
        {
            // Stop rendering during recreation
            renderTimer.Stop();

            // Dispose existing renderer
            if (volumeRenderer != null)
            {
                try
                {
                    volumeRenderer.Dispose();
                }
                catch { } // Ignore disposal errors
            }

            // Create new renderer
            volumeRenderer = new SharpDXVolumeRenderer(mainForm, renderPanel);
            Logger.Log("[SharpDXViewerForm] Renderer recreated");

            // Restart timer
            renderTimer.Start();
        }

        private void InitializeComponent()
        {
            this.Text = "3D Volume Viewer (SharpDX)";
            this.Size = new Size(1200, 800);
            try
            {
                var iconPath = System.IO.Path.Combine(Application.StartupPath, "favicon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
            }
            catch { /* ignore icon load errors */ }

            // Create a Panel to host the SharpDX rendering
            renderPanel = new Panel();
            renderPanel.Dock = DockStyle.Fill;
            renderPanel.BackColor = Color.Black;
            this.Controls.Add(renderPanel);

            // Hook form events
            this.Load += (s, e) => OnFormLoaded();
            this.FormClosing += (s, e) => OnFormClosing(e);
            this.SizeChanged += (s, e) => { volumeRenderer?.OnResize(); };
            renderPanel.SizeChanged += (s, e) => { volumeRenderer?.OnResize(); };

            // Add render timer
            renderTimer = new Timer();
            renderTimer.Interval = 50; // 20 fps
            renderTimer.Tick += (s, e) => {
                if (volumeRenderer != null)
                {
                    volumeRenderer.Render();
                }
            };
        }

        private void OnFormLoaded()
        {
            try
            {
                if (formLoaded) return;
                formLoaded = true;

                // Create the volume renderer
                volumeRenderer = new SharpDXVolumeRenderer(mainForm, renderPanel);
                Logger.Log("[SharpDXViewerForm] Volume renderer created.");

                // Create the control panel
                controlPanel = new SharpDXControlPanel(this, mainForm, volumeRenderer);
                controlPanel.Show(this);
                PositionWindows();

                // IMPORTANT: Force initial renders with high quality and no LOD
                volumeRenderer.UseLodSystem = false;
                volumeRenderer.NeedsRender = true;

                // Perform multiple high-quality renders to ensure the volume is visible
                for (int i = 0; i < 3; i++) // Try multiple times to ensure it works
                {
                    try
                    {
                        volumeRenderer.ForceInitialRender();
                        Application.DoEvents(); // Let UI update
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[SharpDXViewerForm] Initial render attempt {i} failed: {ex.Message}");
                    }
                }

                // Re-enable LOD for normal operation
                volumeRenderer.UseLodSystem = true;

                // Setup the render timer to maintain display
                SetupRenderTimer();
                renderTimer.Start();
                Logger.Log("[SharpDXViewerForm] Render timer started.");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXViewerForm] Error in OnFormLoaded: " + ex.Message);
                MessageBox.Show("Error initializing 3D viewer: " + ex.Message);
            }
        }

        private void PositionWindows()
        {
            // Place control panel to the right of this form
            var screenBounds = Screen.FromControl(this).WorkingArea;
            int panelX = this.Right + 10;
            if (panelX + controlPanel.Width > screenBounds.Width)
            {
                panelX = screenBounds.Width - controlPanel.Width;
            }
            controlPanel.Location = new Point(panelX, this.Top);
        }

        public void SetDebugMode(bool enabled)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.DebugMode = enabled;
                volumeRenderer.NeedsRender = true;
            }
        }

        public void SetColorMap(int colorMapIndex)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.ColorMapIndex = colorMapIndex;
                volumeRenderer.NeedsRender = true;
            }
        }

        public void RunViewportTest()
        {
            if (volumeRenderer != null)
            {
                Logger.Log("[SharpDXViewerForm] Running viewport test");
                volumeRenderer.Render();
            }
        }

        private void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                // Stop the render timer
                renderTimer.Stop();

                // Dispose volume renderer
                volumeRenderer?.Dispose();
                volumeRenderer = null;
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXViewerForm] Error disposing volume renderer: " + ex.Message);
            }
        }

        // Called by the control panel to trigger re-renders
        public async Task ApplyThresholdAndRender(int minThreshold, int maxThreshold, int qualityLevel)
        {
            if (volumeRenderer == null) return;

            try
            {
                // Update the renderer properties
                volumeRenderer.MinThreshold = minThreshold;
                volumeRenderer.MaxThreshold = maxThreshold;

                // Set step size based on quality level
                float stepSize = 0.5f;
                switch (qualityLevel)
                {
                    case 0: stepSize = 1.5f; break;
                    case 1: stepSize = 1.0f; break;
                    case 2: stepSize = 0.5f; break;
                }
                volumeRenderer.SetRaymarchStepSize(stepSize);

             
                volumeRenderer.NeedsRender = true;

                // Allow the render timer to handle the actual rendering
                // This ensures all DirectX calls happen on the UI thread
                // Just wait a bit to ensure the render happens
                await Task.Delay(50);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXViewerForm] Error applying threshold: {ex.Message}");
                throw;
            }
        }
        // Called by control panel for show/hide grayscale
        public void SetGrayscaleVisible(bool isVisible)
        {
            volumeRenderer.ShowGrayscale = isVisible;
            volumeRenderer.NeedsRender = true;
        }

        
        

        public void SetSliceIndices(int xSlice, int ySlice, int zSlice)
        {
            volumeRenderer.UpdateSlices(xSlice, ySlice, zSlice);
            volumeRenderer.NeedsRender = true;
        }

        // Called by control panel for toggling/hiding materials
        public void SetMaterialVisibility(byte matId, bool isVisible)
        {
            volumeRenderer.SetMaterialVisibility(matId, isVisible);
            volumeRenderer.NeedsRender = true;
        }

        // Called by control panel for material opacity changes
        public void SetMaterialOpacity(byte matId, float opacity)
        {
            volumeRenderer.SetMaterialOpacity(matId, opacity);
            volumeRenderer.NeedsRender = true;
        }

        public bool GetMaterialVisibility(byte matId)
        {
            if (volumeRenderer == null) return false;
            return volumeRenderer.GetMaterialVisibility(matId);
        }

        public float GetMaterialOpacity(byte matId)
        {
            if (volumeRenderer == null) return 1.0f;
            return volumeRenderer.GetMaterialOpacity(matId);
        }
        public void SetLodEnabled(bool enabled)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.UseLodSystem = enabled;
                volumeRenderer.NeedsRender = true;
                Logger.Log($"[SharpDXViewerForm] LOD system {(enabled ? "enabled" : "disabled")}");
            }
        }
        // New methods for dataset cutting
        public void SetCutXEnabled(bool enabled)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.CutXEnabled = enabled;
                volumeRenderer.NeedsRender = true;
            }
        }

        public void SetCutYEnabled(bool enabled)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.CutYEnabled = enabled;
                volumeRenderer.NeedsRender = true;
            }
        }

        public void SetCutZEnabled(bool enabled)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.CutZEnabled = enabled;
                volumeRenderer.NeedsRender = true;
            }
        }

        public void SetCutXPosition(float position)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.CutXPosition = position;
                volumeRenderer.NeedsRender = true;
            }
        }

        public void SetCutYPosition(float position)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.CutYPosition = position;
                volumeRenderer.NeedsRender = true;
            }
        }

        public void SetCutZPosition(float position)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.CutZPosition = position;
                volumeRenderer.NeedsRender = true;
            }
        }

        public void SetCutXDirection(float direction)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.CutXDirection = direction;
                volumeRenderer.NeedsRender = true;
            }
        }

        public void SetCutYDirection(float direction)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.CutYDirection = direction;
                volumeRenderer.NeedsRender = true;
            }
        }

        public void SetCutZDirection(float direction)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.CutZDirection = direction;
                volumeRenderer.NeedsRender = true;
            }
        }

        public void ResetAllCuts()
        {
            if (volumeRenderer != null)
            {
                // Reset all cutting plane parameters
                volumeRenderer.CutXEnabled = false;
                volumeRenderer.CutYEnabled = false;
                volumeRenderer.CutZEnabled = false;
                volumeRenderer.CutXPosition = 0.5f;
                volumeRenderer.CutYPosition = 0.5f;
                volumeRenderer.CutZPosition = 0.5f;
                volumeRenderer.CutXDirection = 1.0f;
                volumeRenderer.CutYDirection = 1.0f;
                volumeRenderer.CutZDirection = 1.0f;
                volumeRenderer.NeedsRender = true;
            }
        }
        public bool ToggleMeasurementMode()
        {
            measurementMode = !measurementMode;
            isDrawingMeasurement = false;

            // Clean up text controls when exiting measurement mode
            if (!measurementMode)
            {
                volumeRenderer.RemoveAllMeasurementTextControls();
            }

            // Propagate the measurement mode to the renderer
            if (volumeRenderer != null)
            {
                volumeRenderer.SetMeasurementMode(measurementMode);

                // Make sure the control panel is updated to reflect the measurement mode state
                if (controlPanel != null)
                {
                    controlPanel.UpdateMeasurementUI(measurementMode);
                }

                Logger.Log($"[SharpDXViewerForm] Measurement mode {(measurementMode ? "enabled" : "disabled")}");
            }

            volumeRenderer.NeedsRender = true;
            return measurementMode;
        }
        public void SetMeasurementsVisible(bool visible)
        {
            Logger.Log($"[SharpDXViewerForm] Setting all measurements visibility to {visible}");

            if (volumeRenderer != null && volumeRenderer.measurements != null)
            {
                // Apply visibility to all measurements in the renderer's list
                foreach (var measurement in volumeRenderer.measurements)
                {
                    measurement.Visible = visible;
                }

                // Force a re-render to reflect visibility changes
                volumeRenderer.NeedsRender = true;
            }

            // If control panel exists, update the UI to reflect this change
            if (controlPanel != null)
            {
                try
                {
                    controlPanel.BeginInvoke(new Action(() => {
                        // Update checkboxes in the measurements list to match new visibility
                        controlPanel.RefreshMeasurementsList();
                    }));
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SharpDXViewerForm] Error updating UI after changing measurement visibility: {ex.Message}");
                }
            }
        }

        public void SetMeasurementVisibility(int index, bool visible)
        {
            Logger.Log($"[SharpDXViewerForm] Setting measurement at index {index} visibility to {visible}");

            if (volumeRenderer != null && volumeRenderer.measurements != null)
            {
                if (index >= 0 && index < volumeRenderer.measurements.Count)
                {
                    volumeRenderer.measurements[index].Visible = visible;

                    // Force a re-render to reflect visibility changes
                    volumeRenderer.NeedsRender = true;
                }
                else
                {
                    Logger.Log($"[SharpDXViewerForm] Invalid measurement index: {index}, count: {volumeRenderer.measurements.Count}");
                }
            }
        }

        public void DeleteMeasurement(int index)
        {
            Logger.Log($"[SharpDXViewerForm] Deleting measurement at index {index}");

            if (volumeRenderer != null && volumeRenderer.measurements != null)
            {
                if (index >= 0 && index < volumeRenderer.measurements.Count)
                {
                    // Remove the measurement from the renderer's list directly
                    volumeRenderer.measurements.RemoveAt(index);
                    Logger.Log($"[SharpDXViewerForm] Measurement deleted, new count: {volumeRenderer.measurements.Count}");

                    // Force a re-render
                    volumeRenderer.NeedsRender = true;

                    // Also clear any text overlays in the renderer
                    volumeRenderer.ClearMeasurementLabels();
                }
                else
                {
                    Logger.Log($"[SharpDXViewerForm] Cannot delete: Invalid measurement index: {index}, count: {volumeRenderer.measurements.Count}");
                }

                // If control panel exists, update the UI to reflect this change
                if (controlPanel != null)
                {
                    try
                    {
                        controlPanel.BeginInvoke(new Action(() => {
                            controlPanel.RefreshMeasurementsList();
                        }));
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[SharpDXViewerForm] Error updating UI after deleting measurement: {ex.Message}");
                    }
                }
            }
            else
            {
                Logger.Log("[SharpDXViewerForm] Cannot delete measurement: renderer or measurements list is null");
            }
        }

        public List<MeasurementLine> GetMeasurements()
        {
            // Always return a reference to the renderer's measurements list
            if (volumeRenderer != null)
            {
                return volumeRenderer.measurements;
            }
            return new List<MeasurementLine>(); // Return empty list as fallback
        }

        public void ExportMeasurementsToCSV(string filePath)
        {
            try
            {
                // Use the volumeRenderer's measurements list instead of the local measurements field
                var measurementsToExport = volumeRenderer.measurements;

                using (StreamWriter sw = new StreamWriter(filePath))
                {
                    // Write header
                    sw.WriteLine("Label,Distance (voxels),Real Distance,Unit,Start X,Start Y,Start Z,End X,End Y,End Z,On Slice,Slice Type,Slice Position");

                    // Write each measurement
                    foreach (var m in measurementsToExport)
                    {
                        sw.WriteLine($"\"{m.Label}\",{m.Distance:F2},{m.RealDistance:F3},\"{m.Unit}\",{m.Start.X:F1},{m.Start.Y:F1},{m.Start.Z:F1},{m.End.X:F1},{m.End.Y:F1},{m.End.Z:F1},{m.IsOnSlice},{m.SliceType},{m.SlicePosition}");
                    }
                }

                Logger.Log("[SharpDXViewerForm] Exported measurements to: " + filePath);
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXViewerForm] Failed to export measurements: " + ex.Message);
                throw;
            }
        }
        // Called by control panel for "Export Model" (OBJ or STL)
        public async Task ExportModelAsync(bool exportLabels, bool exportGrayscaleSurface,
                                   string filePath, float isoLevel, IProgress<int> progress = null)
        {
            try
            {
                Logger.Log("[SharpDXViewerForm] Starting model export...");
                this.Enabled = false;

                await Task.Run(() =>
                {
                    bool[] labelVisibility = volumeRenderer.GetLabelVisibilityArray();

                    // Create export callback for progress updates
                    Action<int> progressCallback = percent => {
                        progress?.Report(percent);
                        Logger.Log($"[SharpDXViewerForm] Export progress: {percent}%");
                    };

                    VoxelMeshExporter.ExportVisibleVoxels(
                        filePath,
                        grayVol: mainForm.volumeData,
                        labelVol: exportLabels ? mainForm.volumeLabels : null,
                        minThreshold: volumeRenderer.MinThreshold,
                        maxThreshold: volumeRenderer.MaxThreshold,
                        showGrayscale: volumeRenderer.ShowGrayscale,
                        labelVisibility: labelVisibility,
                        sliceX: volumeRenderer.SliceX,
                        sliceY: volumeRenderer.SliceY,
                        sliceZ: volumeRenderer.SliceZ,
                        showSlices: volumeRenderer.ShowOrthoslices
                        
                    );
                });

                Logger.Log("[SharpDXViewerForm] Export done: " + filePath);
                MessageBox.Show("Exported to: " + filePath);
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXViewerForm] Export error: " + ex.Message);
                MessageBox.Show("Export Error: " + ex.Message);
            }
            finally
            {
                this.Enabled = true;
            }
        }
        public async Task ApplyDownsampling(int downsampleFactor, IProgress<int> progress = null)
        {
            if (volumeRenderer == null || mainForm == null)
                return;

            // Make sure the factor is valid
            if (downsampleFactor <= 1)
            {
                Logger.Log("[SharpDXViewerForm] Invalid downsample factor");
                return;
            }

            Logger.Log($"[SharpDXViewerForm] Starting {downsampleFactor}x downsampling");

            try
            {
                // Dispose the current renderer to free up memory
                volumeRenderer.Dispose();
                volumeRenderer = null;

                // Force garbage collection to free memory
                GC.Collect();

                await Task.Run(() => {
                    // Report progress
                    progress?.Report(10);

                    // Create a downsampled version of the volume data
                    Logger.Log("[SharpDXViewerForm] Creating downsampled volume...");

                    ChunkedVolume originalVolume = mainForm.volumeData;
                    if (originalVolume == null)
                    {
                        throw new InvalidOperationException("No volume data available to downsample");
                    }

                    int width = originalVolume.Width;
                    int height = originalVolume.Height;
                    int depth = originalVolume.Depth;

                    // Calculate new dimensions
                    int newWidth = Math.Max(1, width / downsampleFactor);
                    int newHeight = Math.Max(1, height / downsampleFactor);
                    int newDepth = Math.Max(1, depth / downsampleFactor);

                    Logger.Log($"[SharpDXViewerForm] Original size: {width}x{height}x{depth}");
                    Logger.Log($"[SharpDXViewerForm] New size: {newWidth}x{newHeight}x{newDepth}");

                    // Create new volume
                    ChunkedVolume downsampledVolume = new ChunkedVolume(newWidth, newHeight, newDepth);

                    // Process the data in chunks to avoid memory overflow
                    int chunkSize = 64; // Process 64³ chunks at a time
                    int totalChunks = (int)Math.Ceiling((double)newDepth / chunkSize);
                    int processedChunks = 0;

                    // Downsample the volume data
                    for (int z = 0; z < newDepth; z += chunkSize)
                    {
                        // Calculate progress
                        processedChunks++;
                        int progressValue = 10 + (int)(80.0 * processedChunks / totalChunks);
                        progress?.Report(progressValue);

                        int chunkDepth = Math.Min(chunkSize, newDepth - z);

                        for (int y = 0; y < newHeight; y++)
                        {
                            for (int x = 0; x < newWidth; x++)
                            {
                                // For each voxel in the downsampled volume
                                int srcX = x * downsampleFactor;
                                int srcY = y * downsampleFactor;

                                for (int zOffset = 0; zOffset < chunkDepth; zOffset++)
                                {
                                    int srcZ = (z + zOffset) * downsampleFactor;

                                    // Get the value from the original volume
                                    byte value = originalVolume[srcX, srcY, srcZ];

                                    // Set the value in the downsampled volume
                                    downsampledVolume[x, y, z + zOffset] = value;
                                }
                            }
                        }
                    }

                    progress?.Report(90);

                    // Replace the original volume with the downsampled one
                    Logger.Log("[SharpDXViewerForm] Replacing volume data with downsampled version");
                    mainForm.volumeData = downsampledVolume;

                    // Update any dimensions that need updating in the main form
                    //mainForm.UpdateVolumeInfo(downsampledVolume);

                    progress?.Report(95);
                });

                // Create a new renderer with the downsampled data
                Logger.Log("[SharpDXViewerForm] Creating new renderer with downsampled data");
                volumeRenderer = new SharpDXVolumeRenderer(mainForm, renderPanel);
                volumeRenderer.SetControlPanel(controlPanel);

                // Force initial render
                volumeRenderer.NeedsRender = true;
                volumeRenderer.ForceInitialRender();

                progress?.Report(100);
                Logger.Log("[SharpDXViewerForm] Downsampling complete");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXViewerForm] Error applying downsampling: {ex.Message}");

                // Try to recover
                if (volumeRenderer == null)
                {
                    try
                    {
                        volumeRenderer = new SharpDXVolumeRenderer(mainForm, renderPanel);
                        volumeRenderer.SetControlPanel(controlPanel);
                    }
                    catch
                    {
                        // Critical failure - cannot recover
                        MessageBox.Show("Critical rendering error. Please restart the application.",
                            "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }

                throw;
            }
        }
        public void SetStreamingRendererEnabled(bool enabled)
        {
            if (volumeRenderer != null)
            {
                volumeRenderer.UseStreamingRenderer = enabled;
                volumeRenderer.NeedsRender = true;

                if (enabled)
                {
                    MessageBox.Show(
                        "Streaming renderer enabled. Volume chunks will load progressively." +
                        "\n\nNote: The first view may be low-resolution until chunks load.",
                        "Streaming Mode",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                Logger.Log($"[SharpDXViewerForm] Streaming renderer {(enabled ? "enabled" : "disabled")}");
            }
        }
        // Called by control panel for screenshot
        public void TakeScreenshot(string filePath)
        {
            try
            {
                volumeRenderer.SaveScreenshot(filePath);
                Logger.Log("[SharpDXViewerForm] Screenshot saved: " + filePath);
                MessageBox.Show("Screenshot saved: " + filePath);
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXViewerForm] Screenshot error: " + ex.Message);
                MessageBox.Show("Screenshot Error: " + ex.Message);
            }
        }
    }
}