using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Color = System.Drawing.Color;
using Matrix = System.Drawing.Drawing2D.Matrix;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
using Vector3 = SharpDX.Vector3;
using Vector4 = SharpDX.Vector4;

namespace CTSegmenter
{
    public partial class TransformDatasetForm : Form
    {
        private MainForm _mainForm;
        private ChunkedVolume _originalVolume;
        private ChunkedLabelVolume _originalLabels;

        // Current transformation parameters
        private Rectangle _cropRectXY;

        private Rectangle _cropRectXZ;
        private Rectangle _cropRectYZ;

        private double _rotationX = 0;
        private double _rotationY = 0;
        private double _rotationZ = 0;

        private double _scaleX = 1.0;
        private double _scaleY = 1.0;
        private double _scaleZ = 1.0;

        private int _translateX = 0;
        private int _translateY = 0;
        private int _translateZ = 0;

        // Cached original dimensions
        private int _origWidth;

        private int _origHeight;
        private int _origDepth;
        private double _origPixelSize;

        // Current dimensions based on transformations
        private int _newWidth;

        private int _newHeight;
        private int _newDepth;
        private double _newPixelSize;

        // LRU Cache for slice rendering
        private LRUCache<string, Bitmap> _sliceCache = new LRUCache<string, Bitmap>(10);

        // View controls
        private PictureBox _xyView;

        private PictureBox _xzView;
        private PictureBox _yzView;

        // Sliders
        private TrackBar _xySlider;

        private TrackBar _xzSlider;
        private TrackBar _yzSlider;

        // Numeric input fields
        private NumericUpDown _numXYSlice;

        private NumericUpDown _numXZSlice;
        private NumericUpDown _numYZSlice;

        // Transformation input fields
        private NumericUpDown _numRotX;

        private NumericUpDown _numRotY;
        private NumericUpDown _numRotZ;

        private NumericUpDown _numScaleX;
        private NumericUpDown _numScaleY;
        private NumericUpDown _numScaleZ;

        private NumericUpDown _numTranslateX;
        private NumericUpDown _numTranslateY;
        private NumericUpDown _numTranslateZ;

        // Dimension input fields
        private NumericUpDown _numNewWidth;

        private NumericUpDown _numNewHeight;
        private NumericUpDown _numNewDepth;
        private NumericUpDown _numNewPixelSize;

        // Action buttons
        private Button _btnReset;

        private Button _btnApplyToCurrentDataset;
        private Button _btnCreateNewDataset;

        // Interaction state
        private bool _isResizing = false;

        private bool _isDragging = false;
        private Point _lastMousePosition;
        private PictureBox _activeView;
        private ResizeHandle _activeHandle;

        // For progress tracking
        private ProgressBar _progressBar;

        private Label _lblProgress;

        // Current slice indices
        private int _currentXYSlice = 0;

        private int _currentXZSlice = 0;
        private int _currentYZSlice = 0;

        // Handles for resize operations
        private List<ResizeHandle> _xyHandles = new List<ResizeHandle>();

        private List<ResizeHandle> _xzHandles = new List<ResizeHandle>();
        private List<ResizeHandle> _yzHandles = new List<ResizeHandle>();

        // Rotation controls
        private bool _isRotating = false;

        private Point _rotationStartPoint;
        private TransformViewType _rotatingView = TransformViewType.XY;

        // Zoom and pan for each view
        private float _xyZoom = 1.0f;

        private float _xzZoom = 1.0f;
        private float _yzZoom = 1.0f;

        private PointF _xyPan = PointF.Empty;
        private PointF _xzPan = PointF.Empty;
        private PointF _yzPan = PointF.Empty;

        private Rotation3DControl _rotationControl;
        private bool _preserveDatasetDuringRotation = true;
        private CheckBox _chkPreserveDataset;

        // Cancellation token for rendering operations
        private CancellationTokenSource _renderCts = new CancellationTokenSource();

        public TransformDatasetForm(MainForm mainForm)
        {
            _mainForm = mainForm;

            // Store reference to original volume data
            _originalVolume = (ChunkedVolume)mainForm.volumeData;
            _originalLabels = (ChunkedLabelVolume)mainForm.volumeLabels;

            // Store original dimensions
            _origWidth = _originalVolume.Width;
            _origHeight = _originalVolume.Height;
            _origDepth = _originalVolume.Depth;
            _origPixelSize = mainForm.pixelSize;

            // Initialize new dimensions to match original
            _newWidth = _origWidth;
            _newHeight = _origHeight;
            _newDepth = _origDepth;
            _newPixelSize = _origPixelSize;

            // Initialize crop rectangles to full volume size
            _cropRectXY = new Rectangle(0, 0, _origWidth, _origHeight);
            _cropRectXZ = new Rectangle(0, 0, _origWidth, _origDepth);
            _cropRectYZ = new Rectangle(0, 0, _origDepth, _origHeight);

            InitializeComponent();
            InitializeResizeHandles();

            // Start at the middle slice of each view
            _currentXYSlice = _origDepth / 2;
            _currentXZSlice = _origHeight / 2;
            _currentYZSlice = _origWidth / 2;

            // Update sliders and numeric controls
            UpdateSliceControls();

            // Initial render of slice views
            RenderViews();

            Logger.Log("[TransformDatasetForm] Initialized with dataset dimensions: " +
                       $"{_origWidth}x{_origHeight}x{_origDepth}, PixelSize: {_origPixelSize}");
        }

        private void InitializeComponent()
        {
            // Form properties
            this.Text = "Transform Dataset";
            this.Size = new Size(1200, 800);
            this.Icon = _mainForm.Icon;
            this.StartPosition = FormStartPosition.CenterScreen;

            // Create main layout
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(5)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // ===============================================================
            // LEFT PANEL - VIEWS
            // ===============================================================
            TableLayoutPanel viewsPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
            };
            viewsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            viewsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            viewsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            viewsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            // Create XY View (Top Row, Spans 2 columns)
            Panel xyViewPanel = CreateViewPanel(TransformViewType.XY);
            viewsPanel.Controls.Add(xyViewPanel, 0, 0);
            viewsPanel.SetColumnSpan(xyViewPanel, 2);

            // Create XZ View (Bottom Left)
            Panel xzViewPanel = CreateViewPanel(TransformViewType.XZ);
            viewsPanel.Controls.Add(xzViewPanel, 0, 1);

            // Create YZ View (Bottom Right)
            Panel yzViewPanel = CreateViewPanel(TransformViewType.YZ);
            viewsPanel.Controls.Add(yzViewPanel, 1, 1);

            // ===============================================================
            // RIGHT PANEL - CONTROLS
            // ===============================================================
            Panel controlsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };

