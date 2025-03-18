// CTVolumeViewer.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace CTSegmenter
{
    public class CTVolumeViewer : IDisposable
    {
        private Panel renderPanel;
        private Timer renderTimer;
        private Point lastMousePosition;
        private bool isMouseDown = false;
        private bool isInitialized = false;
        private bool disposed = false;

        // Rendering properties
        public float Opacity { get; set; } = 0.05f;
        public float Brightness { get; set; } = 0.0f;
        public float Contrast { get; set; } = 1.0f;
        public int RenderMode { get; set; } = 0; // 0=Volume, 1=MIP, 2=Isosurface
        public bool ShowLabels { get; set; } = true;

        public CTVolumeViewer(Panel panel)
        {
            renderPanel = panel ?? throw new ArgumentNullException(nameof(panel));

            // Initialize logging
            CTViewerNative.InitializeLogging();
            

            // Initialize rendering timer
            renderTimer = new Timer();
            renderTimer.Interval = 16; // ~60 FPS
            renderTimer.Tick += (s, e) => Render();

            // Set up mouse and resize handlers
            renderPanel.MouseDown += Panel_MouseDown;
            renderPanel.MouseMove += Panel_MouseMove;
            renderPanel.MouseUp += Panel_MouseUp;
            renderPanel.MouseWheel += Panel_MouseWheel;
            renderPanel.Resize += Panel_Resize;

            try
            {
                Logger.Log($"Initializing DirectX with panel: {renderPanel.Handle}, {renderPanel.Width}x{renderPanel.Height}");
                // Initialize DirectX viewer
                isInitialized = CTViewerNative.Initialize(renderPanel.Handle, renderPanel.Width, renderPanel.Height);
                if (!isInitialized)
                    throw new Exception("Failed to initialize DirectX renderer");

                // Set initial rendering parameters
                UpdateRenderingParameters();

                // Start rendering
                renderTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Log($"Initialization error: {ex.Message}");
                Logger.Log($"Error initializing 3D viewer: {ex.Message}");
            }
        }

        public void UpdateRenderingParameters()
        {
            if (!isInitialized) return;

            CTViewerNative.SetOpacity(Opacity);
            CTViewerNative.SetBrightness(Brightness);
            CTViewerNative.SetContrast(Contrast);
            CTViewerNative.SetRenderMode(RenderMode);
            CTViewerNative.ShowLabels(ShowLabels);
        }

        public void ResetCamera()
        {
            if (!isInitialized) return;
            CTViewerNative.ResetCamera();
        }

        public async Task LoadVolumeAsync(ChunkedVolume volume, double pixelSize)
        {
            if (!isInitialized || volume == null) return;

            await Task.Run(() => {
                byte[] buffer = ExtractVolumeData(volume);
                if (buffer != null)
                {
                    GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    try
                    {
                        CTViewerNative.LoadVolumeData(
                            handle.AddrOfPinnedObject(),
                            volume.Width,
                            volume.Height,
                            volume.Depth,
                            (float)pixelSize);
                    }
                    finally
                    {
                        handle.Free();
                    }
                }
            });
        }

        public async Task LoadLabelsAsync(ChunkedLabelVolume volume)
        {
            if (!isInitialized || volume == null) return;

            await Task.Run(() => {
                byte[] buffer = ExtractLabelData(volume);
                if (buffer != null)
                {
                    GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    try
                    {
                        CTViewerNative.LoadLabelData(
                            handle.AddrOfPinnedObject(),
                            volume.Width,
                            volume.Height,
                            volume.Depth);
                    }
                    finally
                    {
                        handle.Free();
                    }
                }
            });
        }

        public void UpdateMaterials(List<Material> materials)
        {
            if (!isInitialized || materials == null) return;

            // Convert material colors to array of integers (ARGB)
            int[] colors = new int[256]; // Fixed size for simplicity

            // Initialize all to transparent
            for (int i = 0; i < colors.Length; i++)
                colors[i] = 0;

            // Copy material colors
            foreach (var material in materials)
            {
                if (material.ID < colors.Length)
                    colors[material.ID] = material.Color.ToArgb();
            }

            // Send to native code
            GCHandle handle = GCHandle.Alloc(colors, GCHandleType.Pinned);
            try
            {
                CTViewerNative.UpdateMaterials(handle.AddrOfPinnedObject(), colors.Length);
            }
            finally
            {
                handle.Free();
            }
        }

        private void Render()
        {
            if (isInitialized && !disposed)
                CTViewerNative.Render();
        }

        private void Panel_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                lastMousePosition = e.Location;
                isMouseDown = true;
                renderPanel.Capture = true;
            }
        }

        private void Panel_MouseMove(object sender, MouseEventArgs e)
        {
            if (isMouseDown)
            {
                int deltaX = e.X - lastMousePosition.X;
                int deltaY = e.Y - lastMousePosition.Y;

                if (deltaX != 0 || deltaY != 0)
                {
                    CTViewerNative.RotateCamera(deltaX * 0.01f, deltaY * 0.01f);
                    lastMousePosition = e.Location;
                }
            }
        }

        private void Panel_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isMouseDown = false;
                renderPanel.Capture = false;
            }
        }

        private void Panel_MouseWheel(object sender, MouseEventArgs e)
        {
            CTViewerNative.ZoomCamera(e.Delta * 0.001f);
        }

        private void Panel_Resize(object sender, EventArgs e)
        {
            if (isInitialized && renderPanel.Width > 0 && renderPanel.Height > 0)
                CTViewerNative.Resize(renderPanel.Width, renderPanel.Height);
        }

        private byte[] ExtractVolumeData(ChunkedVolume volume)
        {
            int width = volume.Width;
            int height = volume.Height;
            int depth = volume.Depth;
            byte[] buffer = new byte[width * height * depth];

            // For large volumes, we need to be careful about memory
            const int MAX_SLICES_PER_BATCH = 10;

            for (int z = 0; z < depth; z += MAX_SLICES_PER_BATCH)
            {
                int batchSize = Math.Min(MAX_SLICES_PER_BATCH, depth - z);

                for (int batchZ = 0; batchZ < batchSize; batchZ++)
                {
                    int currentZ = z + batchZ;
                    int offset = currentZ * width * height;

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            buffer[offset + y * width + x] = volume[x, y, currentZ];
                        }
                    }
                }
            }

            return buffer;
        }

        private byte[] ExtractLabelData(ChunkedLabelVolume volume)
        {
            int width = volume.Width;
            int height = volume.Height;
            int depth = volume.Depth;
            byte[] buffer = new byte[width * height * depth];

            // For large volumes, we need to be careful about memory
            const int MAX_SLICES_PER_BATCH = 10;

            for (int z = 0; z < depth; z += MAX_SLICES_PER_BATCH)
            {
                int batchSize = Math.Min(MAX_SLICES_PER_BATCH, depth - z);

                for (int batchZ = 0; batchZ < batchSize; batchZ++)
                {
                    int currentZ = z + batchZ;
                    int offset = currentZ * width * height;

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            buffer[offset + y * width + x] = volume[x, y, currentZ];
                        }
                    }
                }
            }

            return buffer;
        }

        public void Dispose()
        {
            if (!disposed)
            {
                renderTimer.Stop();

                if (isInitialized)
                    CTViewerNative.Shutdown();

                disposed = true;
            }
        }
    }
}
