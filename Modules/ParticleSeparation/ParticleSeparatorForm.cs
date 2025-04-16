using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTSegmenter
{
    public enum ViewType
    {
        XY,
        XZ,
        YZ
    }

    public class ParticleSeparatorForm : Form
    {
        private bool _initialized = false;
        private MainForm mainForm;
        private Material selectedMaterial;
        private ParticleSeparator separator;
        private ParticleSeparator.SeparationResult result;

        // Cancellation support
        private CancellationTokenSource cancellationTokenSource;

        // UI controls - Main Layout
        private TableLayoutPanel mainLayout;
        private ProgressBar progressBar;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel;

        // View panels and picture boxes
        private Panel xyViewPanel, xzViewPanel, yzViewPanel, controlPanel;
        private PictureBox xyPictureBox, xzPictureBox, yzPictureBox;
        private TrackBar xySliceTrackBar, xzSliceTrackBar, yzSliceTrackBar;
        private NumericUpDown xySliceNumeric, xzSliceNumeric, yzSliceNumeric;
        private Label xyLabel, xzLabel, yzLabel;

        // Control panel elements
        private CheckBox useGpuCheckBox;
        private CheckBox generateCsvCheckBox;
        private RadioButton currentSliceRadio;
        private RadioButton wholeVolumeRadio;
        private RadioButton conservativeRadio;
        private RadioButton aggressiveRadio;
        private Button separateButton;
        private Button saveButton;
        private Button exportToMainButton;
        private Button extractParticleButton;
        private Button removeIslandsButton;
        private Button closeButton;
        private Label particleInfoLabel;
        private ListView particleListView;
        private ToolTip toolTip;
        private ComboBox sortOrderComboBox;
        private NumericUpDown minSizeInput;
        private Label minSizeLabel;

        // Current view positions
        private int currentXYSlice;
        private int currentXZSlice;
        private int currentYZSlice;

        // Selected particle highlight
        private int highlightedParticleId = -1;

        // Color palette for particles
        private Dictionary<int, Color> particleColors = new Dictionary<int, Color>();
        private Random random = new Random(0); // Fixed seed for consistent colors

        // Task synchronization for rendering
        private TaskCompletionSource<bool> currentRenderTask = null;
        private readonly object renderLock = new object();

        public ParticleSeparatorForm(MainForm mainForm, Material selectedMaterial)
        {
            Logger.Log("[ParticleSeparatorForm] Opening particle separator for material: " + selectedMaterial.Name);

            this.mainForm = mainForm;
            this.selectedMaterial = selectedMaterial;

            InitializeComponent();

            // IMPORTANT: Don't call methods that might trigger UI updates
            // Set initial values without triggering events
            SafeInitializeViewPositions();

            // Create empty bitmaps for each view
            xyPictureBox.Image = CreateEmptyBitmap(mainForm.GetWidth(), mainForm.GetHeight());
            xzPictureBox.Image = CreateEmptyBitmap(mainForm.GetWidth(), mainForm.GetDepth());
            yzPictureBox.Image = CreateEmptyBitmap(mainForm.GetHeight(), mainForm.GetDepth());

            Logger.Log("[ParticleSeparatorForm] Initial views created with empty bitmaps");

            // Load material preview when form is fully loaded
            this.Load += ParticleSeparatorForm_Load;
        }

        private void ParticleSeparatorForm_Load(object sender, EventArgs e)
        {
            Logger.Log("[ParticleSeparatorForm] Form loaded, loading material preview");
            _initialized = true;
            LoadMaterialPreview();

            // Adjust columns after form has loaded
            AdjustListViewColumns();

            // Make sure particle info panel is properly sized
            particleInfoLabel.MaximumSize = new Size(
                particleInfoLabel.Parent.ClientSize.Width - 20, 0);
        }

        private void SafeInitializeViewPositions()
        {
            // Set initial slice positions
            currentXYSlice = mainForm.CurrentSlice;
            currentXZSlice = mainForm.GetHeight() / 2;
            currentYZSlice = mainForm.GetWidth() / 2;

            // Temporarily remove event handlers
            xySliceTrackBar.ValueChanged -= SliceTrackBar_ValueChanged;
            xySliceNumeric.ValueChanged -= SliceNumeric_ValueChanged;
            xzSliceTrackBar.ValueChanged -= SliceTrackBar_ValueChanged;
            xzSliceNumeric.ValueChanged -= SliceNumeric_ValueChanged;
            yzSliceTrackBar.ValueChanged -= SliceTrackBar_ValueChanged;
            yzSliceNumeric.ValueChanged -= SliceNumeric_ValueChanged;

            // Update controls without triggering events
            xySliceTrackBar.Value = currentXYSlice;
            xySliceNumeric.Value = currentXYSlice;
            xzSliceTrackBar.Value = currentXZSlice;
            xzSliceNumeric.Value = currentXZSlice;
            yzSliceTrackBar.Value = currentYZSlice;
            yzSliceNumeric.Value = currentYZSlice;

            // Reattach event handlers
            xySliceTrackBar.ValueChanged += SliceTrackBar_ValueChanged;
            xySliceNumeric.ValueChanged += SliceNumeric_ValueChanged;
            xzSliceTrackBar.ValueChanged += SliceTrackBar_ValueChanged;
            xzSliceNumeric.ValueChanged += SliceNumeric_ValueChanged;
            yzSliceTrackBar.ValueChanged += SliceTrackBar_ValueChanged;
            yzSliceNumeric.ValueChanged += SliceNumeric_ValueChanged;
        }

        private void LoadMaterialPreview()
        {
            try
            {
                Logger.Log("[ParticleSeparatorForm] Attempting to load material preview");

                // Get volume dimensions
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();

                if (width <= 0 || height <= 0 || depth <= 0)
                {
                    Logger.Log("[ParticleSeparatorForm] Invalid dimensions for preview");
                    return;
                }

                // Set initial slice positions
                currentXYSlice = Math.Min(mainForm.CurrentSlice, depth - 1);
                currentXZSlice = Math.Min(height / 2, height - 1);
                currentYZSlice = Math.Min(width / 2, width - 1);

                // Update slice controls on the UI thread
                this.Invoke((Action)(() => {
                    try
                    {
                        xySliceTrackBar.Value = currentXYSlice;
                        xySliceNumeric.Value = currentXYSlice;

                        xzSliceTrackBar.Value = currentXZSlice;
                        xzSliceNumeric.Value = currentXZSlice;

                        yzSliceTrackBar.Value = currentYZSlice;
                        yzSliceNumeric.Value = currentYZSlice;

                        Logger.Log("[ParticleSeparatorForm] Updated slice controls");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ParticleSeparatorForm] Error updating slice controls: {ex.Message}");
                    }
                }));

                // Render the preview images in a background thread
                Task.Run(() => {
                    try
                    {
                        // Create XY view bitmap (axial)
                        Bitmap xyBitmap = RenderMaterialPreviewBitmap(width, height, ViewType.XY, currentXYSlice);

                        // Create XZ view bitmap (coronal)
                        Bitmap xzBitmap = RenderMaterialPreviewBitmap(width, depth, ViewType.XZ, currentXZSlice);

                        // Create YZ view bitmap (sagittal)
                        Bitmap yzBitmap = RenderMaterialPreviewBitmap(height, depth, ViewType.YZ, currentYZSlice);

                        // Update UI on main thread
                        this.Invoke((Action)(() => {
                            try
                            {
                                // Update XY view
                                if (xyPictureBox.Image != null)
                                {
                                    var oldImage = xyPictureBox.Image;
                                    xyPictureBox.Image = xyBitmap;
                                    oldImage.Dispose();
                                }
                                else
                                {
                                    xyPictureBox.Image = xyBitmap;
                                }

                                // Update XZ view
                                if (xzPictureBox.Image != null)
                                {
                                    var oldImage = xzPictureBox.Image;
                                    xzPictureBox.Image = xzBitmap;
                                    oldImage.Dispose();
                                }
                                else
                                {
                                    xzPictureBox.Image = xzBitmap;
                                }

                                // Update YZ view
                                if (yzPictureBox.Image != null)
                                {
                                    var oldImage = yzPictureBox.Image;
                                    yzPictureBox.Image = yzBitmap;
                                    oldImage.Dispose();
                                }
                                else
                                {
                                    yzPictureBox.Image = yzBitmap;
                                }

                                Logger.Log("[ParticleSeparatorForm] Updated preview images");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[ParticleSeparatorForm] Error updating preview images: {ex.Message}");
                            }
                        }));
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ParticleSeparatorForm] Error rendering preview: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParticleSeparatorForm] Error in LoadMaterialPreview: {ex.Message}");
            }
        }

        private Bitmap RenderMaterialPreviewBitmap(int width, int height, ViewType viewType, int sliceIndex)
        {
            // Create a bitmap with the appropriate dimensions
            Bitmap bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height));

            try
            {
                // Draw directly using GDI+ for simplicity in the preview
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Black);

                    // Access the volume data and label data
                    ChunkedVolume volumeData = (ChunkedVolume)mainForm.volumeData;
                    ChunkedLabelVolume labelData = (ChunkedLabelVolume)mainForm.volumeLabels;

                    // Make sure data is available
                    if (volumeData == null && labelData == null)
                    {
                        // If no data, just show a message
                        using (Font font = new Font("Arial", 10))
                        {
                            g.DrawString("No data available", font, Brushes.White, new PointF(10, 10));
                        }
                        return bitmap;
                    }

                    // Determine how to access the data based on view type
                    switch (viewType)
                    {
                        case ViewType.XY:
                            // Draw the XY plane
                            for (int y = 0; y < Math.Min(height, volumeData?.Height ?? 0); y++)
                            {
                                for (int x = 0; x < Math.Min(width, volumeData?.Width ?? 0); x++)
                                {
                                    // Skip points outside the volume bounds
                                    if (x >= volumeData.Width || y >= volumeData.Height || sliceIndex >= volumeData.Depth)
                                        continue;

                                    // Get grayscale value
                                    byte grayValue = volumeData[x, y, sliceIndex];

                                    // Check if this voxel belongs to the selected material
                                    bool isMaterial = false;

                                    if (labelData != null &&
                                        x < labelData.Width &&
                                        y < labelData.Height &&
                                        sliceIndex < labelData.Depth)
                                    {
                                        if (labelData[x, y, sliceIndex] == selectedMaterial.ID)
                                        {
                                            isMaterial = true;
                                        }
                                    }

                                    // Draw the pixel
                                    if (isMaterial)
                                    {
                                        // Highlight the material with its color
                                        Color materialColor = Color.FromArgb(
                                            180, // Semi-transparent
                                            selectedMaterial.Color);
                                        bitmap.SetPixel(x, y, materialColor);
                                    }
                                    else
                                    {
                                        // Regular grayscale
                                        Color grayColor = Color.FromArgb(grayValue, grayValue, grayValue);
                                        bitmap.SetPixel(x, y, grayColor);
                                    }
                                }
                            }
                            break;

                        case ViewType.XZ:
                            // Draw the XZ plane
                            for (int z = 0; z < Math.Min(height, volumeData?.Depth ?? 0); z++)
                            {
                                for (int x = 0; x < Math.Min(width, volumeData?.Width ?? 0); x++)
                                {
                                    // Skip points outside the volume bounds
                                    if (x >= volumeData.Width || sliceIndex >= volumeData.Height || z >= volumeData.Depth)
                                        continue;

                                    // Get grayscale value
                                    byte grayValue = volumeData[x, sliceIndex, z];

                                    // Check if this voxel belongs to the selected material
                                    bool isMaterial = false;

                                    if (labelData != null &&
                                        x < labelData.Width &&
                                        sliceIndex < labelData.Height &&
                                        z < labelData.Depth)
                                    {
                                        if (labelData[x, sliceIndex, z] == selectedMaterial.ID)
                                        {
                                            isMaterial = true;
                                        }
                                    }

                                    // Draw the pixel
                                    if (isMaterial)
                                    {
                                        // Highlight the material with its color
                                        Color materialColor = Color.FromArgb(
                                            180, // Semi-transparent
                                            selectedMaterial.Color);
                                        bitmap.SetPixel(x, z, materialColor);
                                    }
                                    else
                                    {
                                        // Regular grayscale
                                        Color grayColor = Color.FromArgb(grayValue, grayValue, grayValue);
                                        bitmap.SetPixel(x, z, grayColor);
                                    }
                                }
                            }
                            break;

                        case ViewType.YZ:
                            // Draw the YZ plane
                            for (int z = 0; z < Math.Min(height, volumeData?.Depth ?? 0); z++)
                            {
                                for (int y = 0; y < Math.Min(width, volumeData?.Height ?? 0); y++)
                                {
                                    // Skip points outside the volume bounds
                                    if (sliceIndex >= volumeData.Width || y >= volumeData.Height || z >= volumeData.Depth)
                                        continue;

                                    // Get grayscale value
                                    byte grayValue = volumeData[sliceIndex, y, z];

                                    // Check if this voxel belongs to the selected material
                                    bool isMaterial = false;

                                    if (labelData != null &&
                                        sliceIndex < labelData.Width &&
                                        y < labelData.Height &&
                                        z < labelData.Depth)
                                    {
                                        if (labelData[sliceIndex, y, z] == selectedMaterial.ID)
                                        {
                                            isMaterial = true;
                                        }
                                    }

                                    // Draw the pixel
                                    if (isMaterial)
                                    {
                                        // Highlight the material with its color
                                        Color materialColor = Color.FromArgb(
                                            180, // Semi-transparent
                                            selectedMaterial.Color);
                                        bitmap.SetPixel(y, z, materialColor);
                                    }
                                    else
                                    {
                                        // Regular grayscale
                                        Color grayColor = Color.FromArgb(grayValue, grayValue, grayValue);
                                        bitmap.SetPixel(y, z, grayColor);
                                    }
                                }
                            }
                            break;
                    }

                    // Add view information
                    using (Font font = new Font("Arial", 9, FontStyle.Bold))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(100, Color.Black)))
                    {
                        string text;
                        switch (viewType)
                        {
                            case ViewType.XY:
                                text = $"Slice: {sliceIndex + 1}/{volumeData?.Depth ?? 0}";
                                break;
                            case ViewType.XZ:
                                text = $"Y Position: {sliceIndex + 1}/{volumeData?.Height ?? 0}";
                                break;
                            case ViewType.YZ:
                                text = $"X Position: {sliceIndex + 1}/{volumeData?.Width ?? 0}";
                                break;
                            default:
                                text = "";
                                break;
                        }

                        // Measure the text to create a background rectangle
                        SizeF textSize = g.MeasureString(text, font);

                        // Draw background rectangle
                        g.FillRectangle(bgBrush, 5, 5, textSize.Width + 10, textSize.Height + 5);

                        // Draw text on top of background
                        g.DrawString(text, font, textBrush, new PointF(10, 7));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[RenderMaterialPreviewBitmap] Error for {viewType} view: {ex.Message}");

                // Draw an error message
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Black);
                    using (Font font = new Font("Arial", 9))
                    {
                        g.DrawString($"Rendering Error: {ex.Message}", font, Brushes.Red, new PointF(10, 10));
                    }
                }
            }

            return bitmap;
        }


        private void RenderXYSlice(byte[] rgbValues, int stride, int width, int height, int z)
        {
            const int bytesPerPixel = 4; // BGRA format
            ChunkedVolume volumeData = (ChunkedVolume)mainForm.volumeData;
            ChunkedLabelVolume volumeLabels = (ChunkedLabelVolume)mainForm.volumeLabels;

            // Process each pixel
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int pixelOffset = y * stride + x * bytesPerPixel;

                    // Default to black
                    byte r = 0, g = 0, b = 0, a = 255;

                    try
                    {
                        // Get grayscale value if volume data is available
                        if (volumeData != null && x < volumeData.Width && y < volumeData.Height && z < volumeData.Depth)
                        {
                            byte grayValue = volumeData[x, y, z];
                            r = g = b = grayValue;
                        }

                        // Get label value
                        if (volumeLabels != null &&
                            x < volumeLabels.Width &&
                            y < volumeLabels.Height &&
                            z < volumeLabels.Depth)
                        {
                            byte label = volumeLabels[x, y, z];

                            // If this pixel belongs to the selected material
                            if (label == selectedMaterial.ID)
                            {
                                // Apply semi-transparent color overlay
                                Color materialColor = selectedMaterial.Color;
                                double alpha = 0.7; // 70% opacity
                                r = (byte)(r * (1 - alpha) + materialColor.R * alpha);
                                g = (byte)(g * (1 - alpha) + materialColor.G * alpha);
                                b = (byte)(b * (1 - alpha) + materialColor.B * alpha);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but continue processing pixels
                        if (x % 100 == 0 && y % 100 == 0) // Limit logging frequency
                            Logger.Log($"[RenderXYSlice] Error at ({x},{y},{z}): {ex.Message}");
                    }

                    // Set pixel values in BGRA format
                    rgbValues[pixelOffset] = b;     // Blue
                    rgbValues[pixelOffset + 1] = g; // Green
                    rgbValues[pixelOffset + 2] = r; // Red
                    rgbValues[pixelOffset + 3] = a; // Alpha
                }
            }
        }

        private void RenderXZSlice(byte[] rgbValues, int stride, int width, int depth, int y)
        {
            const int bytesPerPixel = 4; // BGRA format
            ChunkedVolume volumeData = (ChunkedVolume)mainForm.volumeData;
            ChunkedLabelVolume volumeLabels = (ChunkedLabelVolume)mainForm.volumeLabels;

            // Process each pixel
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int pixelOffset = z * stride + x * bytesPerPixel;

                    // Default to black
                    byte r = 0, g = 0, b = 0, a = 255;

                    try
                    {
                        // Get grayscale value if volume data is available
                        if (volumeData != null && x < volumeData.Width && y < volumeData.Height && z < volumeData.Depth)
                        {
                            byte grayValue = volumeData[x, y, z];
                            r = g = b = grayValue;
                        }

                        // Get label value
                        if (volumeLabels != null &&
                            x < volumeLabels.Width &&
                            y < volumeLabels.Height &&
                            z < volumeLabels.Depth)
                        {
                            byte label = volumeLabels[x, y, z];

                            // If this pixel belongs to the selected material
                            if (label == selectedMaterial.ID)
                            {
                                // Apply semi-transparent color overlay
                                Color materialColor = selectedMaterial.Color;
                                double alpha = 0.7; // 70% opacity
                                r = (byte)(r * (1 - alpha) + materialColor.R * alpha);
                                g = (byte)(g * (1 - alpha) + materialColor.G * alpha);
                                b = (byte)(b * (1 - alpha) + materialColor.B * alpha);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but continue processing pixels
                        if (x % 100 == 0 && z % 100 == 0) // Limit logging frequency
                            Logger.Log($"[RenderXZSlice] Error at ({x},{y},{z}): {ex.Message}");
                    }

                    // Set pixel values in BGRA format
                    rgbValues[pixelOffset] = b;     // Blue
                    rgbValues[pixelOffset + 1] = g; // Green
                    rgbValues[pixelOffset + 2] = r; // Red
                    rgbValues[pixelOffset + 3] = a; // Alpha
                }
            }
        }

        private void RenderYZSlice(byte[] rgbValues, int stride, int height, int depth, int x)
        {
            const int bytesPerPixel = 4; // BGRA format
            ChunkedVolume volumeData = (ChunkedVolume)mainForm.volumeData;
            ChunkedLabelVolume volumeLabels = (ChunkedLabelVolume)mainForm.volumeLabels;

            // Process each pixel
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    int pixelOffset = z * stride + y * bytesPerPixel;

                    // Default to black
                    byte r = 0, g = 0, b = 0, a = 255;

                    try
                    {
                        // Get grayscale value if volume data is available
                        if (volumeData != null && x < volumeData.Width && y < volumeData.Height && z < volumeData.Depth)
                        {
                            byte grayValue = volumeData[x, y, z];
                            r = g = b = grayValue;
                        }

                        // Get label value
                        if (volumeLabels != null &&
                            x < volumeLabels.Width &&
                            y < volumeLabels.Height &&
                            z < volumeLabels.Depth)
                        {
                            byte label = volumeLabels[x, y, z];

                            // If this pixel belongs to the selected material
                            if (label == selectedMaterial.ID)
                            {
                                // Apply semi-transparent color overlay
                                Color materialColor = selectedMaterial.Color;
                                double alpha = 0.7; // 70% opacity
                                r = (byte)(r * (1 - alpha) + materialColor.R * alpha);
                                g = (byte)(g * (1 - alpha) + materialColor.G * alpha);
                                b = (byte)(b * (1 - alpha) + materialColor.B * alpha);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but continue processing pixels
                        if (y % 100 == 0 && z % 100 == 0) // Limit logging frequency
                            Logger.Log($"[RenderYZSlice] Error at ({x},{y},{z}): {ex.Message}");
                    }

                    // Set pixel values in BGRA format
                    rgbValues[pixelOffset] = b;     // Blue
                    rgbValues[pixelOffset + 1] = g; // Green
                    rgbValues[pixelOffset + 2] = r; // Red
                    rgbValues[pixelOffset + 3] = a; // Alpha
                }
            }
        }



        private void InitializeViewPositions()
        {
            // Set initial slice positions
            currentXYSlice = mainForm.CurrentSlice;
            currentXZSlice = mainForm.GetHeight() / 2;
            currentYZSlice = mainForm.GetWidth() / 2;

            // Update slice controls
            xySliceTrackBar.Value = currentXYSlice;
            xySliceNumeric.Value = currentXYSlice;
            xzSliceTrackBar.Value = currentXZSlice;
            xzSliceNumeric.Value = currentXZSlice;
            yzSliceTrackBar.Value = currentYZSlice;
            yzSliceNumeric.Value = currentYZSlice;
        }

        private void InitializeComponent()
        {
            // Set up the form
            this.Text = $"Particle Separator - {selectedMaterial.Name}";
            this.Size = new Size(1200, 800);
            this.MinimumSize = new Size(900, 700);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterScreen;

            // Create tooltips
            toolTip = new ToolTip();

            // Create status strip and progress bar
            statusStrip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("Ready");
            progressBar = new ProgressBar
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                Style = ProgressBarStyle.Continuous
            };

            statusStrip.Items.Add(statusLabel);
            this.Controls.Add(progressBar);
            this.Controls.Add(statusStrip);

            // Create main layout - 2x2 grid
            mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 2,
                Padding = new Padding(5)
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            this.Controls.Add(mainLayout);

            // Create panels for each view
            xyViewPanel = CreateViewPanel("XY View (Axial)");
            xzViewPanel = CreateViewPanel("XZ View (Coronal)");
            yzViewPanel = CreateViewPanel("YZ View (Sagittal)");
            controlPanel = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };

            // Create picture boxes and add them to view panels
            xyPictureBox = CreatePictureBox(ViewType.XY);
            xzPictureBox = CreatePictureBox(ViewType.XZ);
            yzPictureBox = CreatePictureBox(ViewType.YZ);

            // Create slice controls and add them to view panels
            xySliceTrackBar = CreateSliceTrackBar(0, Math.Max(0, mainForm.GetDepth() - 1), ViewType.XY);
            xySliceNumeric = CreateSliceNumeric(0, Math.Max(0, mainForm.GetDepth() - 1), ViewType.XY);
            xyLabel = new Label { Text = "XY Slice:", AutoSize = true };

            // Get control panel from the tuple
            Panel xyControlPanel = ((Tuple<Panel, Panel>)xyViewPanel.Tag).Item1;
            xyControlPanel.Controls.Add(xyLabel);
            xyControlPanel.Controls.Add(xySliceTrackBar);
            xyControlPanel.Controls.Add(xySliceNumeric);

            // Position controls properly
            xyLabel.Location = new Point(5, 10);
            xySliceTrackBar.Location = new Point(80, 5);
            xySliceNumeric.Location = new Point(290, 7);

            // XZ controls
            xzSliceTrackBar = CreateSliceTrackBar(0, Math.Max(0, mainForm.GetHeight() - 1), ViewType.XZ);
            xzSliceNumeric = CreateSliceNumeric(0, Math.Max(0, mainForm.GetHeight() - 1), ViewType.XZ);
            xzLabel = new Label { Text = "XZ Position (Y):", AutoSize = true };

            Panel xzControlPanel = ((Tuple<Panel, Panel>)xzViewPanel.Tag).Item1;
            xzControlPanel.Controls.Add(xzLabel);
            xzControlPanel.Controls.Add(xzSliceTrackBar);
            xzControlPanel.Controls.Add(xzSliceNumeric);

            xzLabel.Location = new Point(5, 10);
            xzSliceTrackBar.Location = new Point(100, 5);
            xzSliceNumeric.Location = new Point(310, 7);

            // YZ controls
            yzSliceTrackBar = CreateSliceTrackBar(0, Math.Max(0, mainForm.GetWidth() - 1), ViewType.YZ);
            yzSliceNumeric = CreateSliceNumeric(0, Math.Max(0, mainForm.GetWidth() - 1), ViewType.YZ);
            yzLabel = new Label { Text = "YZ Position (X):", AutoSize = true };

            Panel yzControlPanel = ((Tuple<Panel, Panel>)yzViewPanel.Tag).Item1;
            yzControlPanel.Controls.Add(yzLabel);
            yzControlPanel.Controls.Add(yzSliceTrackBar);
            yzControlPanel.Controls.Add(yzSliceNumeric);

            yzLabel.Location = new Point(5, 10);
            yzSliceTrackBar.Location = new Point(100, 5);
            yzSliceNumeric.Location = new Point(310, 7);

            // Add all panels to main layout
            mainLayout.Controls.Add(xyViewPanel, 0, 0);
            mainLayout.Controls.Add(xzViewPanel, 1, 0);
            mainLayout.Controls.Add(yzViewPanel, 0, 1);
            mainLayout.Controls.Add(controlPanel, 1, 1);

            // Create controls for the control panel
            InitializeControlPanel();
        }

        private Panel CreateViewPanel(string title)
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(5)
            };

            // Add a table layout for better organization
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1
            };

            // Configure rows - title, controls, image
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));  // Title row
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));  // Controls row
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Image row

            // Add title
            Label titleLabel = new Label
            {
                Text = title,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 10, FontStyle.Bold),
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(titleLabel, 0, 0);

            // Add an inner panel for controls
            Panel controlsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 5, 0, 5)
            };
            layout.Controls.Add(controlsPanel, 0, 1);

            // We'll add the slice controls to this panel from outside

            // Add a panel for the picture box
            Panel imagePanel = new Panel
            {
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(imagePanel, 0, 2);

            // Add the layout to the panel
            panel.Controls.Add(layout);

            // Store references to inner panels for later use
            panel.Tag = new Tuple<Panel, Panel>(controlsPanel, imagePanel);

            return panel;
        }


        private PictureBox CreatePictureBox(ViewType viewType)
        {
            PictureBox pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black,
                Tag = viewType
            };
            pictureBox.MouseClick += PictureBox_MouseClick;

            // Find the appropriate panel to add the picture box to
            Panel targetPanel = null;

            switch (viewType)
            {
                case ViewType.XY:
                    targetPanel = ((Tuple<Panel, Panel>)xyViewPanel.Tag).Item2;
                    break;
                case ViewType.XZ:
                    targetPanel = ((Tuple<Panel, Panel>)xzViewPanel.Tag).Item2;
                    break;
                case ViewType.YZ:
                    targetPanel = ((Tuple<Panel, Panel>)yzViewPanel.Tag).Item2;
                    break;
            }

            if (targetPanel != null)
            {
                targetPanel.Controls.Add(pictureBox);
            }

            return pictureBox;
        }

        private TrackBar CreateSliceTrackBar(int min, int max, ViewType viewType)
        {
            TrackBar trackBar = new TrackBar
            {
                Location = new Point(100, 30), // Adjusted position
                Width = 200,
                Minimum = min,
                Maximum = max,
                Value = min,
                TickFrequency = Math.Max(1, (max - min) / 20),
                TickStyle = TickStyle.BottomRight,
                Tag = viewType
            };
            trackBar.ValueChanged += SliceTrackBar_ValueChanged;
            return trackBar;
        }

        private NumericUpDown CreateSliceNumeric(int min, int max, ViewType viewType)
        {
            NumericUpDown numericUpDown = new NumericUpDown
            {
                Location = new Point(310, 25),
                Width = 70,
                Minimum = min,
                Maximum = max,
                Value = min,
                Tag = viewType
            };
            numericUpDown.ValueChanged += SliceNumeric_ValueChanged;
            return numericUpDown;
        }

        private void InitializeControlPanel()
        {
            // Clear any existing controls
            controlPanel.Controls.Clear();

            // Create a new scrollable panel
            Panel scrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(0)
            };
            controlPanel.Controls.Add(scrollPanel);

            // Instead of a nested panel, use a TableLayoutPanel for better layout management
            TableLayoutPanel mainTable = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 12, // One row for each major control section
                Dock = DockStyle.Top,
                AutoSize = true,
                Width = controlPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 5,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };

            // Add appropriate sizing for each row
            mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Title
            mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Material info
            mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Processing options
            mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Dimension options
            mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Particle management
            mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Particles label
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 180)); // Particle list
            mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Particle info
            mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Extract button
            mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Separate/Save buttons
            mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Export/Close buttons
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); // Bottom padding

            scrollPanel.Controls.Add(mainTable);

            // ROW 0: Title
            Label titleLabel = new Label
            {
                Text = "Particle Controls",
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 12, FontStyle.Bold),
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 10, 10, 5)
            };
            mainTable.Controls.Add(titleLabel, 0, 0);

            // ROW 1: Material Info
            Label materialLabel = new Label
            {
                Text = $"Selected Material: {selectedMaterial.Name}",
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 5, 10, 10)
            };
            mainTable.Controls.Add(materialLabel, 0, 1);

            // ROW 2: Processing Options
            GroupBox processingOptions = new GroupBox
            {
                Text = "Processing Options",
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 5, 10, 5),
                Height = 110
            };
            mainTable.Controls.Add(processingOptions, 0, 2);

            useGpuCheckBox = new CheckBox
            {
                Text = "Process on GPU (if available)",
                Checked = true,
                AutoSize = true,
                Location = new Point(20, 25)
            };
            processingOptions.Controls.Add(useGpuCheckBox);

            generateCsvCheckBox = new CheckBox
            {
                Text = "Generate CSV file with results",
                Checked = true,
                AutoSize = true,
                Location = new Point(20, 50)
            };
            processingOptions.Controls.Add(generateCsvCheckBox);

            Label extentLabel = new Label
            {
                Text = "Particle Detection:",
                AutoSize = true,
                Location = new Point(20, 75)
            };
            processingOptions.Controls.Add(extentLabel);

            conservativeRadio = new RadioButton
            {
                Text = "Conservative",
                Checked = true,
                AutoSize = true,
                Location = new Point(120, 75)
            };
            toolTip.SetToolTip(conservativeRadio, "Filters out very small particles to reduce noise");
            processingOptions.Controls.Add(conservativeRadio);

            aggressiveRadio = new RadioButton
            {
                Text = "Aggressive",
                AutoSize = true,
                Location = new Point(210, 75)
            };
            toolTip.SetToolTip(aggressiveRadio, "Detects all particles including small ones");
            processingOptions.Controls.Add(aggressiveRadio);

            // ROW 3: Dimension Options
            GroupBox dimensionOptions = new GroupBox
            {
                Text = "Dimension",
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 5, 10, 5),
                Height = 80
            };
            mainTable.Controls.Add(dimensionOptions, 0, 3);

            currentSliceRadio = new RadioButton
            {
                Text = "Current Slice Only (2D)",
                Checked = true,
                AutoSize = true,
                Location = new Point(20, 25)
            };
            toolTip.SetToolTip(currentSliceRadio, "Process only the current slice");
            dimensionOptions.Controls.Add(currentSliceRadio);

            wholeVolumeRadio = new RadioButton
            {
                Text = "Whole Volume (3D Object Separation)",
                AutoSize = true,
                Location = new Point(20, 50)
            };
            toolTip.SetToolTip(wholeVolumeRadio, "Process the entire 3D volume");
            dimensionOptions.Controls.Add(wholeVolumeRadio);

            // ROW 4: Particle Management
            GroupBox particleOptions = new GroupBox
            {
                Text = "Particle Management",
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 5, 10, 5),
                Height = 120
            };
            mainTable.Controls.Add(particleOptions, 0, 4);

            Label sortLabel = new Label
            {
                Text = "Sort By:",
                AutoSize = true,
                Location = new Point(20, 25)
            };
            particleOptions.Controls.Add(sortLabel);

            sortOrderComboBox = new ComboBox
            {
                Location = new Point(80, 22),
                Width = 180,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            sortOrderComboBox.Items.AddRange(new object[] {
        "Largest to Smallest",
        "Smallest to Largest",
        "ID (Ascending)",
        "ID (Descending)"
    });
            sortOrderComboBox.SelectedIndex = 0;
            sortOrderComboBox.SelectedIndexChanged += SortOrderComboBox_SelectedIndexChanged;
            particleOptions.Controls.Add(sortOrderComboBox);

            minSizeLabel = new Label
            {
                Text = "Min Size (voxels):",
                AutoSize = true,
                Location = new Point(20, 55)
            };
            particleOptions.Controls.Add(minSizeLabel);

            minSizeInput = new NumericUpDown
            {
                Location = new Point(130, 53),
                Width = 130,
                Minimum = 1,
                Maximum = 100000,
                Value = 10
            };
            particleOptions.Controls.Add(minSizeInput);

            removeIslandsButton = new Button
            {
                Text = "Remove Small Particles",
                Location = new Point(105, 85),
                Size = new Size(155, 25),
                Enabled = false
            };
            removeIslandsButton.Click += RemoveIslandsButton_Click;
            particleOptions.Controls.Add(removeIslandsButton);

            // ROW 5: Particles Label
            Label particlesLabel = new Label
            {
                Text = "Detected Particles:",
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 5, 10, 0)
            };
            mainTable.Controls.Add(particlesLabel, 0, 5);

            // ROW 6: Particle List View
            particleListView = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,  // Enable multi-selection
                HideSelection = false,
                VirtualMode = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 0, 10, 5)
            };
            particleListView.Columns.Add("ID", 40);
            particleListView.Columns.Add("Volume", 60);
            particleListView.Columns.Add("μm³", 80);
            particleListView.Columns.Add("Center", 80);
            particleListView.RetrieveVirtualItem += ParticleListView_RetrieveVirtualItem;
            particleListView.SelectedIndexChanged += ParticleListView_SelectedIndexChanged;
            particleListView.CacheVirtualItems += ParticleListView_CacheVirtualItems;
            mainTable.Controls.Add(particleListView, 0, 6);

            // ROW 7: Particle Info - Adjust the panel to properly show all text
            Panel infoPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 5, 10, 5),
                Height = 90,  // Increase height to give more room for text
                AutoSize = true,  // Allow panel to grow based on content
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            mainTable.Controls.Add(infoPanel, 0, 7);

            particleInfoLabel = new Label
            {
                Text = "Select a particle in the preview or list to see details",
                AutoSize = true,
                Dock = DockStyle.Top,  // Dock to top instead of Fill so it can expand
                Padding = new Padding(3),  // Add some padding
                MaximumSize = new Size(infoPanel.Width - 20, 0),  // Allow wrapping with max width
                MinimumSize = new Size(infoPanel.Width - 20, 70)  // Set minimum height
            };
            infoPanel.Controls.Add(particleInfoLabel);

            // ROW 8: Extract Button - Make sure button text is fully visible
            extractParticleButton = new Button
            {
                Text = "Extract Selected Particle",
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 5, 10, 10),
                Height = 35,  // Increase button height
                AutoEllipsis = true,  // Show "..." if text doesn't fit
                Enabled = false
            };
            extractParticleButton.Click += ExtractParticleButton_Click;
            mainTable.Controls.Add(extractParticleButton, 0, 8);

            // ROW 9: Separate/Save Buttons
            TableLayoutPanel buttonsRow1 = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 0, 10, 5)
            };
            buttonsRow1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttonsRow1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            mainTable.Controls.Add(buttonsRow1, 0, 9);

            separateButton = new Button
            {
                Text = "Separate Particles",
                Dock = DockStyle.Fill,
                Height = 30
            };
            separateButton.Click += SeparateButton_Click;
            buttonsRow1.Controls.Add(separateButton, 0, 0);

            saveButton = new Button
            {
                Text = "Save Results",
                Dock = DockStyle.Fill,
                Height = 30,
                Enabled = false
            };
            saveButton.Click += SaveButton_Click;
            buttonsRow1.Controls.Add(saveButton, 1, 0);

            // ROW 10: Export/Close Buttons
            TableLayoutPanel buttonsRow2 = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 0, 10, 5)
            };
            buttonsRow2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttonsRow2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            mainTable.Controls.Add(buttonsRow2, 0, 10);

            exportToMainButton = new Button
            {
                Text = "Export to Main View",
                Dock = DockStyle.Fill,
                Height = 30,
                Enabled = false
            };
            exportToMainButton.Click += ExportToMainButton_Click;
            buttonsRow2.Controls.Add(exportToMainButton, 0, 0);

            closeButton = new Button
            {
                Text = "Close",
                Dock = DockStyle.Fill,
                Height = 30
            };
            closeButton.Click += (s, e) => this.Close();
            buttonsRow2.Controls.Add(closeButton, 1, 0);

            // Force the initial size calculation of the table
            mainTable.PerformLayout();
            AdjustListViewColumns();
        }



        private async void SortOrderComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (result == null || result.Particles == null) return;

            // Show "Sorting..." in the status
            statusLabel.Text = "Sorting particles...";
            Logger.Log($"[ParticleSeparatorForm] Sorting particles using order: {sortOrderComboBox.SelectedItem}");

            await Task.Run(() => {
                // Sort the particles based on the selected criterion
                switch (sortOrderComboBox.SelectedIndex)
                {
                    case 0: // Largest to Smallest
                        result.Particles = result.Particles.OrderByDescending(p => p.VoxelCount).ToList();
                        break;
                    case 1: // Smallest to Largest
                        result.Particles = result.Particles.OrderBy(p => p.VoxelCount).ToList();
                        break;
                    case 2: // ID (Ascending)
                        result.Particles = result.Particles.OrderBy(p => p.Id).ToList();
                        break;
                    case 3: // ID (Descending)
                        result.Particles = result.Particles.OrderByDescending(p => p.Id).ToList();
                        break;
                }
            });

            // Update the UI
            UpdateParticleList();
            statusLabel.Text = "Particles sorted";
        }

        private async void RemoveIslandsButton_Click(object sender, EventArgs e)
        {
            if (result == null || result.Particles == null) return;

            int minSize = (int)minSizeInput.Value;
            int originalCount = result.Particles.Count;

            // Confirm the operation
            DialogResult dialogResult = MessageBox.Show(
                $"This will remove all particles smaller than {minSize} voxels.\n\nContinue?",
                "Remove Small Particles",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (dialogResult != DialogResult.Yes) return;

            // Disable UI during operation
            removeIslandsButton.Enabled = false;
            statusLabel.Text = $"Removing particles smaller than {minSize} voxels...";
            Logger.Log($"[ParticleSeparatorForm] Removing particles smaller than {minSize} voxels");
            progressBar.Style = ProgressBarStyle.Marquee;

            try
            {
                await Task.Run(() => {
                    // Filter the particles
                    List<ParticleSeparator.Particle> filteredParticles =
                        result.Particles.Where(p => p.VoxelCount >= minSize).ToList();

                    // Set of valid particle IDs after filtering
                    HashSet<int> validParticleIds = new HashSet<int>(
                        filteredParticles.Select(p => p.Id));

                    // Update the label volume to remove small particles
                    int width = result.LabelVolume.GetLength(0);
                    int height = result.LabelVolume.GetLength(1);
                    int depth = result.LabelVolume.GetLength(2);

                    for (int z = 0; z < depth; z++)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                int label = result.LabelVolume[x, y, z];
                                if (label > 0 && !validParticleIds.Contains(label))
                                {
                                    result.LabelVolume[x, y, z] = 0;
                                }
                            }
                        }
                    }

                    // Update the particles collection
                    result.Particles = filteredParticles;
                });

                // Update UI
                int removedCount = originalCount - result.Particles.Count;
                UpdateParticleList();
                UpdateAllViews();

                statusLabel.Text = $"Removed {removedCount} particles smaller than {minSize} voxels";
                MessageBox.Show(
                    $"Removed {removedCount} particles smaller than {minSize} voxels.\n\n" +
                    $"{result.Particles.Count} particles remain.",
                    "Operation Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                Logger.Log($"[ParticleSeparatorForm] Removed {removedCount} particles; {result.Particles.Count} remain");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[ParticleSeparatorForm] Error removing small particles: {ex.Message}");
                statusLabel.Text = "Error removing particles";
            }
            finally
            {
                removeIslandsButton.Enabled = true;
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Value = 0;
            }
        }

        private async void ExtractParticleButton_Click(object sender, EventArgs e)
        {
            // Get all selected particle IDs
            List<int> selectedParticleIds = particleListView.Tag as List<int> ?? new List<int>();
            if (selectedParticleIds.Count == 0 && highlightedParticleId != -1)
            {
                selectedParticleIds.Add(highlightedParticleId);
            }

            if (result == null || selectedParticleIds.Count == 0) return;

            // Find the particles by IDs
            List<ParticleSeparator.Particle> selectedParticles = new List<ParticleSeparator.Particle>();
            foreach (int id in selectedParticleIds)
            {
                var particle = result.Particles.FirstOrDefault(p => p.Id == id);
                if (particle != null)
                {
                    selectedParticles.Add(particle);
                }
            }

            if (selectedParticles.Count == 0) return;

            // Disable the button during processing
            extractParticleButton.Enabled = false;
            statusLabel.Text = $"Extracting {selectedParticles.Count} particle(s)...";
            Logger.Log($"[ParticleSeparatorForm] Extracting {selectedParticles.Count} particles");
            progressBar.Style = ProgressBarStyle.Marquee;

            try
            {
                // Create a new material for the extracted particles
                string matName = $"{selectedMaterial.Name}_Extracted";
                byte matId = 0;

                // Create the material on the UI thread
                await Task.Run(() => {
                    this.Invoke((Action)(() => {
                        matId = mainForm.GetNextMaterialID();
                        Material newMat = new Material(matName, Color.Yellow, 0, 0, matId);
                        mainForm.Materials.Add(newMat);
                    }));

                    // Extract the particles to the new material
                    int width = mainForm.GetWidth();
                    int height = mainForm.GetHeight();
                    int depth = mainForm.GetDepth();

                    // Create a hashset of selected particle IDs for faster lookup
                    HashSet<int> particleIdSet = new HashSet<int>(selectedParticleIds);

                    for (int z = 0; z < depth; z++)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                // Check if this voxel is within the bounds of the label volume
                                if (x < result.LabelVolume.GetLength(0) &&
                                    y < result.LabelVolume.GetLength(1) &&
                                    z < result.LabelVolume.GetLength(2))
                                {
                                    int label = result.LabelVolume[x, y, z];

                                    // If this voxel is part of any selected particle
                                    if (particleIdSet.Contains(label))
                                    {
                                        // Set it to the new material
                                        mainForm.volumeLabels[x, y, z] = matId;
                                    }
                                }
                            }
                        }
                    }
                });

                // Update main view
                mainForm.RenderViews();

                statusLabel.Text = $"{selectedParticles.Count} particle(s) extracted to material '{matName}'";
                MessageBox.Show(
                    $"{selectedParticles.Count} particle(s) have been extracted to a new material named '{matName}'.",
                    "Extraction Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                Logger.Log($"[ParticleSeparatorForm] {selectedParticles.Count} particles extracted to material '{matName}'");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[ParticleSeparatorForm] Error extracting particles: {ex.Message}");
                statusLabel.Text = "Error extracting particles";
            }
            finally
            {
                extractParticleButton.Enabled = true;
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Value = 0;
            }
        }

        private void ParticleListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (result == null || result.Particles == null || e.ItemIndex < 0 || e.ItemIndex >= result.Particles.Count)
            {
                e.Item = new ListViewItem();
                return;
            }

            var particle = result.Particles[e.ItemIndex];
            ListViewItem item = new ListViewItem(particle.Id.ToString());

            // Format numbers with appropriate spacing
            item.SubItems.Add(particle.VoxelCount.ToString("N0"));
            item.SubItems.Add(particle.VolumeMicrometers.ToString("N0"));

            // Format center coordinates to fit in the column
            string centerText = $"({particle.Center.X}, {particle.Center.Y}, {particle.Center.Z})";
            item.SubItems.Add(centerText);

            // Set the item's background color to match the particle color
            if (particleColors.TryGetValue(particle.Id, out Color color))
            {
                item.BackColor = Color.FromArgb(50, color);
                item.ForeColor = Color.Black;
            }

            e.Item = item;
        }

        private void ParticleListView_CacheVirtualItems(object sender, CacheVirtualItemsEventArgs e)
        {
            // This is where you would implement caching for virtual items
            // For our purposes, we're already caching everything in memory
        }

        private void SliceTrackBar_ValueChanged(object sender, EventArgs e)
        {
            // Skip if form isn't fully initialized
            if (!_initialized || !IsHandleCreated) return;

            TrackBar trackBar = sender as TrackBar;
            if (trackBar == null) return;

            ViewType viewType = (ViewType)trackBar.Tag;
            NumericUpDown numeric = null;

            switch (viewType)
            {
                case ViewType.XY:
                    numeric = xySliceNumeric;
                    if (numeric.Value != trackBar.Value)
                    {
                        numeric.Value = trackBar.Value;
                        currentXYSlice = trackBar.Value;
                    }
                    break;
                case ViewType.XZ:
                    numeric = xzSliceNumeric;
                    if (numeric.Value != trackBar.Value)
                    {
                        numeric.Value = trackBar.Value;
                        currentXZSlice = trackBar.Value;
                    }
                    break;
                case ViewType.YZ:
                    numeric = yzSliceNumeric;
                    if (numeric.Value != trackBar.Value)
                    {
                        numeric.Value = trackBar.Value;
                        currentYZSlice = trackBar.Value;
                    }
                    break;
            }

            // Update the specific view that changed
            UpdateView(viewType);
        }

        private void SliceNumeric_ValueChanged(object sender, EventArgs e)
        {
            // Skip if form isn't fully initialized
            if (!_initialized || !IsHandleCreated) return;

            NumericUpDown numeric = sender as NumericUpDown;
            if (numeric == null) return;

            ViewType viewType = (ViewType)numeric.Tag;
            TrackBar trackBar = null;

            switch (viewType)
            {
                case ViewType.XY:
                    trackBar = xySliceTrackBar;
                    if (trackBar.Value != (int)numeric.Value)
                    {
                        trackBar.Value = (int)numeric.Value;
                        currentXYSlice = (int)numeric.Value;
                    }
                    break;
                case ViewType.XZ:
                    trackBar = xzSliceTrackBar;
                    if (trackBar.Value != (int)numeric.Value)
                    {
                        trackBar.Value = (int)numeric.Value;
                        currentXZSlice = (int)numeric.Value;
                    }
                    break;
                case ViewType.YZ:
                    trackBar = yzSliceTrackBar;
                    if (trackBar.Value != (int)numeric.Value)
                    {
                        trackBar.Value = (int)numeric.Value;
                        currentYZSlice = (int)numeric.Value;
                    }
                    break;
            }

            // Update the specific view that changed
            UpdateView(viewType);
        }

        private void ParticleListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (particleListView.SelectedIndices.Count == 0 ||
                result == null)
            {
                highlightedParticleId = -1;
                particleInfoLabel.Text = "Select a particle in the preview or list to see details";
                extractParticleButton.Enabled = false;
                UpdateAllViews();
                return;
            }

            // Store all selected particle IDs
            List<int> selectedParticleIds = new List<int>();

            foreach (int selectedIndex in particleListView.SelectedIndices)
            {
                if (selectedIndex < result.Particles.Count)
                {
                    ParticleSeparator.Particle particle = result.Particles[selectedIndex];
                    selectedParticleIds.Add(particle.Id);

                    // If this is the first/primary selection, update info and center view
                    if (particleListView.SelectedIndices[0] == selectedIndex)
                    {
                        // Update primary selection for highlighting
                        highlightedParticleId = particle.Id;

                        // Update particle info
                        UpdateParticleInfo(particle);

                        // Set the current slices to the particle's center
                        if (result.Is3D)
                        {
                            currentXYSlice = particle.Center.Z;
                            currentXZSlice = particle.Center.Y;
                            currentYZSlice = particle.Center.X;

                            // Safely update track bars
                            SafeSetTrackBarValue(xySliceTrackBar, currentXYSlice);
                            SafeSetNumericValue(xySliceNumeric, currentXYSlice);

                            SafeSetTrackBarValue(xzSliceTrackBar, currentXZSlice);
                            SafeSetNumericValue(xzSliceNumeric, currentXZSlice);

                            SafeSetTrackBarValue(yzSliceTrackBar, currentYZSlice);
                            SafeSetNumericValue(yzSliceNumeric, currentYZSlice);
                        }
                    }
                }
            }

            // Store all selected IDs in a tag
            particleListView.Tag = selectedParticleIds;

            // Enable the extract button
            extractParticleButton.Text = selectedParticleIds.Count > 1 ?
                $"Extract {selectedParticleIds.Count} Selected Particles" :
                "Extract Selected Particle";
            extractParticleButton.Enabled = true;

            // Update all views to highlight the selected particles
            UpdateAllViews();
        }

        private void PictureBox_MouseClick(object sender, MouseEventArgs e)
        {
            PictureBox pictureBox = sender as PictureBox;
            if (pictureBox == null || result == null || result.LabelVolume == null) return;

            ViewType viewType = (ViewType)pictureBox.Tag;

            // Convert mouse coordinates to image coordinates
            Point imgCoord = GetImageCoordinates(pictureBox, e.Location);

            // Get the label at this position based on the view type
            int label = GetLabelAtPosition(imgCoord, viewType);

            if (label > 0)
            {
                // Find the corresponding particle
                var selectedParticle = result.Particles.FirstOrDefault(p => p.Id == label);
                if (selectedParticle != null)
                {
                    // Get current selections
                    List<int> selectedParticleIds = particleListView.Tag as List<int> ?? new List<int>();

                    // Determine if we're adding to selection or starting new selection
                    bool isShiftPressed = (ModifierKeys & Keys.Shift) == Keys.Shift;
                    bool isCtrlPressed = (ModifierKeys & Keys.Control) == Keys.Control;

                    if (!isShiftPressed && !isCtrlPressed)
                    {
                        // Clear current selection if not extending with modifiers
                        selectedParticleIds.Clear();
                        particleListView.SelectedIndices.Clear();
                    }

                    // Set the highlighted particle
                    highlightedParticleId = label;

                    // Add to selected IDs if not already selected
                    if (!selectedParticleIds.Contains(label))
                    {
                        selectedParticleIds.Add(label);
                    }
                    else if (isCtrlPressed)
                    {
                        // Toggle selection if Ctrl is pressed
                        selectedParticleIds.Remove(label);
                        if (highlightedParticleId == label && selectedParticleIds.Count > 0)
                        {
                            highlightedParticleId = selectedParticleIds[0];
                        }
                    }

                    // Store updated selection
                    particleListView.Tag = selectedParticleIds;

                    // Update particle info for the primary selection
                    UpdateParticleInfo(selectedParticle);

                    // Find and select the particle in the list view
                    int index = result.Particles.IndexOf(selectedParticle);
                    if (index >= 0)
                    {
                        // Update list view selection without clearing existing selections when using modifiers
                        if (!isShiftPressed && !isCtrlPressed)
                        {
                            particleListView.SelectedIndices.Clear();
                        }

                        if (!particleListView.SelectedIndices.Contains(index))
                        {
                            particleListView.SelectedIndices.Add(index);
                        }
                        else if (isCtrlPressed)
                        {
                            particleListView.SelectedIndices.Remove(index);
                        }

                        particleListView.EnsureVisible(index);
                    }

                    // Update extract button text
                    extractParticleButton.Text = selectedParticleIds.Count > 1 ?
                        $"Extract {selectedParticleIds.Count} Selected Particles" :
                        "Extract Selected Particle";

                    // Enable extract button
                    extractParticleButton.Enabled = true;

                    // Update all views to highlight the selection
                    UpdateAllViews();
                }
            }
            else
            {
                // Clicked on background, only clear selection if not using modifiers
                if ((ModifierKeys & (Keys.Shift | Keys.Control)) == Keys.None)
                {
                    highlightedParticleId = -1;
                    particleInfoLabel.Text = "Select a particle in the preview or list to see details";
                    particleListView.SelectedIndices.Clear();
                    particleListView.Tag = null;
                    extractParticleButton.Enabled = false;
                    extractParticleButton.Text = "Extract Selected Particle";
                    UpdateAllViews();
                }
            }
        }


        private Point GetImageCoordinates(PictureBox pictureBox, Point mouseLocation)
        {
            if (pictureBox.Image == null) return new Point(0, 0);

            // Calculate image scaling
            float imageRatio = (float)pictureBox.Image.Width / pictureBox.Image.Height;
            float boxRatio = (float)pictureBox.Width / pictureBox.Height;

            float scaleFactor;
            float offsetX = 0, offsetY = 0;

            if (imageRatio > boxRatio)
            {
                // Image is wider than PictureBox
                scaleFactor = (float)pictureBox.Width / pictureBox.Image.Width;
                offsetY = (pictureBox.Height - (pictureBox.Image.Height * scaleFactor)) / 2;
            }
            else
            {
                // Image is taller than PictureBox
                scaleFactor = (float)pictureBox.Height / pictureBox.Image.Height;
                offsetX = (pictureBox.Width - (pictureBox.Image.Width * scaleFactor)) / 2;
            }

            // Convert to image coordinates
            int imgX = (int)((mouseLocation.X - offsetX) / scaleFactor);
            int imgY = (int)((mouseLocation.Y - offsetY) / scaleFactor);

            // Clamp to image bounds
            imgX = Math.Max(0, Math.Min(pictureBox.Image.Width - 1, imgX));
            imgY = Math.Max(0, Math.Min(pictureBox.Image.Height - 1, imgY));

            return new Point(imgX, imgY);
        }

        private int GetLabelAtPosition(Point position, ViewType viewType)
        {
            if (result == null || result.LabelVolume == null) return 0;

            int x = position.X;
            int y = position.Y;

            // Transform coordinates based on view type
            switch (viewType)
            {
                case ViewType.XY:
                    // Scale coordinates to match the original volume dimensions
                    int volWidth = result.LabelVolume.GetLength(0);
                    int volHeight = result.LabelVolume.GetLength(1);
                    int imgWidth = xyPictureBox.Image.Width;
                    int imgHeight = xyPictureBox.Image.Height;

                    int scaledX = (int)((float)x * volWidth / imgWidth);
                    int scaledY = (int)((float)y * volHeight / imgHeight);

                    // Ensure within bounds
                    if (scaledX < 0 || scaledX >= volWidth || scaledY < 0 || scaledY >= volHeight ||
                        currentXYSlice < 0 || currentXYSlice >= result.LabelVolume.GetLength(2))
                        return 0;

                    return result.LabelVolume[scaledX, scaledY, currentXYSlice];

                case ViewType.XZ:
                    // X remains X, Y becomes Z
                    volWidth = result.LabelVolume.GetLength(0);
                    int volDepth = result.LabelVolume.GetLength(2);
                    imgWidth = xzPictureBox.Image.Width;
                    imgHeight = xzPictureBox.Image.Height;

                    scaledX = (int)((float)x * volWidth / imgWidth);
                    int scaledZ = (int)((float)y * volDepth / imgHeight);

                    // Ensure within bounds
                    if (scaledX < 0 || scaledX >= volWidth || scaledZ < 0 || scaledZ >= volDepth ||
                        currentXZSlice < 0 || currentXZSlice >= result.LabelVolume.GetLength(1))
                        return 0;

                    return result.LabelVolume[scaledX, currentXZSlice, scaledZ];

                case ViewType.YZ:
                    // X becomes Y, Y becomes Z
                    volHeight = result.LabelVolume.GetLength(1);
                    volDepth = result.LabelVolume.GetLength(2);
                    imgWidth = yzPictureBox.Image.Width;
                    imgHeight = yzPictureBox.Image.Height;

                    int ScaledY = (int)((float)x * volHeight / imgWidth);
                    scaledZ = (int)((float)y * volDepth / imgHeight);

                    // Ensure within bounds
                    if (ScaledY < 0 || ScaledY >= volHeight || scaledZ < 0 || scaledZ >= volDepth ||
                        currentYZSlice < 0 || currentYZSlice >= result.LabelVolume.GetLength(0))
                        return 0;

                    return result.LabelVolume[currentYZSlice, ScaledY, scaledZ];
            }

            return 0;
        }

        private void UpdateParticleInfo(ParticleSeparator.Particle particle)
        {
            string info = $"Particle ID: {particle.Id}\r\n" +
                          $"Volume: {particle.VoxelCount:N0} voxels\r\n" +
                          $"        {particle.VolumeMicrometers:N0} µm³\r\n" +
                          $"        {particle.VolumeMillimeters:N6} mm³\r\n" +
                          $"Center: ({particle.Center.X}, {particle.Center.Y}, {particle.Center.Z})\r\n" +
                          $"Size: {particle.Bounds.Width}×{particle.Bounds.Height}×{particle.Bounds.Depth} voxels";

            particleInfoLabel.Text = info;

            // Make sure the particle info label has enough height
            // This will dynamically resize the label based on content
            using (Graphics g = particleInfoLabel.CreateGraphics())
            {
                SizeF textSize = g.MeasureString(info, particleInfoLabel.Font, particleInfoLabel.Width);
                particleInfoLabel.Height = (int)textSize.Height + 10; // Add padding
            }
        }
        private void AdjustListViewColumns()
        {
            // Set wider column widths for the ListView
            particleListView.Columns[0].Width = 50;  // ID
            particleListView.Columns[1].Width = 80;  // Volume
            particleListView.Columns[2].Width = 80;  // μm³
            particleListView.Columns[3].Width = 120; // Center
        }

        private async void UpdateAllViews()
        {
            // Don't update views during initialization or if handle not created
            if (!_initialized || !IsHandleCreated) return;

            try
            {
                // Cancel any running render tasks
                lock (renderLock)
                {
                    if (currentRenderTask != null && !currentRenderTask.Task.IsCompleted)
                    {
                        currentRenderTask.TrySetCanceled();
                    }
                    currentRenderTask = new TaskCompletionSource<bool>();
                }

                // Create a cancellation token source for this render
                using (var cts = new CancellationTokenSource())
                {
                    var token = cts.Token;

                    Logger.Log("[UpdateAllViews] Starting view rendering");

                    // Create tasks for each view
                    var xyTask = Task.Run(() => RenderView(ViewType.XY, token), token);
                    var xzTask = Task.Run(() => RenderView(ViewType.XZ, token), token);
                    var yzTask = Task.Run(() => RenderView(ViewType.YZ, token), token);

                    try
                    {
                        // Wait for all tasks to complete
                        await Task.WhenAll(xyTask, xzTask, yzTask);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[UpdateAllViews] Error during view rendering: {ex.Message}");
                    }

                    // Check again if the form is valid before invoking
                    if (IsDisposed || !IsHandleCreated) return;

                    // Update the UI with the results
                    try
                    {
                        this.Invoke((Action)(() =>
                        {
                            try
                            {
                                if (IsDisposed) return;

                                if (xyPictureBox.Image != null && xyTask.IsCompleted && !xyTask.IsFaulted)
                                {
                                    var oldImage = xyPictureBox.Image;
                                    xyPictureBox.Image = xyTask.Result;
                                    oldImage.Dispose();
                                }
                                else if (xyTask.IsFaulted)
                                {
                                    Logger.Log($"[UpdateAllViews] XY task faulted: {xyTask.Exception.InnerException?.Message}");
                                }

                                if (xzPictureBox.Image != null && xzTask.IsCompleted && !xzTask.IsFaulted)
                                {
                                    var oldImage = xzPictureBox.Image;
                                    xzPictureBox.Image = xzTask.Result;
                                    oldImage.Dispose();
                                }
                                else if (xzTask.IsFaulted)
                                {
                                    Logger.Log($"[UpdateAllViews] XZ task faulted: {xzTask.Exception.InnerException?.Message}");
                                }

                                if (yzPictureBox.Image != null && yzTask.IsCompleted && !yzTask.IsFaulted)
                                {
                                    var oldImage = yzPictureBox.Image;
                                    yzPictureBox.Image = yzTask.Result;
                                    oldImage.Dispose();
                                }
                                else if (yzTask.IsFaulted)
                                {
                                    Logger.Log($"[UpdateAllViews] YZ task faulted: {yzTask.Exception.InnerException?.Message}");
                                }

                                // Mark the task as complete
                                lock (renderLock)
                                {
                                    currentRenderTask.TrySetResult(true);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[ParticleSeparatorForm] Error updating views: {ex.Message}");
                            }
                        }));
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Form was closed or handle destroyed while we were working
                        Logger.Log($"[ParticleSeparatorForm] Cannot update views - form no longer valid: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParticleSeparatorForm] UpdateAllViews error: {ex.Message}");
            }
        }

        private void UpdateView(ViewType viewType)
        {
            // Don't update views during initialization or if handle not created
            if (!_initialized || !IsHandleCreated) return;

            Task.Run(() =>
            {
                try
                {
                    // Render the view
                    var bitmap = RenderView(viewType, CancellationToken.None);

                    // Check again if the form is valid before invoking
                    if (IsDisposed || !IsHandleCreated) return;

                    // Update the UI safely
                    try
                    {
                        this.Invoke((Action)(() =>
                        {
                            try
                            {
                                if (IsDisposed) return;

                                switch (viewType)
                                {
                                    case ViewType.XY:
                                        if (xyPictureBox.Image != null)
                                        {
                                            var oldImage = xyPictureBox.Image;
                                            xyPictureBox.Image = bitmap;
                                            oldImage.Dispose();
                                        }
                                        else
                                        {
                                            xyPictureBox.Image = bitmap;
                                        }
                                        break;
                                    case ViewType.XZ:
                                        if (xzPictureBox.Image != null)
                                        {
                                            var oldImage = xzPictureBox.Image;
                                            xzPictureBox.Image = bitmap;
                                            oldImage.Dispose();
                                        }
                                        else
                                        {
                                            xzPictureBox.Image = bitmap;
                                        }
                                        break;
                                    case ViewType.YZ:
                                        if (yzPictureBox.Image != null)
                                        {
                                            var oldImage = yzPictureBox.Image;
                                            yzPictureBox.Image = bitmap;
                                            oldImage.Dispose();
                                        }
                                        else
                                        {
                                            yzPictureBox.Image = bitmap;
                                        }
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[ParticleSeparatorForm] Error updating view {viewType}: {ex.Message}");
                            }
                        }));
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Form was closed or handle destroyed while we were working
                        Logger.Log($"[ParticleSeparatorForm] Cannot update view - form no longer valid: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ParticleSeparatorForm] UpdateView error for {viewType}: {ex.Message}");
                }
            });
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            // Skip if not initialized
            if (!_initialized) return;

            // Re-adjust the particle info label maximum width when form resizes
            if (particleInfoLabel != null && particleInfoLabel.Parent != null)
            {
                particleInfoLabel.MaximumSize = new Size(
                    particleInfoLabel.Parent.ClientSize.Width - 20, 0);
            }

            // Re-adjust ListView columns
            AdjustListViewColumns();
        }

        private Bitmap RenderView(ViewType viewType, CancellationToken token)
        {
            try
            {
                // If we have no result, just show a blank image
                if (result == null || result.LabelVolume == null)
                {
                    switch (viewType)
                    {
                        case ViewType.XY:
                            return CreateEmptyBitmap(mainForm.GetWidth(), mainForm.GetHeight());
                        case ViewType.XZ:
                            return CreateEmptyBitmap(mainForm.GetWidth(), mainForm.GetDepth());
                        case ViewType.YZ:
                            return CreateEmptyBitmap(mainForm.GetHeight(), mainForm.GetDepth());
                        default:
                            return new Bitmap(1, 1);
                    }
                }

                // Determine dimensions based on view type
                int sourceWidth, sourceHeight, sliceIndex;
                switch (viewType)
                {
                    case ViewType.XY:
                        sourceWidth = result.LabelVolume.GetLength(0);
                        sourceHeight = result.LabelVolume.GetLength(1);
                        sliceIndex = Math.Min(currentXYSlice, result.LabelVolume.GetLength(2) - 1);
                        break;
                    case ViewType.XZ:
                        sourceWidth = result.LabelVolume.GetLength(0);
                        sourceHeight = result.LabelVolume.GetLength(2);
                        sliceIndex = Math.Min(currentXZSlice, result.LabelVolume.GetLength(1) - 1);
                        break;
                    case ViewType.YZ:
                        sourceWidth = result.LabelVolume.GetLength(1);
                        sourceHeight = result.LabelVolume.GetLength(2);
                        sliceIndex = Math.Min(currentYZSlice, result.LabelVolume.GetLength(0) - 1);
                        break;
                    default:
                        return new Bitmap(1, 1);
                }

                // Log the source dimensions
                Logger.Log($"[RenderView] Source dimensions for {viewType}: {sourceWidth}x{sourceHeight}");

                // Cap dimensions to avoid out of memory errors
                const int MaxDimension = 1024; // Maximum size for any dimension
                float scaleFactor = 1.0f;

                if (sourceWidth > MaxDimension || sourceHeight > MaxDimension)
                {
                    scaleFactor = Math.Min((float)MaxDimension / sourceWidth, (float)MaxDimension / sourceHeight);
                    Logger.Log($"[RenderView] Scaling {viewType} view by factor {scaleFactor} to fit in memory");
                }

                // Calculate target dimensions
                int targetWidth = Math.Max(1, (int)(sourceWidth * scaleFactor));
                int targetHeight = Math.Max(1, (int)(sourceHeight * scaleFactor));

                // Create bitmap with proper dimensions
                Bitmap bmp = new Bitmap(targetWidth, targetHeight);

                if (token.IsCancellationRequested)
                    return bmp;

                // Use LockBits for fast pixel manipulation
                Rectangle rect = new Rectangle(0, 0, targetWidth, targetHeight);
                BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                try
                {
                    // Get the pointer to the bitmap data
                    IntPtr ptr = bmpData.Scan0;
                    int stride = bmpData.Stride;
                    int bytes = stride * targetHeight;

                    // Use a different approach to reduce memory pressure
                    const int maxRowsPerBatch = 50;
                    int rowsPerBatch = Math.Min(maxRowsPerBatch, targetHeight);
                    byte[] rgbValues = new byte[stride * rowsPerBatch];  // Process in batches

                    // Process the bitmap in batches of rows
                    for (int yStart = 0; yStart < targetHeight; yStart += rowsPerBatch)
                    {
                        if (token.IsCancellationRequested)
                            break;

                        int rowsInBatch = Math.Min(rowsPerBatch, targetHeight - yStart);
                        Array.Clear(rgbValues, 0, stride * rowsInBatch);  // Clear the buffer

                        // Fill data for this batch of rows
                        FillPixelDataScaled(rgbValues, stride, targetWidth, rowsInBatch, yStart,
                                           viewType, sliceIndex, sourceWidth, sourceHeight, scaleFactor);

                        // Copy this batch to the bitmap
                        Marshal.Copy(rgbValues, 0, IntPtr.Add(ptr, yStart * stride), stride * rowsInBatch);
                    }
                }
                finally
                {
                    // Unlock the bitmap
                    bmp.UnlockBits(bmpData);
                }

                return bmp;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParticleSeparatorForm] RenderView error for {viewType}: {ex.Message}");
                return CreateErrorBitmap($"Rendering error: {ex.Message}");
            }
        }
        private Bitmap CreateErrorBitmap(string errorMessage)
        {
            Bitmap bmp = new Bitmap(400, 100);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                using (Font font = new Font("Arial", 9))
                {
                    g.DrawString(errorMessage, font, Brushes.Red, new RectangleF(10, 10, 380, 80));
                }
            }
            return bmp;
        }
        private void FillPixelDataScaled(byte[] rgbValues, int stride, int targetWidth, int rowsInBatch, int yStart,
                                ViewType viewType, int sliceIndex, int sourceWidth, int sourceHeight, float scaleFactor)
        {
            // Define constants for BGRA byte order
            const int bytesPerPixel = 4;
            const int blueOffset = 0;
            const int greenOffset = 1;
            const int redOffset = 2;
            const int alphaOffset = 3;

            // Get the grayscale data from the main form if available
            ChunkedVolume volumeData = (ChunkedVolume)mainForm.volumeData;

            // Get list of all selected particle IDs
            List<int> selectedParticleIds = particleListView.Tag as List<int> ?? new List<int>();
            if (highlightedParticleId != -1 && !selectedParticleIds.Contains(highlightedParticleId))
            {
                selectedParticleIds.Add(highlightedParticleId);
            }

            // Process each pixel
            for (int y = 0; y < rowsInBatch; y++)
            {
                int targetY = y + yStart;  // Actual Y position in the target image

                // Convert to source coordinates
                int sourceY = (int)(targetY / scaleFactor);
                if (sourceY >= sourceHeight)
                    sourceY = sourceHeight - 1;

                for (int targetX = 0; targetX < targetWidth; targetX++)
                {
                    // Convert to source coordinates
                    int sourceX = (int)(targetX / scaleFactor);
                    if (sourceX >= sourceWidth)
                        sourceX = sourceWidth - 1;

                    // Calculate the index in the byte array
                    int index = y * stride + targetX * bytesPerPixel;

                    // Default to black
                    byte r = 0, g = 0, b = 0, a = 255;

                    // Get the label and grayscale value based on view type
                    int label = 0;
                    byte grayValue = 0;

                    try
                    {
                        switch (viewType)
                        {
                            case ViewType.XY:
                                if (sourceX < result.LabelVolume.GetLength(0) &&
                                    sourceY < result.LabelVolume.GetLength(1) &&
                                    sliceIndex < result.LabelVolume.GetLength(2))
                                {
                                    label = result.LabelVolume[sourceX, sourceY, sliceIndex];
                                    if (volumeData != null && sourceX < volumeData.Width &&
                                        sourceY < volumeData.Height && sliceIndex < volumeData.Depth)
                                    {
                                        grayValue = volumeData[sourceX, sourceY, sliceIndex];
                                    }
                                }
                                break;
                            case ViewType.XZ:
                                if (sourceX < result.LabelVolume.GetLength(0) &&
                                    sliceIndex < result.LabelVolume.GetLength(1) &&
                                    sourceY < result.LabelVolume.GetLength(2))
                                {
                                    label = result.LabelVolume[sourceX, sliceIndex, sourceY];
                                    if (volumeData != null && sourceX < volumeData.Width &&
                                        sliceIndex < volumeData.Height && sourceY < volumeData.Depth)
                                    {
                                        grayValue = volumeData[sourceX, sliceIndex, sourceY];
                                    }
                                }
                                break;
                            case ViewType.YZ:
                                if (sliceIndex < result.LabelVolume.GetLength(0) &&
                                    sourceX < result.LabelVolume.GetLength(1) &&
                                    sourceY < result.LabelVolume.GetLength(2))
                                {
                                    label = result.LabelVolume[sliceIndex, sourceX, sourceY];
                                    if (volumeData != null && sliceIndex < volumeData.Width &&
                                        sourceX < volumeData.Height && sourceY < volumeData.Depth)
                                    {
                                        grayValue = volumeData[sliceIndex, sourceX, sourceY];
                                    }
                                }
                                break;
                        }
                    }
                    catch (IndexOutOfRangeException)
                    {
                        // Silently handle any array index errors by setting label to 0
                        label = 0;
                        grayValue = 0;
                    }

                    // First, draw the grayscale background
                    if (volumeData != null)
                    {
                        r = g = b = grayValue;
                    }

                    // Then overlay the particles with semi-transparent colors
                    if (label > 0)
                    {
                        // Get or create a color for this label
                        if (!particleColors.TryGetValue(label, out Color color))
                        {
                            // Generate a new distinctive color
                            int hue = (label * 40) % 360; // Use ID for hue to get different colors
                            color = HsvToRgb(hue, 1.0, 0.9);
                            particleColors[label] = color;
                        }

                        // Determine opacity based on whether this is a selected particle
                        bool isSelected = selectedParticleIds.Contains(label);
                        bool isPrimarySelection = (label == highlightedParticleId);

                        int opacity;
                        if (isPrimarySelection)
                            opacity = 200; // Primary selection - brightest
                        else if (isSelected)
                            opacity = 180; // Selected but not primary - bright
                        else
                            opacity = 120; // Not selected - semi-transparent

                        // Alpha blending
                        double alpha = opacity / 255.0;
                        r = (byte)(r * (1 - alpha) + color.R * alpha);
                        g = (byte)(g * (1 - alpha) + color.G * alpha);
                        b = (byte)(b * (1 - alpha) + color.B * alpha);
                    }

                    // Set the BGRA values
                    rgbValues[index + blueOffset] = b;
                    rgbValues[index + greenOffset] = g;
                    rgbValues[index + redOffset] = r;
                    rgbValues[index + alphaOffset] = a;
                }
            }
        }
        private void FillPixelDataOptimized(byte[] rgbValues, int stride, int width, int height,
                                           ViewType viewType, int sliceIndex, float scaleX, float scaleY,
                                           CancellationToken token)
        {
            // Define constants for BGRA byte order
            const int bytesPerPixel = 4;
            const int blueOffset = 0;
            const int greenOffset = 1;
            const int redOffset = 2;
            const int alphaOffset = 3;

            // Get the grayscale data from the main form if available
            ChunkedVolume volumeData = (ChunkedVolume)mainForm.volumeData;

            // Original dimensions
            int origWidth = 0, origHeight = 0;
            int volDepth = result.LabelVolume.GetLength(2);

            switch (viewType)
            {
                case ViewType.XY:
                    origWidth = result.LabelVolume.GetLength(0);
                    origHeight = result.LabelVolume.GetLength(1);
                    break;
                case ViewType.XZ:
                    origWidth = result.LabelVolume.GetLength(0);
                    origHeight = volDepth;
                    break;
                case ViewType.YZ:
                    origWidth = result.LabelVolume.GetLength(1);
                    origHeight = volDepth;
                    break;
            }

            // Prepare particle color cache
            if (particleColors.Count == 0)
            {
                // Initialize with some common colors for small particles
                for (int i = 1; i <= 100; i++)
                {
                    // Generate a new distinctive color
                    int hue = (i * 40) % 360; // Use ID for hue to get different colors
                    particleColors[i] = HsvToRgb(hue, 1.0, 0.9);
                }
            }

            // Process in rows for better cache locality
            for (int y = 0; y < height; y++)
            {
                if (y % 50 == 0 && token.IsCancellationRequested)
                    return;

                // Map to original coordinates
                int origY = scaleY < 1.0f ? (int)(y / scaleY) : y;
                if (origY >= origHeight)
                    origY = origHeight - 1;

                for (int x = 0; x < width; x++)
                {
                    // Map to original coordinates
                    int origX = scaleX < 1.0f ? (int)(x / scaleX) : x;
                    if (origX >= origWidth)
                        origX = origWidth - 1;

                    // Calculate the index in the byte array
                    int index = y * stride + x * bytesPerPixel;

                    // Default to black
                    byte r = 0, g = 0, b = 0, a = 255;

                    // Get the label and grayscale value based on view type
                    int label = 0;
                    byte grayValue = 0;

                    switch (viewType)
                    {
                        case ViewType.XY:
                            if (origX < result.LabelVolume.GetLength(0) &&
                                origY < result.LabelVolume.GetLength(1) &&
                                sliceIndex < result.LabelVolume.GetLength(2))
                            {
                                label = result.LabelVolume[origX, origY, sliceIndex];
                                if (volumeData != null && origX < volumeData.Width &&
                                    origY < volumeData.Height && sliceIndex < volumeData.Depth)
                                {
                                    grayValue = volumeData[origX, origY, sliceIndex];
                                }
                            }
                            break;
                        case ViewType.XZ:
                            if (origX < result.LabelVolume.GetLength(0) &&
                                sliceIndex < result.LabelVolume.GetLength(1) &&
                                origY < result.LabelVolume.GetLength(2))
                            {
                                label = result.LabelVolume[origX, sliceIndex, origY];
                                if (volumeData != null && origX < volumeData.Width &&
                                    sliceIndex < volumeData.Height && origY < volumeData.Depth)
                                {
                                    grayValue = volumeData[origX, sliceIndex, origY];
                                }
                            }
                            break;
                        case ViewType.YZ:
                            if (sliceIndex < result.LabelVolume.GetLength(0) &&
                                origX < result.LabelVolume.GetLength(1) &&
                                origY < result.LabelVolume.GetLength(2))
                            {
                                label = result.LabelVolume[sliceIndex, origX, origY];
                                if (volumeData != null && sliceIndex < volumeData.Width &&
                                    origX < volumeData.Height && origY < volumeData.Depth)
                                {
                                    grayValue = volumeData[sliceIndex, origX, origY];
                                }
                            }
                            break;
                    }

                    // First, draw the grayscale background
                    if (volumeData != null)
                    {
                        r = g = b = grayValue;
                    }

                    // Then overlay the particles with semi-transparent colors
                    if (label > 0)
                    {
                        // Get or create a color for this label
                        if (!particleColors.TryGetValue(label, out Color color))
                        {
                            // Generate a new distinctive color
                            int hue = (label * 40) % 360; // Use ID for hue to get different colors
                            color = HsvToRgb(hue, 1.0, 0.9);
                            particleColors[label] = color;
                        }

                        // Determine opacity based on whether this is the highlighted particle
                        int opacity = label == highlightedParticleId ? 180 : 120;

                        // Alpha blending
                        double alpha = opacity / 255.0;
                        r = (byte)(r * (1 - alpha) + color.R * alpha);
                        g = (byte)(g * (1 - alpha) + color.G * alpha);
                        b = (byte)(b * (1 - alpha) + color.B * alpha);
                    }

                    // Set the BGRA values
                    rgbValues[index + blueOffset] = b;
                    rgbValues[index + greenOffset] = g;
                    rgbValues[index + redOffset] = r;
                    rgbValues[index + alphaOffset] = a;
                }
            }
        }

        private Bitmap CreateEmptyBitmap(int width, int height)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
            }
            return bmp;
        }

        private Bitmap CreateFastBitmap(int width, int height, ViewType viewType, int sliceIndex)
        {
            // Limit dimensions for very large volumes to avoid OutOfMemoryException
            int maxDimension = 2048;
            if (width > maxDimension || height > maxDimension)
            {
                float scale = Math.Min((float)maxDimension / width, (float)maxDimension / height);
                width = (int)(width * scale);
                height = (int)(height * scale);
                Logger.Log($"[CreateFastBitmap] Scaling down view to {width}x{height} to conserve memory");
            }

            // Ensure valid dimensions
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            try
            {
                // Create the bitmap
                Bitmap bmp = new Bitmap(width, height);

                // Use LockBits for fast pixel manipulation
                Rectangle rect = new Rectangle(0, 0, width, height);
                BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                try
                {
                    // Get the pointer to the bitmap data
                    IntPtr ptr = bmpData.Scan0;
                    int stride = bmpData.Stride;
                    int bytes = stride * height;

                    // Use a different approach to reduce memory pressure
                    const int maxRowsPerBatch = 20;
                    byte[] rgbValues = new byte[stride * maxRowsPerBatch];  // Process 20 rows at a time instead of full image

                    // Process the bitmap in batches of rows
                    for (int yStart = 0; yStart < height; yStart += maxRowsPerBatch)
                    {
                        int rowsInBatch = Math.Min(maxRowsPerBatch, height - yStart);
                        Array.Clear(rgbValues, 0, stride * rowsInBatch);  // Clear the buffer

                        // Fill data for this batch of rows
                        FillPixelDataBatch(rgbValues, stride, width, rowsInBatch, yStart, viewType, sliceIndex);

                        // Copy this batch to the bitmap
                        Marshal.Copy(rgbValues, 0, IntPtr.Add(ptr, yStart * stride), stride * rowsInBatch);
                    }
                }
                finally
                {
                    // Unlock the bitmap
                    bmp.UnlockBits(bmpData);
                }

                return bmp;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CreateFastBitmap] Error: {ex.Message}");

                // Create a simpler bitmap with an error message
                Bitmap errorBmp = new Bitmap(Math.Min(width, 400), Math.Min(height, 200));
                using (Graphics g = Graphics.FromImage(errorBmp))
                {
                    g.Clear(Color.Black);
                    using (Font font = new Font("Arial", 10))
                    {
                        g.DrawString($"Rendering error: {ex.Message.Substring(0, Math.Min(ex.Message.Length, 50))}...",
                                    font, Brushes.Red, new PointF(10, 10));
                    }
                }
                return errorBmp;
            }
        }
        private void FillPixelDataBatch(byte[] rgbValues, int stride, int width, int rowsInBatch, int yStart,
                               ViewType viewType, int sliceIndex)
        {
            // Define constants for BGRA byte order
            const int bytesPerPixel = 4;
            const int blueOffset = 0;
            const int greenOffset = 1;
            const int redOffset = 2;
            const int alphaOffset = 3;

            // Get the grayscale data from the main form if available
            ChunkedVolume volumeData = (ChunkedVolume)mainForm.volumeData;

            for (int y = 0; y < rowsInBatch; y++)
            {
                int actualY = y + yStart;  // Actual Y position in the full image

                for (int x = 0; x < width; x++)
                {
                    int index = y * stride + x * bytesPerPixel;

                    // Default to black
                    byte r = 0, g = 0, b = 0, a = 255;

                    // Get the label and grayscale value based on view type
                    int label = 0;
                    byte grayValue = 0;

                    switch (viewType)
                    {
                        case ViewType.XY:
                            if (x < result.LabelVolume.GetLength(0) &&
                                actualY < result.LabelVolume.GetLength(1) &&
                                sliceIndex < result.LabelVolume.GetLength(2))
                            {
                                label = result.LabelVolume[x, actualY, sliceIndex];
                                if (volumeData != null && x < volumeData.Width &&
                                    actualY < volumeData.Height && sliceIndex < volumeData.Depth)
                                {
                                    grayValue = volumeData[x, actualY, sliceIndex];
                                }
                            }
                            break;
                        case ViewType.XZ:
                            if (x < result.LabelVolume.GetLength(0) &&
                                sliceIndex < result.LabelVolume.GetLength(1) &&
                                actualY < result.LabelVolume.GetLength(2))
                            {
                                label = result.LabelVolume[x, sliceIndex, actualY];
                                if (volumeData != null && x < volumeData.Width &&
                                    sliceIndex < volumeData.Height && actualY < volumeData.Depth)
                                {
                                    grayValue = volumeData[x, sliceIndex, actualY];
                                }
                            }
                            break;
                        case ViewType.YZ:
                            if (sliceIndex < result.LabelVolume.GetLength(0) &&
                                x < result.LabelVolume.GetLength(1) &&
                                actualY < result.LabelVolume.GetLength(2))
                            {
                                label = result.LabelVolume[sliceIndex, x, actualY];
                                if (volumeData != null && sliceIndex < volumeData.Width &&
                                    x < volumeData.Height && actualY < volumeData.Depth)
                                {
                                    grayValue = volumeData[sliceIndex, x, actualY];
                                }
                            }
                            break;
                    }

                    // First, draw the grayscale background
                    if (volumeData != null)
                    {
                        r = g = b = grayValue;
                    }

                    // Then overlay the particles with semi-transparent colors
                    if (label > 0)
                    {
                        // Get or create a color for this label
                        if (!particleColors.TryGetValue(label, out Color color))
                        {
                            // Generate a new distinctive color
                            int hue = (label * 40) % 360; // Use ID for hue to get different colors
                            color = HsvToRgb(hue, 1.0, 0.9);
                            particleColors[label] = color;
                        }

                        // Determine opacity based on whether this is the highlighted particle
                        int opacity = label == highlightedParticleId ? 180 : 120;

                        // Alpha blending
                        double alpha = opacity / 255.0;
                        r = (byte)(r * (1 - alpha) + color.R * alpha);
                        g = (byte)(g * (1 - alpha) + color.G * alpha);
                        b = (byte)(b * (1 - alpha) + color.B * alpha);
                    }

                    // Set the BGRA values
                    rgbValues[index + blueOffset] = b;
                    rgbValues[index + greenOffset] = g;
                    rgbValues[index + redOffset] = r;
                    rgbValues[index + alphaOffset] = a;
                }
            }
        }
        private void LogMemoryUsage(string context)
        {
            GC.Collect(); // Optional: collect garbage before measuring
            using (Process process = Process.GetCurrentProcess())
            {
                long memoryBytes = process.PrivateMemorySize64;
                double memoryMB = memoryBytes / (1024.0 * 1024.0);
                Logger.Log($"[Memory] {context} - Using {memoryMB:F2} MB");
            }
        }

        private void FillPixelData(byte[] rgbValues, int stride, int width, int height, ViewType viewType, int sliceIndex)
        {
            // Define constants for BGRA byte order
            const int bytesPerPixel = 4;
            const int blueOffset = 0;
            const int greenOffset = 1;
            const int redOffset = 2;
            const int alphaOffset = 3;

            // Get the grayscale data from the main form if available
            ChunkedVolume volumeData = (ChunkedVolume)mainForm.volumeData;

            // Get list of all selected particle IDs
            List<int> selectedParticleIds = particleListView.Tag as List<int> ?? new List<int>();
            if (highlightedParticleId != -1 && !selectedParticleIds.Contains(highlightedParticleId))
            {
                selectedParticleIds.Add(highlightedParticleId);
            }

            // Process each pixel
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Calculate the index in the byte array
                    int index = y * stride + x * bytesPerPixel;

                    // Default to black
                    byte r = 0, g = 0, b = 0, a = 255;

                    // Get the label and grayscale value based on view type
                    int label = 0;
                    byte grayValue = 0;

                    switch (viewType)
                    {
                        case ViewType.XY:
                            if (x < result.LabelVolume.GetLength(0) &&
                                y < result.LabelVolume.GetLength(1) &&
                                sliceIndex < result.LabelVolume.GetLength(2))
                            {
                                label = result.LabelVolume[x, y, sliceIndex];
                                if (volumeData != null && x < volumeData.Width &&
                                    y < volumeData.Height && sliceIndex < volumeData.Depth)
                                {
                                    grayValue = volumeData[x, y, sliceIndex];
                                }
                            }
                            break;
                        case ViewType.XZ:
                            if (x < result.LabelVolume.GetLength(0) &&
                                sliceIndex < result.LabelVolume.GetLength(1) &&
                                y < result.LabelVolume.GetLength(2))
                            {
                                label = result.LabelVolume[x, sliceIndex, y];
                                if (volumeData != null && x < volumeData.Width &&
                                    sliceIndex < volumeData.Height && y < volumeData.Depth)
                                {
                                    grayValue = volumeData[x, sliceIndex, y];
                                }
                            }
                            break;
                        case ViewType.YZ:
                            if (sliceIndex < result.LabelVolume.GetLength(0) &&
                                x < result.LabelVolume.GetLength(1) &&
                                y < result.LabelVolume.GetLength(2))
                            {
                                label = result.LabelVolume[sliceIndex, x, y];
                                if (volumeData != null && sliceIndex < volumeData.Width &&
                                    x < volumeData.Height && y < volumeData.Depth)
                                {
                                    grayValue = volumeData[sliceIndex, x, y];
                                }
                            }
                            break;
                    }

                    // First, draw the grayscale background
                    if (volumeData != null)
                    {
                        r = g = b = grayValue;
                    }

                    // Then overlay the particles with semi-transparent colors
                    if (label > 0)
                    {
                        // Get or create a color for this label
                        if (!particleColors.TryGetValue(label, out Color color))
                        {
                            // Generate a new distinctive color
                            int hue = (label * 40) % 360; // Use ID for hue to get different colors
                            color = HsvToRgb(hue, 1.0, 0.9);
                            particleColors[label] = color;
                        }

                        // Determine opacity based on whether this is a selected particle
                        bool isSelected = selectedParticleIds.Contains(label);
                        bool isPrimarySelection = (label == highlightedParticleId);

                        int opacity;
                        if (isPrimarySelection)
                            opacity = 200; // Primary selection - brightest
                        else if (isSelected)
                            opacity = 180; // Selected but not primary - bright
                        else
                            opacity = 120; // Not selected - semi-transparent

                        // Alpha blending
                        double alpha = opacity / 255.0;
                        r = (byte)(r * (1 - alpha) + color.R * alpha);
                        g = (byte)(g * (1 - alpha) + color.G * alpha);
                        b = (byte)(b * (1 - alpha) + color.B * alpha);
                    }

                    // Set the BGRA values
                    rgbValues[index + blueOffset] = b;
                    rgbValues[index + greenOffset] = g;
                    rgbValues[index + redOffset] = r;
                    rgbValues[index + alphaOffset] = a;
                }
            }
        }


        private void UpdateParticleList()
        {
            if (result == null || result.Particles == null)
            {
                particleListView.VirtualListSize = 0;
                particleListView.Refresh();
                return;
            }

            // Use virtual mode for better performance with large lists
            particleListView.VirtualListSize = result.Particles.Count;
            particleListView.Refresh();

            // Enable the remove islands button if we have particles
            removeIslandsButton.Enabled = result.Particles.Count > 0;
        }

        private async void SeparateButton_Click(object sender, EventArgs e)
        {
            // Cancel any current operation
            cancellationTokenSource?.Cancel();
            cancellationTokenSource = new CancellationTokenSource();

            // Disable UI controls
            separateButton.Enabled = false;
            saveButton.Enabled = false;
            exportToMainButton.Enabled = false;
            useGpuCheckBox.Enabled = false;
            currentSliceRadio.Enabled = false;
            wholeVolumeRadio.Enabled = false;
            conservativeRadio.Enabled = false;
            aggressiveRadio.Enabled = false;

            statusLabel.Text = "Separating particles...";
            progressBar.Value = 0;
            progressBar.Style = ProgressBarStyle.Continuous;

            try
            {
                Logger.Log("[ParticleSeparatorForm] Starting particle separation");
                Logger.Log($"[ParticleSeparatorForm] Mode: {(wholeVolumeRadio.Checked ? "3D" : "2D")}, " +
                           $"Method: {(conservativeRadio.Checked ? "Conservative" : "Aggressive")}, " +
                           $"GPU: {useGpuCheckBox.Checked}");

                // Create progress reporter
                var progress = new Progress<int>(p => {
                    progressBar.Value = p;
                    statusLabel.Text = $"Separating particles... {p}%";
                    Application.DoEvents();
                });

                // Create separator
                separator = new ParticleSeparator(mainForm, selectedMaterial, useGpuCheckBox.Checked);

                // Run separation
                result = await Task.Run(() => separator.SeparateParticles(
                    wholeVolumeRadio.Checked,
                    conservativeRadio.Checked,
                    mainForm.CurrentSlice,
                    progress,
                    cancellationTokenSource.Token),
                    cancellationTokenSource.Token);

                // First, update the trackbar and numeric ranges before setting values
                UpdateSliceControlRanges();

                // Now set current position values (safely, within the updated ranges)
                if (result.Is3D)
                {
                    currentXYSlice = Math.Min(result.CurrentSlice, result.LabelVolume.GetLength(2) - 1);
                    currentXZSlice = Math.Min(result.LabelVolume.GetLength(1) / 2, result.LabelVolume.GetLength(1) - 1);
                    currentYZSlice = Math.Min(result.LabelVolume.GetLength(0) / 2, result.LabelVolume.GetLength(0) - 1);
                }
                else
                {
                    currentXYSlice = 0;
                    currentXZSlice = 0;
                    currentYZSlice = 0;
                }

                // Update slice controls safely
                SafeSetTrackBarValue(xySliceTrackBar, currentXYSlice);
                SafeSetNumericValue(xySliceNumeric, currentXYSlice);

                SafeSetTrackBarValue(xzSliceTrackBar, currentXZSlice);
                SafeSetNumericValue(xzSliceNumeric, currentXZSlice);

                SafeSetTrackBarValue(yzSliceTrackBar, currentYZSlice);
                SafeSetNumericValue(yzSliceNumeric, currentYZSlice);

                // Update UI with results
                UpdateParticleList();
                UpdateAllViews();

                // Enable save button
                saveButton.Enabled = true;
                exportToMainButton.Enabled = true;

                statusLabel.Text = $"Separation complete. Found {result.Particles.Count} particles.";
                Logger.Log($"[ParticleSeparatorForm] Separation complete. Found {result.Particles.Count} particles.");

                // Generate CSV file if requested
                if (generateCsvCheckBox.Checked)
                {
                    SaveFileDialog saveDialog = new SaveFileDialog
                    {
                        Filter = "CSV Files (*.csv)|*.csv",
                        Title = "Save Particle Data CSV",
                        FileName = $"particles_{selectedMaterial.Name}.csv"
                    };

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        separator.SaveToCsv(saveDialog.FileName, result.Particles);
                        statusLabel.Text = $"Saved CSV data for {result.Particles.Count} particles.";
                        Logger.Log($"[ParticleSeparatorForm] Saved CSV data to {saveDialog.FileName}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                statusLabel.Text = "Operation cancelled.";
                Logger.Log("[ParticleSeparatorForm] Operation cancelled by user");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Error during separation.";
                Logger.Log($"[ParticleSeparatorForm] Error: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                // Re-enable UI controls
                separateButton.Enabled = true;
                useGpuCheckBox.Enabled = true;
                currentSliceRadio.Enabled = true;
                wholeVolumeRadio.Enabled = true;
                conservativeRadio.Enabled = true;
                aggressiveRadio.Enabled = true;
                progressBar.Value = 0;
            }
        }
        private void UpdateSliceControlRanges()
        {
            if (result == null || result.LabelVolume == null)
                return;

            // Update XY trackbar/numeric ranges
            int xyMax = Math.Max(0, result.LabelVolume.GetLength(2) - 1);
            xySliceTrackBar.Minimum = 0;
            xySliceTrackBar.Maximum = xyMax;
            xySliceNumeric.Minimum = 0;
            xySliceNumeric.Maximum = xyMax;

            // Update XZ trackbar/numeric ranges
            int xzMax = Math.Max(0, result.LabelVolume.GetLength(1) - 1);
            xzSliceTrackBar.Minimum = 0;
            xzSliceTrackBar.Maximum = xzMax;
            xzSliceNumeric.Minimum = 0;
            xzSliceNumeric.Maximum = xzMax;

            // Update YZ trackbar/numeric ranges
            int yzMax = Math.Max(0, result.LabelVolume.GetLength(0) - 1);
            yzSliceTrackBar.Minimum = 0;
            yzSliceTrackBar.Maximum = yzMax;
            yzSliceNumeric.Minimum = 0;
            yzSliceNumeric.Maximum = yzMax;

            Logger.Log($"[UpdateSliceControlRanges] XY:{xyMax}, XZ:{xzMax}, YZ:{yzMax}");
        }

        private void SafeSetTrackBarValue(TrackBar trackBar, int value)
        {
            if (trackBar == null) return;

            // Temporarily remove the event handler
            trackBar.ValueChanged -= SliceTrackBar_ValueChanged;

            // Ensure value is within range
            int safeValue = Math.Max(trackBar.Minimum, Math.Min(trackBar.Maximum, value));
            trackBar.Value = safeValue;

            // Reattach the event handler
            trackBar.ValueChanged += SliceTrackBar_ValueChanged;
        }

        private void SafeSetNumericValue(NumericUpDown numeric, int value)
        {
            if (numeric == null) return;

            // Temporarily remove the event handler
            numeric.ValueChanged -= SliceNumeric_ValueChanged;

            // Ensure value is within range
            decimal safeValue = Math.Max(numeric.Minimum, Math.Min(numeric.Maximum, value));
            numeric.Value = safeValue;

            // Reattach the event handler
            numeric.ValueChanged += SliceNumeric_ValueChanged;
        }
        private void SaveButton_Click(object sender, EventArgs e)
        {
            if (result == null || result.Particles == null)
            {
                MessageBox.Show("No results to save.", "No Results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Create save dialog
            SaveFileDialog saveDialog = new SaveFileDialog
            {
                Filter = "Binary Files (*.bin)|*.bin|CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
                Title = "Save Particle Results",
                FileName = $"particles_{selectedMaterial.Name}"
            };

            if (saveDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    Logger.Log($"[ParticleSeparatorForm] Saving results to {saveDialog.FileName}");
                    statusLabel.Text = "Saving results...";
                    progressBar.Style = ProgressBarStyle.Marquee;

                    if (saveDialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    {
                        // Save as CSV
                        separator.SaveToCsv(saveDialog.FileName, result.Particles);
                        statusLabel.Text = $"Saved {result.Particles.Count} particles to CSV";
                    }
                    else
                    {
                        // Save as binary
                        separator.SaveToBinaryFile(saveDialog.FileName, result);
                        statusLabel.Text = $"Saved results to binary file";
                    }

                    MessageBox.Show("Results saved successfully.", "Save Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Logger.Log("[ParticleSeparatorForm] Results saved successfully");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving results: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    statusLabel.Text = "Error saving results";
                    Logger.Log($"[ParticleSeparatorForm] Save error: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    progressBar.Style = ProgressBarStyle.Continuous;
                    progressBar.Value = 0;
                }
            }
        }

        private void ExportToMainButton_Click(object sender, EventArgs e)
        {
            if (result == null || result.Particles == null)
            {
                MessageBox.Show("No results to export.", "No Results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Ask the user if they want to create a new material for each particle
                DialogResult dr = MessageBox.Show(
                    "Would you like to create a separate material for each particle?\n\n" +
                    "Yes: Create a new material for each particle\n" +
                    "No: Use random colors but keep the same material\n" +
                    "Cancel: Abort operation",
                    "Export Options",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (dr == DialogResult.Cancel)
                    return;

                bool createNewMaterials = (dr == DialogResult.Yes);

                // Create a progress form
                using (ProgressForm progressForm = new ProgressForm("Exporting particles to main view..."))
                {
                    progressForm.Show();
                    Logger.Log("[ParticleSeparatorForm] Exporting particles to main view");
                    Logger.Log($"[ParticleSeparatorForm] Creating new materials: {createNewMaterials}");

                    // Determine the slices to process
                    int startSlice = 0;
                    int endSlice = mainForm.GetDepth() - 1;

                    if (!result.Is3D)
                    {
                        // For 2D result, only process the current slice
                        startSlice = result.CurrentSlice;
                        endSlice = result.CurrentSlice;
                    }

                    // Create materials if needed
                    Dictionary<int, byte> particleMaterialMap = new Dictionary<int, byte>();

                    if (createNewMaterials)
                    {
                        // Ask how many materials to create (if there are many particles)
                        int maxMaterials = 10; // Default limit

                        if (result.Particles.Count > 20)
                        {
                            using (var matDialog = new Form
                            {
                                Text = "Material Limit",
                                Size = new Size(400, 150),
                                FormBorderStyle = FormBorderStyle.FixedDialog,
                                StartPosition = FormStartPosition.CenterParent,
                                MaximizeBox = false,
                                MinimizeBox = false
                            })
                            {
                                Label label = new Label
                                {
                                    Text = $"You have {result.Particles.Count} particles. How many materials would you like to create?\n" +
                                           $"(Particles will be grouped by size)",
                                    Location = new Point(10, 10),
                                    Size = new Size(380, 40)
                                };

                                NumericUpDown numMaterials = new NumericUpDown
                                {
                                    Location = new Point(150, 60),
                                    Size = new Size(80, 20),
                                    Minimum = 1,
                                    Maximum = 100,
                                    Value = Math.Min(20, result.Particles.Count)
                                };

                                Button okButton = new Button
                                {
                                    Text = "OK",
                                    Location = new Point(150, 90),
                                    DialogResult = DialogResult.OK
                                };

                                matDialog.Controls.Add(label);
                                matDialog.Controls.Add(numMaterials);
                                matDialog.Controls.Add(okButton);
                                matDialog.AcceptButton = okButton;

                                if (matDialog.ShowDialog() == DialogResult.OK)
                                {
                                    maxMaterials = (int)numMaterials.Value;
                                }
                            }
                        }
                        else
                        {
                            maxMaterials = result.Particles.Count;
                        }

                        Logger.Log($"[ParticleSeparatorForm] Creating {maxMaterials} materials for particles");

                        // Create materials
                        var sortedParticles = result.Particles.OrderByDescending(p => p.VoxelCount).ToList();
                        int particlesPerMaterial = (int)Math.Ceiling((double)sortedParticles.Count / maxMaterials);

                        for (int i = 0; i < Math.Min(maxMaterials, sortedParticles.Count); i++)
                        {
                            // Create a new material
                            string matName = $"{selectedMaterial.Name}_Particle{i + 1}";
                            Color matColor = HsvToRgb((i * 30) % 360, 1.0, 0.9); // Generate distinct colors

                            byte matId = mainForm.GetNextMaterialID();
                            Material newMat = new Material(matName, matColor, 0, 0, matId);
                            mainForm.Materials.Add(newMat);

                            // Assign particles to this material
                            int startIdx = i * particlesPerMaterial;
                            int endIdx = Math.Min((i + 1) * particlesPerMaterial, sortedParticles.Count);

                            for (int j = startIdx; j < endIdx; j++)
                            {
                                particleMaterialMap[sortedParticles[j].Id] = matId;
                            }
                        }
                    }

                    // Apply the label volume to the main view
                    progressForm.UpdateProgress(0, endSlice - startSlice + 1);

                    for (int z = startSlice; z <= endSlice; z++)
                    {
                        // For 3D view, check if the z-index is valid for the label volume
                        if (result.Is3D && z >= result.LabelVolume.GetLength(2))
                            continue;

                        int sliceZ = result.Is3D ? z : 0; // For 2D, we have just one slice at index 0

                        for (int y = 0; y < mainForm.GetHeight() && y < result.LabelVolume.GetLength(1); y++)
                        {
                            for (int x = 0; x < mainForm.GetWidth() && x < result.LabelVolume.GetLength(0); x++)
                            {
                                int label = result.LabelVolume[x, y, sliceZ];

                                if (label > 0)
                                {
                                    if (createNewMaterials && particleMaterialMap.ContainsKey(label))
                                    {
                                        // Use the mapped material ID
                                        mainForm.volumeLabels[x, y, z] = particleMaterialMap[label];
                                    }
                                    else
                                    {
                                        // Use the original material
                                        mainForm.volumeLabels[x, y, z] = selectedMaterial.ID;
                                    }
                                }
                            }
                        }

                        progressForm.UpdateProgress(z - startSlice + 1, endSlice - startSlice + 1);
                    }

                    // Update the main view
                    mainForm.RenderViews();
                    mainForm.SaveLabelsChk();
                }

                statusLabel.Text = "Particles exported to main view";
                Logger.Log("[ParticleSeparatorForm] Particles exported to main view");
                MessageBox.Show("Particles have been exported to the main view.", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting results: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Error exporting results";
                Logger.Log($"[ParticleSeparatorForm] Export error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static Color HsvToRgb(double h, double s, double v)
        {
            int hi = (int)(Math.Floor(h / 60)) % 6;
            double f = h / 60 - Math.Floor(h / 60);

            v = v * 255;
            int p = (int)(v * (1 - s));
            int q = (int)(v * (1 - f * s));
            int t = (int)(v * (1 - (1 - f) * s));

            if (hi == 0)
                return Color.FromArgb((int)v, t, p);
            else if (hi == 1)
                return Color.FromArgb(q, (int)v, p);
            else if (hi == 2)
                return Color.FromArgb(p, (int)v, t);
            else if (hi == 3)
                return Color.FromArgb(p, q, (int)v);
            else if (hi == 4)
                return Color.FromArgb(t, p, (int)v);
            else
                return Color.FromArgb((int)v, p, q);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Cancel any running operation
            cancellationTokenSource?.Cancel();

            // Dispose resources
            separator?.Dispose();
            xyPictureBox.Image?.Dispose();
            xzPictureBox.Image?.Dispose();
            yzPictureBox.Image?.Dispose();

            base.OnFormClosing(e);
            Logger.Log("[ParticleSeparatorForm] Form closing");
        }
    }
}