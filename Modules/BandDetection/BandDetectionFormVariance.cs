using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
// Add ILGPU references
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Algorithms;

namespace CTSegmenter
{
    public partial class BandDetectionForm
    {
        // Fields for variance-based detection
        private bool useVarianceDetection = false;
        private int slicesToIntegrate = 10;

        // Separate variance maps and images for each view
        private float[,] xyVarianceMap = null;
        private float[,] xzVarianceMap = null;
        private float[,] yzVarianceMap = null;

        private Bitmap xyVarianceImage = null;
        private Bitmap xzVarianceImage = null;
        private Bitmap yzVarianceImage = null;

        private bool isShowingVarianceMap = false;
        private Button btnSwitchToNormal;

        // Arrays for variance-based peaks
        private int[] xyVarianceDarkPeaks = null;
        private int[] xyVarianceBrightPeaks = null;
        private int[] xzVarianceDarkPeaks = null;
        private int[] xzVarianceBrightPeaks = null;
        private int[] yzVarianceDarkPeaks = null;
        private int[] yzVarianceBrightPeaks = null;

        // UI components for variance-based detection
        private TabControl tabControl;
        private CheckBox chkUseVariance;
        private NumericUpDown numSlicesToIntegrate;
        private Label lblSlicesToIntegrate;
        private Button btnCalculateVariance;
        private Button btnVarianceComposite;
        private Label gpuStatusLabel;
        private CancellationTokenSource varianceCancellationTokenSource;

        private float varianceThreshold = 0.0001f;
        private float varianceContrastFactor = 0.5f;
        private bool invertVarianceDisplay = true;
        private string varianceDisplayMode = "Variance"; // Could be "Variance" or "StdDev"
        private Button btnExportVarianceData;

        // ILGPU fields
        private bool gpuAvailable = false;
        private Context ilgpuContext = null;
        private Accelerator accelerator = null;
        private Action<Index1D, ArrayView<byte>, ArrayView<float>, int, int, int, int, int> calculateVarianceKernel = null;

