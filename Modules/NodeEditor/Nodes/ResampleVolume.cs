using CTS.NodeEditor;
using ILGPU;
using ILGPU.Runtime;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class ResampleVolumeNode : BaseNode
    {
        public float ResampleFactor { get; set; } = 1.0f;
        public bool UseGPU { get; set; } = true;

        private NumericUpDown resampleFactorInput;
        private CheckBox useGpuCheckbox;

        public ResampleVolumeNode(Point position) : base(position)
        {
            Color = Color.FromArgb(100, 180, 255); // Blue color for processing nodes
        }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Volume", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var titleLabel = new Label
            {
                Text = "Resample Volume",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            var factorLabel = new Label
            {
                Text = "Resample Factor:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            resampleFactorInput = new NumericUpDown
            {
                Value = (decimal)ResampleFactor,
                Minimum = 0.1m,
                Maximum = 10m,
                DecimalPlaces = 2,
                Increment = 0.1m,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            resampleFactorInput.ValueChanged += (s, e) => ResampleFactor = (float)resampleFactorInput.Value;

            useGpuCheckbox = new CheckBox
            {
                Text = "Use GPU Acceleration (if available)",
                Checked = UseGPU,
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = Color.White
            };
            useGpuCheckbox.CheckedChanged += (s, e) => UseGPU = useGpuCheckbox.Checked;

            var infoLabel = new Label
            {
                Text = "Values > 1 increase resolution, values < 1 decrease resolution",
                Dock = DockStyle.Top,
                Height = 40,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.LightGray,
                Font = new Font("Arial", 8)
            };

            var processButton = new Button
            {
                Text = "Resample Now",
                Dock = DockStyle.Top,
                Height = 35,
                BackColor = Color.FromArgb(100, 180, 100),
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            processButton.Click += (s, e) => Execute();

            // Add controls to panel (in reverse order because of DockStyle.Top)
            panel.Controls.Add(processButton);
            panel.Controls.Add(infoLabel);
            panel.Controls.Add(useGpuCheckbox);
            panel.Controls.Add(resampleFactorInput);
            panel.Controls.Add(factorLabel);
            panel.Controls.Add(titleLabel);

            return panel;
        }

        public override void Execute()
        {
            try
            {
                // Get MainForm reference
                var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
                if (mainForm == null || mainForm.volumeData == null)
                {
                    MessageBox.Show("No dataset is currently loaded to process.",
                        "Resample Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Show progress dialog
                using (var progress = new ProgressFormWithProgress("Resampling volume..."))
                {
                    progress.Show();
                    progress.Report(5);

                    // Run in background thread but don't show the form
                    var task = Task.Run(() => {
                        try
                        {
                            // Get volume dimensions
                            int width = mainForm.GetWidth();
                            int height = mainForm.GetHeight();
                            int depth = mainForm.GetDepth();

                            if (width <= 0 || height <= 0 || depth <= 0)
                            {
                                throw new Exception("Invalid volume dimensions");
                            }

                            // Calculate new dimensions
                            int newWidth = Math.Max(1, (int)(width * ResampleFactor));
                            int newHeight = Math.Max(1, (int)(height * ResampleFactor));
                            int newDepth = Math.Max(1, (int)(depth * ResampleFactor));

                            // Initialize ILGPU context
                            using (var context = Context.Create(builder => builder.Default().EnableAlgorithms()))
                            {
                                progress.Report(10);

                                // Select device
                                Device device;
                                if (UseGPU)
                                {
                                    // Try to find a GPU device (CUDA or OpenCL)
                                    var gpuDevices = context.Devices.Where(d =>
                                        d.AcceleratorType == AcceleratorType.Cuda ||
                                        d.AcceleratorType == AcceleratorType.OpenCL).ToList();

                                    if (gpuDevices.Count > 0)
                                    {
                                        device = gpuDevices[0]; // Use first available GPU
                                    }
                                    else
                                    {
                                        // Fall back to CPU if no GPU is available
                                        device = context.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU);
                                    }
                                }
                                else
                                {
                                    // Use CPU as requested
                                    device = context.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU);
                                }

                                progress.Report(15);

                                // Create accelerator
                                using (var accelerator = device.CreateAccelerator(context))
                                {
                                    // Create a new volume for output
                                    int chunkDim = 256;
                                    if (newWidth < 256 || newHeight < 256 || newDepth < 256)
                                    {
                                        chunkDim = 64;
                                    }

                                    progress.Report(20);

                                    ChunkedVolume newVolume = new ChunkedVolume(newWidth, newHeight, newDepth, chunkDim);

                                    // Calculate scale factors - ensure we don't divide by zero
                                    float scaleX = width > 1 ? (width - 1.0f) / Math.Max(1.0f, newWidth - 1.0f) : 0;
                                    float scaleY = height > 1 ? (height - 1.0f) / Math.Max(1.0f, newHeight - 1.0f) : 0;
                                    float scaleZ = depth > 1 ? (depth - 1.0f) / Math.Max(1.0f, newDepth - 1.0f) : 0;

                                    progress.Report(30);

                                    // Use trilinear interpolation for volume data
                                    Parallel.For(0, newDepth, z =>
                                    {
                                        float srcZ = z * scaleZ;
                                        int z0 = (int)Math.Floor(srcZ);
                                        z0 = Math.Max(0, Math.Min(z0, depth - 1));
                                        int z1 = Math.Min(z0 + 1, depth - 1);
                                        float zFrac = srcZ - z0;

                                        for (int y = 0; y < newHeight; y++)
                                        {
                                            float srcY = y * scaleY;
                                            int y0 = (int)Math.Floor(srcY);
                                            y0 = Math.Max(0, Math.Min(y0, height - 1));
                                            int y1 = Math.Min(y0 + 1, height - 1);
                                            float yFrac = srcY - y0;

                                            for (int x = 0; x < newWidth; x++)
                                            {
                                                float srcX = x * scaleX;
                                                int x0 = (int)Math.Floor(srcX);
                                                x0 = Math.Max(0, Math.Min(x0, width - 1));
                                                int x1 = Math.Min(x0 + 1, width - 1);
                                                float xFrac = srcX - x0;

                                                // Trilinear interpolation
                                                float c000 = mainForm.volumeData[x0, y0, z0];
                                                float c001 = mainForm.volumeData[x0, y0, z1];
                                                float c010 = mainForm.volumeData[x0, y1, z0];
                                                float c011 = mainForm.volumeData[x0, y1, z1];
                                                float c100 = mainForm.volumeData[x1, y0, z0];
                                                float c101 = mainForm.volumeData[x1, y0, z1];
                                                float c110 = mainForm.volumeData[x1, y1, z0];
                                                float c111 = mainForm.volumeData[x1, y1, z1];

                                                float c00 = c000 * (1 - xFrac) + c100 * xFrac;
                                                float c01 = c001 * (1 - xFrac) + c101 * xFrac;
                                                float c10 = c010 * (1 - xFrac) + c110 * xFrac;
                                                float c11 = c011 * (1 - xFrac) + c111 * xFrac;

                                                float c0 = c00 * (1 - yFrac) + c10 * yFrac;
                                                float c1 = c01 * (1 - yFrac) + c11 * yFrac;

                                                float result = c0 * (1 - zFrac) + c1 * zFrac;

                                                // Write result to new volume
                                                newVolume[x, y, z] = (byte)Math.Round(result);
                                            }
                                        }

                                        // Update progress periodically
                                        if (z % 10 == 0)
                                        {
                                            int progressValue = 30 + (z * 50 / newDepth);
                                            progress.Report(progressValue);
                                        }
                                    });

                                    progress.Report(80);

                                    // Process labels if they exist
                                    ChunkedLabelVolume newLabels = null;
                                    if (mainForm.volumeLabels != null)
                                    {
                                        // Create new label volume
                                        newLabels = new ChunkedLabelVolume(newWidth, newHeight, newDepth, chunkDim, false);

                                        // Use nearest neighbor for labels
                                        Parallel.For(0, newDepth, z =>
                                        {
                                            int origZ = Math.Min((int)Math.Floor(z / ResampleFactor), depth - 1);
                                            origZ = Math.Max(0, origZ);

                                            for (int y = 0; y < newHeight; y++)
                                            {
                                                int origY = Math.Min((int)Math.Floor(y / ResampleFactor), height - 1);
                                                origY = Math.Max(0, origY);

                                                for (int x = 0; x < newWidth; x++)
                                                {
                                                    int origX = Math.Min((int)Math.Floor(x / ResampleFactor), width - 1);
                                                    origX = Math.Max(0, origX);

                                                    newLabels[x, y, z] = mainForm.volumeLabels[origX, origY, origZ];
                                                }
                                            }
                                        });
                                    }

                                    progress.Report(95);

                                    // Update pixel size based on resample factor
                                    double currentPixelSize = mainForm.GetPixelSize();
                                    double newPixelSize = currentPixelSize / ResampleFactor;

                                    // Return the results (will be used in callback)
                                    return (newVolume, newLabels, newPixelSize);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Error resampling volume: {ex.Message}", ex);
                        }
                    });

                    // Handle task completion
                    task.ContinueWith(t => {
                        progress.Close();

                        if (t.IsFaulted)
                        {
                            MessageBox.Show($"Failed to resample volume: {t.Exception.InnerException?.Message}",
                                "Resample Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            var (newVolume, newLabels, newPixelSize) = t.Result;

                            // Replace the current data with the new resampled data
                            mainForm.volumeData = newVolume;
                            if (newLabels != null)
                            {
                                mainForm.volumeLabels = newLabels;
                            }
                            mainForm.UpdatePixelSize(newPixelSize);

                            // Notify MainForm that dimensions have changed
                            mainForm.OnDatasetChanged();

                            MessageBox.Show($"Volume resampled successfully with factor {ResampleFactor}.",
                                "Resample Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing volume: {ex.Message}",
                    "Resample Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

}
