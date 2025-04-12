using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpDX; // for Vector3, etc.
using CTSegmenter;
using Point = System.Drawing.Point;
using Color = System.Drawing.Color;

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

            Logger.Log("[SharpDXViewerForm] Starting threshold update...");
            volumeRenderer.MinThreshold = minThreshold;
            volumeRenderer.MaxThreshold = maxThreshold;

            // Example approach for LOD or 'quality' slider:
            // Let's interpret qualityLevel=0 => stepSize=1.5f, qualityLevel=1 => stepSize=1.0f, etc.
            // Tweak as desired
            float stepSize = 0.5f;
            switch (qualityLevel)
            {
                case 0: stepSize = 1.5f; break;
                case 1: stepSize = 1.0f; break;
                case 2: stepSize = 0.5f; break;
            }
            volumeRenderer.SetRaymarchStepSize(stepSize);

            // Force a redraw
            await Task.Run(() => { volumeRenderer.Render(); });
            Logger.Log("[SharpDXViewerForm] Threshold update complete.");
        }

        // Called by control panel for show/hide grayscale
        public void SetGrayscaleVisible(bool isVisible)
        {
            volumeRenderer.ShowGrayscale = isVisible;
            volumeRenderer.NeedsRender = true;
        }

        // Called by control panel for toggling slices
        public void SetSlicesEnabled(bool enabled)
        {
            volumeRenderer.ShowOrthoslices = enabled;
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

        // Called by control panel for "Export Model" (OBJ or STL)
        public async Task ExportModelAsync(bool exportLabels, bool exportGrayscaleSurface,
                                           string filePath, float isoLevel)
        {
            try
            {
                Logger.Log("[SharpDXViewerForm] Exporting model...");
                this.Enabled = false;
                await Task.Run(() =>
                {
                    bool[] labelVisibility = volumeRenderer.GetLabelVisibilityArray(); // must expose this from renderer
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