        // Method to add variance-based detection UI components
        private void InitializeVarianceDetection()
        {
            try
            {
                Logger.Log("[BandDetectionForm] Initializing variance detection controls");

                // Initialize ILGPU
                InitializeGPU();

                // Make parameters groupbox taller
                parametersGroupBox.Height = 220;

                // Create tab control for variance detection
                tabControl = new TabControl
                {
                    Dock = DockStyle.Fill,
                    Height = 200
                };

                TabPage standardTab = new TabPage("Standard");
                TabPage varianceTab = new TabPage("Variance Detection");

                // Move existing controls to the standard tab
                Panel standardPanel = new Panel
                {
                    Dock = DockStyle.Fill,
                    AutoScroll = true
                };

                // Copy controls to the standard panel
                Control[] originalControls = new Control[parametersGroupBox.Controls.Count];
                parametersGroupBox.Controls.CopyTo(originalControls, 0);

                foreach (Control control in originalControls)
                {
                    if (!(control is Label && control.Text == "Processing Parameters"))
                    {
                        parametersGroupBox.Controls.Remove(control);
                        standardPanel.Controls.Add(control);
                    }
                }

                standardTab.Controls.Add(standardPanel);

                // Create variance detection panel with controls
                Panel variancePanel = new Panel
                {
                    Dock = DockStyle.Fill,
                    AutoScroll = true,
                    Padding = new Padding(10)
                };

                // Add variance detection controls
                chkUseVariance = new CheckBox
                {
                    Text = "Use Variance Detection",
                    Checked = useVarianceDetection,
                    AutoSize = true,
                    Location = new Point(10, 10)
                };

                lblSlicesToIntegrate = new Label
                {
                    Text = "Slices to Integrate:",
                    AutoSize = true,
                    Location = new Point(10, 40),
                    Enabled = useVarianceDetection
                };

                numSlicesToIntegrate = new NumericUpDown
                {
                    Minimum = 2,
                    Maximum = 100,
                    Value = slicesToIntegrate,
                    Width = 60,
                    Location = new Point(120, 38),
                    Enabled = useVarianceDetection
                };

                // Row 2: Variance threshold and contrast
                Label lblVarianceThreshold = new Label
                {
                    Text = "Variance Threshold:",
                    AutoSize = true,
                    Location = new Point(200, 10),
                    Enabled = useVarianceDetection
                };

                NumericUpDown numVarianceThreshold = new NumericUpDown
                {
                    Minimum = 0.000001m, // Enforce non-zero minimum
                    Maximum = 1,
                    DecimalPlaces = 6,
                    Increment = 0.000001m,
                    Value = Math.Max(0.000001m, (decimal)varianceThreshold), // Ensure initial value is valid
                    Width = 80,
                    Location = new Point(310, 8),
                    Enabled = useVarianceDetection
                };

                Label lblContrastFactor = new Label
                {
                    Text = "Contrast Factor:",
                    AutoSize = true,
                    Location = new Point(200, 40),
                    Enabled = useVarianceDetection
                };

                NumericUpDown numContrastFactor = new NumericUpDown
                {
                    Minimum = 0.1m,
                    Maximum = 5m,
                    DecimalPlaces = 2,
                    Increment = 0.1m,
                    Value = (decimal)varianceContrastFactor,
                    Width = 60,
                    Location = new Point(310, 38),
                    Enabled = useVarianceDetection
                };

                // Row 3: Display options
                CheckBox chkInvertVariance = new CheckBox
                {
                    Text = "Invert Variance Display (Dark = Low Variance)",
                    Checked = invertVarianceDisplay,
                    AutoSize = true,
                    Location = new Point(10, 70),
                    Enabled = useVarianceDetection
                };

                // Row 4: Buttons
                btnCalculateVariance = new Button
                {
                    Text = "Calculate Variance Maps",
                    Width = 160,
                    Location = new Point(10, 100),
                    Enabled = useVarianceDetection
                };

                btnSwitchToNormal = new Button
                {
                    Text = "Show Normal View",
                    Width = 160,
                    Location = new Point(180, 100),
                    Enabled = false
                };

                btnVarianceComposite = new Button
                {
                    Text = "Save Variance Composite",
                    Width = 160,
                    Location = new Point(10, 130),
                    Enabled = false
                };
                btnExportVarianceData = new Button
                {
                    Text = "Export Variance Data",
                    Width = 160,
                    Location = new Point(180, 130), // Next to variance composite button
                    Enabled = false // Will be enabled when variance maps are calculated
                };
                btnExportVarianceData.Click += (s, e) => ExportVarianceData();

                // Event handlers
                chkUseVariance.CheckedChanged += (s, e) =>
                {
                    useVarianceDetection = chkUseVariance.Checked;
                    numSlicesToIntegrate.Enabled = useVarianceDetection;
                    lblSlicesToIntegrate.Enabled = useVarianceDetection;
                    numVarianceThreshold.Enabled = useVarianceDetection;
                    lblVarianceThreshold.Enabled = useVarianceDetection;
                    numContrastFactor.Enabled = useVarianceDetection;
                    lblContrastFactor.Enabled = useVarianceDetection;
                    chkInvertVariance.Enabled = useVarianceDetection;
                    btnExportVarianceData.Enabled = useVarianceDetection && xyVarianceImage != null;
                    btnCalculateVariance.Enabled = useVarianceDetection;
                    btnVarianceComposite.Enabled = useVarianceDetection && xyVarianceImage != null;
                    btnSwitchToNormal.Enabled = useVarianceDetection && xyVarianceImage != null;
                };

                numSlicesToIntegrate.ValueChanged += (s, e) => slicesToIntegrate = (int)numSlicesToIntegrate.Value;
                numVarianceThreshold.ValueChanged += (s, e) => varianceThreshold = (float)numVarianceThreshold.Value;
                numContrastFactor.ValueChanged += (s, e) => varianceContrastFactor = (float)numContrastFactor.Value;
                chkInvertVariance.CheckedChanged += (s, e) =>
                {
                    invertVarianceDisplay = chkInvertVariance.Checked;
                    // If we have variance maps, recalculate the display
                    if (xyVarianceMap != null && isShowingVarianceMap)
                    {
                        xyVarianceImage?.Dispose();
                        xzVarianceImage?.Dispose();
                        yzVarianceImage?.Dispose();

                        xyVarianceImage = CreateVarianceMapImage(xyVarianceMap);
                        xzVarianceImage = CreateVarianceMapImage(xzVarianceMap);
                        yzVarianceImage = CreateVarianceMapImage(yzVarianceMap);

                        ShowVarianceMaps();
                    }
                };

                btnCalculateVariance.Click += (s, e) => CalculateVarianceMaps();
                btnSwitchToNormal.Click += (s, e) => ToggleVarianceView();
                btnVarianceComposite.Click += (s, e) => CreateVarianceComposite();

                // Status label for GPU status
                gpuStatusLabel = new Label
                {
                    AutoSize = true,
                    Location = new Point(10, 160),
                    Font = new Font("Arial", 8),
                    ForeColor = gpuAvailable ? Color.Green : Color.Blue,
                    Text = gpuAvailable ?
                        "Status: GPU acceleration enabled" :
                        "Status: CPU mode (no GPU acceleration)"
                };

                // Add all controls to variance panel
                variancePanel.Controls.Add(chkUseVariance);
                variancePanel.Controls.Add(lblSlicesToIntegrate);
                variancePanel.Controls.Add(numSlicesToIntegrate);
                variancePanel.Controls.Add(lblVarianceThreshold);
                variancePanel.Controls.Add(numVarianceThreshold);
                variancePanel.Controls.Add(lblContrastFactor);
                variancePanel.Controls.Add(numContrastFactor);
                variancePanel.Controls.Add(chkInvertVariance);
                variancePanel.Controls.Add(btnCalculateVariance);
                variancePanel.Controls.Add(btnSwitchToNormal);
                variancePanel.Controls.Add(btnVarianceComposite);
                variancePanel.Controls.Add(btnExportVarianceData);
                variancePanel.Controls.Add(gpuStatusLabel);
                numVarianceThreshold.ValueChanged += (s, e) =>
                {
                    // Prevent exact zero values - choose a small positive number instead
                    if ((decimal)numVarianceThreshold.Value <= 0m)
                    {
                        Logger.Log("[BandDetectionForm] Warning: Zero threshold detected, using minimum safe value");
                        numVarianceThreshold.Value = 0.000001m; // Use 1e-6 as minimum
                        varianceThreshold = 0.000001f;
                    }
                    else
                    {
                        varianceThreshold = (float)numVarianceThreshold.Value;
                    }

                    // Regenerate images when parameters change
                    RegenerateVarianceImages();
                };

                numContrastFactor.ValueChanged += (s, e) =>
                {
                    varianceContrastFactor = (float)numContrastFactor.Value;
                    // Regenerate images when parameters change
                    RegenerateVarianceImages();
                };

                // Add description label
                Label descriptionLabel = new Label
                {
                    Text = "This feature calculates variance across slices to identify bands\n" +
                           "that maintain consistent density through the volume.\n" +
                           "Low variance (bright when inverted) indicates potential density bands.",
                    AutoSize = true,
                    Location = new Point(10, 185)
                };

                variancePanel.Controls.Add(descriptionLabel);
                Label lblSliceInfo = new Label
                {
                    Text = "Note: Variance is calculated across slices centered on the current preview position,\n" +
           "extending half the integration count in each direction.",
                    AutoSize = true,
                    Location = new Point(10, 230),
                    Font = new Font("Arial", 8, FontStyle.Italic),
                    ForeColor = Color.DarkBlue
                };
                variancePanel.Controls.Add(lblSliceInfo);
                varianceTab.Controls.Add(variancePanel);

                // Add tabs to tab control
                tabControl.Controls.Add(standardTab);
                tabControl.Controls.Add(varianceTab);

                // Add tab control to parameters group box
                parametersGroupBox.Controls.Add(tabControl);

                // Select the standard tab by default
                tabControl.SelectedIndex = 0;

                // Increase form height
                this.Height += 100;

                Logger.Log("[BandDetectionForm] Variance detection controls initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error initializing variance detection controls: {ex.Message}");
                MessageBox.Show($"Error setting up variance detection: {ex.Message}", "Initialization Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Initialize ILGPU context and accelerator
        private void InitializeGPU()
        {
            try
            {
                Logger.Log("[BandDetectionForm] Initializing ILGPU...");

                // Create context
                ilgpuContext = Context.Create(builder => builder.Default().EnableAlgorithms());

                // Get all devices
                var devices = ilgpuContext.Devices;
                Logger.Log($"[BandDetectionForm] Found {devices.Length} ILGPU device(s)");

                // Try to create an accelerator using the best available device
                foreach (var device in devices)
                {
                    try
                    {
                        // Prefer CUDA > OpenCL > CPU
                        if (device.AcceleratorType == AcceleratorType.Cuda ||
                            device.AcceleratorType == AcceleratorType.OpenCL)
                        {
                            Logger.Log($"[BandDetectionForm] Trying {device.AcceleratorType} device: {device.Name}");
                            accelerator = device.CreateAccelerator(ilgpuContext);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[BandDetectionForm] Failed to create accelerator for {device.Name}: {ex.Message}");
                    }
                }

                // If no GPU device available, fall back to CPU
                if (accelerator == null && devices.Length > 0)
                {
                    Logger.Log("[BandDetectionForm] No GPU devices available, falling back to CPU accelerator");
                    try
                    {
                        var cpuDevice = devices.First(d => d.AcceleratorType == AcceleratorType.CPU);
                        accelerator = cpuDevice.CreateAccelerator(ilgpuContext);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[BandDetectionForm] Failed to create CPU accelerator: {ex.Message}");
                    }
                }

                if (accelerator != null)
                {
                    Logger.Log($"[BandDetectionForm] Successfully initialized {accelerator.AcceleratorType} accelerator: {accelerator.Name}");

                    // Compile kernels
                    CompileKernels();

                    // Set GPU available flag based on accelerator type
                    gpuAvailable = accelerator.AcceleratorType == AcceleratorType.Cuda ||
                                   accelerator.AcceleratorType == AcceleratorType.OpenCL;

                    Logger.Log($"[BandDetectionForm] GPU acceleration is {(gpuAvailable ? "enabled" : "disabled")}");
                }
                else
                {
                    Logger.Log("[BandDetectionForm] Failed to initialize any ILGPU accelerator");
                    gpuAvailable = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error initializing ILGPU: {ex.Message}");
                gpuAvailable = false;
            }
        }

        // Compile ILGPU kernels
        private void CompileKernels()
        {
            if (accelerator == null)
                return;

            try
            {
                // Compile the variance calculation kernel
                calculateVarianceKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<float>, int, int, int, int, int>(
                    CalculateVarianceKernel);

                Logger.Log("[BandDetectionForm] Successfully compiled ILGPU kernels");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error compiling ILGPU kernels: {ex.Message}");
                // Disable GPU if kernel compilation fails
                gpuAvailable = false;
            }
        }

        // ILGPU kernel for calculating variance
        private static void CalculateVarianceKernel(
            Index1D index,
            ArrayView<byte> volumeData,
            ArrayView<float> varianceMap,
            int width,
            int height,
            int depth,
            int sliceStart,
            int sliceEnd)
        {
            // Calculate x, y coordinates from 1D index
            int x = index % width;
            int y = index / width;

            // Skip if out of bounds
            if (x >= width || y >= height)
                return;

            // Calculate variance for the current position
            float sum = 0.0f;
            float sumSquared = 0.0f;
            int validSlices = 0;

            // For each slice in the range
            for (int z = sliceStart; z <= sliceEnd; z++)
            {
                // Skip if out of bounds
                if (z < 0 || z >= depth)
                    continue;

                // Get voxel value and normalize to 0-1
                int offset = z * width * height + y * width + x;
                if (offset >= 0 && offset < volumeData.Length)
                {
                    byte voxelValue = volumeData[offset];
                    float val = voxelValue / 255.0f;

                    // Update sums
                    sum += val;
                    sumSquared += val * val;
                    validSlices++;
                }
            }

            // Calculate variance only if we have at least 2 slices
            if (validSlices >= 2)
            {
                float mean = sum / validSlices;
                float variance = (sumSquared / validSlices) - (mean * mean);

                // Store variance in the output map
                varianceMap[y * width + x] = variance;
            }
        }

        private void RegenerateVarianceImages()
        {
            if (xyVarianceMap == null || !isShowingVarianceMap)
                return;

            try
            {
                // Create temporary variables to hold new images
                Bitmap newXyImage = null;
                Bitmap newXzImage = null;
                Bitmap newYzImage = null;

                try
                {
                    // Regenerate images with new parameters
                    newXyImage = CreateVarianceMapImage(xyVarianceMap);
                    newXzImage = CreateVarianceMapImage(xzVarianceMap);
                    newYzImage = CreateVarianceMapImage(yzVarianceMap);

                    // Only when all images are successfully created, dispose old ones and assign new ones
                    Bitmap oldXyImage = xyVarianceImage;
                    Bitmap oldXzImage = xzVarianceImage;
                    Bitmap oldYzImage = yzVarianceImage;

                    xyVarianceImage = newXyImage;
                    xzVarianceImage = newXzImage;
                    yzVarianceImage = newYzImage;

                    // Now safely dispose old images
                    oldXyImage?.Dispose();
                    oldXzImage?.Dispose();
                    oldYzImage?.Dispose();

                    newXyImage = newXzImage = newYzImage = null; // Clear references so they aren't disposed in finally
                }
                catch (Exception ex)
                {
                    Logger.Log($"[BandDetectionForm] Error regenerating variance images: {ex.Message}");
                    throw;
                }
                finally
                {
                    // Clean up on failure
                    newXyImage?.Dispose();
                    newXzImage?.Dispose();
                    newYzImage?.Dispose();
                }

                // Recalculate peaks with new threshold
                DetectBandsInXYVarianceMap();
                DetectBandsInXZVarianceMap();
                DetectBandsInYZVarianceMap();

                // Update display
                ShowVarianceMaps();

                // Update UI
                this.Invoke(new Action(() => {
                    btnSwitchToNormal.Text = "Show Normal View";
                    btnVarianceComposite.Enabled = true;
                }));
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error regenerating variance images: {ex.Message}");
                MessageBox.Show($"Error applying new threshold: {ex.Message}",
                              "Parameter Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Method to toggle between normal and variance views
        private void ToggleVarianceView()
        {
            try
            {
                if (isShowingVarianceMap)
                {
                    // Switch back to normal view - with robust checks
                    bool normalViewsAvailable = xyProcessedImage != null && xzProcessedImage != null && yzProcessedImage != null;

                    if (normalViewsAvailable)
                    {
                        RestoreNormalViews();
                        btnSwitchToNormal.Text = "Show Variance Maps";
                        isShowingVarianceMap = false;
                    }
                    else
                    {
                        // Normal views not available, need to process them first
                        MessageBox.Show("Normal views need to be processed first.",
                                      "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        isShowingVarianceMap = false;
                        Task.Run(() => ProcessAllViews());
                    }
                }
                else
                {
                    // Check if variance maps exist before trying to show them
                    bool varianceMapsExist = xyVarianceImage != null && xzVarianceImage != null && yzVarianceImage != null;

                    if (varianceMapsExist)
                    {
                        // Switch to variance view
                        ShowVarianceMaps();
                        btnSwitchToNormal.Text = "Show Normal View";
                        isShowingVarianceMap = true;
                    }
                    else
                    {
                        MessageBox.Show("Please calculate variance maps first.",
                                      "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error toggling variance view: {ex.Message}");
                MessageBox.Show($"Error switching views: {ex.Message}",
                              "View Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        // Method to restore normal views
        private void RestoreNormalViews()
        {
            if (xyProcessedImage != null && xyPictureBox != null)
            {
                xyPictureBox.Image = xyProcessedImage;
                xyPictureBox.Invalidate();
            }

            if (xzProcessedImage != null && xzPictureBox != null)
            {
                xzPictureBox.Image = xzProcessedImage;
                xzPictureBox.Invalidate();
            }

            if (yzProcessedImage != null && yzPictureBox != null)
            {
                yzPictureBox.Image = yzProcessedImage;
                yzPictureBox.Invalidate();
            }

            // Restore original chart data
            UpdateXYChart();
            UpdateXZChart();
            UpdateYZChart();
        }

        // Method to show variance maps
        private void ShowVarianceMaps()
        {
            if (xyVarianceImage != null && xyPictureBox != null)
            {
                xyPictureBox.Image = xyVarianceImage;
                xyPictureBox.Invalidate();
                UpdateVarianceXYChart();
            }

            if (xzVarianceImage != null && xzPictureBox != null)
            {
                xzPictureBox.Image = xzVarianceImage;
                xzPictureBox.Invalidate();
                UpdateVarianceXZChart();
            }

            if (yzVarianceImage != null && yzPictureBox != null)
            {
                yzPictureBox.Image = yzVarianceImage;
                yzPictureBox.Invalidate();
                UpdateVarianceYZChart();
            }
        }

        // Method to update XY chart with variance data
        private void UpdateVarianceXYChart()
        {
            if (xyVarianceMap == null || xyChart == null)
                return;

            try
            {
                // Make sure chart has correct series
                if (xyChart.Series.Count == 0 || xyChart.Series["Profile"] == null)
                    ConfigureChart(xyChart, "XY Variance Profile", "Variance", "Row");

                // Clear all points
                foreach (var series in xyChart.Series)
                {
                    series.Points.Clear();
                }

                // Create row profile from variance map
                double[] rowProfile = new double[xyVarianceMap.GetLength(1)];
                for (int y = 0; y < xyVarianceMap.GetLength(1); y++)
                {
                    double sum = 0;
                    for (int x = 0; x < xyVarianceMap.GetLength(0); x++)
                    {
                        sum += xyVarianceMap[x, y];
                    }
                    rowProfile[y] = sum / xyVarianceMap.GetLength(0);
                }

                // Apply Gaussian smoothing to the row profile
                double[] smoothRowProfile = GaussianSmoothArray(rowProfile, (float)gaussianSigma);

                // Add profile data
                for (int i = 0; i < smoothRowProfile.Length; i++)
                {
                    xyChart.Series["Profile"].Points.AddXY(smoothRowProfile[i], i);
                }

                // Add dark peaks
                if (showPeaks && xyVarianceDarkPeaks != null)
                {
                    foreach (int peak in xyVarianceDarkPeaks)
                    {
                        if (peak < smoothRowProfile.Length)
                            xyChart.Series["Dark Peaks"].Points.AddXY(smoothRowProfile[peak], peak);
                    }
                }

                // Add bright peaks
                if (showPeaks && xyVarianceBrightPeaks != null)
                {
                    foreach (int peak in xyVarianceBrightPeaks)
                    {
                        if (peak < smoothRowProfile.Length)
                            xyChart.Series["Bright Peaks"].Points.AddXY(smoothRowProfile[peak], peak);
                    }
                }

                // Set visibility based on showPeaks
                xyChart.Series["Dark Peaks"].Enabled = showPeaks;
                xyChart.Series["Bright Peaks"].Enabled = showPeaks;

                xyChart.ChartAreas[0].RecalculateAxesScale();
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error updating XY variance chart: {ex.Message}");
            }
        }

        // Method to update XZ chart with variance data
        private void UpdateVarianceXZChart()
        {
            if (xzVarianceMap == null || xzChart == null)
                return;

            try
            {
                // Make sure chart has correct series
                if (xzChart.Series.Count == 0 || xzChart.Series["Profile"] == null)
                    ConfigureChart(xzChart, "XZ Variance Profile", "Variance", "Row");

                // Clear all points
                foreach (var series in xzChart.Series)
                {
                    series.Points.Clear();
                }

                // Create row profile from variance map
                double[] rowProfile = new double[xzVarianceMap.GetLength(1)];
                for (int z = 0; z < xzVarianceMap.GetLength(1); z++)
                {
                    double sum = 0;
                    for (int x = 0; x < xzVarianceMap.GetLength(0); x++)
                    {
                        sum += xzVarianceMap[x, z];
                    }
                    rowProfile[z] = sum / xzVarianceMap.GetLength(0);
                }

                // Apply Gaussian smoothing to the row profile
                double[] smoothRowProfile = GaussianSmoothArray(rowProfile, (float)gaussianSigma);

                // Add profile data
                for (int i = 0; i < smoothRowProfile.Length; i++)
                {
                    xzChart.Series["Profile"].Points.AddXY(smoothRowProfile[i], i);
                }

                // Add dark peaks
                if (showPeaks && xzVarianceDarkPeaks != null)
                {
                    foreach (int peak in xzVarianceDarkPeaks)
                    {
                        if (peak < smoothRowProfile.Length)
                            xzChart.Series["Dark Peaks"].Points.AddXY(smoothRowProfile[peak], peak);
                    }
                }

                // Add bright peaks
                if (showPeaks && xzVarianceBrightPeaks != null)
                {
                    foreach (int peak in xzVarianceBrightPeaks)
                    {
                        if (peak < smoothRowProfile.Length)
                            xzChart.Series["Bright Peaks"].Points.AddXY(smoothRowProfile[peak], peak);
                    }
                }

                // Set visibility based on showPeaks
                xzChart.Series["Dark Peaks"].Enabled = showPeaks;
                xzChart.Series["Bright Peaks"].Enabled = showPeaks;

                xzChart.ChartAreas[0].RecalculateAxesScale();
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error updating XZ variance chart: {ex.Message}");
            }
        }

        // Method to update YZ chart with variance data  
        private void UpdateVarianceYZChart()
        {
            if (yzVarianceMap == null || yzChart == null)
                return;

            try
            {
                // Make sure chart has correct series
                if (yzChart.Series.Count == 0 || yzChart.Series["Profile"] == null)
                    ConfigureChart(yzChart, "YZ Variance Profile", "Variance", "Row");

                // Clear all points
                foreach (var series in yzChart.Series)
                {
                    series.Points.Clear();
                }

                // Create row profile from variance map
                double[] rowProfile = new double[yzVarianceMap.GetLength(1)];
                for (int z = 0; z < yzVarianceMap.GetLength(1); z++)
                {
                    double sum = 0;
                    for (int y = 0; y < yzVarianceMap.GetLength(0); y++)
                    {
                        sum += yzVarianceMap[y, z];
                    }
                    rowProfile[z] = sum / yzVarianceMap.GetLength(0);
                }

                // Apply Gaussian smoothing to the row profile
                double[] smoothRowProfile = GaussianSmoothArray(rowProfile, (float)gaussianSigma);

                // Add profile data
                for (int i = 0; i < smoothRowProfile.Length; i++)
                {
                    yzChart.Series["Profile"].Points.AddXY(smoothRowProfile[i], i);
                }

                // Add dark peaks
                if (showPeaks && yzVarianceDarkPeaks != null)
                {
                    foreach (int peak in yzVarianceDarkPeaks)
                    {
                        if (peak < smoothRowProfile.Length)
                            yzChart.Series["Dark Peaks"].Points.AddXY(smoothRowProfile[peak], peak);
                    }
                }

                // Add bright peaks
                if (showPeaks && yzVarianceBrightPeaks != null)
                {
                    foreach (int peak in yzVarianceBrightPeaks)
                    {
                        if (peak < smoothRowProfile.Length)
                            yzChart.Series["Bright Peaks"].Points.AddXY(smoothRowProfile[peak], peak);
                    }
                }

                // Set visibility based on showPeaks
                yzChart.Series["Dark Peaks"].Enabled = showPeaks;
                yzChart.Series["Bright Peaks"].Enabled = showPeaks;

                yzChart.ChartAreas[0].RecalculateAxesScale();
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error updating YZ variance chart: {ex.Message}");
            }
        }

        // Method to calculate variance maps for all views
        private void CalculateVarianceMaps()
        {
            if (mainForm.volumeData == null)
            {
                MessageBox.Show("No volume data loaded.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                // Cancel any previous operations
                if (varianceCancellationTokenSource != null)
                {
                    varianceCancellationTokenSource.Cancel();
                    varianceCancellationTokenSource.Dispose();
                }

                // Create new cancellation token source
                varianceCancellationTokenSource = new CancellationTokenSource();
                var token = varianceCancellationTokenSource.Token;

                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();
                int sliceCount = (int)numSlicesToIntegrate.Value;

                // Validate parameters
                if (width <= 0 || height <= 0 || depth <= 0)
                {
                    MessageBox.Show("Invalid volume dimensions.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (sliceCount < 2)
                {
                    MessageBox.Show("At least 2 slices are needed for variance calculation.",
                                  "Parameter Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Show progress form
                ProgressForm progressForm = new ProgressForm("Calculating variance maps...");
                progressForm.Show();

                // Disable buttons during calculation
                btnCalculateVariance.Enabled = false;
                btnVarianceComposite.Enabled = false;
                btnSwitchToNormal.Enabled = false;

                // Run the calculation in a background task
                Task.Run(() =>
                {
                    try
                    {
                        Logger.Log($"[BandDetectionForm] Starting variance map calculation with {sliceCount} slices");

                        // Clean up old resources first
                        DisposeVarianceResources();

                        // Create new variance maps
                        xyVarianceMap = new float[width, height];
                        xzVarianceMap = new float[width, depth];
                        yzVarianceMap = new float[height, depth];

                        // Initialize maps with zeros
                        for (int y = 0; y < height; y++)
                            for (int x = 0; x < width; x++)
                                xyVarianceMap[x, y] = 0f;

                        for (int z = 0; z < depth; z++)
                            for (int x = 0; x < width; x++)
                                xzVarianceMap[x, z] = 0f;

                        for (int z = 0; z < depth; z++)
                            for (int y = 0; y < height; y++)
                                yzVarianceMap[y, z] = 0f;

                        // Calculate variance maps with error handling
                        bool xySuccess = false, xzSuccess = false, yzSuccess = false;

                        try
                        {
                            // Calculate XY variance map (25% of progress)
                            progressForm.UpdateProgress(0);
                            if (gpuAvailable && accelerator != null && calculateVarianceKernel != null)
                            {
                                CalculateXYVarianceMapGPU(width, height, depth, sliceCount, progress =>
                                {
                                    if (!token.IsCancellationRequested)
                                        this.Invoke(new Action(() => progressForm.UpdateProgress(progress / 4)));
                                }, token);
                            }
                            else
                            {
                                CalculateXYVarianceMap(width, height, depth, sliceCount, progress =>
                                {
                                    if (!token.IsCancellationRequested)
                                        this.Invoke(new Action(() => progressForm.UpdateProgress(progress / 4)));
                                }, token);
                            }
                            xySuccess = true;
                            token.ThrowIfCancellationRequested();
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[BandDetectionForm] Error in XY variance calculation: {ex.Message}");
                        }

                        try
                        {
                            // Calculate XZ variance map (25-50% of progress)
                            if (gpuAvailable && accelerator != null && calculateVarianceKernel != null)
                            {
                                CalculateXZVarianceMapGPU(width, height, depth, sliceCount, progress =>
                                {
                                    if (!token.IsCancellationRequested)
                                        this.Invoke(new Action(() => progressForm.UpdateProgress(25 + progress / 4)));
                                }, token);
                            }
                            else
                            {
                                CalculateXZVarianceMap(width, height, depth, sliceCount, progress =>
                                {
                                    if (!token.IsCancellationRequested)
                                        this.Invoke(new Action(() => progressForm.UpdateProgress(25 + progress / 4)));
                                }, token);
                            }
                            xzSuccess = true;
                            token.ThrowIfCancellationRequested();
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[BandDetectionForm] Error in XZ variance calculation: {ex.Message}");
                        }

                        try
                        {
                            // Calculate YZ variance map (50-75% of progress)
                            if (gpuAvailable && accelerator != null && calculateVarianceKernel != null)
                            {
                                CalculateYZVarianceMapGPU(width, height, depth, sliceCount, progress =>
                                {
                                    if (!token.IsCancellationRequested)
                                        this.Invoke(new Action(() => progressForm.UpdateProgress(50 + progress / 4)));
                                }, token);
                            }
                            else
                            {
                                CalculateYZVarianceMap(width, height, depth, sliceCount, progress =>
                                {
                                    if (!token.IsCancellationRequested)
                                        this.Invoke(new Action(() => progressForm.UpdateProgress(50 + progress / 4)));
                                }, token);
                            }
                            yzSuccess = true;
                            token.ThrowIfCancellationRequested();
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[BandDetectionForm] Error in YZ variance calculation: {ex.Message}");
                        }

                        // Only continue if at least one calculation succeeded
                        if (!xySuccess && !xzSuccess && !yzSuccess)
                        {
                            throw new Exception("All variance calculations failed.");
                        }

                        // Create bitmaps from the variance maps
                        this.Invoke(new Action(() =>
                        {
                            if (!token.IsCancellationRequested)
                            {
                                progressForm.UpdateProgress(75);

                                try
                                {
                                    // Create images from variance maps
                                    if (xySuccess)
                                        xyVarianceImage = CreateVarianceMapImage(xyVarianceMap);
                                    if (xzSuccess)
                                        xzVarianceImage = CreateVarianceMapImage(xzVarianceMap);
                                    if (yzSuccess)
                                        yzVarianceImage = CreateVarianceMapImage(yzVarianceMap);

                                    progressForm.UpdateProgress(85);

                                    // Detect bands in variance maps
                                    if (xySuccess)
                                        DetectBandsInXYVarianceMap();
                                    if (xzSuccess)
                                        DetectBandsInXZVarianceMap();
                                    if (yzSuccess)
                                        DetectBandsInYZVarianceMap();

                                    progressForm.UpdateProgress(95);

                                    // Show variance maps
                                    ShowVarianceMaps();
                                    isShowingVarianceMap = true;
                                    btnSwitchToNormal.Text = "Show Normal View";

                                    // Re-enable buttons
                                    btnCalculateVariance.Enabled = true;
                                    btnVarianceComposite.Enabled = true;
                                    btnSwitchToNormal.Enabled = true;
                                    btnExportVarianceData.Enabled = true;

                                    progressForm.UpdateProgress(100);
                                    progressForm.Close();

                                    MessageBox.Show($"Variance maps calculation complete.\nSuccessful maps: " +
                                                  $"{(xySuccess ? "XY " : "")}{(xzSuccess ? "XZ " : "")}{(yzSuccess ? "YZ" : "")}",
                                                  "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                }
                                catch (Exception ex)
                                {
                                    progressForm.Close();
                                    MessageBox.Show($"Error creating variance maps: {ex.Message}",
                                                  "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                    btnCalculateVariance.Enabled = true;
                                }
                            }
                        }));
                    }
                    catch (OperationCanceledException)
                    {
                        this.Invoke(new Action(() =>
                        {
                            Logger.Log("[BandDetectionForm] Variance calculation was cancelled");
                            btnCalculateVariance.Enabled = true;
                            progressForm.Close();
                        }));
                    }
                    catch (Exception ex)
                    {
                        this.Invoke(new Action(() =>
                        {
                            Logger.Log($"[BandDetectionForm] Error in variance map calculation: {ex.Message}");
                            MessageBox.Show($"Error calculating variance maps: {ex.Message}",
                                "Calculation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            btnCalculateVariance.Enabled = true;
                            progressForm.Close();
                        }));
                    }
                }, token);
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error setting up variance calculation: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnCalculateVariance.Enabled = true;
            }
        }

        // Calculate XY variance map on GPU
        private void CalculateXYVarianceMapGPU(int width, int height, int depth, int sliceCount, Action<int> progressCallback, CancellationToken token)
        {
            Logger.Log("[BandDetectionForm] Calculating XY variance map using GPU");
            token.ThrowIfCancellationRequested();

            // Get current slice
            int centerSlice = (int)xySliceTrackBar.Value;

            // Calculate starting slice centered around current slice
            int startSlice = Math.Max(0, centerSlice - sliceCount / 2);
            int endSlice = Math.Min(depth - 1, startSlice + sliceCount - 1);
            int actualSliceCount = endSlice - startSlice + 1;

            Logger.Log($"[BandDetectionForm] XY GPU: Calculating variance from slice {startSlice} to {endSlice} (total: {actualSliceCount})");

            // Create a flat copy of the volume data
            byte[] flatVolumeData = new byte[width * height * depth];
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        flatVolumeData[z * width * height + y * width + x] = mainForm.volumeData[x, y, z];
                    }
                }
            }

            try
            {
                // Upload data to GPU
                using (var deviceVolumeData = accelerator.Allocate1D<byte>(flatVolumeData))
                {
                    // Create buffer for results
                    float[] flatVarianceMap = new float[width * height];
                    using (var deviceVarianceMap = accelerator.Allocate1D<float>(flatVarianceMap))
                    {
                        // Launch kernel
                        calculateVarianceKernel(width * height, deviceVolumeData.View, deviceVarianceMap.View,
                                              width, height, depth, startSlice, endSlice);

                        // Wait for GPU to finish
                        accelerator.Synchronize();

                        // Copy results back
                        deviceVarianceMap.CopyToCPU(flatVarianceMap);

                        // Convert flat array to 2D array
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                xyVarianceMap[x, y] = flatVarianceMap[y * width + x];
                            }
                        }
                    }
                }

                // Update progress
                progressCallback(100);
                Logger.Log("[BandDetectionForm] XY variance map GPU calculation complete");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] GPU error in XY variance calculation: {ex.Message}");
                Logger.Log("[BandDetectionForm] Falling back to CPU calculation");

                // Fall back to CPU calculation
                CalculateXYVarianceMap(width, height, depth, sliceCount, progressCallback, token);
            }
        }

        // Calculate XZ variance map on GPU
        private void CalculateXZVarianceMapGPU(int width, int height, int depth, int sliceCount, Action<int> progressCallback, CancellationToken token)
        {
            Logger.Log("[BandDetectionForm] Calculating XZ variance map using GPU");
            token.ThrowIfCancellationRequested();

            // Get current XZ slice position (Y value)
            int centerY = (int)xzSliceTrackBar.Value;

            // Calculate Y range centered around current Y
            int startY = Math.Max(0, centerY - sliceCount / 2);
            int endY = Math.Min(height - 1, startY + sliceCount - 1);
            int actualYCount = endY - startY + 1;

            Logger.Log($"[BandDetectionForm] XZ GPU: Calculating variance from Y {startY} to {endY} (total: {actualYCount})");

            // Create a flat copy of the volume data
            byte[] flatVolumeData = new byte[width * height * depth];
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        flatVolumeData[z * width * height + y * width + x] = mainForm.volumeData[x, y, z];
                    }
                }
            }

            try
            {
                // Process each x-z slice
                // For XZ view, we need to process differently than XY
                // We could create a specialized kernel, but for simplicity and code reuse,
                // we'll do this by transforming to a flat result and then reshaping

                // Upload data to GPU
                using (var deviceVolumeData = accelerator.Allocate1D<byte>(flatVolumeData))
                {
                    // Since the kernel requires a flat output array, we'll have to do some processing
                    float[] flatVarianceMap = new float[width * depth];

                    // Process in batches for better progress reporting
                    int batchSize = Math.Max(1, width * depth / 100);

                    // Process the variance computation on CPU by iterating over each X-Z position
                    Parallel.For(0, width * depth, token.CanBeCanceled ? new ParallelOptions { CancellationToken = token } : new ParallelOptions(), i =>
                    {
                        int x = i % width;
                        int z = i / width;

                        // Calculate mean and variance across Y slices
                        float sum = 0;
                        float sumSquared = 0;
                        int validPositions = 0;

                        // Process Y positions
                        for (int y = startY; y <= endY; y++)
                        {
                            byte voxelValue = flatVolumeData[z * width * height + y * width + x];
                            float val = voxelValue / 255.0f;

                            // Update sums
                            sum += val;
                            sumSquared += val * val;
                            validPositions++;
                        }

                        // Calculate variance
                        if (validPositions >= 2)
                        {
                            float mean = sum / validPositions;
                            float variance = (sumSquared / validPositions) - (mean * mean);
                            flatVarianceMap[z * width + x] = variance;
                        }

                        // Report progress periodically
                        if (i % batchSize == 0 || i == width * depth - 1)
                        {
                            int progress = (int)((i + 1) * 100.0 / (width * depth));
                            progressCallback(progress);
                        }
                    });

                    // Reshape to 2D array
                    for (int z = 0; z < depth; z++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            xzVarianceMap[x, z] = flatVarianceMap[z * width + x];
                        }
                    }
                }

                Logger.Log("[BandDetectionForm] XZ variance map GPU calculation complete");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] GPU error in XZ variance calculation: {ex.Message}");
                Logger.Log("[BandDetectionForm] Falling back to CPU calculation");

                // Fall back to CPU calculation
                CalculateXZVarianceMap(width, height, depth, sliceCount, progressCallback, token);
            }
        }

        // Calculate YZ variance map on GPU
        private void CalculateYZVarianceMapGPU(int width, int height, int depth, int sliceCount, Action<int> progressCallback, CancellationToken token)
        {
            Logger.Log("[BandDetectionForm] Calculating YZ variance map using GPU");
            token.ThrowIfCancellationRequested();

            // Get current YZ slice position (X value)
            int centerX = (int)yzSliceTrackBar.Value;

            // Calculate X range centered around current X
            int startX = Math.Max(0, centerX - sliceCount / 2);
            int endX = Math.Min(width - 1, startX + sliceCount - 1);
            int actualXCount = endX - startX + 1;

            Logger.Log($"[BandDetectionForm] YZ GPU: Calculating variance from X {startX} to {endX} (total: {actualXCount})");

            // Create a flat copy of the volume data
            byte[] flatVolumeData = new byte[width * height * depth];
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        flatVolumeData[z * width * height + y * width + x] = mainForm.volumeData[x, y, z];
                    }
                }
            }

            try
            {
                // Process each Y-Z slice
                // Like with XZ, we'll handle this specially since it's a different projection

                // Upload data to GPU
                using (var deviceVolumeData = accelerator.Allocate1D<byte>(flatVolumeData))
                {
                    // Since the kernel requires a flat output array, we'll have to do some processing
                    float[] flatVarianceMap = new float[height * depth];

                    // Process in batches for better progress reporting
                    int batchSize = Math.Max(1, height * depth / 100);

                    // Process the variance computation on CPU by iterating over each Y-Z position
                    Parallel.For(0, height * depth, token.CanBeCanceled ? new ParallelOptions { CancellationToken = token } : new ParallelOptions(), i =>
                    {
                        int y = i % height;
                        int z = i / height;

                        // Calculate mean and variance across X slices
                        float sum = 0;
                        float sumSquared = 0;
                        int validPositions = 0;

                        // Process X positions
                        for (int x = startX; x <= endX; x++)
                        {
                            byte voxelValue = flatVolumeData[z * width * height + y * width + x];
                            float val = voxelValue / 255.0f;

                            // Update sums
                            sum += val;
                            sumSquared += val * val;
                            validPositions++;
                        }

                        // Calculate variance
                        if (validPositions >= 2)
                        {
                            float mean = sum / validPositions;
                            float variance = (sumSquared / validPositions) - (mean * mean);
                            flatVarianceMap[z * height + y] = variance;
                        }

                        // Report progress periodically
                        if (i % batchSize == 0 || i == height * depth - 1)
                        {
                            int progress = (int)((i + 1) * 100.0 / (height * depth));
                            progressCallback(progress);
                        }
                    });

                    // Reshape to 2D array
                    for (int z = 0; z < depth; z++)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            yzVarianceMap[y, z] = flatVarianceMap[z * height + y];
                        }
                    }
                }

                Logger.Log("[BandDetectionForm] YZ variance map GPU calculation complete");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] GPU error in YZ variance calculation: {ex.Message}");
                Logger.Log("[BandDetectionForm] Falling back to CPU calculation");

                // Fall back to CPU calculation
                CalculateYZVarianceMap(width, height, depth, sliceCount, progressCallback, token);
            }
        }

        // Calculate XY variance map on CPU (fallback)
        private void CalculateXYVarianceMap(int width, int height, int depth, int sliceCount, Action<int> progressCallback, CancellationToken token)
        {
            Logger.Log("[BandDetectionForm] Calculating XY variance map on CPU");
            token.ThrowIfCancellationRequested();

            // Process in batches of rows for better progress reporting
            int batchSize = Math.Max(1, height / 100);

            // Initialize variance map
            xyVarianceMap = new float[width, height];

            // Get current slice
            int centerSlice = (int)xySliceTrackBar.Value;

            // Calculate starting slice centered around current slice
            int startSlice = Math.Max(0, centerSlice - sliceCount / 2);
            int endSlice = Math.Min(depth - 1, startSlice + sliceCount - 1);
            int actualSliceCount = endSlice - startSlice + 1;

            Logger.Log($"[BandDetectionForm] XY: Calculating variance from slice {startSlice} to {endSlice} (total: {actualSliceCount})");

            // Create a ParallelOptions object to specify cancellation token
            ParallelOptions parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = token
            };

            // Process each row
            Parallel.For(0, height, parallelOptions, y =>
            {
                // Check for cancellation
                token.ThrowIfCancellationRequested();

                // For each pixel in the row
                for (int x = 0; x < width; x++)
                {
                    // For each slice, calculate mean and variance
                    float sum = 0;
                    float sumSquared = 0;
                    int validSlices = 0;

                    // Process slices
                    for (int z = startSlice; z <= endSlice; z++)
                    {
                        // Get voxel value and normalize to 0-1
                        byte voxelValue = mainForm.volumeData[x, y, z];
                        float val = voxelValue / 255.0f;

                        // Update sums
                        sum += val;
                        sumSquared += val * val;
                        validSlices++;
                    }

                    // Calculate variance only if we have at least 2 slices
                    if (validSlices >= 2)
                    {
                        float mean = sum / validSlices;
                        float variance = (sumSquared / validSlices) - (mean * mean);

                        // Store variance
                        xyVarianceMap[x, y] = variance;
                    }
                }

                // Report progress periodically
                if (y % batchSize == 0 || y == height - 1)
                {
                    int progress = (int)((y + 1) * 100.0 / height);
                    progressCallback(progress);
                }
            });

            // Final cancellation check
            token.ThrowIfCancellationRequested();
            Logger.Log("[BandDetectionForm] XY variance map calculation complete");
        }

        // Calculate XZ variance map on CPU (fallback)
        private void CalculateXZVarianceMap(int width, int height, int depth, int sliceCount, Action<int> progressCallback, CancellationToken token)
        {
            Logger.Log("[BandDetectionForm] Calculating XZ variance map on CPU");
            token.ThrowIfCancellationRequested();

            // Process in batches for better progress reporting
            int batchSize = Math.Max(1, width / 100);

            // Initialize variance map
            xzVarianceMap = new float[width, depth];

            // Get current XZ slice position (Y value)
            int centerY = (int)xzSliceTrackBar.Value;

            // Calculate Y range centered around current Y
            int startY = Math.Max(0, centerY - sliceCount / 2);
            int endY = Math.Min(height - 1, startY + sliceCount - 1);
            int actualYCount = endY - startY + 1;

            Logger.Log($"[BandDetectionForm] XZ: Calculating variance from Y {startY} to {endY} (total: {actualYCount})");

            // Create a ParallelOptions object to specify cancellation token
            ParallelOptions parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = token
            };

            // Process each X position
            Parallel.For(0, width, parallelOptions, x =>
            {
                // Check for cancellation
                token.ThrowIfCancellationRequested();

                // For each Z position
                for (int z = 0; z < depth; z++)
                {
                    // Calculate mean and variance across Y slices
                    float sum = 0;
                    float sumSquared = 0;
                    int validPositions = 0;

                    // Process Y positions
                    for (int y = startY; y <= endY; y++)
                    {
                        // Get voxel value and normalize to 0-1
                        byte voxelValue = mainForm.volumeData[x, y, z];
                        float val = voxelValue / 255.0f;

                        // Update sums
                        sum += val;
                        sumSquared += val * val;
                        validPositions++;
                    }

                    // Calculate variance only if we have at least 2 positions
                    if (validPositions >= 2)
                    {
                        float mean = sum / validPositions;
                        float variance = (sumSquared / validPositions) - (mean * mean);

                        // Store variance
                        xzVarianceMap[x, z] = variance;
                    }
                }

                // Report progress periodically
                if (x % batchSize == 0 || x == width - 1)
                {
                    int progress = (int)((x + 1) * 100.0 / width);
                    progressCallback(progress);
                }
            });

            // Final cancellation check
            token.ThrowIfCancellationRequested();
            Logger.Log("[BandDetectionForm] XZ variance map calculation complete");
        }

        // Calculate YZ variance map on CPU (fallback)
        private void CalculateYZVarianceMap(int width, int height, int depth, int sliceCount, Action<int> progressCallback, CancellationToken token)
        {
            Logger.Log("[BandDetectionForm] Calculating YZ variance map on CPU");
            token.ThrowIfCancellationRequested();

            // Process in batches for better progress reporting
            int batchSize = Math.Max(1, height / 100);

            // Initialize variance map
            yzVarianceMap = new float[height, depth];

            // Get current YZ slice position (X value)
            int centerX = (int)yzSliceTrackBar.Value;

            // Calculate X range centered around current X
            int startX = Math.Max(0, centerX - sliceCount / 2);
            int endX = Math.Min(width - 1, startX + sliceCount - 1);
            int actualXCount = endX - startX + 1;

            Logger.Log($"[BandDetectionForm] YZ: Calculating variance from X {startX} to {endX} (total: {actualXCount})");

            // Create a ParallelOptions object to specify cancellation token
            ParallelOptions parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = token
            };

            // Process each Y position
            Parallel.For(0, height, parallelOptions, y =>
            {
                // Check for cancellation
                token.ThrowIfCancellationRequested();

                // For each Z position
                for (int z = 0; z < depth; z++)
                {
                    // Calculate mean and variance across X slices
                    float sum = 0;
                    float sumSquared = 0;
                    int validPositions = 0;

                    // Process X positions
                    for (int x = startX; x <= endX; x++)
                    {
                        // Get voxel value and normalize to 0-1
                        byte voxelValue = mainForm.volumeData[x, y, z];
                        float val = voxelValue / 255.0f;

                        // Update sums
                        sum += val;
                        sumSquared += val * val;
                        validPositions++;
                    }

                    // Calculate variance only if we have at least 2 positions
                    if (validPositions >= 2)
                    {
                        float mean = sum / validPositions;
                        float variance = (sumSquared / validPositions) - (mean * mean);

                        // Store variance
                        yzVarianceMap[y, z] = variance;
                    }
                }

                // Report progress periodically
                if (y % batchSize == 0 || y == height - 1)
                {
                    int progress = (int)((y + 1) * 100.0 / height);
                    progressCallback(progress);
                }
            });

            // Final cancellation check
            token.ThrowIfCancellationRequested();
            Logger.Log("[BandDetectionForm] YZ variance map calculation complete");
        }

        // Create a bitmap from a variance map
        private Bitmap CreateVarianceMapImage(float[,] varianceMap)
        {
            if (varianceMap == null)
            {
                Logger.Log("[BandDetectionForm] Cannot create variance map from null data");
                return CreateFallbackBitmap();
            }

            int width = varianceMap.GetLength(0);
            int height = varianceMap.GetLength(1);

            // Validate dimensions
            if (width <= 0 || height <= 0)
            {
                Logger.Log($"[BandDetectionForm] Invalid variance map dimensions: {width}x{height}");
                return CreateFallbackBitmap();
            }

            try
            {
                // Enforce a minimum threshold to prevent numerical issues
                // This is essential for preventing the "Parameter non valido" error
                float effectiveThreshold = varianceThreshold;
                if (effectiveThreshold < 1e-10f)
                {
                    Logger.Log("[BandDetectionForm] Warning: Threshold too small, using safe minimum value");
                    effectiveThreshold = 1e-10f; // Safe minimum threshold
                }

                // Calculate statistics safely
                float minVariance = float.MaxValue;
                float maxVariance = float.MinValue;
                float sum = 0;
                int validCount = 0;

                // First pass: find min/max and validate data
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float value = varianceMap[x, y];

                        // Skip invalid values
                        if (float.IsNaN(value) || float.IsInfinity(value))
                            continue;

                        minVariance = Math.Min(minVariance, value);
                        maxVariance = Math.Max(maxVariance, value);
                        sum += value;
                        validCount++;
                    }
                }

                // Handle empty or invalid map
                if (validCount == 0 || minVariance > maxVariance)
                {
                    Logger.Log("[BandDetectionForm] No valid variance data found");
                    return CreateFallbackBitmap();
                }

                // Mean value for fallback if needed
                float meanVariance = sum / validCount;

                // Create fixed range that's guaranteed to be non-zero
                float range = maxVariance - minVariance;
                if (range < 0.0001f)
                {
                    range = 0.0001f;
                    // If range is too small, artificially create a range around the mean
                    minVariance = meanVariance - range / 2;
                    maxVariance = meanVariance + range / 2;
                }

                // Log statistics for debugging
                Logger.Log($"[BandDetectionForm] Variance stats: min={minVariance:E6}, max={maxVariance:E6}, " +
                          $"mean={meanVariance:E6}, range={range:E6}, threshold={effectiveThreshold:E6}");

                // Create a buffer for processing bitmap data
                // Using a simple array rather than LockBits to avoid potential issues
                byte[] rgbValues = new byte[width * height * 3];

                // Process each pixel with safe operations
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float value = varianceMap[x, y];

                        // Handle invalid values
                        if (float.IsNaN(value) || float.IsInfinity(value))
                            value = meanVariance;

                        // Standardized approach that works with any threshold
                        float normalizedValue;

                        // First normalize to 0-1 range regardless of threshold
                        normalizedValue = (value - minVariance) / range;
                        normalizedValue = Math.Max(0f, Math.Min(1f, normalizedValue));

                        // Then apply threshold effect (after normalization)
                        if (value < effectiveThreshold)
                            normalizedValue = 0f;

                        // Apply contrast enhancement with safety limits
                        float safeContrastFactor = Math.Max(0.1f, Math.Min(5.0f, varianceContrastFactor));
                        try
                        {
                            normalizedValue = (float)Math.Pow(normalizedValue, 1.0f / safeContrastFactor);
                        }
                        catch
                        {
                            // If pow fails (e.g., with negative values due to floating point errors)
                            normalizedValue = normalizedValue * safeContrastFactor; // Linear fallback
                        }

                        // Apply inversion if needed
                        if (invertVarianceDisplay)
                            normalizedValue = 1.0f - normalizedValue;

                        // Final clamp to ensure valid range
                        normalizedValue = Math.Max(0f, Math.Min(1f, normalizedValue));

                        // Convert to byte value with extra safety
                        byte pixelValue;
                        try
                        {
                            pixelValue = (byte)(normalizedValue * 255);
                        }
                        catch
                        {
                            pixelValue = 128; // Fallback to mid-gray
                        }

                        // Calculate offset in the byte array (RGB format)
                        int offset = (y * width + x) * 3;
                        if (offset + 2 < rgbValues.Length)
                        {
                            rgbValues[offset] = pixelValue;     // B
                            rgbValues[offset + 1] = pixelValue; // G
                            rgbValues[offset + 2] = pixelValue; // R
                        }
                    }
                }

                // Create bitmap using the safer SetPixel approach
                Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);

