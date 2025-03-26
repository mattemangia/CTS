using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Drawing.Drawing2D;

namespace CTSegmenter
{
    internal class SegmentAnythingCT
    {
        // Main form components
        private Form samForm;
        private TableLayoutPanel mainLayout;
        private Panel xyPanel, xzPanel, yzPanel;
        private PictureBox xyViewer, xzViewer, yzViewer;
        private Panel controlPanel;
        private TextBox modelPathTextBox;
        private RadioButton rbCPU, rbGPU;
        private Button btnLoadModel, btnApply, btnClose;
        private Button btnPositivePrompt, btnNegativePrompt;
        private CheckBox chkAutoUpdate;
        private Label statusLabel;

        // Slice control components 
        private Label lblSliceXY;
        private TrackBar sliderXY;
        private NumericUpDown numXY;
        private CheckBox chkSyncWithMainView;

        // Orthoslice controls
        private Label lblSliceXZ;
        private TrackBar sliderXZ;
        private NumericUpDown numXZ;

        private Label lblSliceYZ;
        private TrackBar sliderYZ;
        private NumericUpDown numYZ;

        // Scrollbar controls
        private HScrollBar xyHScroll, xzHScroll, yzHScroll;
        private VScrollBar xyVScroll, xzVScroll, yzVScroll;

        // Zoom and pan state variables
        private float xyZoom = 1.0f, xzZoom = 1.0f, yzZoom = 1.0f;
        private Point xyPan = Point.Empty, xzPan = Point.Empty, yzPan = Point.Empty;

        // Image bounds tracking
        private Rectangle xyImageBounds = Rectangle.Empty;
        private Rectangle xzImageBounds = Rectangle.Empty;
        private Rectangle yzImageBounds = Rectangle.Empty;

        // References to parent application components
        private MainForm mainForm;
        private Material selectedMaterial;
        private AnnotationManager annotationManager;
        private Dictionary<int, bool> pointTypes = new Dictionary<int, bool>(); // Maps point ID to isPositive

        // ONNX model components
        private InferenceSession encoderSession;
        private InferenceSession decoderSession;
        private string encoderPath;
        private string decoderPath;
        private bool useGPU = false;

        // Slices information
        private int xySlice, xzRow, yzCol;

        // Segmentation results
        private byte[,] segmentationMask;

        // Prompt mode
        private enum PromptMode { Positive, Negative }
        private PromptMode currentMode = PromptMode.Positive;

        // Slice change callback
        private Action<int> sliceChangeCallback;

        public SegmentAnythingCT(MainForm mainForm, Material selectedMaterial, AnnotationManager annotationManager)
        {
            Logger.Log("[SegmentAnythingCT] Creating SAM interface");
            this.mainForm = mainForm;
            this.selectedMaterial = selectedMaterial;
            this.annotationManager = annotationManager;

            // Get current slice positions from MainForm
            xySlice = mainForm.CurrentSlice;
            xzRow = mainForm.XzSliceY;
            yzCol = mainForm.YzSliceX;

            // Initialize form and UI
            InitializeForm();

            // Set default model paths
            string onnxDirectory = Path.Combine(Application.StartupPath, "ONNX");
            encoderPath = Path.Combine(onnxDirectory, "sam2.1_large.encoder.onnx");
            decoderPath = Path.Combine(onnxDirectory, "sam2.1_large.decoder.onnx");
            modelPathTextBox.Text = onnxDirectory;

            // Register for slice changes from MainForm
            sliceChangeCallback = UpdateSliceFromMainForm;
            mainForm.RegisterSliceChangeCallback(sliceChangeCallback);

            // Try to load models automatically
            try
            {
                Logger.Log("[SegmentAnythingCT] Attempting to load ONNX models");
                LoadONNXModels();
                statusLabel.Text = "Models loaded successfully";
            }
            catch (Exception ex)
            {
                Logger.Log($"[SegmentAnythingCT] Error loading models: {ex.Message}");
                statusLabel.Text = $"Error loading models: {ex.Message}";
            }
        }

        private void InitializeForm()
        {
            Logger.Log("[SegmentAnythingCT] Initializing form");
            samForm = new Form
            {
                Text = "Segment Anything - CT",
                Size = new Size(1100, 850),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.Sizable
            };

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

            // Create viewers with scrollbars
            xyPanel = CreateViewerPanel("XY Slice");
            xzPanel = CreateViewerPanel("XZ Slice");
            yzPanel = CreateViewerPanel("YZ Slice");

            // Create control panel
            controlPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                BackColor = Color.WhiteSmoke,
                AutoScroll = true
            };

            // Add components to control panel
            Label lblModelPath = new Label
            {
                Text = "Model Directory:",
                Location = new Point(10, 10),
                AutoSize = true
            };

