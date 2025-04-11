using WinForms = System.Windows.Forms;
using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using HelixToolkit.Wpf.SharpDX;
using System.Windows.Media;
using System.Windows.Input;
using System.ComponentModel;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Controls;
using TabControl = System.Windows.Forms.TabControl;
using Panel = System.Windows.Forms.Panel;
using Button = System.Windows.Forms.Button;
using CheckBox = System.Windows.Forms.CheckBox;
using System.Linq;
using Color = System.Drawing.Color;
using ComboBox = System.Windows.Forms.ComboBox;
using Label = System.Windows.Forms.Label;
using Orientation = System.Windows.Forms.Orientation;
using ProgressBar = System.Windows.Forms.ProgressBar;

namespace CTSegmenter
{
    public partial class CTViewer3DForm : Form
    {
        private MainForm mainForm;
        private ElementHost elementHost;
        private Viewport3DX viewport;
        private VolumeRenderer volumeRenderer;
        private bool renderingInProgress = false;
        private CTViewerControlPanel controlPanel;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);

        public CTViewer3DForm(MainForm main)
        {
            try
            {
                Logger.Log("[CTViewer3DForm] Initializing 3D viewer...");

                // Set DPI awareness for high-DPI displays
                if (Environment.OSVersion.Version.Major >= 10)
                {
                    SetProcessDpiAwareness(2); // PROCESS_PER_MONITOR_DPI_AWARE
                }
                else if (Environment.OSVersion.Version.Major >= 6)
                {
                    SetProcessDPIAware();
                }

                mainForm = main;
                InitializeComponent();
                InitializeViewport();
                Logger.Log("[CTViewer3DForm] Viewport initialized");

                // Create VolumeRenderer with the main form and the HelixToolkit viewport
                volumeRenderer = new VolumeRenderer(mainForm, viewport);
                Logger.Log("[CTViewer3DForm] Volume renderer created");

                // Create the control panel (separate form) after volume renderer is ready
                controlPanel = new CTViewerControlPanel(this, mainForm, volumeRenderer);
                controlPanel.Show(this); // Show as owned by this form
                PositionWindows();
                Logger.Log("[CTViewer3DForm] Initialization complete");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize 3D viewer: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log("[CTViewer3DForm] Error in constructor: " + ex.Message);
            }
        }

        private void PositionWindows()
        {
            // Position both forms side by side
            Rectangle screenBounds = Screen.FromControl(this).WorkingArea;

            // Main viewport gets 75% of width
            this.Location = new Point(screenBounds.Left, screenBounds.Top);
            this.Size = new Size((int)(screenBounds.Width * 0.75), screenBounds.Height - 40);

            // Control panel gets remaining width
            controlPanel.Location = new Point(this.Right + 5, screenBounds.Top);
            controlPanel.Size = new Size(screenBounds.Width - this.Width - 15, this.Height);
        }

        private void InitializeComponent()
        {
            this.elementHost = new ElementHost();

            // Basic form properties
            this.Text = "3D Volume Viewer";
            this.BackColor = Color.FromArgb(20, 20, 20);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.Manual;
            this.MinimumSize = new Size(500, 400);

            // Setup elementHost for WPF Viewport3DX
            this.elementHost.Dock = DockStyle.Fill;
            this.elementHost.BackColor = Color.Black;
            this.Controls.Add(this.elementHost);

            // Handle resize to reposition control panel
            this.Resize += (s, e) => {
                if (controlPanel != null && !controlPanel.IsDisposed)
                {
                    controlPanel.Location = new Point(this.Right + 5, controlPanel.Top);
                    controlPanel.Height = this.Height;
                }
            };

            // Handle form closing
            this.FormClosing += (s, e) => {
                if (controlPanel != null && !controlPanel.IsDisposed)
                {
                    controlPanel.Close();
                }
            };
        }

        private void InitializeViewport()
        {
            // Initialize HelixToolkit Viewport3DX
            viewport = new Viewport3DX();
            viewport.BackgroundColor = System.Windows.Media.Color.FromArgb(255, 0, 0, 0);
            viewport.EnableRenderFrustum = false;
            viewport.IsShadowMappingEnabled = false;

            elementHost.Child = viewport;
        }