            FlowLayoutPanel transformControls = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoScroll = true,
                WrapContents = false,
                Padding = new Padding(5)
            };

            // Add Rotation Group
            GroupBox rotationGroup = CreateGroupBox("Rotation (degrees)", 220, 120);
            TableLayoutPanel rotationLayout = CreateControlLayout(2, 3);

            AddLabelAndNumeric(rotationLayout, "X-Axis:", _numRotX = CreateNumericUpDown(-360, 360, 0.1m, 0, 1), 0);
            AddLabelAndNumeric(rotationLayout, "Y-Axis:", _numRotY = CreateNumericUpDown(-360, 360, 0.1m, 0, 1), 1);
            AddLabelAndNumeric(rotationLayout, "Z-Axis:", _numRotZ = CreateNumericUpDown(-360, 360, 0.1m, 0, 1), 2);

            rotationGroup.Controls.Add(rotationLayout);

            // Add Rotation Options Group
            GroupBox rotationOptionsGroup = new GroupBox { Text = "Rotation Options", Width = 220, Height = 80 }; // Increased height from 70 to 80
            TableLayoutPanel rotationOptionsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 1,
                Padding = new Padding(5)
            };

            _chkPreserveDataset = new CheckBox
            {
                Text = "Preserve entire dataset when rotating",
                Checked = true,
                AutoSize = true,
                MaximumSize = new Size(200, 60), // Set maximum width to force wrapping
                Dock = DockStyle.Fill
            };
            _chkPreserveDataset.CheckedChanged += (s, e) =>
            {
                _preserveDatasetDuringRotation = _chkPreserveDataset.Checked;
                UpdatePreview();
            };

            rotationOptionsLayout.Controls.Add(_chkPreserveDataset, 0, 0);
            rotationOptionsGroup.Controls.Add(rotationOptionsLayout);

            // Add Scale Group
            GroupBox scaleGroup = CreateGroupBox("Scale", 220, 120);
            TableLayoutPanel scaleLayout = CreateControlLayout(2, 3);

            AddLabelAndNumeric(scaleLayout, "X-Axis:", _numScaleX = CreateNumericUpDown(0.1m, 10.0m, 0.05m, 1.0m, 2), 0);
            AddLabelAndNumeric(scaleLayout, "Y-Axis:", _numScaleY = CreateNumericUpDown(0.1m, 10.0m, 0.05m, 1.0m, 2), 1);
            AddLabelAndNumeric(scaleLayout, "Z-Axis:", _numScaleZ = CreateNumericUpDown(0.1m, 10.0m, 0.05m, 1.0m, 2), 2);

            scaleGroup.Controls.Add(scaleLayout);

            // Add Translation Group
            GroupBox translateGroup = CreateGroupBox("Translation (pixels)", 220, 120);
            TableLayoutPanel translateLayout = CreateControlLayout(2, 3);

            AddLabelAndNumeric(translateLayout, "X-Axis:", _numTranslateX = CreateNumericUpDown(-1000, 1000, 1, 0, 0), 0);
            AddLabelAndNumeric(translateLayout, "Y-Axis:", _numTranslateY = CreateNumericUpDown(-1000, 1000, 1, 0, 0), 1);
            AddLabelAndNumeric(translateLayout, "Z-Axis:", _numTranslateZ = CreateNumericUpDown(-1000, 1000, 1, 0, 0), 2);

            translateGroup.Controls.Add(translateLayout);

            // Add New Dimensions Group
            GroupBox dimensionsGroup = CreateGroupBox("New Dimensions", 220, 150);
            TableLayoutPanel dimensionsLayout = CreateControlLayout(2, 4);
            dimensionsLayout.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 35F);
            dimensionsLayout.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 65F);

            AddLabelAndNumeric(dimensionsLayout, "Width:", _numNewWidth = CreateNumericUpDown(1, 10000, 1, _origWidth, 0), 0);
            AddLabelAndNumeric(dimensionsLayout, "Height:", _numNewHeight = CreateNumericUpDown(1, 10000, 1, _origHeight, 0), 1);
            AddLabelAndNumeric(dimensionsLayout, "Depth:", _numNewDepth = CreateNumericUpDown(1, 10000, 1, _origDepth, 0), 2);
            AddLabelAndNumeric(dimensionsLayout, "Pixel Size (µm):", _numNewPixelSize = CreateNumericUpDown(0.001m, 1000, 0.001m, (decimal)(_origPixelSize * 1e6), 3), 3);

            dimensionsGroup.Controls.Add(dimensionsLayout);

            // Add Action Buttons
            _btnReset = CreateButton("Reset", 220, 30);
            _btnApplyToCurrentDataset = CreateButton("Apply to Current Dataset", 220, 30);
            _btnCreateNewDataset = CreateButton("Create New Dataset", 220, 30);

            // Add Progress Bar
            _progressBar = new ProgressBar { Width = 220, Height = 20, Visible = false };
            _lblProgress = new Label { Text = "Processing...", Width = 220, Height = 20, Visible = false };

            // Add 3D Rotation Control
            _rotationControl = new Rotation3DControl { Size = new Size(200, 200), Margin = new Padding(10) };
            _rotationControl.RotationChanged += RotationControl_RotationChanged;

            // Add all controls to the panel
            transformControls.Controls.Add(rotationGroup);
            transformControls.Controls.Add(rotationOptionsGroup);
            transformControls.Controls.Add(scaleGroup);
            transformControls.Controls.Add(translateGroup);
            transformControls.Controls.Add(dimensionsGroup);
            transformControls.Controls.Add(_rotationControl);
            transformControls.Controls.Add(_btnReset);
            transformControls.Controls.Add(_btnApplyToCurrentDataset);
            transformControls.Controls.Add(_btnCreateNewDataset);
            transformControls.Controls.Add(_progressBar);
            transformControls.Controls.Add(_lblProgress);

            controlsPanel.Controls.Add(transformControls);

            // Add panels to main layout
            mainLayout.Controls.Add(viewsPanel, 0, 0);
            mainLayout.Controls.Add(controlsPanel, 1, 0);

            this.Controls.Add(mainLayout);

            // Add event handlers for buttons
            _btnReset.Click += BtnReset_Click;
            _btnApplyToCurrentDataset.Click += BtnApplyToCurrentDataset_Click;
            _btnCreateNewDataset.Click += BtnCreateNewDataset_Click;

            // Update rotation numeric fields when rotation control changes
            _numRotX.ValueChanged += (s, e) =>
            {
                _rotationX = (double)_numRotX.Value;
                _rotationControl.RotationX = _rotationX;
                UpdatePreview();
            };

            _numRotY.ValueChanged += (s, e) =>
            {
                _rotationY = (double)_numRotY.Value;
                _rotationControl.RotationY = _rotationY;
                UpdatePreview();
            };

            _numRotZ.ValueChanged += (s, e) =>
            {
                _rotationZ = (double)_numRotZ.Value;
                _rotationControl.RotationZ = _rotationZ;
                UpdatePreview();
            };

            // Add event handlers for scale and translation
            _numScaleX.ValueChanged += (s, e) => { _scaleX = (double)_numScaleX.Value; UpdatePreview(); };
            _numScaleY.ValueChanged += (s, e) => { _scaleY = (double)_numScaleY.Value; UpdatePreview(); };
            _numScaleZ.ValueChanged += (s, e) => { _scaleZ = (double)_numScaleZ.Value; UpdatePreview(); };

            _numTranslateX.ValueChanged += (s, e) => { _translateX = (int)_numTranslateX.Value; UpdatePreview(); };
            _numTranslateY.ValueChanged += (s, e) => { _translateY = (int)_numTranslateY.Value; UpdatePreview(); };
            _numTranslateZ.ValueChanged += (s, e) => { _translateZ = (int)_numTranslateZ.Value; UpdatePreview(); };

            // Add event handlers for dimension changes
            _numNewWidth.ValueChanged += (s, e) => { _newWidth = (int)_numNewWidth.Value; UpdatePreview(); };
            _numNewHeight.ValueChanged += (s, e) => { _newHeight = (int)_numNewHeight.Value; UpdatePreview(); };
            _numNewDepth.ValueChanged += (s, e) => { _newDepth = (int)_numNewDepth.Value; UpdatePreview(); };
            _numNewPixelSize.ValueChanged += (s, e) => { _newPixelSize = (double)_numNewPixelSize.Value * 1e-6; UpdatePreview(); };
        }

        // Helper methods for creating UI elements
        private Panel CreateViewPanel(TransformViewType viewType)
        {
            Panel viewPanel = new Panel { Dock = DockStyle.Fill };

            // Create PictureBox
            PictureBox pictureBox = null;
            TrackBar slider = null;
            NumericUpDown numericUpDown = null;
            string sliceLabel = "";

            switch (viewType)
            {
                case TransformViewType.XY:
                    pictureBox = _xyView = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Normal };
                    slider = _xySlider = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = _origDepth - 1, Value = _currentXYSlice };
                    numericUpDown = _numXYSlice = new NumericUpDown { Dock = DockStyle.Right, Width = 60, Minimum = 0, Maximum = _origDepth - 1, Value = _currentXYSlice };
                    sliceLabel = "XY Slice:";

                    pictureBox.Paint += XyView_Paint;
                    slider.Scroll += (s, e) => { _currentXYSlice = slider.Value; numericUpDown.Value = _currentXYSlice; RenderViews(); };
                    numericUpDown.ValueChanged += (s, e) => { _currentXYSlice = (int)numericUpDown.Value; slider.Value = _currentXYSlice; RenderViews(); };
                    break;

                case TransformViewType.XZ:
                    pictureBox = _xzView = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Normal };
                    slider = _xzSlider = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = _origHeight - 1, Value = _currentXZSlice };
                    numericUpDown = _numXZSlice = new NumericUpDown { Dock = DockStyle.Right, Width = 60, Minimum = 0, Maximum = _origHeight - 1, Value = _currentXZSlice };
                    sliceLabel = "XZ Slice:";

                    pictureBox.Paint += XzView_Paint;
                    slider.Scroll += (s, e) => { _currentXZSlice = slider.Value; numericUpDown.Value = _currentXZSlice; RenderViews(); };
                    numericUpDown.ValueChanged += (s, e) => { _currentXZSlice = (int)numericUpDown.Value; slider.Value = _currentXZSlice; RenderViews(); };
                    break;

                case TransformViewType.YZ:
                    pictureBox = _yzView = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Normal };
                    slider = _yzSlider = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = _origWidth - 1, Value = _currentYZSlice };
                    numericUpDown = _numYZSlice = new NumericUpDown { Dock = DockStyle.Right, Width = 60, Minimum = 0, Maximum = _origWidth - 1, Value = _currentYZSlice };
                    sliceLabel = "YZ Slice:";

                    pictureBox.Paint += YzView_Paint;
                    slider.Scroll += (s, e) => { _currentYZSlice = slider.Value; numericUpDown.Value = _currentYZSlice; RenderViews(); };
                    numericUpDown.ValueChanged += (s, e) => { _currentYZSlice = (int)numericUpDown.Value; slider.Value = _currentYZSlice; RenderViews(); };
                    break;
            }

            // Add event handlers for mouse interactions
            pictureBox.MouseDown += View_MouseDown;
            pictureBox.MouseMove += View_MouseMove;
            pictureBox.MouseUp += View_MouseUp;
            pictureBox.MouseWheel += View_MouseWheel;

            // Create slice controls panel
            Panel slicePanel = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            Label lblSlice = new Label { Text = sliceLabel, Dock = DockStyle.Left, Width = 60, TextAlign = ContentAlignment.MiddleLeft };

            slicePanel.Controls.Add(slider);
            slicePanel.Controls.Add(numericUpDown);
            slicePanel.Controls.Add(lblSlice);

            // Add controls to view panel
            viewPanel.Controls.Add(pictureBox);
            viewPanel.Controls.Add(slicePanel);

            return viewPanel;
        }

        private GroupBox CreateGroupBox(string title, int width, int height)
        {
            return new GroupBox
            {
                Text = title,
                Width = width,
                Height = height
            };
        }

        private TableLayoutPanel CreateControlLayout(int columns, int rows)
        {
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = columns,
                RowCount = rows,
                Padding = new Padding(5)
            };

            // Add default column styles
            for (int i = 0; i < columns; i++)
            {
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            }

            // Add default row styles
            for (int i = 0; i < rows; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            }

            return layout;
        }

        private NumericUpDown CreateNumericUpDown(decimal min, decimal max, decimal increment, decimal value, int decimalPlaces)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Increment = increment,
                DecimalPlaces = decimalPlaces,
                Value = value,
                Dock = DockStyle.Fill
            };
        }

        private void AddLabelAndNumeric(TableLayoutPanel layout, string labelText, NumericUpDown numericUpDown, int row)
        {
            Label label = new Label { Text = labelText, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(numericUpDown, 1, row);
        }

        private Button CreateButton(string text, int width, int height)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = height
            };
        }

        private void InitializeResizeHandles()
        {
            // Create resize handles for XY view
            _xyHandles.Clear();
            _xyHandles.Add(new ResizeHandle(ResizeHandleType.TopLeft, _cropRectXY));
            _xyHandles.Add(new ResizeHandle(ResizeHandleType.Top, _cropRectXY));
            _xyHandles.Add(new ResizeHandle(ResizeHandleType.TopRight, _cropRectXY));
            _xyHandles.Add(new ResizeHandle(ResizeHandleType.Left, _cropRectXY));
            _xyHandles.Add(new ResizeHandle(ResizeHandleType.Right, _cropRectXY));
            _xyHandles.Add(new ResizeHandle(ResizeHandleType.BottomLeft, _cropRectXY));
            _xyHandles.Add(new ResizeHandle(ResizeHandleType.Bottom, _cropRectXY));
            _xyHandles.Add(new ResizeHandle(ResizeHandleType.BottomRight, _cropRectXY));

            // Create resize handles for XZ view
            _xzHandles.Clear();
            _xzHandles.Add(new ResizeHandle(ResizeHandleType.TopLeft, _cropRectXZ));
            _xzHandles.Add(new ResizeHandle(ResizeHandleType.Top, _cropRectXZ));
            _xzHandles.Add(new ResizeHandle(ResizeHandleType.TopRight, _cropRectXZ));
            _xzHandles.Add(new ResizeHandle(ResizeHandleType.Left, _cropRectXZ));
            _xzHandles.Add(new ResizeHandle(ResizeHandleType.Right, _cropRectXZ));
            _xzHandles.Add(new ResizeHandle(ResizeHandleType.BottomLeft, _cropRectXZ));
            _xzHandles.Add(new ResizeHandle(ResizeHandleType.Bottom, _cropRectXZ));
            _xzHandles.Add(new ResizeHandle(ResizeHandleType.BottomRight, _cropRectXZ));

            // Create resize handles for YZ view
            _yzHandles.Clear();
            _yzHandles.Add(new ResizeHandle(ResizeHandleType.TopLeft, _cropRectYZ));
            _yzHandles.Add(new ResizeHandle(ResizeHandleType.Top, _cropRectYZ));
            _yzHandles.Add(new ResizeHandle(ResizeHandleType.TopRight, _cropRectYZ));
            _yzHandles.Add(new ResizeHandle(ResizeHandleType.Left, _cropRectYZ));
            _yzHandles.Add(new ResizeHandle(ResizeHandleType.Right, _cropRectYZ));
            _yzHandles.Add(new ResizeHandle(ResizeHandleType.BottomLeft, _cropRectYZ));
            _yzHandles.Add(new ResizeHandle(ResizeHandleType.Bottom, _cropRectYZ));
            _yzHandles.Add(new ResizeHandle(ResizeHandleType.BottomRight, _cropRectYZ));
        }

        private void UpdateSliceControls()
        {
            // Update sliders and numeric controls based on current slices
            _xySlider.Maximum = _origDepth - 1;
            _xySlider.Value = Math.Min(_currentXYSlice, _xySlider.Maximum);
            _numXYSlice.Maximum = _origDepth - 1;
            _numXYSlice.Value = _xySlider.Value;
            _currentXYSlice = _xySlider.Value;

            _xzSlider.Maximum = _origHeight - 1;
            _xzSlider.Value = Math.Min(_currentXZSlice, _xzSlider.Maximum);
            _numXZSlice.Maximum = _origHeight - 1;
            _numXZSlice.Value = _xzSlider.Value;
            _currentXZSlice = _xzSlider.Value;

            _yzSlider.Maximum = _origWidth - 1;
            _yzSlider.Value = Math.Min(_currentYZSlice, _yzSlider.Maximum);
            _numYZSlice.Maximum = _origWidth - 1;
            _numYZSlice.Value = _yzSlider.Value;
            _currentYZSlice = _yzSlider.Value;
        }

        private void View_MouseDown(object sender, MouseEventArgs e)
        {
            _activeView = sender as PictureBox;
            _lastMousePosition = e.Location;

            if (_activeView == null)
                return;

            // Get view point from client coordinates
            Point viewPoint = ConvertClientToView(_activeView, e.Location);
            Rectangle cropRect;

            // Determine which crop rectangle and handles to use
            List<ResizeHandle> handles = null;
            if (_activeView == _xyView)
            {
                handles = _xyHandles;
                cropRect = _cropRectXY;

                // Check for rotation handle click in XY view
                Rectangle rotHandle = new Rectangle(
                    cropRect.Right - 10, cropRect.Top - 10, 20, 20);
                if (rotHandle.Contains(viewPoint))
                {
                    _isRotating = true;
                    _rotationStartPoint = e.Location;
                    _rotatingView = TransformViewType.XY;
                    _activeView.Capture = true;
                    return;
                }
            }
            else if (_activeView == _xzView)
            {
                handles = _xzHandles;
                cropRect = _cropRectXZ;

                // Check for rotation handle click in XZ view
                Rectangle rotHandle = new Rectangle(
                    cropRect.Left - 10, cropRect.Top - 10, 20, 20);
                if (rotHandle.Contains(viewPoint))
                {
                    _isRotating = true;
                    _rotationStartPoint = e.Location;
                    _rotatingView = TransformViewType.XZ;
                    _activeView.Capture = true;
                    return;
                }
            }
            else if (_activeView == _yzView)
            {
                handles = _yzHandles;
                cropRect = _cropRectYZ;

                // Check for rotation handle click in YZ view
                Rectangle rotHandle = new Rectangle(
                    cropRect.Left - 10, cropRect.Top - 10, 20, 20);
                if (rotHandle.Contains(viewPoint))
                {
                    _isRotating = true;
                    _rotationStartPoint = e.Location;
                    _rotatingView = TransformViewType.YZ;
                    _activeView.Capture = true;
                    return;
                }
            }
            else
            {
                return; // Unknown view
            }

            // Check for resize handle clicks
            if (handles != null)
            {
                foreach (var handle in handles)
                {
                    if (handle.HitTest(viewPoint, GetZoomForView(_activeView)))
                    {
                        _isResizing = true;
                        _activeHandle = handle;
                        _activeView.Capture = true;
                        return;
                    }
                }

                // Check if inside crop rectangle (for dragging)
                if (cropRect.Contains(viewPoint))
                {
                    _isDragging = true;
                    _activeView.Capture = true;
                    return;
                }
            }

            // Middle mouse button for panning
            if (e.Button == MouseButtons.Middle)
            {
                _activeView.Capture = true;
            }
        }

        private void View_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is PictureBox view)
            {
                Point currentPosition = e.Location;
                Point viewPoint = ConvertClientToView(view, currentPosition);
                if (_isRotating && _activeView == view)
                {
                    Point delta = new Point(
                        currentPosition.X - _lastMousePosition.X,
                        currentPosition.Y - _lastMousePosition.Y
                    );

                    // Different rotation for each view
                    switch (_rotatingView)
                    {
                        case TransformViewType.XY:
                            // XY view rotates around Z axis
                            _rotationZ = (_rotationZ + delta.X * 0.5) % 360;
                            break;

                        case TransformViewType.XZ:
                            // XZ view rotates around Y axis
                            _rotationY = (_rotationY + delta.X * 0.5) % 360;
                            break;

                        case TransformViewType.YZ:
                            // YZ view rotates around X axis
                            _rotationX = (_rotationX + delta.Y * 0.5) % 360;
                            break;
                    }

                    // Update rotation control
                    _rotationControl.RotationX = _rotationX;
                    _rotationControl.RotationY = _rotationY;
                    _rotationControl.RotationZ = _rotationZ;

                    // Update numeric controls
                    _numRotX.Value = (decimal)Math.Round(_rotationX, 1);
                    _numRotY.Value = (decimal)Math.Round(_rotationY, 1);
                    _numRotZ.Value = (decimal)Math.Round(_rotationZ, 1);

                    _lastMousePosition = currentPosition;
                    UpdatePreview();
                    _activeView.Invalidate();
                    return;
                }

                // Update cursor based on resize handles
                List<ResizeHandle> handles = GetHandlesForView(view);
                if (handles != null && !_isResizing && !_isDragging)
                {
                    Cursor cursor = Cursors.Default;
                    foreach (var handle in handles)
                    {
                        if (handle.HitTest(viewPoint, GetZoomForView(view)))
                        {
                            cursor = GetCursorForHandleType(handle.Type);
                            break;
                        }
                    }
                    view.Cursor = cursor;
                }

                // Handle resizing
                if (_isResizing && _activeView == view && _activeHandle != null)
                {
                    Point delta = new Point(
                        currentPosition.X - _lastMousePosition.X,
                        currentPosition.Y - _lastMousePosition.Y
                    );

                    UpdateCropRectangle(_activeView, _activeHandle, delta);
                    _lastMousePosition = currentPosition;
                    _activeView.Invalidate();
                }

                // Handle dragging
                if (_isDragging && _activeView == view)
                {
                    Point delta = new Point(
                        currentPosition.X - _lastMousePosition.X,
                        currentPosition.Y - _lastMousePosition.Y
                    );

                    MoveCropRectangle(_activeView, delta);
                    _lastMousePosition = currentPosition;
                    _activeView.Invalidate();
                }

                // Handle panning with middle mouse button
                if (e.Button == MouseButtons.Middle && _activeView == view)
                {
                    if (_activeView == _xyView)
                    {
                        _xyPan = new PointF(
                            _xyPan.X + (currentPosition.X - _lastMousePosition.X) / _xyZoom,
                            _xyPan.Y + (currentPosition.Y - _lastMousePosition.Y) / _xyZoom
                        );
                    }
                    else if (_activeView == _xzView)
                    {
                        _xzPan = new PointF(
                            _xzPan.X + (currentPosition.X - _lastMousePosition.X) / _xzZoom,
                            _xzPan.Y + (currentPosition.Y - _lastMousePosition.Y) / _xzZoom
                        );
                    }
                    else if (_activeView == _yzView)
                    {
                        _yzPan = new PointF(
                            _yzPan.X + (currentPosition.X - _lastMousePosition.X) / _yzZoom,
                            _yzPan.Y + (currentPosition.Y - _lastMousePosition.Y) / _yzZoom
                        );
                    }

                    _lastMousePosition = currentPosition;
                    _activeView.Invalidate();
                }
            }
        }

        private void View_MouseUp(object sender, MouseEventArgs e)
        {
            if (_isRotating)
            {
                _isRotating = false;
                UpdatePreview();
            }
            if (_isResizing || _isDragging)
            {
                UpdatePreview();
            }

            _isResizing = false;
            _isDragging = false;
            _activeHandle = null;

            if (_activeView != null)
            {
                _activeView.Capture = false;
                _activeView = null;
            }
        }

        private void View_MouseWheel(object sender, MouseEventArgs e)
        {
            if (sender is PictureBox view)
            {
                // Get mouse position before zoom
                Point mousePos = e.Location;

                // Calculate zoom factor
                float zoomFactor = e.Delta > 0 ? 1.1f : 0.9f;
                float oldZoom = GetZoomForView(view);
                float newZoom = Math.Max(0.1f, Math.Min(10.0f, oldZoom * zoomFactor));

                // Adjust pan to keep mouse position fixed
                PointF pan = GetPanForView(view);
                PointF newPan = new PointF(
                    pan.X + (mousePos.X - pan.X) * (1 - newZoom / oldZoom),
                    pan.Y + (mousePos.Y - pan.Y) * (1 - newZoom / oldZoom)
                );

                // Apply new zoom and pan
                if (view == _xyView)
                {
                    _xyZoom = newZoom;
                    _xyPan = newPan;
                }
                else if (view == _xzView)
                {
                    _xzZoom = newZoom;
                    _xzPan = newPan;
                }
                else if (view == _yzView)
                {
                    _yzZoom = newZoom;
                    _yzPan = newPan;
                }

                view.Invalidate();
            }
        }

        private void XyView_Paint(object sender, PaintEventArgs e)
        {
            PaintView(e.Graphics, _xyView, _cropRectXY, _xyZoom, _xyPan, _xyHandles, TransformViewType.XY);
        }

        private void XzView_Paint(object sender, PaintEventArgs e)
        {
            PaintView(e.Graphics, _xzView, _cropRectXZ, _xzZoom, _xzPan, _xzHandles, TransformViewType.XZ);
        }

        private void YzView_Paint(object sender, PaintEventArgs e)
        {
            PaintView(e.Graphics, _yzView, _cropRectYZ, _yzZoom, _yzPan, _yzHandles, TransformViewType.YZ);
        }

        private void PaintView(Graphics g, PictureBox view, Rectangle cropRect, float zoom, PointF pan, List<ResizeHandle> handles, TransformViewType viewType)
        {
            if (view.Image == null)
                return;

            // Clear the entire view to prevent ghost images
            g.Clear(view.BackColor);

            // Configure graphics quality
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            // Calculate image position and size based on zoom and pan
            int imgWidth = view.Image.Width;
            int imgHeight = view.Image.Height;

            // Apply zoom
            float scaledWidth = imgWidth * zoom;
            float scaledHeight = imgHeight * zoom;

            // Calculate centered position
            float x = (view.Width - scaledWidth) / 2 + pan.X;
            float y = (view.Height - scaledHeight) / 2 + pan.Y;

            // Draw the image with proper scaling
            RectangleF destRect = new RectangleF(x, y, scaledWidth, scaledHeight);
            g.DrawImage(view.Image, destRect, new RectangleF(0, 0, imgWidth, imgHeight), GraphicsUnit.Pixel);

            // Apply transform for handles and other overlays
            Matrix transform = new Matrix();
            transform.Translate(x, y);
            transform.Scale(zoom, zoom);
            g.Transform = transform;

            // Draw crop rectangle
            using (Pen cropPen = new Pen(Color.Yellow, 1.0f / zoom))
            {
                g.DrawRectangle(cropPen, cropRect);
            }

            // Draw resize handles
            foreach (var handle in handles)
            {
                handle.Draw(g, zoom);
            }

            // Draw rotation handles
            int rotHandleSize = (int)(15 / zoom);

            if (viewType == TransformViewType.XY)
            {
                // Draw rotation handle for Z rotation (XY view)
                using (Pen rotPen = new Pen(Color.Blue, 2.0f / zoom))
                using (Brush rotBrush = new SolidBrush(Color.FromArgb(150, 0, 100, 255)))
                {
                    g.FillEllipse(rotBrush,
                                 cropRect.Right - rotHandleSize / 2,
                                 cropRect.Top - rotHandleSize / 2,
                                 rotHandleSize, rotHandleSize);
                    g.DrawLine(rotPen,
                              cropRect.Right,
                              cropRect.Top,
                              cropRect.Right + rotHandleSize / 2,
                              cropRect.Top - rotHandleSize / 2);

                    // Add a small "Z" label
                    using (Font labelFont = new Font("Arial", 8 / zoom, FontStyle.Bold))
                    using (Brush labelBrush = new SolidBrush(Color.White))
                    {
                        g.DrawString("Z", labelFont, labelBrush,
                                    cropRect.Right - rotHandleSize / 4,
                                    cropRect.Top - rotHandleSize / 4);
                    }
                }
            }
            else if (viewType == TransformViewType.XZ)
            {
                // Draw rotation handle for Y rotation (XZ view)
                using (Pen rotPen = new Pen(Color.Green, 2.0f / zoom))
                using (Brush rotBrush = new SolidBrush(Color.FromArgb(150, 0, 200, 0)))
                {
                    g.FillEllipse(rotBrush,
                                 cropRect.Left - rotHandleSize / 2,
                                 cropRect.Top - rotHandleSize / 2,
                                 rotHandleSize, rotHandleSize);
                    g.DrawLine(rotPen,
                              cropRect.Left,
                              cropRect.Top,
                              cropRect.Left - rotHandleSize / 2,
                              cropRect.Top - rotHandleSize / 2);

                    // Add a small "Y" label
                    using (Font labelFont = new Font("Arial", 8 / zoom, FontStyle.Bold))
                    using (Brush labelBrush = new SolidBrush(Color.White))
                    {
                        g.DrawString("Y", labelFont, labelBrush,
                                    cropRect.Left - rotHandleSize / 4,
                                    cropRect.Top - rotHandleSize / 4);
                    }
                }
            }
            else if (viewType == TransformViewType.YZ)
            {
                // Draw rotation handle for X rotation (YZ view)
                using (Pen rotPen = new Pen(Color.Red, 2.0f / zoom))
                using (Brush rotBrush = new SolidBrush(Color.FromArgb(150, 255, 0, 0)))
                {
                    g.FillEllipse(rotBrush,
                                 cropRect.Left - rotHandleSize / 2,
                                 cropRect.Top - rotHandleSize / 2,
                                 rotHandleSize, rotHandleSize);
                    g.DrawLine(rotPen,
                              cropRect.Left,
                              cropRect.Top,
                              cropRect.Left - rotHandleSize / 2,
                              cropRect.Top - rotHandleSize / 2);

                    // Add a small "X" label
                    using (Font labelFont = new Font("Arial", 8 / zoom, FontStyle.Bold))
                    using (Brush labelBrush = new SolidBrush(Color.White))
                    {
                        g.DrawString("X", labelFont, labelBrush,
                                    cropRect.Left - rotHandleSize / 4,
                                    cropRect.Top - rotHandleSize / 4);
                    }
                }
            }

            // Draw axes indicators
            using (Font axisFont = new Font("Arial", 12 / zoom))
            using (Brush axisBrush = new SolidBrush(Color.White))
            using (Pen axisPen = new Pen(Color.Red, 2 / zoom))
            {
                int margin = 10;

                switch (viewType)
                {
                    case TransformViewType.XY:
                        g.DrawString("X", axisFont, axisBrush, margin, margin);
                        g.DrawLine(axisPen, margin, margin + 20, margin + 20, margin + 20);
                        g.DrawString("Y", axisFont, axisBrush, margin, margin + 30);
                        g.DrawLine(axisPen, margin, margin + 50, margin, margin + 70);
                        break;

                    case TransformViewType.XZ:
                        g.DrawString("X", axisFont, axisBrush, margin, margin);
                        g.DrawLine(axisPen, margin, margin + 20, margin + 20, margin + 20);
                        g.DrawString("Z", axisFont, axisBrush, margin, margin + 30);
                        g.DrawLine(axisPen, margin, margin + 50, margin, margin + 70);
                        break;

                    case TransformViewType.YZ:
                        g.DrawString("Y", axisFont, axisBrush, margin, margin);
                        g.DrawLine(axisPen, margin, margin + 20, margin + 20, margin + 20);
                        g.DrawString("Z", axisFont, axisBrush, margin, margin + 30);
                        g.DrawLine(axisPen, margin, margin + 50, margin, margin + 70);
                        break;
                }
            }

            // Draw rotation indicator
            using (Font rotFont = new Font("Arial", 10 / zoom))
            using (Brush rotBrush = new SolidBrush(Color.Yellow))
            using (Brush bgBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
            {
                string rotInfo = $"Rotation: X:{_rotationX:0.0}° Y:{_rotationY:0.0}° Z:{_rotationZ:0.0}°";
                SizeF textSize = g.MeasureString(rotInfo, rotFont);
                float textX = cropRect.X;
                float textY = cropRect.Y - 30 / zoom;

                g.FillRectangle(bgBrush, textX, textY, textSize.Width, textSize.Height);
                g.DrawString(rotInfo, rotFont, rotBrush, textX, textY);
            }

            // Draw transformation info
            using (Font transFont = new Font("Arial", 10 / zoom))
            using (Brush transBrush = new SolidBrush(Color.Cyan))
            using (Brush bgBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
            {
                string transInfo = $"Scale: {_scaleX:0.00}x{_scaleY:0.00}x{_scaleZ:0.00}";
                SizeF textSize = g.MeasureString(transInfo, transFont);
                float textX = cropRect.X;
                float textY = cropRect.Y - 50 / zoom;

                g.FillRectangle(bgBrush, textX, textY, textSize.Width, textSize.Height);
                g.DrawString(transInfo, transFont, transBrush, textX, textY);
            }

            // Reset transform
            g.ResetTransform();

            // Display current slice info
            using (Font sliceFont = new Font("Arial", 10))
            using (Brush sliceBrush = new SolidBrush(Color.White))
            using (Brush bgBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
            {
                string sliceInfo = "";
                switch (viewType)
                {
                    case TransformViewType.XY:
                        sliceInfo = $"XY Slice: {_currentXYSlice}";
                        break;

                    case TransformViewType.XZ:
                        sliceInfo = $"XZ Slice: {_currentXZSlice}";
                        break;

                    case TransformViewType.YZ:
                        sliceInfo = $"YZ Slice: {_currentYZSlice}";
                        break;
                }

                SizeF textSize = g.MeasureString(sliceInfo, sliceFont);
                RectangleF textRect = new RectangleF(5, 5, textSize.Width + 10, textSize.Height + 5);

                g.FillRectangle(bgBrush, textRect);
                g.DrawString(sliceInfo, sliceFont, sliceBrush, 10, 7);
            }

            // Display new dimensions
            using (Font dimFont = new Font("Arial", 9))
            using (Brush dimBrush = new SolidBrush(Color.LightGreen))
            using (Brush bgBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
            {
                string dimInfo = $"New Size: {_newWidth}×{_newHeight}×{_newDepth}";
                SizeF textSize = g.MeasureString(dimInfo, dimFont);
                float yPos = view.Height - textSize.Height - 10;
                RectangleF textRect = new RectangleF(5, yPos, textSize.Width + 10, textSize.Height + 5);

                g.FillRectangle(bgBrush, textRect);
                g.DrawString(dimInfo, dimFont, dimBrush, 10, yPos + 2);
            }
        }

        private Point ConvertClientToView(PictureBox view, Point clientPoint)
        {
            if (view == null || view.Image == null)
                return clientPoint;

            float zoom = GetZoomForView(view);
            PointF pan = GetPanForView(view);

            // Calculate image position based on zoom and pan
            int imgWidth = view.Image.Width;
            int imgHeight = view.Image.Height;
            float x = (view.Width - imgWidth * zoom) / 2 + pan.X;
            float y = (view.Height - imgHeight * zoom) / 2 + pan.Y;

            // Convert to image coordinates
            Point viewPoint = new Point(
                (int)((clientPoint.X - x) / zoom),
                (int)((clientPoint.Y - y) / zoom)
            );

            return viewPoint;
        }

        private List<ResizeHandle> GetHandlesForView(PictureBox view)
        {
            if (view == _xyView)
                return _xyHandles;
            else if (view == _xzView)
                return _xzHandles;
            else if (view == _yzView)
                return _yzHandles;
            return null;
        }

        private float GetZoomForView(PictureBox view)
        {
            if (view == _xyView)
                return _xyZoom;
            else if (view == _xzView)
                return _xzZoom;
            else if (view == _yzView)
                return _yzZoom;
            return 1.0f;
        }

        private PointF GetPanForView(PictureBox view)
        {
            if (view == _xyView)
                return _xyPan;
            else if (view == _xzView)
                return _xzPan;
            else if (view == _yzView)
                return _yzPan;
            return PointF.Empty;
        }

        private Cursor GetCursorForHandleType(ResizeHandleType type)
        {
            switch (type)
            {
                case ResizeHandleType.TopLeft:
                case ResizeHandleType.BottomRight:
                    return Cursors.SizeNWSE;

                case ResizeHandleType.TopRight:
                case ResizeHandleType.BottomLeft:
                    return Cursors.SizeNESW;

                case ResizeHandleType.Top:
                case ResizeHandleType.Bottom:
                    return Cursors.SizeNS;

                case ResizeHandleType.Left:
                case ResizeHandleType.Right:
                    return Cursors.SizeWE;

                default:
                    return Cursors.Default;
            }
        }

        private void UpdateCropRectangle(PictureBox view, ResizeHandle handle, Point delta)
        {
            Rectangle cropRect;
            float zoom = GetZoomForView(view);

            // Scale delta according to zoom
            delta = new Point(
                (int)(delta.X / zoom),
                (int)(delta.Y / zoom)
            );

            if (view == _xyView)
            {
                cropRect = _cropRectXY;
            }
            else if (view == _xzView)
            {
                cropRect = _cropRectXZ;
            }
            else if (view == _yzView)
            {
                cropRect = _cropRectYZ;
            }
            else
            {
                return;
            }

            // Apply handle movement
            Rectangle newRect = handle.UpdateRectangle(cropRect, delta);

            // Enforce minimum size and constrain to view bounds
            int minSize = 10;
            newRect.Width = Math.Max(minSize, newRect.Width);
            newRect.Height = Math.Max(minSize, newRect.Height);

            int maxX = 0, maxY = 0;
            if (view == _xyView)
            {
                maxX = _origWidth;
                maxY = _origHeight;
            }
            else if (view == _xzView)
            {
                maxX = _origWidth;
                maxY = _origDepth;
            }
            else if (view == _yzView)
            {
                maxX = _origDepth;
                maxY = _origHeight;
            }

            // Constrain to view bounds
            if (newRect.Right > maxX)
                newRect.Width = maxX - newRect.X;
            if (newRect.Bottom > maxY)
                newRect.Height = maxY - newRect.Y;
            if (newRect.X < 0)
            {
                newRect.Width += newRect.X;
                newRect.X = 0;
            }
            if (newRect.Y < 0)
            {
                newRect.Height += newRect.Y;
                newRect.Y = 0;
            }

            // Update the appropriate crop rectangle
            if (view == _xyView)
            {
                _cropRectXY = newRect;
            }
            else if (view == _xzView)
            {
                _cropRectXZ = newRect;
            }
            else if (view == _yzView)
            {
                _cropRectYZ = newRect;
            }

            // Update handle positions
            UpdateHandlePositions();
        }

        private void MoveCropRectangle(PictureBox view, Point delta)
        {
            Rectangle cropRect;
            float zoom = GetZoomForView(view);

            // Scale delta according to zoom
            delta = new Point(
                (int)(delta.X / zoom),
                (int)(delta.Y / zoom)
            );

            if (view == _xyView)
            {
                cropRect = _cropRectXY;
            }
            else if (view == _xzView)
            {
                cropRect = _cropRectXZ;
            }
            else if (view == _yzView)
            {
                cropRect = _cropRectYZ;
            }
            else
            {
                return;
            }

            // Create new rectangle with delta applied
            Rectangle newRect = new Rectangle(
                cropRect.X + delta.X,
                cropRect.Y + delta.Y,
                cropRect.Width,
                cropRect.Height
            );

            // Constrain to view bounds
            int maxX = 0, maxY = 0;
            if (view == _xyView)
            {
                maxX = _origWidth;
                maxY = _origHeight;
            }
            else if (view == _xzView)
            {
                maxX = _origWidth;
                maxY = _origDepth;
            }
            else if (view == _yzView)
            {
                maxX = _origDepth;
                maxY = _origHeight;
            }

            if (newRect.X < 0)
                newRect.X = 0;
            if (newRect.Y < 0)
                newRect.Y = 0;
            if (newRect.Right > maxX)
                newRect.X = maxX - newRect.Width;
            if (newRect.Bottom > maxY)
                newRect.Y = maxY - newRect.Height;

            // Update the appropriate crop rectangle
            if (view == _xyView)
            {
                _cropRectXY = newRect;
            }
            else if (view == _xzView)
            {
                _cropRectXZ = newRect;
            }
            else if (view == _yzView)
            {
                _cropRectYZ = newRect;
            }

            // Update handle positions
            UpdateHandlePositions();
        }

        private void UpdateHandlePositions()
        {
            // Update XY handles
            foreach (var handle in _xyHandles)
            {
                handle.UpdatePositionFromRect(_cropRectXY);
            }

            // Update XZ handles
            foreach (var handle in _xzHandles)
            {
                handle.UpdatePositionFromRect(_cropRectXZ);
            }

            // Update YZ handles
            foreach (var handle in _yzHandles)
            {
                handle.UpdatePositionFromRect(_cropRectYZ);
            }
        }

        private void RenderViews()
        {
            try
            {
                // Cancel any previous rendering operations
                _renderCts.Cancel();
                _renderCts = new CancellationTokenSource();
                CancellationToken token = _renderCts.Token;

                // Render XY view
                Task.Run(() =>
                {
                    string cacheKey = $"XY_{_currentXYSlice}";
                    Bitmap xyBitmap = _sliceCache.Get(cacheKey);

                    if (xyBitmap == null)
                    {
                        xyBitmap = RenderXYSlice(_currentXYSlice);
                        _sliceCache.Add(cacheKey, xyBitmap);
                    }

                    if (!token.IsCancellationRequested)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (_xyView.Image != null)
                            {
                                var oldImage = _xyView.Image;
                                _xyView.Image = new Bitmap(xyBitmap);
                                oldImage.Dispose();
                            }
                            else
                            {
                                _xyView.Image = new Bitmap(xyBitmap);
                            }
                            _xyView.Invalidate();
                        }));
                    }
                }, token);

                // Render XZ view
                Task.Run(() =>
                {
                    string cacheKey = $"XZ_{_currentXZSlice}";
                    Bitmap xzBitmap = _sliceCache.Get(cacheKey);

                    if (xzBitmap == null)
                    {
                        xzBitmap = RenderXZSlice(_currentXZSlice);
                        _sliceCache.Add(cacheKey, xzBitmap);
                    }

                    if (!token.IsCancellationRequested)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (_xzView.Image != null)
                            {
                                var oldImage = _xzView.Image;
                                _xzView.Image = new Bitmap(xzBitmap);
                                oldImage.Dispose();
                            }
                            else
                            {
                                _xzView.Image = new Bitmap(xzBitmap);
                            }
                            _xzView.Invalidate();
                        }));
                    }
                }, token);

                // Render YZ view
                Task.Run(() =>
                {
                    string cacheKey = $"YZ_{_currentYZSlice}";
                    Bitmap yzBitmap = _sliceCache.Get(cacheKey);

                    if (yzBitmap == null)
                    {
                        yzBitmap = RenderYZSlice(_currentYZSlice);
                        _sliceCache.Add(cacheKey, yzBitmap);
                    }

                    if (!token.IsCancellationRequested)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (_yzView.Image != null)
                            {
                                var oldImage = _yzView.Image;
                                _yzView.Image = new Bitmap(yzBitmap);
                                oldImage.Dispose();
                            }
                            else
                            {
                                _yzView.Image = new Bitmap(yzBitmap);
                            }
                            _yzView.Invalidate();
                        }));
                    }
                }, token);
            }
            catch (Exception ex)
            {
                Logger.Log($"[TransformDatasetForm] Error rendering views: {ex.Message}");
            }
        }

        private Bitmap RenderXYSlice(int sliceZ)
        {
            // Create bitmap with exact dimensions
            Bitmap bitmap = new Bitmap(_origWidth, _origHeight, PixelFormat.Format8bppIndexed);

            // Set up grayscale palette
            ColorPalette palette = bitmap.Palette;
            for (int i = 0; i < 256; i++)
            {
                palette.Entries[i] = Color.FromArgb(255, i, i, i);
            }
            bitmap.Palette = palette;

            // Calculate transformation matrix
            Matrix4x4 transformMatrix = CalculateTransformationMatrix();
            Matrix4x4 invTransform;
            Matrix4x4.Invert(transformMatrix, out invTransform);

            // Lock the bitmap for direct access
            BitmapData bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                bitmap.PixelFormat);

            try
            {
                int stride = bitmapData.Stride;
                byte[] rowBuffer = new byte[stride];

                // Get source crop parameters
                int cropX = _cropRectXY.X;
                int cropY = _cropRectXY.Y;
                int cropZ = _cropRectXZ.Y;
                int cropWidth = _cropRectXY.Width;
                int cropHeight = _cropRectXY.Height;
                int cropDepth = _cropRectXZ.Height;

                // Process each row
                for (int y = 0; y < _origHeight; y++)
                {
                    // Clear row buffer to handle padding correctly
                    Array.Clear(rowBuffer, 0, stride);

                    // Fill pixel data for this row with transformed values
                    for (int x = 0; x < _origWidth; x++)
                    {
                        // Apply inverse transformation to get source voxel
                        Vector4 pos = new Vector4(x, y, sliceZ, 1.0f);
                        Vector4 srcPos = Vector4Extensions.Transform(pos, invTransform);

                        float srcX = srcPos.X;
                        float srcY = srcPos.Y;
                        float srcZ = srcPos.Z;

                        // Check if source point is within valid range
                        if (srcX >= cropX && srcX < cropX + cropWidth &&
                            srcY >= cropY && srcY < cropY + cropHeight &&
                            srcZ >= cropZ && srcZ < cropZ + cropDepth)
                        {
                            // Use trilinear interpolation for smoother results
                            rowBuffer[x] = SampleVolumeTrilinear(_originalVolume, srcX, srcY, srcZ);
                        }
                        else
                        {
                            // Use black for voxels outside the source volume
                            rowBuffer[x] = 0;
                        }
                    }

                    // Copy row to bitmap
                    IntPtr rowPtr = bitmapData.Scan0 + (y * stride);
                    Marshal.Copy(rowBuffer, 0, rowPtr, stride);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TransformDatasetForm] Error rendering XY slice {sliceZ}: {ex.Message}");
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            return bitmap;
        }

        private Bitmap RenderXZSlice(int sliceY)
        {
            // Create bitmap with exact dimensions
            Bitmap bitmap = new Bitmap(_origWidth, _origDepth, PixelFormat.Format8bppIndexed);

            // Set up grayscale palette
            ColorPalette palette = bitmap.Palette;
            for (int i = 0; i < 256; i++)
            {
                palette.Entries[i] = Color.FromArgb(255, i, i, i);
            }
            bitmap.Palette = palette;

            // Calculate transformation matrix
            Matrix4x4 transformMatrix = CalculateTransformationMatrix();
            Matrix4x4 invTransform;
            Matrix4x4.Invert(transformMatrix, out invTransform);

            // Lock the bitmap for direct access
            BitmapData bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                bitmap.PixelFormat);

            try
            {
                int stride = bitmapData.Stride;
                byte[] rowBuffer = new byte[stride];

                // Get source crop parameters
                int cropX = _cropRectXY.X;
                int cropY = _cropRectXY.Y;
                int cropZ = _cropRectXZ.Y;
                int cropWidth = _cropRectXY.Width;
                int cropHeight = _cropRectXY.Height;
                int cropDepth = _cropRectXZ.Height;

                // Process each row (z-axis becomes the y-axis in the bitmap)
                for (int z = 0; z < _origDepth; z++)
                {
                    // Clear row buffer to handle padding correctly
                    Array.Clear(rowBuffer, 0, stride);

                    // Fill pixel data for this row with transformed values
                    for (int x = 0; x < _origWidth; x++)
                    {
                        // Apply inverse transformation to get source voxel
                        Vector4 pos = new Vector4(x, sliceY, z, 1.0f);
                        Vector4 srcPos = Vector4Extensions.Transform(pos, invTransform);

                        float srcX = srcPos.X;
                        float srcY = srcPos.Y;
                        float srcZ = srcPos.Z;

                        // Check if source point is within valid range
                        if (srcX >= cropX && srcX < cropX + cropWidth &&
                            srcY >= cropY && srcY < cropY + cropHeight &&
                            srcZ >= cropZ && srcZ < cropZ + cropDepth)
                        {
                            // Use trilinear interpolation for smoother results
                            rowBuffer[x] = SampleVolumeTrilinear(_originalVolume, srcX, srcY, srcZ);
                        }
                        else
                        {
                            // Use black for voxels outside the source volume
                            rowBuffer[x] = 0;
                        }
                    }

                    // Copy row to bitmap
                    IntPtr rowPtr = bitmapData.Scan0 + (z * stride);
                    Marshal.Copy(rowBuffer, 0, rowPtr, stride);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TransformDatasetForm] Error rendering XZ slice {sliceY}: {ex.Message}");
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            return bitmap;
        }

        private Bitmap RenderYZSlice(int sliceX)
        {
            // Create bitmap with exact dimensions
            Bitmap bitmap = new Bitmap(_origDepth, _origHeight, PixelFormat.Format8bppIndexed);

            // Set up grayscale palette
            ColorPalette palette = bitmap.Palette;
            for (int i = 0; i < 256; i++)
            {
                palette.Entries[i] = Color.FromArgb(255, i, i, i);
            }
            bitmap.Palette = palette;

            // Calculate transformation matrix
            Matrix4x4 transformMatrix = CalculateTransformationMatrix();
            Matrix4x4 invTransform;
            Matrix4x4.Invert(transformMatrix, out invTransform);

            // Lock the bitmap for direct access
            BitmapData bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                bitmap.PixelFormat);

            try
            {
                int stride = bitmapData.Stride;
                byte[] rowBuffer = new byte[stride];

                // Get source crop parameters
                int cropX = _cropRectXY.X;
                int cropY = _cropRectXY.Y;
                int cropZ = _cropRectXZ.Y;
                int cropWidth = _cropRectXY.Width;
                int cropHeight = _cropRectXY.Height;
                int cropDepth = _cropRectXZ.Height;

                // Process each row (y-coordinate forms rows in the bitmap)
                for (int y = 0; y < _origHeight; y++)
                {
                    // Clear row buffer to handle padding correctly
                    Array.Clear(rowBuffer, 0, stride);

                    // Fill pixel data for this row with transformed values
                    for (int z = 0; z < _origDepth; z++)
                    {
                        // Apply inverse transformation to get source voxel
                        Vector4 pos = new Vector4(sliceX, y, z, 1.0f);
                        Vector4 srcPos = Vector4Extensions.Transform(pos, invTransform);

                        float srcX = srcPos.X;
                        float srcY = srcPos.Y;
                        float srcZ = srcPos.Z;

                        // Check if source point is within valid range
                        if (srcX >= cropX && srcX < cropX + cropWidth &&
                            srcY >= cropY && srcY < cropY + cropHeight &&
                            srcZ >= cropZ && srcZ < cropZ + cropDepth)
                        {
                            // Use trilinear interpolation for smoother results
                            rowBuffer[z] = SampleVolumeTrilinear(_originalVolume, srcX, srcY, srcZ);
                        }
                        else
                        {
                            // Use black for voxels outside the source volume
                            rowBuffer[z] = 0;
                        }
                    }

                    // Copy row to bitmap
                    IntPtr rowPtr = bitmapData.Scan0 + (y * stride);
                    Marshal.Copy(rowBuffer, 0, rowPtr, stride);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TransformDatasetForm] Error rendering YZ slice {sliceX}: {ex.Message}");
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            return bitmap;
        }

        private void UpdatePreview()
        {
            // Need to clear the slice cache - implement a workaround for missing Clear() method
            // Create a new cache instead of clearing
            _sliceCache = new LRUCache<string, Bitmap>(10);

            // Calculate new dimensions based on crop and scale
            CalculateNewDimensions();

            // Update numeric controls to reflect the new dimensions
            _numNewWidth.Value = _newWidth;
            _numNewHeight.Value = _newHeight;
            _numNewDepth.Value = _newDepth;

            // Re-render the views
            RenderViews();
        }

        private void CalculateNewDimensions()
        {
            // Calculate new dimensions based on crop rectangles
            int croppedWidth = Math.Min(_cropRectXY.Width, _cropRectXZ.Width);
            int croppedHeight = Math.Min(_cropRectXY.Height, _cropRectYZ.Height);
            int croppedDepth = Math.Min(_cropRectXZ.Height, _cropRectYZ.Width);

            // If preserving dataset during rotation, calculate the dimensions needed to contain the entire rotated volume
            if (_preserveDatasetDuringRotation && (Math.Abs(_rotationX) > 0.001 || Math.Abs(_rotationY) > 0.001 || Math.Abs(_rotationZ) > 0.001))
            {
                // Calculate the bounding box for the rotated volume
                var corners = GetCuboidCorners(croppedWidth, croppedHeight, croppedDepth);
                var rotatedCorners = TransformCorners(corners);
                var newBounds = CalculateBoundingBox(rotatedCorners);

                // Use the dimensions from the rotated bounding box
                croppedWidth = newBounds.Width;
                croppedHeight = newBounds.Height;
                croppedDepth = newBounds.Depth;
            }

            // Apply scaling
            _newWidth = (int)(croppedWidth * _scaleX);
            _newHeight = (int)(croppedHeight * _scaleY);
            _newDepth = (int)(croppedDepth * _scaleZ);

            // Ensure minimum dimensions
            _newWidth = Math.Max(1, _newWidth);
            _newHeight = Math.Max(1, _newHeight);
            _newDepth = Math.Max(1, _newDepth);

            // Update pixel size based on scaling
            double avgScale = (_scaleX + _scaleY + _scaleZ) / 3.0;
            _newPixelSize = _origPixelSize / avgScale;
        }

        private SharpDX.Vector3[] GetCuboidCorners(int width, int height, int depth)
        {
            // Create an array of the 8 corners of the cuboid
            return new SharpDX.Vector3[]
            {
        new SharpDX.Vector3(0, 0, 0),
        new SharpDX.Vector3(width, 0, 0),
        new SharpDX.Vector3(0, height, 0),
        new SharpDX.Vector3(width, height, 0),
        new SharpDX.Vector3(0, 0, depth),
        new SharpDX.Vector3(width, 0, depth),
        new SharpDX.Vector3(0, height, depth),
        new SharpDX.Vector3(width, height, depth)
            };
        }

        private SharpDX.Vector3[] TransformCorners(SharpDX.Vector3[] corners)
        {
            // Get rotation matrices (convert degrees to radians)
            float rotX = (float)(_rotationX * Math.PI / 180.0);
            float rotY = (float)(_rotationY * Math.PI / 180.0);
            float rotZ = (float)(_rotationZ * Math.PI / 180.0);

            // Calculate center of rotation (center of the crop rectangle)
            float centerX = _cropRectXY.Width / 2.0f;
            float centerY = _cropRectXY.Height / 2.0f;
            float centerZ = _cropRectXZ.Height / 2.0f;

            // Create rotation matrices
            Matrix4x4 rotationX = Matrix4x4.CreateRotationX(rotX);
            Matrix4x4 rotationY = Matrix4x4.CreateRotationY(rotY);
            Matrix4x4 rotationZ = Matrix4x4.CreateRotationZ(rotZ);

            // Combined rotation matrix
            Matrix4x4 rotationMatrix = rotationX * rotationY * rotationZ;

            SharpDX.Vector3[] transformedCorners = new SharpDX.Vector3[corners.Length];

            // Apply rotation to each corner
            for (int i = 0; i < corners.Length; i++)
            {
                SharpDX.Vector3 corner = corners[i];
                // Center the point at origin before rotation
                SharpDX.Vector3 centered = new SharpDX.Vector3(
                    corner.X - centerX,
                    corner.Y - centerY,
                    corner.Z - centerZ
                );

                // Apply rotation
                SharpDX.Vector3 rotated = Matrix4x4Extensions.Transform(centered, rotationMatrix);

                // Move back to original coordinate space
                transformedCorners[i] = new SharpDX.Vector3(
                    rotated.X + centerX,
                    rotated.Y + centerY,
                    rotated.Z + centerZ
                );
            }

            return transformedCorners;
        }

        private (int Width, int Height, int Depth) CalculateBoundingBox(Vector3[] corners)
        {
            // Find the minimum and maximum coordinates
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            foreach (var corner in corners)
            {
                minX = Math.Min(minX, corner.X);
                minY = Math.Min(minY, corner.Y);
                minZ = Math.Min(minZ, corner.Z);

                maxX = Math.Max(maxX, corner.X);
                maxY = Math.Max(maxY, corner.Y);
                maxZ = Math.Max(maxZ, corner.Z);
            }

            // Calculate dimensions and add a small margin to ensure all data is contained
            int width = (int)Math.Ceiling(maxX - minX) + 2;
            int height = (int)Math.Ceiling(maxY - minY) + 2;
            int depth = (int)Math.Ceiling(maxZ - minZ) + 2;

            return (width, height, depth);
        }

        private void BtnReset_Click(object sender, EventArgs e)
        {
            // Reset transformation parameters
            _rotationX = 0;
            _rotationY = 0;
            _rotationZ = 0;

            // Reset rotation control
            _rotationControl.RotationX = 0;
            _rotationControl.RotationY = 0;
            _rotationControl.RotationZ = 0;

            _scaleX = 1.0;
            _scaleY = 1.0;
            _scaleZ = 1.0;

            _translateX = 0;
            _translateY = 0;
            _translateZ = 0;

            // Reset crop rectangles
            _cropRectXY = new Rectangle(0, 0, _origWidth, _origHeight);
            _cropRectXZ = new Rectangle(0, 0, _origWidth, _origDepth);
            _cropRectYZ = new Rectangle(0, 0, _origDepth, _origHeight);

            // Reset zoom and pan
            _xyZoom = 1.0f;
            _xzZoom = 1.0f;
            _yzZoom = 1.0f;

            _xyPan = PointF.Empty;
            _xzPan = PointF.Empty;
            _yzPan = PointF.Empty;

            // Reset numeric controls
            _numRotX.Value = 0;
            _numRotY.Value = 0;
            _numRotZ.Value = 0;

            _numScaleX.Value = 1.0m;
            _numScaleY.Value = 1.0m;
            _numScaleZ.Value = 1.0m;

            _numTranslateX.Value = 0;
            _numTranslateY.Value = 0;
            _numTranslateZ.Value = 0;

            _numNewWidth.Value = _origWidth;
            _numNewHeight.Value = _origHeight;
            _numNewDepth.Value = _origDepth;
            _numNewPixelSize.Value = (decimal)(_origPixelSize * 1e6);

            // Reset dimensions
            _newWidth = _origWidth;
            _newHeight = _origHeight;
            _newDepth = _origDepth;
            _newPixelSize = _origPixelSize;

            // Update handle positions
            UpdateHandlePositions();

            // Clear the cache and render
            _sliceCache = new LRUCache<string, Bitmap>(10);
            RenderViews();

            Logger.Log("[TransformDatasetForm] Reset all transformations");
        }

        private void BtnApplyToCurrentDataset_Click(object sender, EventArgs e)
        {
            DialogResult result = MessageBox.Show(
                "Are you sure you want to apply these transformations to the current dataset? This operation cannot be undone.",
                "Confirm Apply Transformations",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                ApplyTransformations(false);
            }
        }

        private void BtnCreateNewDataset_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog dialog = new FolderBrowserDialog();
            dialog.Description = "Select folder to save the transformed dataset";

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                string outputFolder = dialog.SelectedPath;

                // Check if directory exists and is writeable
                if (!Directory.Exists(outputFolder))
                {
                    try
                    {
                        Directory.CreateDirectory(outputFolder);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error creating output directory: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                // Check if directory is writable
                try
                {
                    string testFile = Path.Combine(outputFolder, "test.tmp");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Output directory is not writable: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                ApplyTransformations(true, outputFolder);
            }
        }

        private async void ApplyTransformations(bool createNew, string outputFolder = null)
        {
            try
            {
                // Show progress bar
                _progressBar.Value = 0;
                _progressBar.Visible = true;
                _lblProgress.Visible = true;

                // Disable buttons while processing
                _btnReset.Enabled = false;
                _btnApplyToCurrentDataset.Enabled = false;
                _btnCreateNewDataset.Enabled = false;

                // Create a cancellation token for the operation
                CancellationTokenSource cts = new CancellationTokenSource();
                CancellationToken token = cts.Token;

                // Get transformed dataset
                ChunkedVolume transformedVolume = null;
                ChunkedLabelVolume transformedLabels = null;

                await Task.Run(() =>
                {
                    // Transform the volume data
                    transformedVolume = TransformVolume(_originalVolume, token);

                    // Transform labels if they exist
                    if (_originalLabels != null)
                    {
                        transformedLabels = TransformLabels(_originalLabels, token);
                    }

                    if (createNew && !string.IsNullOrEmpty(outputFolder))
                    {
                        // Save transformed dataset to disk
                        SaveTransformedDataset(transformedVolume, outputFolder, token);
                    }
                }, token);

                if (createNew)
                {
                    MessageBox.Show($"Transformed dataset saved to {outputFolder}", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    // Update the main form with the transformed dataset
                    _mainForm.volumeData = transformedVolume;
                    _mainForm.volumeLabels = transformedLabels;
                    _mainForm.pixelSize = _newPixelSize;

                    // Notify main form that dataset has changed
                    _mainForm.OnDatasetChanged();

                    MessageBox.Show("Transformations applied to current dataset.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                Logger.Log($"[TransformDatasetForm] Applied transformations: Crop={_cropRectXY}, Rotation=({_rotationX},{_rotationY},{_rotationZ}), " +
                           $"Scale=({_scaleX},{_scaleY},{_scaleZ}), Translate=({_translateX},{_translateY},{_translateZ}), " +
                           $"NewDims={_newWidth}x{_newHeight}x{_newDepth}, PixelSize={_newPixelSize}");

                // Close the form if creating a new dataset
                if (createNew)
                {
                    this.Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying transformations: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[TransformDatasetForm] Error applying transformations: {ex.Message}");
            }
            finally
            {
                // Hide progress bar
                _progressBar.Visible = false;
                _lblProgress.Visible = false;

                // Re-enable buttons
                _btnReset.Enabled = true;
                _btnApplyToCurrentDataset.Enabled = true;
                _btnCreateNewDataset.Enabled = true;
            }
        }

        private ChunkedVolume TransformVolume(ChunkedVolume sourceVolume, CancellationToken token)
        {
            // Create a new volume with the transformed dimensions
            ChunkedVolume transformedVolume = new ChunkedVolume(_newWidth, _newHeight, _newDepth);

            // Get source crop parameters
            int cropX = _cropRectXY.X;
            int cropY = _cropRectXY.Y;
            int cropZ = _cropRectXZ.Y;
            int cropWidth = _cropRectXY.Width;
            int cropHeight = _cropRectXY.Height;
            int cropDepth = _cropRectXZ.Height;

            // Calculate transformation matrix
            Matrix4x4 transformMatrix = CalculateTransformationMatrix();

            // Process each voxel in the new volume
            int totalVoxels = _newWidth * _newHeight * _newDepth;
            int processedVoxels = 0;

            // Process slices in parallel
            Parallel.For(0, _newDepth, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount }, z =>
            {
                for (int y = 0; y < _newHeight; y++)
                {
                    // Update progress every 100 rows
                    if (y % 100 == 0)
                    {
                        int sliceVoxels = _newWidth * _newHeight;
                        int sliceProgress = (int)((z * sliceVoxels + y * _newWidth) / (double)totalVoxels * 100);
                        this.BeginInvoke(new Action(() =>
                        {
                            _progressBar.Value = Math.Min(sliceProgress, 100);
                            _lblProgress.Text = $"Processing... {sliceProgress}%";
                        }));
                    }

                    for (int x = 0; x < _newWidth; x++)
                    {
                        // Apply inverse transformation to get source coordinates
                        (double srcX, double srcY, double srcZ) = ApplyInverseTransform(x, y, z, transformMatrix);

                        // Check if the source coordinates are within the valid range
                        if (srcX >= cropX && srcX < cropX + cropWidth &&
                            srcY >= cropY && srcY < cropY + cropHeight &&
                            srcZ >= cropZ && srcZ < cropZ + cropDepth)
                        {
                            // Use trilinear interpolation to get the voxel value
                            byte value = SampleVolumeTrilinear(sourceVolume, srcX, srcY, srcZ);

                            // Set the voxel in the transformed volume
                            transformedVolume.SetVoxel(x, y, z, value);
                        }
                    }
                }

                Interlocked.Add(ref processedVoxels, _newWidth * _newHeight);
            });

            return transformedVolume;
        }

        private ChunkedLabelVolume TransformLabels(ChunkedLabelVolume sourceLabels, CancellationToken token)
        {
            // Create a new label volume with the transformed dimensions
            ChunkedLabelVolume transformedLabels = new ChunkedLabelVolume(_newWidth, _newHeight, _newDepth, 256, false, null);

            // Get source crop parameters
            int cropX = _cropRectXY.X;
            int cropY = _cropRectXY.Y;
            int cropZ = _cropRectXZ.Y;
            int cropWidth = _cropRectXY.Width;
            int cropHeight = _cropRectXY.Height;
            int cropDepth = _cropRectXZ.Height;

            // Calculate transformation matrix
            Matrix4x4 transformMatrix = CalculateTransformationMatrix();

            // Process each voxel in the new volume
            int totalVoxels = _newWidth * _newHeight * _newDepth;
            int processedVoxels = 0;

            // Process slices in parallel
            Parallel.For(0, _newDepth, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount }, z =>
            {
                for (int y = 0; y < _newHeight; y++)
                {
                    // Update progress every 100 rows
                    if (y % 100 == 0)
                    {
                        int sliceVoxels = _newWidth * _newHeight;
                        int sliceProgress = (int)((z * sliceVoxels + y * _newWidth) / (double)totalVoxels * 100);
                        this.BeginInvoke(new Action(() =>
                        {
                            _progressBar.Value = Math.Min(sliceProgress, 100);
                            _lblProgress.Text = $"Processing labels... {sliceProgress}%";
                        }));
                    }

                    for (int x = 0; x < _newWidth; x++)
                    {
                        // Apply inverse transformation to get source coordinates
                        (double srcX, double srcY, double srcZ) = ApplyInverseTransform(x, y, z, transformMatrix);

                        // Check if the source coordinates are within the valid range
                        if (srcX >= cropX && srcX < cropX + cropWidth &&
                            srcY >= cropY && srcY < cropY + cropHeight &&
                            srcZ >= cropZ && srcZ < cropZ + cropDepth)
                        {
                            // For labels, use nearest neighbor interpolation
                            byte value = SampleVolumeNearestNeighbor(sourceLabels, srcX, srcY, srcZ);

                            // Set the voxel in the transformed volume
                            transformedLabels.SetVoxel(x, y, z, value);
                        }
                    }
                }

                Interlocked.Add(ref processedVoxels, _newWidth * _newHeight);
            });

            return transformedLabels;
        }

        private void SaveTransformedDataset(ChunkedVolume volume, string outputFolder, CancellationToken token)
        {
            // Create BMP files from the volume slices
            for (int z = 0; z < volume.Depth; z++)
            {
                if (token.IsCancellationRequested)
                    break;

                // Update progress
                int progress = (int)((z + 1) / (double)volume.Depth * 100);
                this.BeginInvoke(new Action(() =>
                {
                    _progressBar.Value = Math.Min(progress, 100);
                    _lblProgress.Text = $"Saving slice {z + 1}/{volume.Depth}... {progress}%";
                }));

                // Get the XY slice data
                byte[] sliceData = volume.GetSliceXY(z);

                // Create a bitmap from the slice data
                using (Bitmap bitmap = new Bitmap(volume.Width, volume.Height, PixelFormat.Format8bppIndexed))
                {
                    // Set up a grayscale palette
                    ColorPalette palette = bitmap.Palette;
                    for (int i = 0; i < 256; i++)
                    {
                        palette.Entries[i] = Color.FromArgb(255, i, i, i);
                    }
                    bitmap.Palette = palette;

                    // Copy slice data to bitmap
                    BitmapData bitmapData = bitmap.LockBits(
                        new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                        ImageLockMode.WriteOnly,
                        bitmap.PixelFormat);

                    Marshal.Copy(sliceData, 0, bitmapData.Scan0, sliceData.Length);
                    bitmap.UnlockBits(bitmapData);

                    // Save the bitmap as BMP
                    string filename = Path.Combine(outputFolder, $"slice_{z:D5}.bmp");
                    bitmap.Save(filename, ImageFormat.Bmp);
                }
            }

            // Save metadata file with pixel size information
            string metadataFile = Path.Combine(outputFolder, "metadata.txt");
            File.WriteAllText(metadataFile, $"Width: {volume.Width}\nHeight: {volume.Height}\nDepth: {volume.Depth}\nPixelSize: {_newPixelSize}");
        }

        private Matrix4x4 CalculateTransformationMatrix()
        {
            // Create translation matrix
            Matrix4x4 translation = Matrix4x4.CreateTranslation(_translateX, _translateY, _translateZ);

            // Create rotation matrices (convert degrees to radians)
            float rotX = (float)(_rotationX * Math.PI / 180.0);
            float rotY = (float)(_rotationY * Math.PI / 180.0);
            float rotZ = (float)(_rotationZ * Math.PI / 180.0);

            // Calculate center of rotation (center of the crop rectangle)
            float centerX = _cropRectXY.Width / 2.0f;
            float centerY = _cropRectXY.Height / 2.0f;
            float centerZ = _cropRectXZ.Height / 2.0f;

            // Create translation matrices to move to and from origin
            Matrix4x4 translateToOrigin = Matrix4x4.CreateTranslation(-centerX, -centerY, -centerZ);
            Matrix4x4 translateFromOrigin = Matrix4x4.CreateTranslation(centerX, centerY, centerZ);

            // Create rotation matrices
            Matrix4x4 rotationX = Matrix4x4.CreateRotationX(rotX);
            Matrix4x4 rotationY = Matrix4x4.CreateRotationY(rotY);
            Matrix4x4 rotationZ = Matrix4x4.CreateRotationZ(rotZ);

            // Create scale matrix
            Matrix4x4 scale = Matrix4x4.CreateScale((float)_scaleX, (float)_scaleY, (float)_scaleZ);

            // Combine transformations: translate to origin, scale, rotate, translate back, then apply final translation
            Matrix4x4 transform = translateToOrigin * scale * rotationX * rotationY * rotationZ * translateFromOrigin * translation;

            return transform;
        }

        private (double, double, double) ApplyInverseTransform(int x, int y, int z, Matrix4x4 transform)
        {
            // Get the original dimensions
            double cropWidth = _cropRectXY.Width;
            double cropHeight = _cropRectXY.Height;
            double cropDepth = _cropRectXZ.Height;

            // If preserving dataset, adjust the transformation to account for the expanded volume
            if (_preserveDatasetDuringRotation && (Math.Abs(_rotationX) > 0.001 || Math.Abs(_rotationY) > 0.001 || Math.Abs(_rotationZ) > 0.001))
            {
                // Calculate center offsets for the new volume
                double centerOffsetX = (_newWidth / _scaleX - cropWidth) / 2.0;
                double centerOffsetY = (_newHeight / _scaleY - cropHeight) / 2.0;
                double centerOffsetZ = (_newDepth / _scaleZ - cropDepth) / 2.0;

                // Adjust coordinates to account for the expanded volume
                double normX = (x / (double)_newWidth) * (cropWidth + 2 * centerOffsetX) - centerOffsetX;
                double normY = (y / (double)_newHeight) * (cropHeight + 2 * centerOffsetY) - centerOffsetY;
                double normZ = (z / (double)_newDepth) * (cropDepth + 2 * centerOffsetZ) - centerOffsetZ;

                // Try to invert the transformation matrix
                Matrix4x4 invTransform;
                Matrix4x4.Invert(transform, out invTransform);

                // Apply inverse transformation with adjusted center
                Vector4 sourcePoint = Vector4Extensions.Transform(
                    new Vector4((float)normX, (float)normY, (float)normZ, 1.0f),
                    invTransform);

                // Add crop offsets
                double resultX = sourcePoint.X + _cropRectXY.X;
                double resultY = sourcePoint.Y + _cropRectXY.Y;
                double resultZ = sourcePoint.Z + _cropRectXZ.Y;

                return (resultX, resultY, resultZ);
            }
            else
            {
                // Original implementation for crop mode
                double normX = x / (double)_newWidth;
                double normY = y / (double)_newHeight;
                double normZ = z / (double)_newDepth;

                double sourceX = normX * cropWidth;
                double sourceY = normY * cropHeight;
                double sourceZ = normZ * cropDepth;

                // Try to invert the transformation matrix
                Matrix4x4 invTransform;
                Matrix4x4.Invert(transform, out invTransform);

                // Apply inverse transformation
                Vector4 sourcePoint = Vector4Extensions.Transform(
                    new Vector4((float)sourceX, (float)sourceY, (float)sourceZ, 1.0f),
                    invTransform);

                // Add crop offsets
                double resultX = sourcePoint.X + _cropRectXY.X;
                double resultY = sourcePoint.Y + _cropRectXY.Y;
                double resultZ = sourcePoint.Z + _cropRectXZ.Y;

                return (resultX, resultY, resultZ);
            }
        }

        private byte SampleVolumeTrilinear(ChunkedVolume volume, double x, double y, double z)
        {
            // Clamp coordinates to volume bounds
            x = Math.Max(0, Math.Min(volume.Width - 1.001, x));
            y = Math.Max(0, Math.Min(volume.Height - 1.001, y));
            z = Math.Max(0, Math.Min(volume.Depth - 1.001, z));

            // Get integer coordinates and fractions
            int x0 = (int)Math.Floor(x);
            int y0 = (int)Math.Floor(y);
            int z0 = (int)Math.Floor(z);
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            int z1 = z0 + 1;

            double xd = x - x0;
            double yd = y - y0;
            double zd = z - z0;

            // Get voxel values at corners
            byte v000 = volume.GetVoxel(x0, y0, z0);
            byte v001 = volume.GetVoxel(x0, y0, z1);
            byte v010 = volume.GetVoxel(x0, y1, z0);
            byte v011 = volume.GetVoxel(x0, y1, z1);
            byte v100 = volume.GetVoxel(x1, y0, z0);
            byte v101 = volume.GetVoxel(x1, y0, z1);
            byte v110 = volume.GetVoxel(x1, y1, z0);
            byte v111 = volume.GetVoxel(x1, y1, z1);

            // Interpolate along x axis
            double c00 = v000 * (1 - xd) + v100 * xd;
            double c01 = v001 * (1 - xd) + v101 * xd;
            double c10 = v010 * (1 - xd) + v110 * xd;
            double c11 = v011 * (1 - xd) + v111 * xd;

            // Interpolate along y axis
            double c0 = c00 * (1 - yd) + c10 * yd;
            double c1 = c01 * (1 - yd) + c11 * yd;

            // Interpolate along z axis
            double c = c0 * (1 - zd) + c1 * zd;

            // Convert back to byte
            return (byte)Math.Round(Math.Max(0, Math.Min(255, c)));
        }

        private byte SampleVolumeNearestNeighbor(ChunkedLabelVolume volume, double x, double y, double z)
        {
            // Clamp coordinates to volume bounds
            x = Math.Max(0, Math.Min(volume.Width - 1, x));
            y = Math.Max(0, Math.Min(volume.Height - 1, y));
            z = Math.Max(0, Math.Min(volume.Depth - 1, z));

            // Round to nearest integer
            int nx = (int)Math.Round(x);
            int ny = (int)Math.Round(y);
            int nz = (int)Math.Round(z);

            // Get voxel value
            return volume.GetVoxel(nx, ny, nz);
        }

        private void RotationControl_RotationChanged(object sender, RotationChangedEventArgs e)
        {
            // Update numeric controls
            _numRotX.Value = (decimal)Math.Round(e.RotationX, 1);
            _numRotY.Value = (decimal)Math.Round(e.RotationY, 1);
            _numRotZ.Value = (decimal)Math.Round(e.RotationZ, 1);

            // Update internal values
            _rotationX = e.RotationX;
            _rotationY = e.RotationY;
            _rotationZ = e.RotationZ;

            // Update the preview in real-time
            UpdatePreview();
        }

        private byte GetTransformedVoxelValue(ChunkedVolume volume, int x, int y, int z)
        {
            // Apply inverse transformations to get source coordinates
            Matrix4x4 transformMatrix = CalculateTransformationMatrix();
            Matrix4x4 invTransform;
            Matrix4x4.Invert(transformMatrix, out invTransform);

            // Apply transformations
            Vector4 pos = new Vector4(x, y, z, 1.0f);
            Vector4 srcPos = Vector4Extensions.Transform(pos, invTransform);

            // Get coordinates in original volume space
            int srcX = (int)Math.Round(srcPos.X);
            int srcY = (int)Math.Round(srcPos.Y);
            int srcZ = (int)Math.Round(srcPos.Z);

            // Check bounds
            if (srcX >= 0 && srcX < volume.Width &&
                srcY >= 0 && srcY < volume.Height &&
                srcZ >= 0 && srcZ < volume.Depth)
            {
                return volume[srcX, srcY, srcZ];
            }

            return 0; // Return black for out of bounds
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            // Cancel any pending operations
            _renderCts.Cancel();

            // Since Values property doesn't exist, we can't directly dispose the cache contents
            // But we're still handling disposal of views' images
            if (_xyView.Image != null) _xyView.Image.Dispose();
            if (_xzView.Image != null) _xzView.Image.Dispose();
            if (_yzView.Image != null) _yzView.Image.Dispose();
        }
    }

    public enum TransformViewType
    {
        XY,
        XZ,
        YZ
    }

    public enum ResizeHandleType
    {
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left
    }

    public class ResizeHandle
    {
        private const int HandleSize = 8;

        public ResizeHandleType Type { get; private set; }
        public Rectangle Bounds { get; private set; }
        private Rectangle _parentRect;

        public ResizeHandle(ResizeHandleType type, Rectangle parentRect)
        {
            Type = type;
            _parentRect = parentRect;
            UpdatePositionFromRect(parentRect);
        }

        public void UpdatePositionFromRect(Rectangle rect)
        {
            _parentRect = rect;
            int halfSize = HandleSize / 2;

            switch (Type)
            {
                case ResizeHandleType.TopLeft:
                    Bounds = new Rectangle(rect.X - halfSize, rect.Y - halfSize, HandleSize, HandleSize);
                    break;

                case ResizeHandleType.Top:
                    Bounds = new Rectangle(rect.X + rect.Width / 2 - halfSize, rect.Y - halfSize, HandleSize, HandleSize);
                    break;

                case ResizeHandleType.TopRight:
                    Bounds = new Rectangle(rect.Right - halfSize, rect.Y - halfSize, HandleSize, HandleSize);
                    break;

                case ResizeHandleType.Right:
                    Bounds = new Rectangle(rect.Right - halfSize, rect.Y + rect.Height / 2 - halfSize, HandleSize, HandleSize);
                    break;

                case ResizeHandleType.BottomRight:
                    Bounds = new Rectangle(rect.Right - halfSize, rect.Bottom - halfSize, HandleSize, HandleSize);
                    break;

                case ResizeHandleType.Bottom:
                    Bounds = new Rectangle(rect.X + rect.Width / 2 - halfSize, rect.Bottom - halfSize, HandleSize, HandleSize);
                    break;

                case ResizeHandleType.BottomLeft:
                    Bounds = new Rectangle(rect.X - halfSize, rect.Bottom - halfSize, HandleSize, HandleSize);
                    break;

                case ResizeHandleType.Left:
                    Bounds = new Rectangle(rect.X - halfSize, rect.Y + rect.Height / 2 - halfSize, HandleSize, HandleSize);
                    break;
            }
        }

        public bool HitTest(Point point, float zoom)
        {
            // Scale handle size based on zoom
            int scaledSize = (int)(HandleSize / zoom);

            // Create a scaled bounds for hit testing
            Rectangle scaledBounds = new Rectangle(
                (int)(Bounds.X - scaledSize / 2.0f + HandleSize / 2.0f),
                (int)(Bounds.Y - scaledSize / 2.0f + HandleSize / 2.0f),
                scaledSize,
                scaledSize
            );

            return scaledBounds.Contains(point);
        }

        public void Draw(Graphics g, float zoom)
        {
            using (Brush handleBrush = new SolidBrush(Color.Yellow))
            {
                // Draw a scaled handle
                int scaledSize = (int)(HandleSize / zoom);

                float centerX = Bounds.X + HandleSize / 2.0f;
                float centerY = Bounds.Y + HandleSize / 2.0f;

                g.FillRectangle(handleBrush,
                    centerX - scaledSize / 2.0f,
                    centerY - scaledSize / 2.0f,
                    scaledSize,
                    scaledSize);
            }
        }

        public Rectangle UpdateRectangle(Rectangle rect, Point delta)
        {
            Rectangle newRect = rect;

            switch (Type)
            {
                case ResizeHandleType.TopLeft:
                    newRect.X += delta.X;
                    newRect.Y += delta.Y;
                    newRect.Width -= delta.X;
                    newRect.Height -= delta.Y;
                    break;

                case ResizeHandleType.Top:
                    newRect.Y += delta.Y;
                    newRect.Height -= delta.Y;
                    break;

                case ResizeHandleType.TopRight:
                    newRect.Y += delta.Y;
                    newRect.Width += delta.X;
                    newRect.Height -= delta.Y;
                    break;

                case ResizeHandleType.Right:
                    newRect.Width += delta.X;
                    break;

                case ResizeHandleType.BottomRight:
                    newRect.Width += delta.X;
                    newRect.Height += delta.Y;
                    break;

                case ResizeHandleType.Bottom:
                    newRect.Height += delta.Y;
                    break;

                case ResizeHandleType.BottomLeft:
                    newRect.X += delta.X;
                    newRect.Width -= delta.X;
                    newRect.Height += delta.Y;
                    break;

                case ResizeHandleType.Left:
                    newRect.X += delta.X;
                    newRect.Width -= delta.X;
                    break;
            }

            return newRect;
        }
    }
}