            modelPathTextBox = new TextBox
            {
                Location = new Point(10, 30),
                Width = 280,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            Button btnBrowse = new Button
            {
                Text = "Browse...",
                Location = new Point(300, 29),
                Width = 80,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnBrowse.Click += (s, e) => BrowseForModelDirectory();

            // Device selection
            Label lblDevice = new Label
            {
                Text = "Execution Device:",
                Location = new Point(10, 60),
                AutoSize = true
            };

            rbCPU = new RadioButton
            {
                Text = "CPU",
                Location = new Point(10, 80),
                Checked = true,
                AutoSize = true
            };

            rbGPU = new RadioButton
            {
                Text = "GPU",
                Location = new Point(80, 80),
                AutoSize = true
            };

            // Add action buttons
            btnLoadModel = new Button
            {
                Text = "Load Models",
                Location = new Point(10, 110),
                Width = 100,
                Height = 30
            };
            btnLoadModel.Click += (s, e) => LoadONNXModels();

            // Add prompt buttons
            btnPositivePrompt = new Button
            {
                Text = "Positive Prompt (+)",
                Location = new Point(10, 150),
                Width = 140,
                Height = 30,
                BackColor = Color.LightGreen
            };
            btnPositivePrompt.Click += (s, e) => {
                currentMode = PromptMode.Positive;
                btnPositivePrompt.BackColor = Color.LightGreen;
                btnNegativePrompt.BackColor = SystemColors.Control;
                Logger.Log("[SegmentAnythingCT] Switched to positive prompt mode");
            };

            btnNegativePrompt = new Button
            {
                Text = "Negative Prompt (-)",
                Location = new Point(160, 150),
                Width = 140,
                Height = 30
            };
            btnNegativePrompt.Click += (s, e) => {
                currentMode = PromptMode.Negative;
                btnPositivePrompt.BackColor = SystemColors.Control;
                btnNegativePrompt.BackColor = Color.LightPink;
                Logger.Log("[SegmentAnythingCT] Switched to negative prompt mode");
            };

            chkAutoUpdate = new CheckBox
            {
                Text = "Auto-update when annotations change",
                Location = new Point(10, 190),
                AutoSize = true,
                Checked = true
            };

            btnApply = new Button
            {
                Text = "Apply Mask",
                Location = new Point(10, 220),
                Width = 100,
                Height = 30
            };
            btnApply.Click += (s, e) => ApplySegmentationMask();

            btnClose = new Button
            {
                Text = "Close",
                Location = new Point(120, 220),
                Width = 100,
                Height = 30
            };
            btnClose.Click += (s, e) => samForm.Close();

            statusLabel = new Label
            {
                Text = "Ready",
                Location = new Point(10, 260),
                AutoSize = true
            };

            // Add slice controls
            // XY Slice
            lblSliceXY = new Label
            {
                Text = $"XY Slice: {xySlice} / {(mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0)}",
                Location = new Point(10, 300),
                AutoSize = true
            };

            sliderXY = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0,
                Value = xySlice,
                Location = new Point(10, 320),
                Width = 220,
                TickStyle = TickStyle.None
            };

            numXY = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderXY.Maximum,
                Value = xySlice,
                Location = new Point(240, 320),
                Width = 60
            };

            // Add event handlers for XY slice controls
            sliderXY.Scroll += (s, e) => {
                xySlice = sliderXY.Value;
                UpdateSliceControls();
                UpdateViewers();
            };

            numXY.ValueChanged += (s, e) => {
                if (numXY.Value != xySlice)
                {
                    xySlice = (int)numXY.Value;
                    UpdateSliceControls();
                    UpdateViewers();
                }
            };

            // XZ Slice
            lblSliceXZ = new Label
            {
                Text = $"XZ Row: {xzRow} / {(mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 0)}",
                Location = new Point(10, 350),
                AutoSize = true
            };

            sliderXZ = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 0,
                Value = xzRow,
                Location = new Point(10, 370),
                Width = 220,
                TickStyle = TickStyle.None
            };

