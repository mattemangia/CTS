using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTSegmenter
{
    public class BrightnessContrastForm : Form
    {
        private MainForm mainForm;
        private ChunkedVolume volumeData;
        private int currentSlice;

        // Adjustment parameters
        private int brightness = 0;
        private int contrast = 100;
        private byte blackPoint = 0;
        private byte whitePoint = 255;

        // Image processing
        private Bitmap displayBitmap;
        private int[] histogram = new int[256];

        // UI elements
        private PictureBox slicePreview;
        private PictureBox histogramPreview;
        private TrackBar sliceTrackBar;
        private TrackBar brightnessTrackBar;
        private TrackBar contrastTrackBar;
        private NumericUpDown sliceNumeric;
        private NumericUpDown brightnessNumeric;
        private NumericUpDown contrastNumeric;
        private Button pickBlackBtn;
        private Button pickWhiteBtn;
        private Button resetBtn;
        private Button normalizeBtn;
        private Button equalizeBtn;
        private Button overwriteBtn;
        private Button exportBtn;
        private ProgressBar progressBar;
        private bool isPickingBlack = false;
        private bool isPickingWhite = false;

        // Histogram interaction
        private bool isDraggingBlack = false;
        private bool isDraggingWhite = false;

        public BrightnessContrastForm(MainForm form)
        {
            mainForm = form;
            volumeData = form.volumeData;
            currentSlice = form.CurrentSlice;
            InitializeComponent();
            LoadCurrentSlice();
        }

        private void InitializeComponent()
        {
            // Form setup
            this.Text = "Brightness & Contrast Adjustment";
            this.Size = new Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterScreen;

            // Main layout
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));

            // Image preview
            slicePreview = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            slicePreview.MouseClick += SlicePreview_MouseClick;
            mainLayout.Controls.Add(slicePreview, 0, 0);
            mainLayout.SetRowSpan(slicePreview, 2);

            // Controls panel
            Panel controlsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            mainLayout.Controls.Add(controlsPanel, 1, 0);

            // Histogram panel
            Panel histogramPanel = new Panel
            {
                Dock = DockStyle.Fill
            };
            mainLayout.Controls.Add(histogramPanel, 1, 1);

            // Histogram preview
            histogramPreview = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };
            histogramPreview.Paint += HistogramPreview_Paint;
            histogramPreview.MouseDown += HistogramPreview_MouseDown;
            histogramPreview.MouseMove += HistogramPreview_MouseMove;
            histogramPreview.MouseUp += HistogramPreview_MouseUp;
            histogramPanel.Controls.Add(histogramPreview);

            // Controls layout
            TableLayoutPanel controlsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 7
            };
            controlsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
            controlsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));

            // Slice controls
            controlsLayout.Controls.Add(new Label { Text = "Slice:", AutoSize = true }, 0, 0);
            sliceTrackBar = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetDepth() - 1,
                Value = currentSlice,
                Width = 200,
                TickFrequency = Math.Max(1, mainForm.GetDepth() / 20)
            };
            sliceTrackBar.ValueChanged += SliceTrackBar_ValueChanged;
            controlsLayout.Controls.Add(sliceTrackBar, 0, 1);

            sliceNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = mainForm.GetDepth() - 1,
                Value = currentSlice
            };
            sliceNumeric.ValueChanged += SliceNumeric_ValueChanged;
            controlsLayout.Controls.Add(sliceNumeric, 1, 1);

            // Brightness controls
            controlsLayout.Controls.Add(new Label { Text = "Brightness:", AutoSize = true }, 0, 2);
            brightnessTrackBar = new TrackBar
            {
                Minimum = -128,
                Maximum = 128,
                Value = brightness,
                Width = 200,
                TickFrequency = 32
            };
            brightnessTrackBar.ValueChanged += BrightnessTrackBar_ValueChanged;
            controlsLayout.Controls.Add(brightnessTrackBar, 0, 3);

            brightnessNumeric = new NumericUpDown
            {
                Minimum = -128,
                Maximum = 128,
                Value = brightness
            };
            brightnessNumeric.ValueChanged += BrightnessNumeric_ValueChanged;
            controlsLayout.Controls.Add(brightnessNumeric, 1, 3);

            // Contrast controls
            controlsLayout.Controls.Add(new Label { Text = "Contrast:", AutoSize = true }, 0, 4);
            contrastTrackBar = new TrackBar
            {
                Minimum = 1,
                Maximum = 200,
                Value = contrast,
                Width = 200,
                TickFrequency = 20
            };
            contrastTrackBar.ValueChanged += ContrastTrackBar_ValueChanged;
            controlsLayout.Controls.Add(contrastTrackBar, 0, 5);

            contrastNumeric = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 200,
                Value = contrast
            };
            contrastNumeric.ValueChanged += ContrastNumeric_ValueChanged;
            controlsLayout.Controls.Add(contrastNumeric, 1, 5);

            // Black/White point controls
            FlowLayoutPanel pickPointsPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight
            };

            pickBlackBtn = new Button
            {
                Text = "Pick Black Point",
                AutoSize = true
            };
            pickBlackBtn.Click += PickBlackBtn_Click;
            pickPointsPanel.Controls.Add(pickBlackBtn);

            pickWhiteBtn = new Button
            {
                Text = "Pick White Point",
                AutoSize = true
            };
            pickWhiteBtn.Click += PickWhiteBtn_Click;
            pickPointsPanel.Controls.Add(pickWhiteBtn);

            controlsLayout.Controls.Add(pickPointsPanel, 0, 6);
            controlsLayout.SetColumnSpan(pickPointsPanel, 2);

            // Histogram operations buttons
            FlowLayoutPanel histogramBtnsPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0, 10, 0, 0)
            };

            resetBtn = new Button
            {
                Text = "Reset",
                AutoSize = true
            };
            resetBtn.Click += ResetBtn_Click;
            histogramBtnsPanel.Controls.Add(resetBtn);

            normalizeBtn = new Button
            {
                Text = "Normalize",
                AutoSize = true
            };
            normalizeBtn.Click += NormalizeBtn_Click;
            histogramBtnsPanel.Controls.Add(normalizeBtn);

            equalizeBtn = new Button
            {
                Text = "Equalize",
                AutoSize = true
            };
            equalizeBtn.Click += EqualizeBtn_Click;
            histogramBtnsPanel.Controls.Add(equalizeBtn);

            controlsLayout.RowCount = 8;
            controlsLayout.Controls.Add(histogramBtnsPanel, 0, 7);
            controlsLayout.SetColumnSpan(histogramBtnsPanel, 2);

            // Dataset operation buttons
            FlowLayoutPanel datasetBtnsPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0, 10, 0, 0)
            };

            overwriteBtn = new Button
            {
                Text = "Overwrite Dataset",
                AutoSize = true
            };
            overwriteBtn.Click += OverwriteBtn_Click;
            datasetBtnsPanel.Controls.Add(overwriteBtn);

            exportBtn = new Button
            {
                Text = "Export to New Dataset",
                AutoSize = true
            };
            exportBtn.Click += ExportBtn_Click;
            datasetBtnsPanel.Controls.Add(exportBtn);

            controlsLayout.RowCount = 9;
            controlsLayout.Controls.Add(datasetBtnsPanel, 0, 8);
            controlsLayout.SetColumnSpan(datasetBtnsPanel, 2);

            // Progress bar
            progressBar = new ProgressBar
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                Visible = false
            };

            controlsPanel.Controls.Add(controlsLayout);
            this.Controls.Add(progressBar);
            this.Controls.Add(mainLayout);

            // Add tooltips
            ToolTip toolTip = new ToolTip();
            toolTip.SetToolTip(pickBlackBtn, "Click on the image to set the black point");
            toolTip.SetToolTip(pickWhiteBtn, "Click on the image to set the white point");
            toolTip.SetToolTip(resetBtn, "Reset all adjustments to default values");
            toolTip.SetToolTip(normalizeBtn, "Stretch contrast to use full intensity range");
            toolTip.SetToolTip(equalizeBtn, "Apply histogram equalization to enhance contrast");
            toolTip.SetToolTip(overwriteBtn, "Apply current adjustments to the entire dataset");
            toolTip.SetToolTip(exportBtn, "Export adjusted dataset as 8-bit BMP files");

            Logger.Log("[BrightnessContrastForm] Form initialized");
        }

        private void LoadCurrentSlice()
        {
            try
            {
                // Create a bitmap for the current slice
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();

                if (displayBitmap != null)
                {
                    displayBitmap.Dispose();
                }

                displayBitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);

                // Lock bits for fast processing
                BitmapData bmpData = displayBitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    displayBitmap.PixelFormat);

                int stride = bmpData.Stride;
                IntPtr scan0 = bmpData.Scan0;

                // Clear histogram
                Array.Clear(histogram, 0, histogram.Length);

                unsafe
                {
                    byte* p = (byte*)(void*)scan0;

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            // Get grayscale value from volume
                            byte gray = volumeData[x, y, currentSlice];

                            // Apply brightness/contrast adjustment
                            int adjustedValue = ApplyBrightnessContrast(gray);
                            byte newGray = (byte)Math.Max(0, Math.Min(255, adjustedValue));

                            // Update histogram
                            histogram[gray]++;

                            // Set RGB (grayscale)
                            int offset = y * stride + x * 3;
                            p[offset] = newGray;     // Blue
                            p[offset + 1] = newGray; // Green
                            p[offset + 2] = newGray; // Red
                        }
                    }
                }

                displayBitmap.UnlockBits(bmpData);

                // Update UI
                slicePreview.Image = displayBitmap;
                histogramPreview.Invalidate();

                Logger.Log($"[BrightnessContrastForm] Loaded slice {currentSlice} with brightness={brightness}, contrast={contrast}, black={blackPoint}, white={whitePoint}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BrightnessContrastForm] Error loading slice: {ex.Message}");
                MessageBox.Show($"Error loading slice: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private int ApplyBrightnessContrast(byte value)
        {
            // Map the value from [blackPoint, whitePoint] to [0, 255]
            double normalized = 0;
            if (whitePoint > blackPoint)
            {
                normalized = (value - blackPoint) / (double)(whitePoint - blackPoint);
            }
            normalized = Math.Max(0, Math.Min(1, normalized));

            // Apply contrast (percentage)
            double contrasted = (normalized - 0.5) * (contrast / 100.0) + 0.5;
            contrasted = Math.Max(0, Math.Min(1, contrasted));

            // Apply brightness (offset)
            int result = (int)(contrasted * 255) + brightness;
            return result;
        }

        private void HistogramPreview_Paint(object sender, PaintEventArgs e)
        {
            // Draw histogram
            int maxCount = histogram.Max();
            int width = histogramPreview.Width;
            int height = histogramPreview.Height - 20; // Reserve space for markers

            e.Graphics.Clear(Color.Black);

            // Draw histogram bars
            using (Pen pen = new Pen(Color.White))
            {
                for (int i = 0; i < 256; i++)
                {
                    int x = i * width / 256;
                    int barHeight = maxCount > 0 ? (int)(histogram[i] * height / maxCount) : 0;
                    e.Graphics.DrawLine(pen, x, height, x, height - barHeight);
                }
            }

            // Draw black point marker
            int blackX = blackPoint * width / 256;
            using (Pen blackPen = new Pen(Color.Blue, 2))
            {
                e.Graphics.DrawLine(blackPen, blackX, 0, blackX, height);
                e.Graphics.DrawString("B", Font, Brushes.Blue, blackX - 5, height + 5);
            }

            // Draw white point marker
            int whiteX = whitePoint * width / 256;
            using (Pen whitePen = new Pen(Color.Red, 2))
            {
                e.Graphics.DrawLine(whitePen, whiteX, 0, whiteX, height);
                e.Graphics.DrawString("W", Font, Brushes.Red, whiteX - 5, height + 5);
            }
        }

        // Event handlers for controls
        private void SliceTrackBar_ValueChanged(object sender, EventArgs e)
        {
            currentSlice = sliceTrackBar.Value;
            sliceNumeric.Value = currentSlice;
            LoadCurrentSlice();
        }

        private void SliceNumeric_ValueChanged(object sender, EventArgs e)
        {
            currentSlice = (int)sliceNumeric.Value;
            sliceTrackBar.Value = currentSlice;
            LoadCurrentSlice();
        }

        private void BrightnessTrackBar_ValueChanged(object sender, EventArgs e)
        {
            brightness = brightnessTrackBar.Value;
            brightnessNumeric.Value = brightness;
            LoadCurrentSlice();
        }

        private void BrightnessNumeric_ValueChanged(object sender, EventArgs e)
        {
            brightness = (int)brightnessNumeric.Value;
            brightnessTrackBar.Value = brightness;
            LoadCurrentSlice();
        }

        private void ContrastTrackBar_ValueChanged(object sender, EventArgs e)
        {
            contrast = contrastTrackBar.Value;
            contrastNumeric.Value = contrast;
            LoadCurrentSlice();
        }

        private void ContrastNumeric_ValueChanged(object sender, EventArgs e)
        {
            contrast = (int)contrastNumeric.Value;
            contrastTrackBar.Value = contrast;
            LoadCurrentSlice();
        }

        private void HistogramPreview_MouseDown(object sender, MouseEventArgs e)
        {
            int width = histogramPreview.Width;
            int blackX = blackPoint * width / 256;
            int whiteX = whitePoint * width / 256;

            // Check if user clicked near one of the markers
            if (Math.Abs(e.X - blackX) < 5)
            {
                isDraggingBlack = true;
            }
            else if (Math.Abs(e.X - whiteX) < 5)
            {
                isDraggingWhite = true;
            }
        }

        private void HistogramPreview_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDraggingBlack && !isDraggingWhite)
                return;

            int width = histogramPreview.Width;
            int newValue = Math.Max(0, Math.Min(255, e.X * 256 / width));

            if (isDraggingBlack)
            {
                blackPoint = (byte)Math.Min(newValue, whitePoint - 1);
                LoadCurrentSlice();
            }
            else if (isDraggingWhite)
            {
                whitePoint = (byte)Math.Max(newValue, blackPoint + 1);
                LoadCurrentSlice();
            }
        }

        private void HistogramPreview_MouseUp(object sender, MouseEventArgs e)
        {
            isDraggingBlack = false;
            isDraggingWhite = false;
        }

        // Black/white point picking from image
        private void PickBlackBtn_Click(object sender, EventArgs e)
        {
            isPickingBlack = true;
            isPickingWhite = false;
            slicePreview.Cursor = Cursors.Cross;
            pickBlackBtn.BackColor = Color.LightBlue;
            pickWhiteBtn.BackColor = SystemColors.Control;
            Logger.Log("[BrightnessContrastForm] Black point picking mode enabled");
        }

        private void PickWhiteBtn_Click(object sender, EventArgs e)
        {
            isPickingWhite = true;
            isPickingBlack = false;
            slicePreview.Cursor = Cursors.Cross;
            pickWhiteBtn.BackColor = Color.LightBlue;
            pickBlackBtn.BackColor = SystemColors.Control;
            Logger.Log("[BrightnessContrastForm] White point picking mode enabled");
        }

        private void SlicePreview_MouseClick(object sender, MouseEventArgs e)
        {
            if (!isPickingBlack && !isPickingWhite)
                return;

            // Convert click coordinates to image coordinates
            int width = slicePreview.Image.Width;
            int height = slicePreview.Image.Height;

            // Calculate the scaled and centered image position in the PictureBox
            double scale = Math.Min(
                (double)slicePreview.Width / width,
                (double)slicePreview.Height / height);

            int scaledWidth = (int)(width * scale);
            int scaledHeight = (int)(height * scale);

            int offsetX = (slicePreview.Width - scaledWidth) / 2;
            int offsetY = (slicePreview.Height - scaledHeight) / 2;

            // Convert PictureBox coordinates to image coordinates
            int imageX = (int)((e.X - offsetX) / scale);
            int imageY = (int)((e.Y - offsetY) / scale);

            // Make sure we're within the image bounds
            if (imageX < 0 || imageX >= width || imageY < 0 || imageY >= height)
                return;

            // Get the pixel value at the clicked point
            byte pixelValue = volumeData[imageX, imageY, currentSlice];

            if (isPickingBlack)
            {
                blackPoint = pixelValue;
                isPickingBlack = false;
                pickBlackBtn.BackColor = SystemColors.Control;
                slicePreview.Cursor = Cursors.Default;
                Logger.Log($"[BrightnessContrastForm] Black point set to {blackPoint} at position ({imageX}, {imageY})");
            }
            else if (isPickingWhite)
            {
                whitePoint = pixelValue;
                isPickingWhite = false;
                pickWhiteBtn.BackColor = SystemColors.Control;
                slicePreview.Cursor = Cursors.Default;
                Logger.Log($"[BrightnessContrastForm] White point set to {whitePoint} at position ({imageX}, {imageY})");
            }

            // Ensure black point < white point
            if (blackPoint >= whitePoint)
            {
                byte temp = blackPoint;
                blackPoint = (byte)Math.Max(0, whitePoint - 1);
                whitePoint = (byte)Math.Min(255, temp + 1);
                Logger.Log($"[BrightnessContrastForm] Adjusted points to ensure black < white: black={blackPoint}, white={whitePoint}");
            }

            LoadCurrentSlice();
        }

        private void ResetBtn_Click(object sender, EventArgs e)
        {
            // Reset all adjustment values to defaults
            brightness = 0;
            brightnessTrackBar.Value = 0;
            brightnessNumeric.Value = 0;

            contrast = 100;
            contrastTrackBar.Value = 100;
            contrastNumeric.Value = 100;

            blackPoint = 0;
            whitePoint = 255;

            isPickingBlack = false;
            isPickingWhite = false;
            pickBlackBtn.BackColor = SystemColors.Control;
            pickWhiteBtn.BackColor = SystemColors.Control;
            slicePreview.Cursor = Cursors.Default;

            LoadCurrentSlice();
            Logger.Log("[BrightnessContrastForm] All adjustments reset to default values");
        }

        private void NormalizeBtn_Click(object sender, EventArgs e)
        {
            // Find min and max values in the current slice for auto contrast
            byte min = 255;
            byte max = 0;
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte value = volumeData[x, y, currentSlice];
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            }

            // Ensure we don't have a flat image
            if (min == max)
            {
                MessageBox.Show("The image has uniform intensity. Normalization has no effect.",
                                "Normalization", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Set black and white points to min and max values
            blackPoint = min;
            whitePoint = max;

            // Reset brightness and contrast
            brightness = 0;
            brightnessTrackBar.Value = 0;
            brightnessNumeric.Value = 0;

            contrast = 100;
            contrastTrackBar.Value = 100;
            contrastNumeric.Value = 100;

            LoadCurrentSlice();
            Logger.Log($"[BrightnessContrastForm] Normalized: min={min}, max={max}");
        }

        private void EqualizeBtn_Click(object sender, EventArgs e)
        {
            // Perform histogram equalization on the current slice
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();

            // Compute cumulative histogram
            int[] cdf = new int[256];
            cdf[0] = histogram[0];

            for (int i = 1; i < 256; i++)
            {
                cdf[i] = cdf[i - 1] + histogram[i];
            }

            // Skip equalization if the image is uniform
            if (cdf[255] == 0)
            {
                MessageBox.Show("The image has uniform intensity. Equalization has no effect.",
                                "Equalization", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Create equalization lookup table
            byte[] equalizedValues = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                equalizedValues[i] = (byte)(Math.Max(0, Math.Min(255,
                                           (cdf[i] - cdf[0]) * 255 / (cdf[255] - cdf[0]))));
            }

            // Create an equalized copy of the current slice
            byte[,] equalizedSlice = new byte[width, height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte originalValue = volumeData[x, y, currentSlice];
                    equalizedSlice[x, y] = equalizedValues[originalValue];
                }
            }

            // Create a dialog to preview and confirm equalization
            using (Form previewForm = new Form())
            {
                previewForm.Text = "Equalization Preview";
                previewForm.Size = new Size(600, 400);
                previewForm.StartPosition = FormStartPosition.CenterParent;

                PictureBox preview = new PictureBox
                {
                    Dock = DockStyle.Fill,
                    SizeMode = PictureBoxSizeMode.Zoom
                };

                // Create preview bitmap
                Bitmap equalizedBitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                BitmapData bmpData = equalizedBitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    equalizedBitmap.PixelFormat);

                int stride = bmpData.Stride;

                unsafe
                {
                    byte* p = (byte*)(void*)bmpData.Scan0;

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            byte gray = equalizedSlice[x, y];
                            int offset = y * stride + x * 3;
                            p[offset] = gray;     // Blue
                            p[offset + 1] = gray; // Green
                            p[offset + 2] = gray; // Red
                        }
                    }
                }

                equalizedBitmap.UnlockBits(bmpData);
                preview.Image = equalizedBitmap;

                Button applyBtn = new Button
                {
                    Text = "Apply",
                    Dock = DockStyle.Bottom,
                    DialogResult = DialogResult.OK
                };

                Button cancelBtn = new Button
                {
                    Text = "Cancel",
                    Dock = DockStyle.Bottom,
                    DialogResult = DialogResult.Cancel
                };

                previewForm.Controls.Add(preview);
                previewForm.Controls.Add(applyBtn);
                previewForm.Controls.Add(cancelBtn);

                // Show dialog and check result
                if (previewForm.ShowDialog() == DialogResult.OK)
                {
                    // Apply equalization to the current slice in memory (without modifying original data)
                    // We do this by adjusting our black/white points to match the equalization

                    // Reset to default settings first
                    brightness = 0;
                    brightnessTrackBar.Value = 0;
                    brightnessNumeric.Value = 0;

                    contrast = 100;
                    contrastTrackBar.Value = 100;
                    contrastNumeric.Value = 100;

                    // Find the new "effective" black and white points
                    blackPoint = 0;
                    whitePoint = 255;

                    // We'll use the equalized values as a custom LUT
                    blackPoint = 0;
                    whitePoint = 255;

                    // Create a new bitmap with equalized values
                    if (displayBitmap != null)
                    {
                        displayBitmap.Dispose();
                    }

                    displayBitmap = new Bitmap(equalizedBitmap);
                    slicePreview.Image = displayBitmap;

                    Logger.Log("[BrightnessContrastForm] Applied histogram equalization");
                }
                else
                {
                    Logger.Log("[BrightnessContrastForm] Histogram equalization cancelled");
                }
            }
        }

        private async void OverwriteBtn_Click(object sender, EventArgs e)
        {
            // Confirm the user wants to permanently modify the dataset
            DialogResult result = MessageBox.Show(
                "This will permanently modify the existing dataset with the current brightness and contrast settings.\n\n" +
                "Do you want to continue?",
                "Overwrite Dataset",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
                return;

            try
            {
                // Prepare for processing
                progressBar.Visible = true;
                progressBar.Value = 0;
                progressBar.Maximum = mainForm.GetDepth();

                // Disable controls during processing
                EnableControls(false);

                // Create a copy of the current settings to use during processing
                int processBrightness = brightness;
                int processContrast = contrast;
                byte processBlackPoint = blackPoint;
                byte processWhitePoint = whitePoint;

                Logger.Log("[BrightnessContrastForm] Starting dataset overwrite...");

                // Create a new volume to hold the adjusted data
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();
                int chunkDim = volumeData.ChunkDim;

                ChunkedVolume newVolume = new ChunkedVolume(width, height, depth, chunkDim);

                // Process all slices in parallel
                await Task.Run(() => {
                    ConcurrentBag<Exception> exceptions = new ConcurrentBag<Exception>();

                    Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, z => {
                        try
                        {
                            // Process each slice
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    byte origValue = volumeData[x, y, z];

                                    // Apply the same adjustment we use for display
                                    int adjustedValue = ApplyAdjustment(origValue, processBlackPoint, processWhitePoint,
                                                                       processBrightness, processContrast);

                                    // Store adjusted value in the new volume
                                    newVolume[x, y, z] = (byte)Math.Max(0, Math.Min(255, adjustedValue));
                                }
                            }

                            // Update progress
                            this.Invoke(new Action(() => {
                                progressBar.Value = Math.Min(progressBar.Maximum, progressBar.Value + 1);
                            }));
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                            Logger.Log($"[BrightnessContrastForm] Error processing slice {z}: {ex.Message}");
                        }
                    });

                    // Check for errors
                    if (exceptions.Count > 0)
                    {
                        throw new AggregateException("Errors occurred during processing", exceptions);
                    }
                });

                // Update the main form with the new volume
                mainForm.UpdateVolumeData(newVolume);

                // Reset adjustment settings
                ResetBtn_Click(null, EventArgs.Empty);

                MessageBox.Show("Dataset has been successfully updated with the adjusted values.",
                              "Operation Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);

                Logger.Log("[BrightnessContrastForm] Dataset overwrite completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BrightnessContrastForm] Error during dataset overwrite: {ex.Message}");
                MessageBox.Show($"An error occurred while processing the dataset:\n\n{ex.Message}",
                              "Processing Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // Re-enable controls and hide progress bar
                EnableControls(true);
                progressBar.Visible = false;
            }
        }

        private async void ExportBtn_Click(object sender, EventArgs e)
        {
            // Ask the user for an export directory
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select a folder to export the adjusted dataset";

                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                string exportFolder = dialog.SelectedPath;

                // Check if directory is empty
                if (Directory.GetFiles(exportFolder).Length > 0)
                {
                    DialogResult result = MessageBox.Show(
                        "The selected folder is not empty. Files may be overwritten.\n\n" +
                        "Do you want to continue?",
                        "Export Dataset",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);

                    if (result != DialogResult.Yes)
                        return;
                }

                try
                {
                    // Prepare for processing
                    progressBar.Visible = true;
                    progressBar.Value = 0;
                    progressBar.Maximum = mainForm.GetDepth();

                    // Disable controls during processing
                    EnableControls(false);

                    // Create a copy of the current settings to use during processing
                    int processBrightness = brightness;
                    int processContrast = contrast;
                    byte processBlackPoint = blackPoint;
                    byte processWhitePoint = whitePoint;

                    Logger.Log($"[BrightnessContrastForm] Starting dataset export to {exportFolder}...");

                    // Process all slices in parallel
                    await Task.Run(() => {
                        ConcurrentBag<Exception> exceptions = new ConcurrentBag<Exception>();
                        int width = mainForm.GetWidth();
                        int height = mainForm.GetHeight();
                        int depth = mainForm.GetDepth();

                        Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, z => {
                            try
                            {
                                // Create a bitmap for this slice
                                using (Bitmap sliceBitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                                {
                                    // Lock the bits for fast processing
                                    BitmapData bmpData = sliceBitmap.LockBits(
                                        new Rectangle(0, 0, width, height),
                                        ImageLockMode.WriteOnly,
                                        sliceBitmap.PixelFormat);

                                    int stride = bmpData.Stride;
                                    IntPtr scan0 = bmpData.Scan0;

                                    unsafe
                                    {
                                        byte* p = (byte*)(void*)scan0;

                                        for (int y = 0; y < height; y++)
                                        {
                                            for (int x = 0; x < width; x++)
                                            {
                                                // Get grayscale value from volume
                                                byte gray = volumeData[x, y, z];

                                                // Apply adjustment
                                                int adjustedValue = ApplyAdjustment(gray, processBlackPoint, processWhitePoint,
                                                                                  processBrightness, processContrast);
                                                byte newGray = (byte)Math.Max(0, Math.Min(255, adjustedValue));

                                                // Set RGB (grayscale)
                                                int offset = y * stride + x * 3;
                                                p[offset] = newGray;     // Blue
                                                p[offset + 1] = newGray; // Green
                                                p[offset + 2] = newGray; // Red
                                            }
                                        }
                                    }

                                    sliceBitmap.UnlockBits(bmpData);

                                    // Save the slice as a BMP file
                                    string filename = Path.Combine(exportFolder, $"slice_{z:D5}.bmp");
                                    sliceBitmap.Save(filename, ImageFormat.Bmp);
                                }

                                // Update progress on the UI thread
                                this.Invoke(new Action(() => {
                                    progressBar.Value = Math.Min(progressBar.Maximum, progressBar.Value + 1);
                                }));
                            }
                            catch (Exception ex)
                            {
                                exceptions.Add(ex);
                                Logger.Log($"[BrightnessContrastForm] Error exporting slice {z}: {ex.Message}");
                            }
                        });

                        // Check for errors
                        if (exceptions.Count > 0)
                        {
                            throw new AggregateException("Errors occurred during export", exceptions);
                        }
                    });

                    MessageBox.Show($"Dataset has been successfully exported to:\n{exportFolder}",
                                  "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    Logger.Log("[BrightnessContrastForm] Dataset export completed successfully");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[BrightnessContrastForm] Error during dataset export: {ex.Message}");
                    MessageBox.Show($"An error occurred while exporting the dataset:\n\n{ex.Message}",
                                  "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    // Re-enable controls and hide progress bar
                    EnableControls(true);
                    progressBar.Visible = false;
                }
            }
        }

        private int ApplyAdjustment(byte value, byte bPoint, byte wPoint, int bright, int cont)
        {
            // Map the value from [blackPoint, whitePoint] to [0, 255]
            double normalized = 0;
            if (wPoint > bPoint)
            {
                normalized = (value - bPoint) / (double)(wPoint - bPoint);
            }
            normalized = Math.Max(0, Math.Min(1, normalized));

            // Apply contrast (percentage)
            double contrasted = (normalized - 0.5) * (cont / 100.0) + 0.5;
            contrasted = Math.Max(0, Math.Min(1, contrasted));

            // Apply brightness (offset)
            int result = (int)(contrasted * 255) + bright;
            return result;
        }

        private void EnableControls(bool enable)
        {
            // Enable/disable all interactive controls during processing
            sliceTrackBar.Enabled = enable;
            sliceNumeric.Enabled = enable;
            brightnessTrackBar.Enabled = enable;
            brightnessNumeric.Enabled = enable;
            contrastTrackBar.Enabled = enable;
            contrastNumeric.Enabled = enable;
            pickBlackBtn.Enabled = enable;
            pickWhiteBtn.Enabled = enable;
            resetBtn.Enabled = enable;
            normalizeBtn.Enabled = enable;
            equalizeBtn.Enabled = enable;
            overwriteBtn.Enabled = enable;
            exportBtn.Enabled = enable;
        }
    }
}