                // Set pixels directly - slower but more stable
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int offset = (y * width + x) * 3;
                        byte value = rgbValues[offset]; // All RGB values are the same (grayscale)
                        bmp.SetPixel(x, y, Color.FromArgb(value, value, value));
                    }
                }

                return bmp;
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error creating variance map image: {ex.Message}");
                return CreateFallbackBitmap();
            }
        }

        private Bitmap CreateFallbackBitmap()
        {
            // Create a simple gray bitmap as fallback
            Bitmap fallback = new Bitmap(100, 100, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(fallback))
            {
                g.Clear(Color.DarkGray);
                using (Font font = new Font("Arial", 10))
                using (Brush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("Variance data\nunavailable", font, brush, 10, 40);
                }
            }
            return fallback;
        }

        // Detect bands in the XY variance map
        private void DetectBandsInXYVarianceMap()
        {
            if (xyVarianceMap == null)
                return;

            Logger.Log("[BandDetectionForm] Detecting bands in XY variance map");

            int width = xyVarianceMap.GetLength(0);
            int height = xyVarianceMap.GetLength(1);

            // Create row profile from variance map
            double[] rowProfile = new double[height];

            // Calculate row averages
            for (int y = 0; y < height; y++)
            {
                double sum = 0;
                for (int x = 0; x < width; x++)
                {
                    sum += xyVarianceMap[x, y];
                }
                rowProfile[y] = sum / width;
            }

            // Apply Gaussian smoothing to the row profile
            double[] smoothRowProfile = GaussianSmoothArray(rowProfile, (float)gaussianSigma);

            // Get min/max values for scaling
            double minVal = smoothRowProfile.Min();
            double maxVal = smoothRowProfile.Max();
            double range = maxVal - minVal;

            // In variance maps, dark peaks (low variance) represent consistent bands across slices
            // We need to find minima (valleys) in the profile, which can be done by finding maxima in the inverted profile
            double[] invertedProfileForDarkPeaks = new double[height];
            for (int i = 0; i < height; i++)
            {
                invertedProfileForDarkPeaks[i] = maxVal - smoothRowProfile[i];
            }

            // Find dark peaks (low variance areas = consistent bands) in the inverted profile
            xyVarianceDarkPeaks = FindPeaks(invertedProfileForDarkPeaks, peakDistance, (float)peakProminence);

            // Find bright peaks (high variance areas = inconsistent regions) directly in the profile
            xyVarianceBrightPeaks = FindPeaks(smoothRowProfile, peakDistance, (float)peakProminence);

            Logger.Log($"[BandDetectionForm] XY: Detected {xyVarianceDarkPeaks.Length} low-variance bands and {xyVarianceBrightPeaks.Length} high-variance regions");
        }

        // Detect bands in the XZ variance map
        private void DetectBandsInXZVarianceMap()
        {
            if (xzVarianceMap == null)
                return;

            Logger.Log("[BandDetectionForm] Detecting bands in XZ variance map");

            int width = xzVarianceMap.GetLength(0);
            int depth = xzVarianceMap.GetLength(1);

            // Create row profile from variance map (along Z axis)
            double[] rowProfile = new double[depth];

            // Calculate row averages
            for (int z = 0; z < depth; z++)
            {
                double sum = 0;
                for (int x = 0; x < width; x++)
                {
                    sum += xzVarianceMap[x, z];
                }
                rowProfile[z] = sum / width;
            }

            // Apply Gaussian smoothing to the row profile
            double[] smoothRowProfile = GaussianSmoothArray(rowProfile, (float)gaussianSigma);

            // Get min/max values for scaling
            double minVal = smoothRowProfile.Min();
            double maxVal = smoothRowProfile.Max();
            double range = maxVal - minVal;

            // In variance maps, dark peaks (low variance) represent consistent bands across slices
            // We need to find minima (valleys) in the profile, which can be done by finding maxima in the inverted profile
            double[] invertedProfileForDarkPeaks = new double[depth];
            for (int i = 0; i < depth; i++)
            {
                invertedProfileForDarkPeaks[i] = maxVal - smoothRowProfile[i];
            }

            // Find dark peaks (low variance areas = consistent bands) in the inverted profile
            xzVarianceDarkPeaks = FindPeaks(invertedProfileForDarkPeaks, peakDistance, (float)peakProminence);

            // Find bright peaks (high variance areas = inconsistent regions) directly in the profile
            xzVarianceBrightPeaks = FindPeaks(smoothRowProfile, peakDistance, (float)peakProminence);

            Logger.Log($"[BandDetectionForm] XZ: Detected {xzVarianceDarkPeaks.Length} low-variance bands and {xzVarianceBrightPeaks.Length} high-variance regions");
        }

        // Detect bands in the YZ variance map
        private void DetectBandsInYZVarianceMap()
        {
            if (yzVarianceMap == null)
                return;

            Logger.Log("[BandDetectionForm] Detecting bands in YZ variance map");

            int height = yzVarianceMap.GetLength(0);
            int depth = yzVarianceMap.GetLength(1);

            // Create row profile from variance map (along Z axis)
            double[] rowProfile = new double[depth];

            // Calculate row averages
            for (int z = 0; z < depth; z++)
            {
                double sum = 0;
                for (int y = 0; y < height; y++)
                {
                    sum += yzVarianceMap[y, z];
                }
                rowProfile[z] = sum / height;
            }

            // Apply Gaussian smoothing to the row profile
            double[] smoothRowProfile = GaussianSmoothArray(rowProfile, (float)gaussianSigma);

            // Get min/max values for scaling
            double minVal = smoothRowProfile.Min();
            double maxVal = smoothRowProfile.Max();
            double range = maxVal - minVal;

            // In variance maps, dark peaks (low variance) represent consistent bands across slices
            // We need to find minima (valleys) in the profile, which can be done by finding maxima in the inverted profile
            double[] invertedProfileForDarkPeaks = new double[depth];
            for (int i = 0; i < depth; i++)
            {
                invertedProfileForDarkPeaks[i] = maxVal - smoothRowProfile[i];
            }

            // Find dark peaks (low variance areas = consistent bands) in the inverted profile
            yzVarianceDarkPeaks = FindPeaks(invertedProfileForDarkPeaks, peakDistance, (float)peakProminence);

            // Find bright peaks (high variance areas = inconsistent regions) directly in the profile
            yzVarianceBrightPeaks = FindPeaks(smoothRowProfile, peakDistance, (float)peakProminence);

            Logger.Log($"[BandDetectionForm] YZ: Detected {yzVarianceDarkPeaks.Length} low-variance bands and {yzVarianceBrightPeaks.Length} high-variance regions");
        }

        // Create a new composite view that shows both the regular image and the variance map side by side
        private void CreateVarianceComposite()
        {
            if (!isShowingVarianceMap || xyVarianceImage == null)
            {
                MessageBox.Show("Please calculate and display the variance maps first", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Use SaveFileDialog to get save location
            SaveFileDialog saveDialog = new SaveFileDialog();
            saveDialog.Filter = "PNG Files|*.png|JPEG Files|*.jpg|All Files|*.*";
            saveDialog.Title = "Save Variance Composite Images";
            saveDialog.FileName = $"variance_composite";

            if (saveDialog.ShowDialog() == DialogResult.OK)
            {
                string basePath = Path.GetDirectoryName(saveDialog.FileName);
                string baseName = Path.GetFileNameWithoutExtension(saveDialog.FileName);
                string extension = Path.GetExtension(saveDialog.FileName);

                try
                {
                    // Create XY composite
                    if (xyProcessedImage != null && xyVarianceImage != null)
                    {
                        string xyFilePath = Path.Combine(basePath, $"{baseName}_xy{extension}");
                        CreateAndSaveComposite(xyProcessedImage, xyVarianceImage, xyFilePath, "XY");
                    }

                    // Create XZ composite
                    if (xzProcessedImage != null && xzVarianceImage != null)
                    {
                        string xzFilePath = Path.Combine(basePath, $"{baseName}_xz{extension}");
                        CreateAndSaveComposite(xzProcessedImage, xzVarianceImage, xzFilePath, "XZ");
                    }

                    // Create YZ composite
                    if (yzProcessedImage != null && yzVarianceImage != null)
                    {
                        string yzFilePath = Path.Combine(basePath, $"{baseName}_yz{extension}");
                        CreateAndSaveComposite(yzProcessedImage, yzVarianceImage, yzFilePath, "YZ");
                    }

                    MessageBox.Show($"All composite images saved to {basePath}", "Save Complete",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[BandDetectionForm] Error saving composites: {ex.Message}");
                    MessageBox.Show($"Error saving composites: {ex.Message}", "Save Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // Helper method to create and save a composite image
        private void CreateAndSaveComposite(Bitmap original, Bitmap variance, string filePath, string label)
        {
            int width = original.Width;
            int height = original.Height;

            // Create a new composite image
            using (Bitmap composite = new Bitmap(width * 2, height, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(composite))
                {
                    g.DrawImage(original, 0, 0, width, height);
                    g.DrawImage(variance, width, 0, width, height);

                    // Draw a dividing line
                    using (Pen dividerPen = new Pen(Color.Red, 2))
                    {
                        g.DrawLine(dividerPen, width, 0, width, height);
                    }

                    // Add labels
                    using (Font font = new Font("Arial", 12, FontStyle.Bold))
                    using (SolidBrush brushOriginal = new SolidBrush(Color.Yellow))
                    using (SolidBrush brushVariance = new SolidBrush(Color.Cyan))
                    {
                        g.DrawString($"{label} Original", font, brushOriginal, 10, 10);
                        g.DrawString($"{label} Variance Map", font, brushVariance, width + 10, 10);
                    }
                }

                composite.Save(filePath);
                Logger.Log($"[BandDetectionForm] Saved {label} variance composite to {filePath}");
            }
        }
        private async void ExportVarianceData()
        {
            if (xyVarianceMap == null || xyVarianceDarkPeaks == null)
            {
                MessageBox.Show("No variance data available to export.", "Export Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Show save file dialog
            SaveFileDialog saveDialog = new SaveFileDialog
            {
                Filter = "Excel Files|*.xlsx|CSV Files|*.csv|All Files|*.*",
                Title = "Export Variance Data",
                FileName = "variance_detection_results.xlsx"
            };

            if (saveDialog.ShowDialog() != DialogResult.OK)
                return;

            try
            {
                // Disable button during export
                btnExportVarianceData.Enabled = false;
                progressBar.Value = 0;
                progressBar.Visible = true;

                string fileName = saveDialog.FileName;
                string extension = Path.GetExtension(fileName).ToLower();

                // Export based on file type
                if (extension == ".xlsx")
                {
                    await ExportVarianceToExcel(fileName);
                }
                else
                {
                    await ExportVarianceToCSV(fileName);
                }

                progressBar.Value = 100;
                btnExportVarianceData.Enabled = true;
                progressBar.Visible = false;

                MessageBox.Show("Variance data exported successfully!", "Export Complete",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error exporting variance data: {ex.Message}");
                MessageBox.Show($"Error exporting variance data: {ex.Message}",
                               "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnExportVarianceData.Enabled = true;
                progressBar.Value = 0;
            }
        }
        private async Task ExportVarianceToExcel(string fileName)
        {
            await Task.Run(() =>
            {
                // Create Excel XML content
                StringBuilder excelXml = new StringBuilder();

                // XML header and workbook start
                excelXml.AppendLine("<?xml version=\"1.0\"?>");
                excelXml.AppendLine("<?mso-application progid=\"Excel.Sheet\"?>");
                excelXml.AppendLine("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\"");
                excelXml.AppendLine(" xmlns:o=\"urn:schemas-microsoft-com:office:office\"");
                excelXml.AppendLine(" xmlns:x=\"urn:schemas-microsoft-com:office:excel\"");
                excelXml.AppendLine(" xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\"");
                excelXml.AppendLine(" xmlns:html=\"http://www.w3.org/TR/REC-html40\">");

                // Styles
                excelXml.AppendLine("<Styles>");
                excelXml.AppendLine(" <Style ss:ID=\"Default\" ss:Name=\"Normal\">");
                excelXml.AppendLine("  <Alignment ss:Vertical=\"Bottom\"/>");
                excelXml.AppendLine("  <Borders/><Font/><Interior/><NumberFormat/><Protection/>");
                excelXml.AppendLine(" </Style>");
                excelXml.AppendLine(" <Style ss:ID=\"s21\">");
                excelXml.AppendLine("  <Font x:Family=\"Swiss\" ss:Bold=\"1\"/>");
                excelXml.AppendLine(" </Style>");
                excelXml.AppendLine(" <Style ss:ID=\"s22\">");
                excelXml.AppendLine("  <Interior ss:Color=\"#DDEBF7\" ss:Pattern=\"Solid\"/>");
                excelXml.AppendLine("  <Font x:Family=\"Swiss\" ss:Bold=\"1\"/>");
                excelXml.AppendLine(" </Style>");
                excelXml.AppendLine(" <Style ss:ID=\"s23\">");
                excelXml.AppendLine("  <NumberFormat ss:Format=\"0.000\"/>");
                excelXml.AppendLine(" </Style>");
                excelXml.AppendLine("</Styles>");

                // XY Variance Worksheet
                AddVarianceExcelWorksheet(excelXml, "XY_Variance", xyVarianceMap, xyVarianceDarkPeaks, xyVarianceBrightPeaks);

                // XZ Variance Worksheet
                AddVarianceExcelWorksheet(excelXml, "XZ_Variance", xzVarianceMap, xzVarianceDarkPeaks, xzVarianceBrightPeaks);

                // YZ Variance Worksheet
                AddVarianceExcelWorksheet(excelXml, "YZ_Variance", yzVarianceMap, yzVarianceDarkPeaks, yzVarianceBrightPeaks);

                // Variance Peak Analysis Worksheet
                AddVariancePeakAnalysisWorksheet(excelXml);

                // Close workbook
                excelXml.AppendLine("</Workbook>");

                // Write to file
                File.WriteAllText(fileName, excelXml.ToString());
            });
        }

        private void AddVarianceExcelWorksheet(StringBuilder xml, string name, float[,] varianceMap, int[] darkPeaks, int[] brightPeaks)
        {
            if (varianceMap == null)
                return;

            int width = varianceMap.GetLength(0);
            int height = varianceMap.GetLength(1);

            // Create row profile from variance map
            double[] profile = new double[height];

            for (int y = 0; y < height; y++)
            {
                double sum = 0;
                for (int x = 0; x < width; x++)
                {
                    sum += varianceMap[x, y];
                }
                profile[y] = sum / width;
            }

            double[] smoothProfile = GaussianSmoothArray(profile, (float)gaussianSigma);

            xml.AppendLine($"<Worksheet ss:Name=\"{name}\">");
            xml.AppendLine("<Table>");

            // Column widths
            xml.AppendLine("<Column ss:Width=\"60\"/>");
            xml.AppendLine("<Column ss:Width=\"80\"/>");
            xml.AppendLine("<Column ss:Width=\"80\"/>");
            xml.AppendLine("<Column ss:Width=\"80\"/>");

            // Headers
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Row</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Variance Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Low Variance Peak</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">High Variance Peak</Data></Cell>");
            xml.AppendLine("</Row>");

            // Data rows
            for (int i = 0; i < smoothProfile.Length; i++)
            {
                bool isDarkPeak = darkPeaks != null && darkPeaks.Contains(i);
                bool isBrightPeak = brightPeaks != null && brightPeaks.Contains(i);

                xml.AppendLine("<Row>");
                xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{i}</Data></Cell>");
                xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{smoothProfile[i].ToString("F6")}</Data></Cell>");
                xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{(isDarkPeak ? 1 : 0)}</Data></Cell>");
                xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{(isBrightPeak ? 1 : 0)}</Data></Cell>");
                xml.AppendLine("</Row>");
            }

            xml.AppendLine("</Table>");
            xml.AppendLine("<WorksheetOptions xmlns=\"urn:schemas-microsoft-com:office:excel\">");
            xml.AppendLine("<FreezePanes/>");
            xml.AppendLine("<FrozenNoSplit/>");
            xml.AppendLine("<SplitHorizontal>1</SplitHorizontal>");
            xml.AppendLine("<TopRowBottomPane>1</TopRowBottomPane>");
            xml.AppendLine("</WorksheetOptions>");
            xml.AppendLine("</Worksheet>");
        }

        private void AddVariancePeakAnalysisWorksheet(StringBuilder xml)
        {
            double pixelSize = mainForm.GetPixelSize() * 1000; // mm

            xml.AppendLine("<Worksheet ss:Name=\"Variance_Peak_Analysis\">");
            xml.AppendLine("<Table>");

            // Column widths
            xml.AppendLine("<Column ss:Width=\"80\"/>");
            xml.AppendLine("<Column ss:Width=\"80\"/>");
            xml.AppendLine("<Column ss:Width=\"100\"/>");
            xml.AppendLine("<Column ss:Width=\"100\"/>");
            xml.AppendLine("<Column ss:Width=\"100\"/>");

            // XY DARK PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">XY Low Variance Bands</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (xyVarianceDarkPeaks != null && xyVarianceMap != null)
            {
                // Create row profile
                int width = xyVarianceMap.GetLength(0);
                int height = xyVarianceMap.GetLength(1);
                double[] profile = new double[height];

                for (int y = 0; y < height; y++)
                {
                    double sum = 0;
                    for (int x = 0; x < width; x++)
                    {
                        sum += xyVarianceMap[x, y];
                    }
                    profile[y] = sum / width;
                }

                double[] smoothProfile = GaussianSmoothArray(profile, (float)gaussianSigma);

                for (int i = 0; i < xyVarianceDarkPeaks.Length; i++)
                {
                    int peak = xyVarianceDarkPeaks[i];
                    double distance = (i < xyVarianceDarkPeaks.Length - 1) ? xyVarianceDarkPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">Low Variance</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");

                    if (peak < smoothProfile.Length)
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{smoothProfile[peak].ToString("F6")}</Data></Cell>");
                    else
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">0</Data></Cell>");

                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add empty row
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");

            // XY HIGH VARIANCE PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">XY High Variance Regions</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (xyVarianceBrightPeaks != null && xyVarianceMap != null)
            {
                // Create row profile if not already created
                int width = xyVarianceMap.GetLength(0);
                int height = xyVarianceMap.GetLength(1);
                double[] profile = new double[height];

                for (int y = 0; y < height; y++)
                {
                    double sum = 0;
                    for (int x = 0; x < width; x++)
                    {
                        sum += xyVarianceMap[x, y];
                    }
                    profile[y] = sum / width;
                }

                double[] smoothProfile = GaussianSmoothArray(profile, (float)gaussianSigma);

                for (int i = 0; i < xyVarianceBrightPeaks.Length; i++)
                {
                    int peak = xyVarianceBrightPeaks[i];
                    double distance = (i < xyVarianceBrightPeaks.Length - 1) ? xyVarianceBrightPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">High Variance</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");

                    if (peak < smoothProfile.Length)
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{smoothProfile[peak].ToString("F6")}</Data></Cell>");
                    else
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">0</Data></Cell>");

                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add empty row
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");

            // XZ LOW VARIANCE PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">XZ Low Variance Bands</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (xzVarianceDarkPeaks != null && xzVarianceMap != null)
            {
                // Create row profile
                int width = xzVarianceMap.GetLength(0);
                int depth = xzVarianceMap.GetLength(1);
                double[] profile = new double[depth];

                for (int z = 0; z < depth; z++)
                {
                    double sum = 0;
                    for (int x = 0; x < width; x++)
                    {
                        sum += xzVarianceMap[x, z];
                    }
                    profile[z] = sum / width;
                }

                double[] smoothProfile = GaussianSmoothArray(profile, (float)gaussianSigma);

                for (int i = 0; i < xzVarianceDarkPeaks.Length; i++)
                {
                    int peak = xzVarianceDarkPeaks[i];
                    double distance = (i < xzVarianceDarkPeaks.Length - 1) ? xzVarianceDarkPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">Low Variance</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");

                    if (peak < smoothProfile.Length)
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{smoothProfile[peak].ToString("F6")}</Data></Cell>");
                    else
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">0</Data></Cell>");

                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add empty row
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");

            // XZ HIGH VARIANCE PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">XZ High Variance Regions</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (xzVarianceBrightPeaks != null && xzVarianceMap != null)
            {
                // Create row profile
                int width = xzVarianceMap.GetLength(0);
                int depth = xzVarianceMap.GetLength(1);
                double[] profile = new double[depth];

                for (int z = 0; z < depth; z++)
                {
                    double sum = 0;
                    for (int x = 0; x < width; x++)
                    {
                        sum += xzVarianceMap[x, z];
                    }
                    profile[z] = sum / width;
                }

                double[] smoothProfile = GaussianSmoothArray(profile, (float)gaussianSigma);

                for (int i = 0; i < xzVarianceBrightPeaks.Length; i++)
                {
                    int peak = xzVarianceBrightPeaks[i];
                    double distance = (i < xzVarianceBrightPeaks.Length - 1) ? xzVarianceBrightPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">High Variance</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");

                    if (peak < smoothProfile.Length)
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{smoothProfile[peak].ToString("F6")}</Data></Cell>");
                    else
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">0</Data></Cell>");

                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add empty row
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");

            // YZ LOW VARIANCE PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">YZ Low Variance Bands</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (yzVarianceDarkPeaks != null && yzVarianceMap != null)
            {
                // Create row profile
                int height = yzVarianceMap.GetLength(0);
                int depth = yzVarianceMap.GetLength(1);
                double[] profile = new double[depth];

                for (int z = 0; z < depth; z++)
                {
                    double sum = 0;
                    for (int y = 0; y < height; y++)
                    {
                        sum += yzVarianceMap[y, z];
                    }
                    profile[z] = sum / height;
                }

                double[] smoothProfile = GaussianSmoothArray(profile, (float)gaussianSigma);

                for (int i = 0; i < yzVarianceDarkPeaks.Length; i++)
                {
                    int peak = yzVarianceDarkPeaks[i];
                    double distance = (i < yzVarianceDarkPeaks.Length - 1) ? yzVarianceDarkPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">Low Variance</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");

                    if (peak < smoothProfile.Length)
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{smoothProfile[peak].ToString("F6")}</Data></Cell>");
                    else
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">0</Data></Cell>");

                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add empty row
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");

            // YZ HIGH VARIANCE PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">YZ High Variance Regions</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (yzVarianceBrightPeaks != null && yzVarianceMap != null)
            {
                // Create row profile
                int height = yzVarianceMap.GetLength(0);
                int depth = yzVarianceMap.GetLength(1);
                double[] profile = new double[depth];

                for (int z = 0; z < depth; z++)
                {
                    double sum = 0;
                    for (int y = 0; y < height; y++)
                    {
                        sum += yzVarianceMap[y, z];
                    }
                    profile[z] = sum / height;
                }

                double[] smoothProfile = GaussianSmoothArray(profile, (float)gaussianSigma);

                for (int i = 0; i < yzVarianceBrightPeaks.Length; i++)
                {
                    int peak = yzVarianceBrightPeaks[i];
                    double distance = (i < yzVarianceBrightPeaks.Length - 1) ? yzVarianceBrightPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">High Variance</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");

                    if (peak < smoothProfile.Length)
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{smoothProfile[peak].ToString("F6")}</Data></Cell>");
                    else
                        xml.AppendLine($"<Cell><Data ss:Type=\"Number\">0</Data></Cell>");

                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add summary statistics section
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">Variance Summary Statistics</Data></Cell>");
            xml.AppendLine("</Row>");

            // Add mean distances
            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"1\"><Data ss:Type=\"String\">View</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Mean Low Var Distance (mm)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Mean High Var Distance (mm)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Number of Peaks</Data></Cell>");
            xml.AppendLine("</Row>");

            // XY stats
            double xyMeanDarkDistance = 0;
            double xyMeanBrightDistance = 0;
            int xyDarkCount = 0, xyBrightCount = 0;

            if (xyVarianceDarkPeaks != null && xyVarianceDarkPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < xyVarianceDarkPeaks.Length - 1; i++)
                {
                    double distance = (xyVarianceDarkPeaks[i + 1] - xyVarianceDarkPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                xyMeanDarkDistance = count > 0 ? sum / count : 0;
                xyDarkCount = xyVarianceDarkPeaks.Length;
            }

            if (xyVarianceBrightPeaks != null && xyVarianceBrightPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < xyVarianceBrightPeaks.Length - 1; i++)
                {
                    double distance = (xyVarianceBrightPeaks[i + 1] - xyVarianceBrightPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                xyMeanBrightDistance = count > 0 ? sum / count : 0;
                xyBrightCount = xyVarianceBrightPeaks.Length;
            }

            xml.AppendLine("<Row>");
            xml.AppendLine("<Cell ss:MergeAcross=\"1\"><Data ss:Type=\"String\">XY</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{xyMeanDarkDistance}</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{xyMeanBrightDistance}</Data></Cell>");
            xml.AppendLine($"<Cell><Data ss:Type=\"String\">Low: {xyDarkCount}, High: {xyBrightCount}</Data></Cell>");
            xml.AppendLine("</Row>");

            // XZ stats
            double xzMeanDarkDistance = 0;
            double xzMeanBrightDistance = 0;
            int xzDarkCount = 0, xzBrightCount = 0;

            if (xzVarianceDarkPeaks != null && xzVarianceDarkPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < xzVarianceDarkPeaks.Length - 1; i++)
                {
                    double distance = (xzVarianceDarkPeaks[i + 1] - xzVarianceDarkPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                xzMeanDarkDistance = count > 0 ? sum / count : 0;
                xzDarkCount = xzVarianceDarkPeaks.Length;
            }

            if (xzVarianceBrightPeaks != null && xzVarianceBrightPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < xzVarianceBrightPeaks.Length - 1; i++)
                {
                    double distance = (xzVarianceBrightPeaks[i + 1] - xzVarianceBrightPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                xzMeanBrightDistance = count > 0 ? sum / count : 0;
                xzBrightCount = xzVarianceBrightPeaks.Length;
            }

            xml.AppendLine("<Row>");
            xml.AppendLine("<Cell ss:MergeAcross=\"1\"><Data ss:Type=\"String\">XZ</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{xzMeanDarkDistance}</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{xzMeanBrightDistance}</Data></Cell>");
            xml.AppendLine($"<Cell><Data ss:Type=\"String\">Low: {xzDarkCount}, High: {xzBrightCount}</Data></Cell>");
            xml.AppendLine("</Row>");

            // YZ stats
            double yzMeanDarkDistance = 0;
            double yzMeanBrightDistance = 0;
            int yzDarkCount = 0, yzBrightCount = 0;

            if (yzVarianceDarkPeaks != null && yzVarianceDarkPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < yzVarianceDarkPeaks.Length - 1; i++)
                {
                    double distance = (yzVarianceDarkPeaks[i + 1] - yzVarianceDarkPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                yzMeanDarkDistance = count > 0 ? sum / count : 0;
                yzDarkCount = yzVarianceDarkPeaks.Length;
            }

            if (yzVarianceBrightPeaks != null && yzVarianceBrightPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < yzVarianceBrightPeaks.Length - 1; i++)
                {
                    double distance = (yzVarianceBrightPeaks[i + 1] - yzVarianceBrightPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                yzMeanBrightDistance = count > 0 ? sum / count : 0;
                yzBrightCount = yzVarianceBrightPeaks.Length;
            }

            xml.AppendLine("<Row>");
            xml.AppendLine("<Cell ss:MergeAcross=\"1\"><Data ss:Type=\"String\">YZ</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{yzMeanDarkDistance}</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{yzMeanBrightDistance}</Data></Cell>");
            xml.AppendLine($"<Cell><Data ss:Type=\"String\">Low: {yzDarkCount}, High: {yzBrightCount}</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("</Table>");
            xml.AppendLine("<WorksheetOptions xmlns=\"urn:schemas-microsoft-com:office:excel\">");
            xml.AppendLine("<FreezePanes/>");
            xml.AppendLine("<FrozenNoSplit/>");
            xml.AppendLine("<SplitHorizontal>2</SplitHorizontal>");
            xml.AppendLine("<TopRowBottomPane>2</TopRowBottomPane>");
            xml.AppendLine("</WorksheetOptions>");
            xml.AppendLine("</Worksheet>");
        }


        private async Task ExportVarianceToCSV(string fileName)
        {
            await Task.Run(() =>
            {
                // Prepare data for export
                List<string> lines = new List<string>();

                // Header
                lines.Add("View Type,Row/Col,Variance Value,Low Variance Peak,High Variance Peak");

                // XY data
                if (xyVarianceMap != null)
                {
                    int width = xyVarianceMap.GetLength(0);
                    int height = xyVarianceMap.GetLength(1);

                    // Create row profile
                    double[] profile = new double[height];
                    for (int y = 0; y < height; y++)
                    {
                        double sum = 0;
                        for (int x = 0; x < width; x++)
                        {
                            sum += xyVarianceMap[x, y];
                        }
                        profile[y] = sum / width;
                    }

                    double[] smoothProfile = GaussianSmoothArray(profile, (float)gaussianSigma);

                    for (int i = 0; i < smoothProfile.Length; i++)
                    {
                        bool isLowVariance = xyVarianceDarkPeaks != null && xyVarianceDarkPeaks.Contains(i);
                        bool isHighVariance = xyVarianceBrightPeaks != null && xyVarianceBrightPeaks.Contains(i);

                        lines.Add($"XY_Variance,{i},{smoothProfile[i]},{(isLowVariance ? "1" : "0")},{(isHighVariance ? "1" : "0")}");
                    }
                }

                // XZ data (similar structure to XY)
                if (xzVarianceMap != null)
                {
                    int width = xzVarianceMap.GetLength(0);
                    int depth = xzVarianceMap.GetLength(1);

                    double[] profile = new double[depth];
                    for (int z = 0; z < depth; z++)
                    {
                        double sum = 0;
                        for (int x = 0; x < width; x++)
                        {
                            sum += xzVarianceMap[x, z];
                        }
                        profile[z] = sum / width;
                    }

                    double[] smoothProfile = GaussianSmoothArray(profile, (float)gaussianSigma);

                    for (int i = 0; i < smoothProfile.Length; i++)
                    {
                        bool isLowVariance = xzVarianceDarkPeaks != null && xzVarianceDarkPeaks.Contains(i);
                        bool isHighVariance = xzVarianceBrightPeaks != null && xzVarianceBrightPeaks.Contains(i);

                        lines.Add($"XZ_Variance,{i},{smoothProfile[i]},{(isLowVariance ? "1" : "0")},{(isHighVariance ? "1" : "0")}");
                    }
                }

                // YZ data (similar structure to XY)
                if (yzVarianceMap != null)
                {
                    int height = yzVarianceMap.GetLength(0);
                    int depth = yzVarianceMap.GetLength(1);

                    double[] profile = new double[depth];
                    for (int z = 0; z < depth; z++)
                    {
                        double sum = 0;
                        for (int y = 0; y < height; y++)
                        {
                            sum += yzVarianceMap[y, z];
                        }
                        profile[z] = sum / height;
                    }

                    double[] smoothProfile = GaussianSmoothArray(profile, (float)gaussianSigma);

                    for (int i = 0; i < smoothProfile.Length; i++)
                    {
                        bool isLowVariance = yzVarianceDarkPeaks != null && yzVarianceDarkPeaks.Contains(i);
                        bool isHighVariance = yzVarianceBrightPeaks != null && yzVarianceBrightPeaks.Contains(i);

                        lines.Add($"YZ_Variance,{i},{smoothProfile[i]},{(isLowVariance ? "1" : "0")},{(isHighVariance ? "1" : "0")}");
                    }
                }

                // Write to file
                File.WriteAllLines(fileName, lines);

                // Create separate peak files
                string folder = Path.GetDirectoryName(fileName);
                string baseName = Path.GetFileNameWithoutExtension(fileName);

                // XY peaks
                if (xyVarianceDarkPeaks != null || xyVarianceBrightPeaks != null)
                {
                    List<string> xyPeakLines = new List<string>();
                    xyPeakLines.Add("Type,Position,Value,Distance to Next,Distance (mm)");

                    // Get the row profile for values
                    if (xyVarianceMap != null)
                    {
                        int width = xyVarianceMap.GetLength(0);
                        int height = xyVarianceMap.GetLength(1);

                        // Create row profile
                        double[] profile = new double[height];
                        for (int y = 0; y < height; y++)
                        {
                            double sum = 0;
                            for (int x = 0; x < width; x++)
                            {
                                sum += xyVarianceMap[x, y];
                            }
                            profile[y] = sum / width;
                        }

                        double[] smoothProfile = GaussianSmoothArray(profile, (float)gaussianSigma);

                        // Low variance peaks
                        if (xyVarianceDarkPeaks != null)
                        {
                            for (int i = 0; i < xyVarianceDarkPeaks.Length; i++)
                            {
                                int peak = xyVarianceDarkPeaks[i];
                                double distance = (i < xyVarianceDarkPeaks.Length - 1) ? xyVarianceDarkPeaks[i + 1] - peak : 0;
                                double pixelSize = mainForm.GetPixelSize() * 1000; // mm
                                double distanceInMm = distance * pixelSize;

                                double value = (peak < smoothProfile.Length) ? smoothProfile[peak] : 0;

                                xyPeakLines.Add($"Low Variance,{peak},{value},{distance},{distanceInMm.ToString("F3")}");
                            }
                        }

                        // High variance peaks
                        if (xyVarianceBrightPeaks != null)
                        {
                            for (int i = 0; i < xyVarianceBrightPeaks.Length; i++)
                            {
                                int peak = xyVarianceBrightPeaks[i];
                                double distance = (i < xyVarianceBrightPeaks.Length - 1) ? xyVarianceBrightPeaks[i + 1] - peak : 0;
                                double pixelSize = mainForm.GetPixelSize() * 1000; // mm
                                double distanceInMm = distance * pixelSize;

                                double value = (peak < smoothProfile.Length) ? smoothProfile[peak] : 0;

                                xyPeakLines.Add($"High Variance,{peak},{value},{distance},{distanceInMm.ToString("F3")}");
                            }
                        }
                    }

                    File.WriteAllLines(Path.Combine(folder, baseName + "_xy_variance_peaks.csv"), xyPeakLines);
                }

                // Create similar peak files for XZ and YZ (not shown for brevity)
            });
        }

        // Override the dispose method to clean up resources
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose bitmap resources
                xyVarianceImage?.Dispose();
                xzVarianceImage?.Dispose();
                yzVarianceImage?.Dispose();

                // Dispose ILGPU resources
                if (accelerator != null)
                {
                    try
                    {
                        if (!accelerator.IsDisposed)
                            accelerator.Dispose();
                        accelerator = null;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[BandDetectionForm] Error disposing accelerator: {ex.Message}");
                    }
                }

                if (ilgpuContext != null)
                {
                    try
                    {
                        if (!ilgpuContext.IsDisposed)
                            ilgpuContext.Dispose();
                        ilgpuContext = null;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[BandDetectionForm] Error disposing ILGPU context: {ex.Message}");
                    }
                }
            }

            base.Dispose(disposing);
        }

        private void DisposeVarianceResources()
        {
            // Dispose old bitmaps
            if (xyVarianceImage != null)
            {
                xyVarianceImage.Dispose();
                xyVarianceImage = null;
            }

            if (xzVarianceImage != null)
            {
                xzVarianceImage.Dispose();
                xzVarianceImage = null;
            }

            if (yzVarianceImage != null)
            {
                yzVarianceImage.Dispose();
                yzVarianceImage = null;
            }

            // Clear arrays
            xyVarianceMap = null;
            xzVarianceMap = null;
            yzVarianceMap = null;

            xyVarianceDarkPeaks = null;
            xyVarianceBrightPeaks = null;
            xzVarianceDarkPeaks = null;
            xzVarianceBrightPeaks = null;
            yzVarianceDarkPeaks = null;
            yzVarianceBrightPeaks = null;

            // Force garbage collection
            Logger.Log("[Dispose] Running Garbage Collector");
            GC.Collect();
        }
    }
}