            numXZ = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderXZ.Maximum,
                Value = xzRow,
                Location = new Point(240, 370),
                Width = 60
            };

            // Add event handlers for XZ slice controls
            sliderXZ.Scroll += (s, e) => {
                xzRow = sliderXZ.Value;
                UpdateSliceControls();
                UpdateViewers();
            };

            numXZ.ValueChanged += (s, e) => {
                if (numXZ.Value != xzRow)
                {
                    xzRow = (int)numXZ.Value;
                    UpdateSliceControls();
                    UpdateViewers();
                }
            };

            // YZ Slice
            lblSliceYZ = new Label
            {
                Text = $"YZ Column: {yzCol} / {(mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 0)}",
                Location = new Point(10, 400),
                AutoSize = true
            };

            sliderYZ = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 0,
                Value = yzCol,
                Location = new Point(10, 420),
                Width = 220,
                TickStyle = TickStyle.None
            };

            numYZ = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderYZ.Maximum,
                Value = yzCol,
                Location = new Point(240, 420),
                Width = 60
            };

            // Add event handlers for YZ slice controls
            sliderYZ.Scroll += (s, e) => {
                yzCol = sliderYZ.Value;
                UpdateSliceControls();
                UpdateViewers();
            };

            numYZ.ValueChanged += (s, e) => {
                if (numYZ.Value != yzCol)
                {
                    yzCol = (int)numYZ.Value;
                    UpdateSliceControls();
                    UpdateViewers();
                }
            };

            // Add sync checkbox
            chkSyncWithMainView = new CheckBox
            {
                Text = "Sync with main view",
                Location = new Point(10, 460),
                Checked = true,
                AutoSize = true
            };

            chkSyncWithMainView.CheckedChanged += (s, e) => {
                // If synchronization is turned on, update the main view
                if (chkSyncWithMainView.Checked)
                {
                    mainForm.CurrentSlice = xySlice;
                    mainForm.XzSliceY = xzRow;
                    mainForm.YzSliceX = yzCol;
                }
            };

            // Add information about using the tool
            Label lblHelp = new Label
            {
                Text = "Instructions:\n" +
                      "- Click on the image to add points\n" +
                      "- Use '+' for foreground, '-' for background points\n" +
                      "- Click on existing points to remove them\n" +
                      "- Use mousewheel to zoom, drag to pan\n" +
                      "- Click 'Apply Mask' to save the segmentation",
                Location = new Point(10, 490),
                Size = new Size(300, 130),
                BorderStyle = BorderStyle.FixedSingle
            };

            // Selected material information
            Label lblMaterial = new Label
            {
                Text = $"Selected Material: {selectedMaterial.Name}",
                Location = new Point(10, 630),
                AutoSize = true,
                ForeColor = selectedMaterial.Color
            };

            // Add controls to panel
            controlPanel.Controls.AddRange(new Control[] {
                lblModelPath, modelPathTextBox, btnBrowse,
                lblDevice, rbCPU, rbGPU,
                btnLoadModel,
                btnPositivePrompt, btnNegativePrompt,
                chkAutoUpdate,
                btnApply, btnClose, statusLabel,
                lblSliceXY, sliderXY, numXY,
                lblSliceXZ, sliderXZ, numXZ,
                lblSliceYZ, sliderYZ, numYZ,
                chkSyncWithMainView,
                lblHelp, lblMaterial
            });

            // Add all components to main layout
            mainLayout.Controls.Add(xyPanel, 0, 0);
            mainLayout.Controls.Add(yzPanel, 1, 0);
            mainLayout.Controls.Add(xzPanel, 0, 1);
            mainLayout.Controls.Add(controlPanel, 1, 1);

            samForm.Controls.Add(mainLayout);

            // Handle form events
            samForm.FormClosing += (s, e) => {
                // Clean up resources
                encoderSession?.Dispose();
                decoderSession?.Dispose();
                xyViewer.Image?.Dispose();
                xzViewer.Image?.Dispose();
                yzViewer.Image?.Dispose();

                // Unregister callback
                mainForm.UnregisterSliceChangeCallback(sliceChangeCallback);

                Logger.Log("[SegmentAnythingCT] Form closing, resources cleaned up");
            };

            // Initially load the slices
            UpdateViewers();
        }

        private Panel CreateViewerPanel(string title)
        {
            Panel container = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(5),
                BorderStyle = BorderStyle.FixedSingle
            };

            Label titleLabel = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                BackColor = Color.Black,
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 25
            };

            PictureBox viewer = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Normal,
                BackColor = Color.Black
            };

            HScrollBar hScroll = new HScrollBar
            {
                Dock = DockStyle.Bottom,
                Height = 20
            };

            VScrollBar vScroll = new VScrollBar
            {
                Dock = DockStyle.Right,
                Width = 20
            };

            // Store the created controls in class variables
            if (title == "XY Slice")
            {
                xyViewer = viewer;
                xyHScroll = hScroll;
                xyVScroll = vScroll;

                // Setup events for XY viewer
                SetupXYViewerEvents();
            }
            else if (title == "XZ Slice")
            {
                xzViewer = viewer;
                xzHScroll = hScroll;
                xzVScroll = vScroll;

                // Setup events for XZ viewer
                SetupXZViewerEvents();
            }
            else if (title == "YZ Slice")
            {
                yzViewer = viewer;
                yzHScroll = hScroll;
                yzVScroll = vScroll;

                // Setup events for YZ viewer
                SetupYZViewerEvents();
            }

            container.Controls.Add(viewer);
            container.Controls.Add(hScroll);
            container.Controls.Add(vScroll);
            container.Controls.Add(titleLabel);

            return container;
        }

        private void SetupXYViewerEvents()
        {
            // XY viewer scroll events
            xyHScroll.Scroll += (s, e) => {
                xyPan.X = -xyHScroll.Value;
                xyViewer.Invalidate();
            };

            xyVScroll.Scroll += (s, e) => {
                xyPan.Y = -xyVScroll.Value;
                xyViewer.Invalidate();
            };

            // XY viewer mouse wheel for zooming
            xyViewer.MouseWheel += (s, e) => {
                float oldZoom = xyZoom;
                // Adjust zoom based on wheel direction
                if (e.Delta > 0)
                    xyZoom = Math.Min(10.0f, xyZoom * 1.1f);
                else
                    xyZoom = Math.Max(0.1f, xyZoom * 0.9f);

                // Adjust scrollbars based on new zoom
                UpdateXYScrollbars();

                // Redraw
                xyViewer.Invalidate();
                Logger.Log($"[SegmentAnythingCT] XY zoom changed to {xyZoom:F2}");
            };

            // XY viewer mouse events for panning and point placement
            Point lastPos = Point.Empty;
            bool isPanning = false;

            xyViewer.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Left)
                {
                    // Convert mouse coordinates to image coordinates
                    float pointX = (e.X - xyPan.X) / xyZoom;
                    float pointY = (e.Y - xyPan.Y) / xyZoom;

                    // Check if within image bounds
                    if (pointX >= 0 && pointX < mainForm.GetWidth() &&
                        pointY >= 0 && pointY < mainForm.GetHeight())
                    {
                        bool pointDeleted = false;
                        var points = annotationManager.GetPointsForSlice(xySlice);
                        foreach (var point in points.ToList())
                        {
                            float dx = point.X - pointX;
                            float dy = point.Y - pointY;
                            float distSq = dx * dx + dy * dy;

                            // If within 10 pixels of a point, delete it
                            if (distSq < 100)
                            { // 10^2
                                annotationManager.RemovePoint(point.ID);
                                // Also remove from our types dictionary
                                if (pointTypes.ContainsKey(point.ID))
                                    pointTypes.Remove(point.ID);
                                pointDeleted = true;
                                Logger.Log($"[SegmentAnythingCT] Deleted point at ({point.X}, {point.Y})");
                                break;
                            }
                        }

                        // If we didn't delete a point, add a new one
                        if (!pointDeleted)
                        {
                            // Use positive/negative as the label prefix to differentiate
                            string label = currentMode == PromptMode.Positive ?
                                "Pos_" + selectedMaterial.Name :
                                "Neg_" + selectedMaterial.Name;

                            // Add the point
                            AnnotationPoint newPoint = annotationManager.AddPoint(pointX, pointY, xySlice, label);

                            // Track whether this is a positive or negative point in our dictionary
                            pointTypes[newPoint.ID] = (currentMode == PromptMode.Positive);

                            Logger.Log($"[SegmentAnythingCT] Added {(currentMode == PromptMode.Positive ? "positive" : "negative")} point at ({pointX}, {pointY})");

                            // Auto-update segmentation if enabled
                            if (chkAutoUpdate.Checked)
                            {
                                Task.Run(() => PerformSegmentation());
                            }
                        }

                        // Update view with new or deleted point
                        UpdateViewers();
                    }
                    else
                    {
                        Logger.Log("[SegmentAnythingCT] Attempted to place point outside image bounds");
                    }
                }
                else if (e.Button == MouseButtons.Right)
                {
                    // Start panning with right mouse button
                    isPanning = true;
                    lastPos = e.Location;
                }
            };

            xyViewer.MouseMove += (s, e) => {
                if (isPanning && e.Button == MouseButtons.Right)
                {
                    // Calculate the move delta
                    int dx = e.X - lastPos.X;
                    int dy = e.Y - lastPos.Y;

                    // Update the panel position
                    xyPan.X += dx;
                    xyPan.Y += dy;
                    UpdateXYScrollbars();

                    lastPos = e.Location;
                    xyViewer.Invalidate();
                }
            };

            xyViewer.MouseUp += (s, e) => {
                if (e.Button == MouseButtons.Right)
                {
                    isPanning = false;
                }
            };

            // Paint event for custom rendering with checkerboard background
            xyViewer.Paint += (s, e) => {
                // Clear background
                e.Graphics.Clear(Color.Black);

                if (xyViewer.Image != null)
                {
                    int imgWidth = xyViewer.Image.Width;
                    int imgHeight = xyViewer.Image.Height;

                    // Calculate the image bounds
                    xyImageBounds = new Rectangle(
                        xyPan.X,
                        xyPan.Y,
                        (int)(imgWidth * xyZoom),
                        (int)(imgHeight * xyZoom));

                    // Draw checkerboard pattern for the entire visible area
                    DrawCheckerboardBackground(e.Graphics, xyViewer.ClientRectangle);

                    // Draw the image with interpolation
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(xyViewer.Image, xyImageBounds);

                    // Draw a border around the image
                    using (Pen borderPen = new Pen(Color.DarkGray, 1))
                    {
                        e.Graphics.DrawRectangle(borderPen, xyImageBounds);
                    }

                    // Draw annotations
                    DrawAnnotationsOnXY(e.Graphics);
                }
            };
        }

        private void SetupXZViewerEvents()
        {
            // XZ viewer scroll events
            xzHScroll.Scroll += (s, e) => {
                xzPan.X = -xzHScroll.Value;
                xzViewer.Invalidate();
            };

            xzVScroll.Scroll += (s, e) => {
                xzPan.Y = -xzVScroll.Value;
                xzViewer.Invalidate();
            };

            // XZ viewer mouse wheel for zooming
            xzViewer.MouseWheel += (s, e) => {
                float oldZoom = xzZoom;
                // Adjust zoom based on wheel direction
                if (e.Delta > 0)
                    xzZoom = Math.Min(10.0f, xzZoom * 1.1f);
                else
                    xzZoom = Math.Max(0.1f, xzZoom * 0.9f);

                // Adjust scrollbars based on new zoom
                UpdateXZScrollbars();

                // Redraw
                xzViewer.Invalidate();
                Logger.Log($"[SegmentAnythingCT] XZ zoom changed to {xzZoom:F2}");
            };

            // XZ viewer mouse events for panning
            Point lastPos = Point.Empty;
            bool isPanning = false;

            xzViewer.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Right)
                {
                    // Start panning with right mouse button
                    isPanning = true;
                    lastPos = e.Location;
                }
            };

            xzViewer.MouseMove += (s, e) => {
                if (isPanning && e.Button == MouseButtons.Right)
                {
                    // Calculate the move delta
                    int dx = e.X - lastPos.X;
                    int dy = e.Y - lastPos.Y;

                    // Update the panel position
                    xzPan.X += dx;
                    xzPan.Y += dy;
                    UpdateXZScrollbars();

                    lastPos = e.Location;
                    xzViewer.Invalidate();
                }
            };

            xzViewer.MouseUp += (s, e) => {
                if (e.Button == MouseButtons.Right)
                {
                    isPanning = false;
                }
            };

            // Paint event for custom rendering with checkerboard background
            xzViewer.Paint += (s, e) => {
                // Clear background
                e.Graphics.Clear(Color.Black);

                if (xzViewer.Image != null)
                {
                    int imgWidth = xzViewer.Image.Width;
                    int imgHeight = xzViewer.Image.Height;

                    // Calculate the image bounds
                    xzImageBounds = new Rectangle(
                        xzPan.X,
                        xzPan.Y,
                        (int)(imgWidth * xzZoom),
                        (int)(imgHeight * xzZoom));

                    // Draw checkerboard pattern for the entire visible area
                    DrawCheckerboardBackground(e.Graphics, xzViewer.ClientRectangle);

                    // Draw the image with interpolation
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(xzViewer.Image, xzImageBounds);

                    // Draw a border around the image
                    using (Pen borderPen = new Pen(Color.DarkGray, 1))
                    {
                        e.Graphics.DrawRectangle(borderPen, xzImageBounds);
                    }
                }
            };
        }

        private void SetupYZViewerEvents()
        {
            // YZ viewer scroll events
            yzHScroll.Scroll += (s, e) => {
                yzPan.X = -yzHScroll.Value;
                yzViewer.Invalidate();
            };

            yzVScroll.Scroll += (s, e) => {
                yzPan.Y = -yzVScroll.Value;
                yzViewer.Invalidate();
            };

            // YZ viewer mouse wheel for zooming
            yzViewer.MouseWheel += (s, e) => {
                float oldZoom = yzZoom;
                // Adjust zoom based on wheel direction
                if (e.Delta > 0)
                    yzZoom = Math.Min(10.0f, yzZoom * 1.1f);
                else
                    yzZoom = Math.Max(0.1f, yzZoom * 0.9f);

                // Adjust scrollbars based on new zoom
                UpdateYZScrollbars();

                // Redraw
                yzViewer.Invalidate();
                Logger.Log($"[SegmentAnythingCT] YZ zoom changed to {yzZoom:F2}");
            };

            // YZ viewer mouse events for panning
            Point lastPos = Point.Empty;
            bool isPanning = false;

            yzViewer.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Right)
                {
                    // Start panning with right mouse button
                    isPanning = true;
                    lastPos = e.Location;
                }
            };

            yzViewer.MouseMove += (s, e) => {
                if (isPanning && e.Button == MouseButtons.Right)
                {
                    // Calculate the move delta
                    int dx = e.X - lastPos.X;
                    int dy = e.Y - lastPos.Y;

                    // Update the panel position
                    yzPan.X += dx;
                    yzPan.Y += dy;
                    UpdateYZScrollbars();

                    lastPos = e.Location;
                    yzViewer.Invalidate();
                }
            };

            yzViewer.MouseUp += (s, e) => {
                if (e.Button == MouseButtons.Right)
                {
                    isPanning = false;
                }
            };

            // Paint event for custom rendering with checkerboard background
            yzViewer.Paint += (s, e) => {
                // Clear background
                e.Graphics.Clear(Color.Black);

                if (yzViewer.Image != null)
                {
                    int imgWidth = yzViewer.Image.Width;
                    int imgHeight = yzViewer.Image.Height;

                    // Calculate the image bounds
                    yzImageBounds = new Rectangle(
                        yzPan.X,
                        yzPan.Y,
                        (int)(imgWidth * yzZoom),
                        (int)(imgHeight * yzZoom));

                    // Draw checkerboard pattern for the entire visible area
                    DrawCheckerboardBackground(e.Graphics, yzViewer.ClientRectangle);

                    // Draw the image with interpolation
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(yzViewer.Image, yzImageBounds);

                    // Draw a border around the image
                    using (Pen borderPen = new Pen(Color.DarkGray, 1))
                    {
                        e.Graphics.DrawRectangle(borderPen, yzImageBounds);
                    }
                }
            };
        }

        private void DrawCheckerboardBackground(Graphics g, Rectangle bounds)
        {
            int cellSize = 10; // Size of checkerboard cells

            using (Brush darkBrush = new SolidBrush(Color.FromArgb(30, 30, 30)))
            using (Brush lightBrush = new SolidBrush(Color.FromArgb(50, 50, 50)))
            {
                for (int x = 0; x < bounds.Width; x += cellSize)
                {
                    for (int y = 0; y < bounds.Height; y += cellSize)
                    {
                        // Alternate colors
                        Brush brush = ((x / cellSize + y / cellSize) % 2 == 0) ? darkBrush : lightBrush;
                        g.FillRectangle(brush, x, y, cellSize, cellSize);
                    }
                }
            }
        }

        private void UpdateXYScrollbars()
        {
            if (xyViewer.Image != null)
            {
                int imageWidth = (int)(xyViewer.Image.Width * xyZoom);
                int imageHeight = (int)(xyViewer.Image.Height * xyZoom);

                xyHScroll.Maximum = Math.Max(0, imageWidth - xyViewer.ClientSize.Width + xyHScroll.LargeChange);
                xyVScroll.Maximum = Math.Max(0, imageHeight - xyViewer.ClientSize.Height + xyVScroll.LargeChange);

                xyHScroll.Value = Math.Min(xyHScroll.Maximum, -xyPan.X);
                xyVScroll.Value = Math.Min(xyVScroll.Maximum, -xyPan.Y);
            }
        }

        private void UpdateXZScrollbars()
        {
            if (xzViewer.Image != null)
            {
                int imageWidth = (int)(xzViewer.Image.Width * xzZoom);
                int imageHeight = (int)(xzViewer.Image.Height * xzZoom);

                xzHScroll.Maximum = Math.Max(0, imageWidth - xzViewer.ClientSize.Width + xzHScroll.LargeChange);
                xzVScroll.Maximum = Math.Max(0, imageHeight - xzViewer.ClientSize.Height + xzVScroll.LargeChange);

                xzHScroll.Value = Math.Min(xzHScroll.Maximum, -xzPan.X);
                xzVScroll.Value = Math.Min(xzVScroll.Maximum, -xzPan.Y);
            }
        }

        private void UpdateYZScrollbars()
        {
            if (yzViewer.Image != null)
            {
                int imageWidth = (int)(yzViewer.Image.Width * yzZoom);
                int imageHeight = (int)(yzViewer.Image.Height * yzZoom);

                yzHScroll.Maximum = Math.Max(0, imageWidth - yzViewer.ClientSize.Width + yzHScroll.LargeChange);
                yzVScroll.Maximum = Math.Max(0, imageHeight - yzViewer.ClientSize.Height + yzVScroll.LargeChange);

                yzHScroll.Value = Math.Min(yzHScroll.Maximum, -yzPan.X);
                yzVScroll.Value = Math.Min(yzVScroll.Maximum, -yzPan.Y);
            }
        }

        private void DrawAnnotationsOnXY(Graphics g)
        {
            // Draw annotations
            var points = annotationManager.GetPointsForSlice(xySlice);

            foreach (var point in points)
            {
                // Get position with zoom and pan applied
                float x = point.X * xyZoom + xyPan.X;
                float y = point.Y * xyZoom + xyPan.Y;

                // Determine color based on point type
                bool isPositive = point.Type == "Positive"; // Check the actual type value
                Color pointColor = isPositive ? Color.Green : Color.Red;

                // Draw larger point circle (10 pixel radius)
                int radius = 10;
                g.FillEllipse(new SolidBrush(Color.FromArgb(128, pointColor)),
                    x - radius, y - radius, radius * 2, radius * 2);
                g.DrawEllipse(new Pen(pointColor, 2),
                    x - radius, y - radius, radius * 2, radius * 2);

                // Draw ID number
                g.DrawString(point.ID.ToString(), new Font("Arial", 8), Brushes.White, x + radius, y + radius);
            }
        }


        private void BrowseForModelDirectory()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the directory containing SAM ONNX models";
                dialog.ShowNewFolderButton = false;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    modelPathTextBox.Text = dialog.SelectedPath;

                    // Update paths
                    encoderPath = Path.Combine(dialog.SelectedPath, "sam2.1_large.encoder.onnx");
                    decoderPath = Path.Combine(dialog.SelectedPath, "sam2.1_large.decoder.onnx");

                    Logger.Log($"[SegmentAnythingCT] Model directory set to: {dialog.SelectedPath}");
                }
            }
        }

        private void LoadONNXModels()
        {
            try
            {
                // Dispose existing sessions if any
                encoderSession?.Dispose();
                decoderSession?.Dispose();

                // Verify files exist
                if (!File.Exists(encoderPath))
                {
                    MessageBox.Show($"Encoder model not found at: {encoderPath}");
                    Logger.Log($"[SegmentAnythingCT] Encoder model not found at: {encoderPath}");
                    return;
                }

                if (!File.Exists(decoderPath))
                {
                    MessageBox.Show($"Decoder model not found at: {decoderPath}");
                    Logger.Log($"[SegmentAnythingCT] Decoder model not found at: {decoderPath}");
                    return;
                }

                // Create session options based on selected device
                useGPU = rbGPU.Checked;
                SessionOptions options = new SessionOptions();

                if (useGPU)
                {
                    // Configure for GPU
                    options.AppendExecutionProvider_CUDA();
                    options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    Logger.Log("[SegmentAnythingCT] Using GPU execution provider");
                }
                else
                {
                    Logger.Log("[SegmentAnythingCT] Using CPU execution provider");
                }

                // Create sessions
                encoderSession = new InferenceSession(encoderPath, options);
                decoderSession = new InferenceSession(decoderPath, options);

                Logger.Log("[SegmentAnythingCT] Models loaded successfully");
                statusLabel.Text = "Models loaded successfully";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading models: {ex.Message}");
                statusLabel.Text = $"Error: {ex.Message}";
                Logger.Log($"[SegmentAnythingCT] Error loading models: {ex.Message}");
            }
        }

        private void UpdateSliceControls()
        {
            // Update XY slice controls
            if (lblSliceXY != null)
                lblSliceXY.Text = $"XY Slice: {xySlice} / {sliderXY.Maximum}";

            if (sliderXY != null && sliderXY.Value != xySlice)
                sliderXY.Value = xySlice;

            if (numXY != null && numXY.Value != xySlice)
                numXY.Value = xySlice;

            // Update XZ slice (row) controls
            if (lblSliceXZ != null)
                lblSliceXZ.Text = $"XZ Row: {xzRow} / {sliderXZ.Maximum}";

            if (sliderXZ != null && sliderXZ.Value != xzRow)
                sliderXZ.Value = xzRow;

            if (numXZ != null && numXZ.Value != xzRow)
                numXZ.Value = xzRow;

            // Update YZ slice (column) controls
            if (lblSliceYZ != null)
                lblSliceYZ.Text = $"YZ Column: {yzCol} / {sliderYZ.Maximum}";

            if (sliderYZ != null && sliderYZ.Value != yzCol)
                sliderYZ.Value = yzCol;

            if (numYZ != null && numYZ.Value != yzCol)
                numYZ.Value = yzCol;

            // If sync with main view is enabled, update the main form
            if (chkSyncWithMainView != null && chkSyncWithMainView.Checked)
            {
                mainForm.CurrentSlice = xySlice;
                mainForm.XzSliceY = xzRow;
                mainForm.YzSliceX = yzCol;
            }
        }

        public void UpdateSliceFromMainForm(int newSlice)
        {
            if (newSlice != xySlice && newSlice >= 0 && newSlice < mainForm.GetDepth())
            {
                xySlice = newSlice;
                UpdateSliceControls();
                UpdateViewers();
            }
        }

        public void UpdateXZFromMainForm(int newRow)
        {
            if (newRow != xzRow && newRow >= 0 && newRow < mainForm.GetHeight())
            {
                xzRow = newRow;
                UpdateSliceControls();
                UpdateViewers();
            }
        }

        public void UpdateYZFromMainForm(int newCol)
        {
            if (newCol != yzCol && newCol >= 0 && newCol < mainForm.GetWidth())
            {
                yzCol = newCol;
                UpdateSliceControls();
                UpdateViewers();
            }
        }

        private void UpdateViewers()
        {
            // Update XY viewer
            using (Bitmap xySliceBitmap = CreateSliceBitmap(xySlice))
            {
                if (xyViewer.Image != null)
                    xyViewer.Image.Dispose();

                xyViewer.Image = new Bitmap(xySliceBitmap);
                UpdateXYScrollbars();
            }

            // Update XZ viewer
            using (Bitmap xzSliceBitmap = CreateXZSliceBitmap(xzRow))
            {
                if (xzViewer.Image != null)
                    xzViewer.Image.Dispose();

                xzViewer.Image = new Bitmap(xzSliceBitmap);
                UpdateXZScrollbars();
            }

            // Update YZ viewer
            using (Bitmap yzSliceBitmap = CreateYZSliceBitmap(yzCol))
            {
                if (yzViewer.Image != null)
                    yzViewer.Image.Dispose();

                yzViewer.Image = new Bitmap(yzSliceBitmap);
                UpdateYZScrollbars();
            }

            // Repaint all viewers
            xyViewer.Invalidate();
            xzViewer.Invalidate();
            yzViewer.Invalidate();
        }

        private unsafe Bitmap CreateSliceBitmap(int sliceZ)
        {
            int w = mainForm.GetWidth();
            int h = mainForm.GetHeight();

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            int bytesPerPixel = 3; // RGB

            byte* ptr = (byte*)bmpData.Scan0;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte val = mainForm.volumeData[x, y, sliceZ];
                    int offset = y * stride + x * bytesPerPixel;

                    // RGB = same value for grayscale
                    ptr[offset] = val;     // Blue
                    ptr[offset + 1] = val; // Green
                    ptr[offset + 2] = val; // Red

                    // Overlay mask if available
                    if (segmentationMask != null && x < segmentationMask.GetLength(0) && y < segmentationMask.GetLength(1) && segmentationMask[x, y] > 0)
                    {
                        // Simple overlay with material color at 50% opacity
                        Color matColor = selectedMaterial.Color;
                        ptr[offset] = (byte)((val + matColor.B) / 2);     // Blue
                        ptr[offset + 1] = (byte)((val + matColor.G) / 2); // Green
                        ptr[offset + 2] = (byte)((val + matColor.R) / 2); // Red
                    }
                }
            }

            bmp.UnlockBits(bmpData);
            return bmp;
        }

        private unsafe Bitmap CreateXZSliceBitmap(int sliceY)
        {
            int w = mainForm.GetWidth();
            int d = mainForm.GetDepth();

            Bitmap bmp = new Bitmap(w, d, PixelFormat.Format24bppRgb);
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, w, d),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            int bytesPerPixel = 3; // RGB

            byte* ptr = (byte*)bmpData.Scan0;

            for (int z = 0; z < d; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte val = mainForm.volumeData[x, sliceY, z];
                    int offset = z * stride + x * bytesPerPixel;

                    // RGB = same value for grayscale
                    ptr[offset] = val;     // Blue
                    ptr[offset + 1] = val; // Green
                    ptr[offset + 2] = val; // Red
                }
            }

            // Mark the current XY slice position
            int xyLine = xySlice;
            if (xyLine >= 0 && xyLine < d)
            {
                for (int x = 0; x < w; x++)
                {
                    int offset = xyLine * stride + x * bytesPerPixel;

                    // Bright yellow line
                    ptr[offset] = 0;       // Blue
                    ptr[offset + 1] = 255; // Green
                    ptr[offset + 2] = 255; // Red
                }
            }

            bmp.UnlockBits(bmpData);
            return bmp;
        }

        private unsafe Bitmap CreateYZSliceBitmap(int sliceX)
        {
            int h = mainForm.GetHeight();
            int d = mainForm.GetDepth();

            Bitmap bmp = new Bitmap(d, h, PixelFormat.Format24bppRgb);
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, d, h),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            int bytesPerPixel = 3; // RGB

            byte* ptr = (byte*)bmpData.Scan0;

            for (int y = 0; y < h; y++)
            {
                for (int z = 0; z < d; z++)
                {
                    byte val = mainForm.volumeData[sliceX, y, z];
                    int offset = y * stride + z * bytesPerPixel;

                    // RGB = same value for grayscale
                    ptr[offset] = val;     // Blue
                    ptr[offset + 1] = val; // Green
                    ptr[offset + 2] = val; // Red
                }
            }

            // Mark the current XY slice position
            int xyLine = xySlice;
            if (xyLine >= 0 && xyLine < d)
            {
                for (int y = 0; y < h; y++)
                {
                    int offset = y * stride + xyLine * bytesPerPixel;

                    // Bright yellow line
                    ptr[offset] = 0;       // Blue
                    ptr[offset + 1] = 255; // Green
                    ptr[offset + 2] = 255; // Red
                }
            }

            bmp.UnlockBits(bmpData);
            return bmp;
        }

        private unsafe Tensor<float> PreprocessImage()
        {
            // Create the image scaled to 1024x1024 for the encoder
            int w = mainForm.GetWidth();
            int h = mainForm.GetHeight();

            // Create a tensor with shape [1, 3, 1024, 1024]
            DenseTensor<float> inputTensor = new DenseTensor<float>(new[] { 1, 3, 1024, 1024 });

            // Create a resized version of the XY slice
            using (Bitmap sliceBitmap = CreateSliceBitmap(xySlice))
            using (Bitmap resized = new Bitmap(1024, 1024))
            {
                using (Graphics g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(sliceBitmap, 0, 0, 1024, 1024);
                }

                // Lock the bitmap and access its pixel data
                BitmapData bmpData = resized.LockBits(
                    new Rectangle(0, 0, resized.Width, resized.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);

                int stride = bmpData.Stride;
                int bytesPerPixel = 3; // RGB

                byte* ptr = (byte*)bmpData.Scan0;

                // Process pixels and normalize to range [0.0, 1.0]
                for (int y = 0; y < 1024; y++)
                {
                    for (int x = 0; x < 1024; x++)
                    {
                        int offset = y * stride + x * bytesPerPixel;

                        // BGR order (standard in Bitmap)
                        byte b = ptr[offset];
                        byte g = ptr[offset + 1];
                        byte r = ptr[offset + 2];

                        // Normalize to range [0.0, 1.0] and convert to RGB order for the model
                        inputTensor[0, 0, y, x] = r / 255.0f;
                        inputTensor[0, 1, y, x] = g / 255.0f;
                        inputTensor[0, 2, y, x] = b / 255.0f;
                    }
                }

                resized.UnlockBits(bmpData);
            }

            return inputTensor;
        }

        private async Task PerformSegmentation()
        {
            if (encoderSession == null || decoderSession == null)
            {
                MessageBox.Show("Models not loaded. Please load models first.");
                return;
            }

            var points = annotationManager.GetPointsForSlice(xySlice).ToList();
            if (points.Count == 0)
            {
                MessageBox.Show("Please add at least one annotation point.");
                return;
            }

            statusLabel.Text = "Segmenting...";
            Logger.Log("[SegmentAnythingCT] Starting segmentation");

            try
            {
                // Create a tensor with the image data
                Tensor<float> imageInput = await Task.Run(() => PreprocessImage());

                // Create input and run the encoder on a background thread
                var encoderInputs = new List<NamedOnnxValue> {
                    NamedOnnxValue.CreateFromTensor("image", imageInput)
                };

                var encoderOutputs = await Task.Run(() => encoderSession.Run(encoderInputs));

                try
                {
                    // Extract encoder outputs
                    var imageEmbed = encoderOutputs.First(x => x.Name == "image_embed").AsTensor<float>();
                    var highResFeats0 = encoderOutputs.First(x => x.Name == "high_res_feats_0").AsTensor<float>();
                    var highResFeats1 = encoderOutputs.First(x => x.Name == "high_res_feats_1").AsTensor<float>();

                    // Prepare point tensors
                    int numPoints = points.Count;
                    float scaleX = 1024.0f / mainForm.GetWidth();
                    float scaleY = 1024.0f / mainForm.GetHeight();
                    DenseTensor<float> pointCoords = new DenseTensor<float>(new[] { 1, numPoints, 2 });
                    DenseTensor<float> pointLabels = new DenseTensor<float>(new[] { 1, numPoints });

                    for (int i = 0; i < numPoints; i++)
                    {
                        float x = points[i].X * scaleX;
                        float y = points[i].Y * scaleY;
                        pointCoords[0, i, 0] = x;
                        pointCoords[0, i, 1] = y;

                        // Determine if positive or negative based on point type
                        bool isPositive = points[i].Type == "Positive";
                        pointLabels[0, i] = isPositive ? 1.0f : 0.0f;
                    }

                    // Rest of tensor preparation
                    DenseTensor<float> maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
                    DenseTensor<float> hasMaskInput = new DenseTensor<float>(new[] { 1 });
                    hasMaskInput[0] = 0;
                    DenseTensor<int> origImSize = new DenseTensor<int>(new[] { 2 });
                    origImSize[0] = mainForm.GetHeight();
                    origImSize[1] = mainForm.GetWidth();

                    // Run decoder on background thread
                    var decoderInputs = new List<NamedOnnxValue> {
                        NamedOnnxValue.CreateFromTensor("image_embed", imageEmbed),
                        NamedOnnxValue.CreateFromTensor("high_res_feats_0", highResFeats0),
                        NamedOnnxValue.CreateFromTensor("high_res_feats_1", highResFeats1),
                        NamedOnnxValue.CreateFromTensor("point_coords", pointCoords),
                        NamedOnnxValue.CreateFromTensor("point_labels", pointLabels),
                        NamedOnnxValue.CreateFromTensor("mask_input", maskInput),
                        NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInput),
                        NamedOnnxValue.CreateFromTensor("orig_im_size", origImSize)
                    };

                    var decoderOutputs = await Task.Run(() => decoderSession.Run(decoderInputs));

                    try
                    {
                        // Process decoder outputs
                        await Task.Run(() => {
                            var masks = decoderOutputs.First(x => x.Name == "masks").AsTensor<float>();
                            var iouPredictions = decoderOutputs.First(x => x.Name == "iou_predictions").AsTensor<float>();

                            int bestMaskIdx = 0;
                            float bestIoU = iouPredictions[0, 0];

                            for (int i = 1; i < iouPredictions.Dimensions[1]; i++)
                            {
                                if (iouPredictions[0, i] > bestIoU)
                                {
                                    bestIoU = iouPredictions[0, i];
                                    bestMaskIdx = i;
                                }
                            }

                            Logger.Log($"[SegmentAnythingCT] Best mask IoU: {bestIoU}");

                            int h = mainForm.GetHeight();
                            int w = mainForm.GetWidth();
                            segmentationMask = new byte[w, h];

                            for (int y = 0; y < h; y++)
                            {
                                for (int x = 0; x < w; x++)
                                {
                                    segmentationMask[x, y] = masks[0, bestMaskIdx, y, x] > 0.0f ? selectedMaterial.ID : (byte)0;
                                }
                            }
                        });

                        // Update UI on the UI thread
                        if (samForm.InvokeRequired)
                        {
                            samForm.Invoke(new Action(() => {
                                UpdateViewers();
                                statusLabel.Text = "Segmentation complete";
                            }));
                        }
                        else
                        {
                            UpdateViewers();
                            statusLabel.Text = "Segmentation complete";
                        }

                        Logger.Log("[SegmentAnythingCT] Segmentation complete");
                    }
                    finally
                    {
                        decoderOutputs.Dispose();
                    }
                }
                finally
                {
                    encoderOutputs.Dispose();
                }
            }
            catch (Exception ex)
            {
                if (samForm.InvokeRequired)
                {
                    samForm.Invoke(new Action(() => {
                        MessageBox.Show($"Error during segmentation: {ex.Message}");
                        statusLabel.Text = $"Error: {ex.Message}";
                    }));
                }
                else
                {
                    MessageBox.Show($"Error during segmentation: {ex.Message}");
                    statusLabel.Text = $"Error: {ex.Message}";
                }

                Logger.Log($"[SegmentAnythingCT] Segmentation error: {ex.Message}");
            }
        }

        private void ApplySegmentationMask()
        {
            if (segmentationMask == null)
            {
                MessageBox.Show("No segmentation result available.");
                return;
            }

            try
            {
                Logger.Log("[SegmentAnythingCT] Applying segmentation mask to volume labels");

                // Apply the mask to the MainForm's current slice
                for (int y = 0; y < mainForm.GetHeight(); y++)
                {
                    for (int x = 0; x < mainForm.GetWidth(); x++)
                    {
                        if (segmentationMask[x, y] > 0)
                        {
                            mainForm.volumeLabels[x, y, xySlice] = segmentationMask[x, y];
                        }
                    }
                }

                // Update the mainForm's temporary selection
                if (mainForm.currentSelection == null ||
                    mainForm.currentSelection.GetLength(0) != mainForm.GetWidth() ||
                    mainForm.currentSelection.GetLength(1) != mainForm.GetHeight())
                {
                    mainForm.currentSelection = new byte[mainForm.GetWidth(), mainForm.GetHeight()];
                }

                // Copy mask to current selection as well
                for (int y = 0; y < mainForm.GetHeight(); y++)
                {
                    for (int x = 0; x < mainForm.GetWidth(); x++)
                    {
                        if (segmentationMask[x, y] > 0)
                        {
                            mainForm.currentSelection[x, y] = segmentationMask[x, y];
                        }
                    }
                }

                // Update MainForm's view
                mainForm.RenderViews();
                mainForm.SaveLabelsChk();

                statusLabel.Text = "Mask applied successfully";
                Logger.Log("[SegmentAnythingCT] Mask applied successfully");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying mask: {ex.Message}");
                statusLabel.Text = $"Error: {ex.Message}";
                Logger.Log($"[SegmentAnythingCT] Error applying mask: {ex.Message}");
            }
        }

        public void Show()
        {
            samForm.Show();
        }
    }
}