// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Numerics;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CTS.D3D11
{
    // --- FIX: Moved enum here from Control Panel ---
    public enum MeasurementMode
    {
        None,
        PlacePoint,
        DrawLineStart
    }

    public partial class D3D11ViewerForm : Form
    {
        private D3D11ControlPanel controlPanel;
        private D3D11VolumeRenderer renderer;
        private Camera camera;
        private MainForm mainForm;

        private Point lastMousePos;
        private volatile bool isRenderLoopRunning = false;
        private readonly System.Windows.Forms.Timer lodResetTimer;
        private CancellationTokenSource renderCancellation;
        private Task renderTask;
        private readonly object disposeLock = new object();
        private volatile bool isDisposing = false;
        private volatile bool formClosed = false;

        public MeasurementMode CurrentMeasurementMode { get; set; } = MeasurementMode.None;
        public event EventHandler<Vector3> PointPicked;

        public D3D11ViewerForm(MainForm mainForm)
        {
            this.mainForm = mainForm;

            InitializeComponent();
            InitializeCamera();

            lodResetTimer = new System.Windows.Forms.Timer { Interval = 250 };
            lodResetTimer.Tick += (s, e) =>
            {
                if (!isDisposing && renderer != null)
                {
                    renderer.SetIsCameraMoving(false);
                }
                lodResetTimer.Stop();
            };

            try
            {
                renderer = new D3D11VolumeRenderer(this.Handle, ClientSize.Width, ClientSize.Height, mainForm);

                // Create control panel after renderer is initialized
                controlPanel = new D3D11ControlPanel(this, mainForm, renderer);
                controlPanel.Owner = this; // Set owner to ensure proper disposal order
                controlPanel.Show();

                // Start render loop
                StartRenderLoop();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize D3D11 Renderer: {ex.Message}\n\nThis might be due to missing DirectX components or an unsupported GPU.",
                    "Renderer Initialization Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[D3D11] Renderer Initialization Failed: {ex}");
                formClosed = true;
                this.Close();
                return;
            }
        }

        private void InitializeComponent()
        {
            this.Text = "CTS 3D Viewer (Direct3D 11)";
            this.Size = new Size(1024, 768);
            this.BackColor = Color.Black;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterScreen;

            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { /* Silently fail if icon is not found */ }
        }

        private void InitializeCamera()
        {
            float volumeSize = Math.Max(mainForm.GetWidth(), Math.Max(mainForm.GetHeight(), mainForm.GetDepth()));
            camera = new Camera(
                new Vector3(volumeSize * 1.5f, volumeSize * 0.5f, volumeSize * 1.5f),
                new Vector3(volumeSize * 0.5f, volumeSize * 0.5f, volumeSize * 0.5f),
                Vector3.UnitY,
                (float)ClientSize.Width / ClientSize.Height);
        }

        private void StartRenderLoop()
        {
            lock (disposeLock)
            {
                if (isDisposing || formClosed) return;

                renderCancellation = new CancellationTokenSource();
                isRenderLoopRunning = true;

                renderTask = Task.Run(async () => await RenderLoop(renderCancellation.Token), renderCancellation.Token);
            }
        }

        private async Task RenderLoop(CancellationToken cancellationToken)
        {
            // Wait a bit to ensure everything is initialized
            await Task.Delay(100, cancellationToken);

            while (!cancellationToken.IsCancellationRequested && isRenderLoopRunning && !formClosed)
            {
                try
                {
                    // Check if we can render
                    bool canRender = false;
                    lock (disposeLock)
                    {
                        canRender = !isDisposing && !formClosed && renderer != null && !renderer.IsDisposed;
                    }

                    if (!canRender)
                    {
                        break;
                    }

                    // Check if form is still valid
                    if (IsDisposed || !IsHandleCreated)
                    {
                        break;
                    }

                    // Render on UI thread for safety
                    if (InvokeRequired)
                    {
                        try
                        {
                            // Use BeginInvoke to avoid blocking
                            BeginInvoke(new Action(() =>
                            {
                                lock (disposeLock)
                                {
                                    if (!isDisposing && !formClosed && renderer != null && !renderer.IsDisposed)
                                    {
                                        renderer.Render(camera);
                                    }
                                }
                            }));
                        }
                        catch (ObjectDisposedException)
                        {
                            // Form was disposed while we were trying to invoke
                            break;
                        }
                        catch (InvalidOperationException)
                        {
                            // Handle cannot be created
                            break;
                        }
                    }
                    else
                    {
                        lock (disposeLock)
                        {
                            if (!isDisposing && !formClosed && renderer != null && !renderer.IsDisposed)
                            {
                                renderer.Render(camera);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[RenderLoop] Error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(16, cancellationToken); // ~60 FPS
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            Logger.Log("[RenderLoop] Exited cleanly");
        }

        public void UpdateRenderParameters(RenderParameters parameters)
        {
            lock (disposeLock)
            {
                if (!isDisposing && renderer != null && !renderer.IsDisposed)
                {
                    renderer.SetRenderParams(parameters);
                }
            }
        }

        public void UpdateMaterialBuffer()
        {
            lock (disposeLock)
            {
                if (!isDisposing && renderer != null && !renderer.IsDisposed)
                {
                    renderer.UpdateMaterialsBuffer();
                }
            }
        }

        public void UpdateMeasurementData(List<MeasurementObject> measurements)
        {
            lock (disposeLock)
            {
                if (!isDisposing && renderer != null && !renderer.IsDisposed)
                {
                    // --- FIX: Corrected type casting error ---
                    renderer.UpdateMeasurementData(measurements);
                }
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            lock (disposeLock)
            {
                if (!isDisposing && renderer != null && !renderer.IsDisposed &&
                    ClientSize.Width > 0 && ClientSize.Height > 0)
                {
                    renderer.Resize(ClientSize.Width, ClientSize.Height);
                    if (camera != null)
                    {
                        camera.AspectRatio = (float)ClientSize.Width / ClientSize.Height;
                    }
                }
            }
        }

        private void NotifyCameraMoving()
        {
            lock (disposeLock)
            {
                if (!isDisposing && renderer != null && !renderer.IsDisposed)
                {
                    renderer.SetIsCameraMoving(true);
                    renderer.NeedsRender = true;
                }
            }

            lodResetTimer.Stop();
            lodResetTimer.Start();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Add F12 as screenshot hotkey
            if (keyData == Keys.F12)
            {
                if (controlPanel != null && !controlPanel.IsDisposed)
                {
                    controlPanel.TakeScreenshot();
                }
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (CurrentMeasurementMode != MeasurementMode.None && e.Button == MouseButtons.Left)
            {
                lock (disposeLock)
                {
                    if (isDisposing || renderer == null || renderer.IsDisposed) return;
                    var pickedPosition = renderer.Pick(e.Location, camera);
                    if (pickedPosition != Vector3.Zero)
                    {
                        PointPicked?.Invoke(this, pickedPosition);
                    }
                }
            }
            else
            {
                lastMousePos = e.Location;
                NotifyCameraMoving();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (CurrentMeasurementMode != MeasurementMode.None)
            {
                Cursor = Cursors.Cross;
            }
            else
            {
                Cursor = Cursors.Default;
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
                else if (e.Button == MouseButtons.Middle) // Alternative zoom
                {
                    float dy = (e.Y - lastMousePos.Y) * 0.01f;
                    camera.Zoom(dy);
                    NotifyCameraMoving();
                }
            }
            lastMousePos = e.Location;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            camera.Zoom(e.Delta * -0.001f);
            NotifyCameraMoving();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Logger.Log("[D3D11ViewerForm] Form closing started");

            lock (disposeLock)
            {
                if (isDisposing) return; // Already disposing
                isDisposing = true;
                formClosed = true;
                isRenderLoopRunning = false;
            }

            // Stop the render loop first
            if (renderCancellation != null)
            {
                renderCancellation.Cancel();
                try
                {
                    // Wait for render loop to finish, but not indefinitely
                    if (renderTask != null && !renderTask.Wait(2000))
                    {
                        Logger.Log("[D3D11ViewerForm] Render task did not complete in time");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[D3D11ViewerForm] Exception waiting for render task: {ex.Message}");
                }

                renderCancellation.Dispose();
                renderCancellation = null;
            }

            // Stop timers
            lodResetTimer?.Stop();
            lodResetTimer?.Dispose();

            // Close control panel first (it references the renderer)
            if (controlPanel != null && !controlPanel.IsDisposed)
            {
                try
                {
                    controlPanel.Close();
                }
                catch { }
                controlPanel = null;
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Logger.Log("[D3D11ViewerForm] Form closed, disposing renderer");

            // Dispose renderer last
            lock (disposeLock)
            {
                if (renderer != null)
                {
                    try
                    {
                        renderer.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[D3D11ViewerForm] Error disposing renderer: {ex.Message}");
                    }
                    renderer = null;
                }
            }

            base.OnFormClosed(e);

            Logger.Log("[D3D11ViewerForm] Form closed completely");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (disposeLock)
                {
                    isDisposing = true;
                    formClosed = true;
                }

                // Ensure everything is cleaned up
                lodResetTimer?.Dispose();
                renderCancellation?.Dispose();

                // The renderer should already be disposed in OnFormClosed
                if (renderer != null)
                {
                    try
                    {
                        renderer.Dispose();
                    }
                    catch { }
                    renderer = null;
                }

                // Clear references
                camera = null;
                mainForm = null;
            }

            base.Dispose(disposing);
        }
    }
}