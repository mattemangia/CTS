using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpDX; // for Vector3, etc.
using CTSegmenter;
using Point = System.Drawing.Point;
using Color = System.Drawing.Color;
using CTSegmenter.SharpDXIntegration;



namespace CTSegmenter.SharpDXIntegration
{
    public partial class SharpDXViewerForm : Form
    {
        private MainForm mainForm;
        private Panel renderPanel;
        private SharpDXVolumeRenderer volumeRenderer;
        private SharpDXControlPanel controlPanel;
        private bool formLoaded = false;

        public SharpDXViewerForm(MainForm main)
        {
            mainForm = main;
            InitializeComponent();
            Logger.Log("[SharpDXViewerForm] Constructor finished.");
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
                controlPanel.Show(this); // show as owned by this form
                PositionWindows();

                // Trigger initial draw
                volumeRenderer.Render();
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

        private void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
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
            volumeRenderer.Render();
        }

        // Called by control panel for toggling slices
        public void SetSlicesEnabled(bool enabled)
        {
            volumeRenderer.ShowOrthoslices = enabled;
            volumeRenderer.Render();
        }

        public void SetSliceIndices(int xSlice, int ySlice, int zSlice)
        {
            volumeRenderer.UpdateSlices(xSlice, ySlice, zSlice);
            volumeRenderer.Render();
        }

        // Called by control panel for toggling/hiding materials
        public void SetMaterialVisibility(byte matId, bool isVisible)
        {
            volumeRenderer.SetMaterialVisibility(matId, isVisible);
            volumeRenderer.Render();
        }

        // Called by control panel for material opacity changes
        public void SetMaterialOpacity(byte matId, float opacity)
        {
            volumeRenderer.SetMaterialOpacity(matId, opacity);
            volumeRenderer.Render();
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

        

        // Called by control panel for “Export Model” (OBJ or STL)
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
