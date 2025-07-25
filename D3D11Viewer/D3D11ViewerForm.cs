// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Numerics;
using System.Diagnostics;
using System.Threading.Tasks;

namespace CTS.D3D11
{
    public partial class D3D11ViewerForm : Form
    {
        private D3D11ControlPanel controlPanel;
        private D3D11VolumeRenderer renderer;
        private Camera camera;
        private MainForm mainForm;

        private Point lastMousePos;
        private bool isRenderLoopRunning = false;
        private readonly System.Windows.Forms.Timer lodResetTimer;

        public D3D11ViewerForm(MainForm mainForm)
        {
            this.mainForm = mainForm;

            InitializeComponent();
            InitializeCamera();

            lodResetTimer = new System.Windows.Forms.Timer { Interval = 250 };
            lodResetTimer.Tick += (s, e) =>
            {
                renderer?.SetIsCameraMoving(false);
                lodResetTimer.Stop();
            };

            try
            {
                renderer = new D3D11VolumeRenderer(this.Handle, ClientSize.Width, ClientSize.Height, mainForm);

                // Create control panel after renderer is initialized
                controlPanel = new D3D11ControlPanel(this, mainForm, renderer);
                controlPanel.Show(this);

                // Start render loop only after everything is initialized
                isRenderLoopRunning = true;
                this.FormClosing += (s, e) => isRenderLoopRunning = false;

                // Start render loop with a small delay to ensure everything is ready
                Task.Delay(100).ContinueWith(_ => Task.Run(RenderLoop));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize D3D11 Renderer: {ex.Message}\n\nThis might be due to missing DirectX components or an unsupported GPU.", "Renderer Initialization Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[D3D11] Renderer Initialization Failed: {ex}");
                this.Close();
                return;
            }
        }

        private void InitializeComponent()
        {
            this.Text = "CTS 3D Viewer (Direct3D 11)";
            this.Size = new Size(1024, 768);
            this.BackColor = Color.Black;
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { /* Silently fail if icon is not found */ }
        }

        private void InitializeCamera()
        {
            float volumeSize = Math.Max(mainForm.GetWidth(), Math.Max(mainForm.GetHeight(), mainForm.GetDepth()));
            camera = new Camera(new Vector3(volumeSize * 1.5f), Vector3.Zero, Vector3.UnitY,
                (float)ClientSize.Width / ClientSize.Height);
        }

        private async Task RenderLoop()
        {
            // Wait a bit more to ensure everything is initialized
            await Task.Delay(100);

            while (isRenderLoopRunning && !IsDisposed)
            {
                try
                {
                    if (renderer != null && !IsDisposed)
                    {
                        // Use BeginInvoke to ensure we're on the UI thread for rendering
                        if (InvokeRequired)
                        {
                            BeginInvoke(new Action(() => renderer.Render(camera)));
                        }
                        else
                        {
                            renderer.Render(camera);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[RenderLoop] Error: {ex.Message}");
                    // Don't crash the loop on render errors
                }

                await Task.Delay(16); // ~60 FPS
            }
        }

        public void UpdateRenderParameters(RenderParameters parameters)
        {
            renderer?.SetRenderParams(parameters);
        }

        public void UpdateMaterialBuffer()
        {
            renderer?.UpdateMaterialsBuffer();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (renderer != null && ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                renderer.Resize(ClientSize.Width, ClientSize.Height);
                if (camera != null)
                {
                    camera.AspectRatio = (float)ClientSize.Width / ClientSize.Height;
                }
            }
        }

        private void NotifyCameraMoving()
        {
            if (renderer != null)
            {
                renderer.SetIsCameraMoving(true);
                renderer.NeedsRender = true; // Force immediate render
            }
            lodResetTimer.Stop();
            lodResetTimer.Start();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            lastMousePos = e.Location;
            NotifyCameraMoving();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button == MouseButtons.Left) // Rotate
            {
                float dx = (e.X - lastMousePos.X) * 0.008f;
                float dy = (e.Y - lastMousePos.Y) * 0.008f;
                camera.Orbit(dx, dy);
                NotifyCameraMoving();
            }
            else if (e.Button == MouseButtons.Right) // Pan
            {
                float dx = (e.X - lastMousePos.X) * 0.5f;
                float dy = (e.Y - lastMousePos.Y) * 0.5f;
                camera.Pan(dx, dy);
                NotifyCameraMoving();
            }
            lastMousePos = e.Location;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            camera.Zoom(e.Delta * -0.001f);
            NotifyCameraMoving();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            isRenderLoopRunning = false;
            lodResetTimer?.Dispose();
            controlPanel?.Close();
            renderer?.Dispose();
            base.OnFormClosed(e);
        }
    }
}