        public void ResetView()
        {
            volumeRenderer?.ResetCameraView();
        }

        public async Task ApplyThresholdAndRender(int minThreshold, int maxThreshold, int qualityLevel)
        {
            if (renderingInProgress) return;

            try
            {
                renderingInProgress = true;
                controlPanel.SetStatus("Rendering...", true);

                // Get voxel stride from quality level
                int voxelStride;
                switch (qualityLevel)
                {
                    case 0: voxelStride = 8; break; // Low
                    case 1: voxelStride = 4; break; // Medium
                    case 2: voxelStride = 2; break; // High
                    case 3: voxelStride = 1; break; // Ultra
                    default: voxelStride = 2; break; // Default High
                }

                // Apply settings to volumeRenderer
                volumeRenderer.MinThreshold = minThreshold;
                volumeRenderer.MaxThreshold = maxThreshold;
                volumeRenderer.VoxelStride = voxelStride;
                volumeRenderer.UseLodRendering = controlPanel.IsLodEnabled;
                volumeRenderer.ShowBwDataset = controlPanel.IsGrayscaleEnabled;

                // Update the volume visualization
                await volumeRenderer.UpdateAsync();

                // Update slice planes if enabled
                if (controlPanel.AreSlicesEnabled)
                {
                    volumeRenderer.ShowSlicePlanes(controlPanel.AreOrthoplanesVisible);
                    if (controlPanel.AreOrthoplanesVisible)
                    {
                        volumeRenderer.UpdateSlicePlanes(
                            controlPanel.XSliceValue,
                            controlPanel.YSliceValue,
                            controlPanel.ZSliceValue,
                            mainForm.GetWidth(), mainForm.GetHeight(), mainForm.GetDepth(),
                            mainForm.GetPixelSize());
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error rendering volume: {ex.Message}", "Render Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log("[CTViewer3DForm] Rendering error: " + ex);
            }
            finally
            {
                controlPanel.SetStatus("Ready", false);
                renderingInProgress = false;
            }
        }

        public async Task ExportModel(string filename)
        {
            try
            {
                controlPanel.SetStatus("Exporting...", true);
                await Task.Run(() => volumeRenderer.ExportModel(filename));
                MessageBox.Show("Export completed successfully.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting model: {ex.Message}", "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                controlPanel.SetStatus("Ready", false);
            }
        }

        public void RunQuickTest()
        {
            Logger.Log("[3D Viewer] Running quick test render");
            volumeRenderer.QuickRenderTest();
        }

        public void UpdateSlice(int axis, int value)
        {
            if (volumeRenderer == null) return;

            switch (axis)
            {
                case 0: // X
                    volumeRenderer.UpdateXSlice(value, mainForm.GetWidth(), mainForm.GetPixelSize());
                    break;
                case 1: // Y
                    volumeRenderer.UpdateYSlice(value, mainForm.GetHeight(), mainForm.GetPixelSize());
                    break;
                case 2: // Z
                    volumeRenderer.UpdateZSlice(value, mainForm.GetDepth(), mainForm.GetPixelSize());
                    break;
            }
        }

        public void ShowSlicePlanes(bool show)
        {
            volumeRenderer?.ShowSlicePlanes(show);
        }

        public void SetMaterialVisibility(byte materialId, bool visible)
        {
            volumeRenderer?.SetMaterialVisibility(materialId, visible);
        }

        public void SetMaterialOpacity(byte materialId, double opacity)
        {
            volumeRenderer?.SetMaterialOpacity(materialId, opacity);
        }

        public double GetMaterialOpacity(byte materialId)
        {
            return volumeRenderer?.GetMaterialOpacity(materialId) ?? 1.0;
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                // Safely dispose of resources in correct order
                if (volumeRenderer != null)
                {
                    volumeRenderer.Dispose(true);
                    volumeRenderer = null;
                }

                

                Logger.Log("[CTViewer3DForm] 3D viewer closed successfully.");
            }
            catch (Exception ex)
            {
                Logger.Log("[CTViewer3DForm] Error during form closing: " + ex.Message);
            }

            base.OnClosed(e);
        }
    }
}
