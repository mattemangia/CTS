using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace CTSegmenter
{
    public class DensityCalibrationPreviewForm : KryptonForm
    {
        private MainForm mainForm;
        private PictureBox previewBox;
        private TrackBar sliceTrackBar;
        private KryptonButton btnSelect;
        private KryptonButton btnCancel;
        private KryptonLabel lblSliceInfo;
        private KryptonLabel lblInstructions;

        private int currentSlice;
        private bool isDrawingSelection = false;
        private Point selectionStart;
        private Point selectionEnd;

        // Cache for storing rendered slice images (key = slice number, value = rendered bitmap)
        private LRUCache<int, Bitmap> sliceCache;

        // Selected region and average gray value
        public Rectangle SelectedRegion { get; private set; }
        public double AverageGrayValue { get; private set; }

        public DensityCalibrationPreviewForm(MainForm mainForm)
        {
            this.mainForm = mainForm;
            this.Text = "Density Calibration - Region Selection";
            this.Size = new Size(800, 700);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // Initialize the slice cache (store last 20 slices)
            sliceCache = new LRUCache<int, Bitmap>(20);

            // Start at the current slice being viewed in the main form
            currentSlice = mainForm.CurrentSlice;

            InitializeControls();
            LoadSlice(currentSlice);
        }

        private void InitializeControls()
        {
            // Instructions label
            lblInstructions = new KryptonLabel
            {
                Text = "Click and drag on the image to select a region for density calibration.",
                Dock = DockStyle.Top,
                Height = 30,
                //TextAlign = ContentAlignment.MiddleCenter,
                StateCommon = { ShortText = { Font = new Font("Segoe UI", 10, FontStyle.Bold) } }
            };

            // Slice information label
            lblSliceInfo = new KryptonLabel
            {
                Text = $"Slice: {currentSlice + 1}/{mainForm.GetDepth()}",
                Dock = DockStyle.Top,
                Height = 25,
                //TextAlign = ContentAlignment.MiddleCenter
            };

            // Preview picture box
            previewBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };

            previewBox.MouseDown += PreviewBox_MouseDown;
            previewBox.MouseMove += PreviewBox_MouseMove;
            previewBox.MouseUp += PreviewBox_MouseUp;
            previewBox.Paint += PreviewBox_Paint;

            // Slice navigation slider
            sliceTrackBar = new TrackBar
            {
                Dock = DockStyle.Bottom,
                Minimum = 0,
                Maximum = mainForm.GetDepth() - 1,
                Value = currentSlice,
                TickFrequency = Math.Max(1, mainForm.GetDepth() / 20),
                Height = 45
            };

            sliceTrackBar.ValueChanged += SliceTrackBar_ValueChanged;

            // Button panel
            KryptonPanel buttonPanel = new KryptonPanel
            {
                Dock = DockStyle.Bottom,
                Height = 50
            };

            // Select button
            btnSelect = new KryptonButton
            {
                Text = "Select Region",
                Location = new Point(this.Width - 250, 10),
                Size = new Size(110, 30),
                Enabled = false
            };

            btnSelect.Click += BtnSelect_Click;

            // Cancel button
            btnCancel = new KryptonButton
            {
                Text = "Cancel",
                Location = new Point(this.Width - 130, 10),
                Size = new Size(110, 30)
            };

            btnCancel.Click += BtnCancel_Click;

            // Add buttons to panel
            buttonPanel.Controls.Add(btnSelect);
            buttonPanel.Controls.Add(btnCancel);

            // Add controls to form
            this.Controls.Add(previewBox);
            this.Controls.Add(buttonPanel);
            this.Controls.Add(sliceTrackBar);
            this.Controls.Add(lblSliceInfo);
            this.Controls.Add(lblInstructions);
        }

        private void SliceTrackBar_ValueChanged(object sender, EventArgs e)
        {
            int newSlice = sliceTrackBar.Value;
            if (newSlice != currentSlice)
            {
                currentSlice = newSlice;
                lblSliceInfo.Text = $"Slice: {currentSlice + 1}/{mainForm.GetDepth()}";
                LoadSlice(currentSlice);

                // Reset selection when changing slices
                isDrawingSelection = false;
                selectionStart = Point.Empty;
                selectionEnd = Point.Empty;
                btnSelect.Enabled = false;
                previewBox.Invalidate();
            }
        }

        private void LoadSlice(int sliceNumber)
        {
            // Try to get from cache first
            Bitmap slice = sliceCache.Get(sliceNumber);

            if (slice == null)
            {
                // Render the slice if not in cache
                slice = RenderSlice(sliceNumber);
                sliceCache.Add(sliceNumber, slice);
            }

            // Display the slice
            previewBox.Image = slice;
        }

        private Bitmap RenderSlice(int sliceNumber)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();

            // Create a new bitmap for the slice
            Bitmap bitmap = new Bitmap(width, height);

            // Get direct access to the bitmap data
            System.Drawing.Imaging.BitmapData bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            int stride = bitmapData.Stride;
            System.IntPtr scan0 = bitmapData.Scan0;

            // Fill the bitmap with the slice data
            unsafe
            {
                byte* p = (byte*)(void*)scan0;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte value = mainForm.volumeData[x, y, sliceNumber];
                        int offset = y * stride + x * 3;

                        p[offset] = value;     // Blue
                        p[offset + 1] = value; // Green
                        p[offset + 2] = value; // Red
                    }
                }
            }

            // Unlock the bitmap
            bitmap.UnlockBits(bitmapData);

            return bitmap;
        }

        private void PreviewBox_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                selectionStart = e.Location;
                selectionEnd = e.Location;
                isDrawingSelection = true;
                btnSelect.Enabled = false;
                previewBox.Invalidate();
            }
        }

        private void PreviewBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDrawingSelection && e.Button == MouseButtons.Left)
            {
                selectionEnd = e.Location;
                previewBox.Invalidate();
            }
        }

        private void PreviewBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (isDrawingSelection && e.Button == MouseButtons.Left)
            {
                selectionEnd = e.Location;
                isDrawingSelection = false;

                // Convert screen coordinates to image coordinates
                Point imgStart = ConvertToImageCoordinates(selectionStart);
                Point imgEnd = ConvertToImageCoordinates(selectionEnd);

                int x = Math.Min(imgStart.X, imgEnd.X);
                int y = Math.Min(imgStart.Y, imgEnd.Y);
                int width = Math.Abs(imgEnd.X - imgStart.X);
                int height = Math.Abs(imgEnd.Y - imgStart.Y);

                // Ensure within bounds
                x = Math.Max(0, Math.Min(x, mainForm.GetWidth() - 1));
                y = Math.Max(0, Math.Min(y, mainForm.GetHeight() - 1));
                width = Math.Min(width, mainForm.GetWidth() - x);
                height = Math.Min(height, mainForm.GetHeight() - y);

                if (width > 5 && height > 5)  // Minimum size check
                {
                    SelectedRegion = new Rectangle(x, y, width, height);
                    AverageGrayValue = CalculateAverageGrayValue(SelectedRegion);
                    btnSelect.Enabled = true;
                }

                previewBox.Invalidate();
            }
        }

        private Point ConvertToImageCoordinates(Point screenPoint)
        {
            // Calculate image scaling within the picture box
            if (previewBox.Image == null)
                return screenPoint;

            // Get the image and picturebox sizes
            double imageWidth = previewBox.Image.Width;
            double imageHeight = previewBox.Image.Height;
            double boxWidth = previewBox.ClientSize.Width;
            double boxHeight = previewBox.ClientSize.Height;

            // Calculate the scaling to fit the image in the picturebox (respecting aspect ratio)
            double ratioX = boxWidth / imageWidth;
            double ratioY = boxHeight / imageHeight;
            double ratio = Math.Min(ratioX, ratioY);

            // Calculate the center offset
            double offsetX = (boxWidth - (imageWidth * ratio)) / 2;
            double offsetY = (boxHeight - (imageHeight * ratio)) / 2;

            // Convert screen coordinates to image coordinates
            int imgX = (int)((screenPoint.X - offsetX) / ratio);
            int imgY = (int)((screenPoint.Y - offsetY) / ratio);

            return new Point(imgX, imgY);
        }

        private double CalculateAverageGrayValue(Rectangle region)
        {
            if (mainForm.volumeData == null) return 128;

            long total = 0;
            int count = 0;

            for (int y = region.Y; y < region.Y + region.Height; y++)
            {
                for (int x = region.X; x < region.X + region.Width; x++)
                {
                    if (x >= 0 && x < mainForm.GetWidth() &&
                        y >= 0 && y < mainForm.GetHeight())
                    {
                        total += mainForm.volumeData[x, y, currentSlice];
                        count++;
                    }
                }
            }

            return count > 0 ? (double)total / count : 128;
        }

        private void PreviewBox_Paint(object sender, PaintEventArgs e)
        {
            // Draw the selection rectangle
            if (!selectionStart.IsEmpty && !selectionEnd.IsEmpty)
            {
                // Calculate the rectangle in screen coordinates
                int x = Math.Min(selectionStart.X, selectionEnd.X);
                int y = Math.Min(selectionStart.Y, selectionEnd.Y);
                int width = Math.Abs(selectionEnd.X - selectionStart.X);
                int height = Math.Abs(selectionEnd.Y - selectionStart.Y);

                // Draw the selection rectangle
                using (Pen pen = new Pen(Color.Yellow, 2))
                {
                    e.Graphics.DrawRectangle(pen, x, y, width, height);
                }

                // Fill with semi-transparent yellow
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(50, Color.Yellow)))
                {
                    e.Graphics.FillRectangle(brush, x, y, width, height);
                }

                // Draw selection info if we have a valid selection
                if (btnSelect.Enabled)
                {
                    string info = $"Region: {SelectedRegion.X},{SelectedRegion.Y} Size: {SelectedRegion.Width}×{SelectedRegion.Height} Avg: {AverageGrayValue:F1}";
                    using (Font font = new Font("Arial", 9, FontStyle.Bold))
                    using (SolidBrush textBrush = new SolidBrush(Color.Yellow))
                    using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(128, Color.Black)))
                    {
                        SizeF textSize = e.Graphics.MeasureString(info, font);
                        RectangleF textRect = new RectangleF(x, y - textSize.Height - 5, textSize.Width, textSize.Height);

                        // Ensure text stays within view
                        if (textRect.Y < 0)
                            textRect.Y = y + height + 5;

                        e.Graphics.FillRectangle(bgBrush, textRect);
                        e.Graphics.DrawString(info, font, textBrush, textRect.Location);
                    }
                }
            }
        }

        private void BtnSelect_Click(object sender, EventArgs e)
        {
            // Return the selected region
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose cached bitmaps by getting the keys first
                if (sliceCache != null)
                {
                    foreach (var key in sliceCache.GetKeys())
                    {
                        var bitmap = sliceCache.Get(key);
                        bitmap?.Dispose();
                    }
                }

                // Dispose the current image
                if (previewBox != null && previewBox.Image != null)
                {
                    previewBox.Image.Dispose();
                    previewBox.Image = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}