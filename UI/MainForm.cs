using Krypton.Docking;
using Krypton.Navigator;
using Krypton.Toolkit;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTSegmenter
{
    // ------------------------------------------------------------------------
    // MainForm – main viewer and controller of segmentation with optimized rendering
    // ------------------------------------------------------------------------
    public partial class MainForm : KryptonForm
    {
        #region Fields
        private KryptonManager _kryptonManager;
        private KryptonDockingManager _dockingManager;
        // Volume metadata
        private int width, height, depth;
        public double pixelSize = 1e-6;
        public int SelectedBinningFactor { get; set; } = 1;
        private OrthogonalViewPanel orthogonalView;
        Panel infoPanel;
        public string CurrentPath { get; private set; } = "";
        private bool useMemoryMapping = true;

        // Materials: index 0 is reserved for the exterior.
        public List<Material> Materials = new List<Material>();
        public IMaterialOperations MaterialOps { get; private set; }

        // The volume data (grayscale) and segmentation (labels)
        public IGrayscaleVolumeData volumeData;
        public ILabelVolumeData volumeLabels;
        private readonly object datasetLock = new object();

        // UI and display elements
        private TableLayoutPanel mainLayout;
        private Panel xyPanel, xzPanel, yzPanel;
        public ScrollablePictureBox xyView, xzView, yzView;
        private Label xyLabel, xzLabel, yzLabel;

        // Cached rendered views
        private Bitmap xyBitmap, xzBitmap, yzBitmap;
        private readonly ReaderWriterLockSlim bitmapLock = new ReaderWriterLockSlim();
        private CancellationTokenSource renderCts = new CancellationTokenSource();
        private bool isRenderingOrtho = false;

        // Display state for all views
        public float xyZoom = 1.0f, xzZoom = 1.0f, yzZoom = 1.0f;
        public PointF xyPan = PointF.Empty, xzPan = PointF.Empty, yzPan = PointF.Empty;

        // Current XY slice and orthoview positions
        private int currentSlice;
        public int CurrentSlice
        {
            get => currentSlice;
            set
            {
                if (currentSlice != value)
                {
                    currentSlice = Math.Max(0, Math.Min(value, depth - 1));
                    RenderViews(ViewType.XY);
                    NotifySliceChangeCallbacks(currentSlice);
                    if (orthogonalView != null)
                        orthogonalView.UpdatePosition(YzSliceX, XzSliceY, currentSlice);
                }
            }
        }

        private int xzSliceY;
        public int XzSliceY
        {
            get => xzSliceY;
            set
            {
                if (xzSliceY != value)
                {
                    xzSliceY = Math.Max(0, Math.Min(value, height - 1));
                    RenderViews(ViewType.XZ);
                    NotifyXZRowChangeCallbacks(xzSliceY);
                    if (orthogonalView != null)
                        orthogonalView.UpdatePosition(YzSliceX, xzSliceY, currentSlice);
                }
            }
        }

        private int yzSliceX;
        public int YzSliceX
        {
            get => yzSliceX;
            set
            {
                if (yzSliceX != value)
                {
                    yzSliceX = Math.Max(0, Math.Min(value, width - 1));
                    RenderViews(ViewType.YZ);
                    NotifyYZColChangeCallbacks(yzSliceX);
                    if (orthogonalView != null)
                        orthogonalView.UpdatePosition(yzSliceX, xzSliceY, currentSlice);
                }
            }
        }


        // Render options
        public bool ShowMask { get; set; } = false;
        public bool EnableThresholdMask { get; set; } = true;
        public bool RenderMaterials { get; set; } = false;
        public bool ShowProjections { get; private set; } = true;

        // Segmentation tools and selection
        public SegmentationTool currentTool = SegmentationTool.Pan;
        private int currentBrushSize = 50;
        private bool showBrushOverlay = false;
        private Point brushOverlayCenter;
        private PointF xzOverlayCenter = PointF.Empty;
        private PointF yzOverlayCenter = PointF.Empty;

        // Current temporary selections
        public byte[,] currentSelection;
        public byte[,] currentSelectionXZ;
        public byte[,] currentSelectionYZ;

        // For interpolation
        public bool[,,] interpolatedMask;

        // Sparse brush selections for different views
        public Dictionary<int, byte[,]> sparseSelectionsZ = new Dictionary<int, byte[,]>();
        public Dictionary<int, byte[,]> sparseSelectionsY = new Dictionary<int, byte[,]>();
        public Dictionary<int, byte[,]> sparseSelectionsX = new Dictionary<int, byte[,]>();

        // Material selection
        public int SelectedMaterialIndex { get; set; } = -1;
        public byte PreviewMin { get; set; }
        public byte PreviewMax { get; set; }

        // Box drawing state
        private bool isBoxDrawingPossible = false;
        private Point boxStartPoint;
        private Point boxCurrentPoint;
        private bool isActuallyDrawingBox = false;

        private bool isXzBoxDrawingPossible = false;
        private Point xzBoxStartPoint;
        private Point xzBoxCurrentPoint;
        private bool isXzActuallyDrawingBox = false;

        private bool isYzBoxDrawingPossible = false;
        private Point yzBoxStartPoint;
        private Point yzBoxCurrentPoint;
        private bool isYzActuallyDrawingBox = false;

        // Callbacks
        private List<Action<int>> sliceChangeCallbacks = new List<Action<int>>();
        private List<Action<int>> xzRowChangeCallbacks = new List<Action<int>>();
        private List<Action<int>> yzColChangeCallbacks = new List<Action<int>>();

        // For SAM annotations
        public AnnotationManager AnnotationMgr { get; set; }

        // Enumeration for view types
        public enum ViewType { XY, XZ, YZ, All }
        public enum Axis { X, Y, Z }

        public enum OrthogonalView { XZ, YZ }

        //Dark theme

        private readonly KryptonManager _km = new KryptonManager();     // one per application
        private KryptonDockingManager _dock;

        #endregion

        #region Initialization and Setup
        public MainForm(string[] args)
        {
            try
            {

                Logger.Log("[MainForm] Constructor start.");
                try
                {
                    string iconPath = Path.Combine(Application.StartupPath, "favicon.ico");
                    if (File.Exists(iconPath))
                        this.Icon = new Icon(iconPath);
                }
                catch { }
                // Set basic form properties
                this.Text = "CT Segmentation Suite - Main Viewer";
                this.Size = new Size(1700, 800);
                this.DoubleBuffered = true;
                var km = new KryptonManager();
                km.GlobalPaletteMode = PaletteMode.Office2010BlackDarkMode;

                // Create and configure the main layout
                SetupMainLayout();
                InitializeDocking();
                // Initialize materials
                InitializeMaterials();

                // If started with an argument, load dataset
                if (args.Length > 0)
                {
                    _ = LoadDatasetAsync(args[0]);
                }

                Logger.Log("[MainForm] Constructor end.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error in MainForm constructor: {ex.Message}\n\n{ex.StackTrace}",
                               "Constructor Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void EnsureSquareLayout()
        {
            // This method ensures that cells in the TableLayoutPanel stay square
            // by adjusting the form size if necessary

            if (mainLayout == null) return;

            // Get the actual size of the layout panel
            int layoutWidth = mainLayout.Width;
            int layoutHeight = mainLayout.Height;

            // Calculate the ideal size for square cells
            int cellSize = Math.Min(layoutWidth / 2, layoutHeight / 2);

            // Update row and column styles to maintain equal sizing
            // This should help maintain square viewers
            for (int i = 0; i < mainLayout.ColumnStyles.Count; i++)
            {
                mainLayout.ColumnStyles[i] = new ColumnStyle(SizeType.Percent, 50F);
            }

            for (int i = 0; i < mainLayout.RowStyles.Count; i++)
            {
                mainLayout.RowStyles[i] = new RowStyle(SizeType.Percent, 50F);
            }
        }
        private void InitializeDocking()
        {
            //--------------------------------------------------------------
            // Global dark palette
            //--------------------------------------------------------------
            _kryptonManager = new KryptonManager();
            _kryptonManager.GlobalPaletteMode = PaletteMode.Office2010Black; // dark

            //--------------------------------------------------------------
            // Host panel (acts like a "document" workspace)
            //--------------------------------------------------------------
            var hostPanel = new KryptonPanel { Dock = DockStyle.Fill };
            Controls.Add(hostPanel);
            hostPanel.Controls.Add(mainLayout); // mainLayout is your 2×2 grid
            hostPanel.BringToFront();

            //--------------------------------------------------------------
            // Docking manager
            //--------------------------------------------------------------
            _dockingManager = new KryptonDockingManager();
            _dockingManager.ManageControl("MainHost", hostPanel);

            //--------------------------------------------------------------
            // Create ControlForm as dockable page on the right
            //--------------------------------------------------------------
            var ctrlForm = new ControlForm(this)
            {
                TopLevel = false,
                FormBorderStyle = FormBorderStyle.None,
                Dock = DockStyle.Fill
            };

            // Create a KryptonPage to host the ControlForm
            var page = new KryptonPage
            {
                Text = "Controls",
                UniqueName = "ControlsPage",
                MinimumSize = new Size(700, 0),  // Set minimum width to 700px
                TextTitle = "Controls"           // Set the page title correctly
            };

            // Configure page flags to allow floating and docking
            // First clear any existing flags that might interfere
            page.ClearFlags(KryptonPageFlags.DockingAllowClose);

            // Then set the flags we want
            page.SetFlags(KryptonPageFlags.DockingAllowFloating |
                         KryptonPageFlags.DockingAllowDocked);

            // Handle form closing to exit the application
            ctrlForm.FormClosing += (s, e) =>
            {
                Application.Exit();
            };

            page.Controls.Add(ctrlForm);
            ctrlForm.Show();

            // Create the dockspace on the right with this page
            var dockspace = _dockingManager.AddDockspace("MainHost", DockingEdge.Right, new[] { page });

            // Ensure the dockspace has adequate width
            if (dockspace?.DockspaceControl != null)
            {
                dockspace.DockspaceControl.Width = 700;
                dockspace.DockspaceControl.MinimumSize = new Size(700, 0);
            }

            // Add a custom handler to the docking manager to handle page removal
            _dockingManager.PageCloseRequest += (s, e) =>
            {
                // When any page is requested to close, exit the application
                Application.Exit();
            };

            // Subscribe to the application exit event to ensure clean shutdown
            Application.ApplicationExit += (s, e) =>
            {
                try
                {
                    if (!IsDisposed)
                    {
                        Dispose();
                    }
                }
                catch { }
            };

            // Handle form closing to properly exit the application
            this.FormClosing += (s, e) =>
            {
                Application.Exit();
            };
        }

        public void DockExternalControlForm(ControlForm ctrl)
        {
            //----------------------------------------------------------------------
            // 1) prepare the already‑created ControlForm
            //----------------------------------------------------------------------
            ctrl.TopLevel = false;
            ctrl.FormBorderStyle = FormBorderStyle.None; // kills old title‑bar
            ctrl.Dock = DockStyle.Fill;

            // wrap it in a KryptonPage
            KryptonPage page = new KryptonPage
            {
                Text = "Controls",
                MinimumSize = new Size(700, 0),  // keeps a nice width
                TextTitle = "Controls"
            };

            // Configure page flags to allow floating and docking
            page.ClearFlags(KryptonPageFlags.DockingAllowClose);

            page.SetFlags(KryptonPageFlags.DockingAllowFloating |
                         KryptonPageFlags.DockingAllowDocked);

            // Handle form closing to exit the application
            ctrl.FormClosing += (s, e) =>
            {
                Application.Exit();
            };

            page.Controls.Add(ctrl);

            //----------------------------------------------------------------------
            // 2) create the docking manager and register this MainForm as host
            //----------------------------------------------------------------------
            _dock = new KryptonDockingManager();
            _dock.ManageControl("MainHost", this);

            // Add a custom handler to the docking manager to handle page removal
            _dock.PageCloseRequest += (s, e) =>
            {
                // When any page is requested to close, exit the application
                Application.Exit();
            };

            //----------------------------------------------------------------------
            // 3) add a RIGHT‑hand dock‑space and drop the page into it
            //    (the overload in *all* Krypton builds is: AddDockspace(path, edge, pages[])
            //----------------------------------------------------------------------
            KryptonDockingDockspace dockElem = _dock.AddDockspace(
                "MainHost",
                DockingEdge.Right,
                new KryptonPage[] { page });   // MUST be an array

            //----------------------------------------------------------------------
            // 4) make the pane wider by default – tweak the real Dockspace control
            //----------------------------------------------------------------------
            if (dockElem?.DockspaceControl != null)
            {
                dockElem.DockspaceControl.Width = 700;  // default width
                dockElem.DockspaceControl.MinimumSize = new Size(700, 0);
            }

            //----------------------------------------------------------------------
            // 5) finally show the embedded form
            //----------------------------------------------------------------------
            ctrl.Show();

            // Handle the form closing event to ensure clean shutdown
            this.FormClosing += (s, e) =>
            {
                Application.Exit();
            };
        }
        private void SetupMainLayout()
        {
            try
            {
                this.BackColor = Color.Black;

                // Create the main 2x2 grid layout
                mainLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 2,
                    CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
                    BackColor = Color.Black
                };

                // Configure column and row styles to maintain strict 50/50 splits
                mainLayout.ColumnStyles.Clear();
                mainLayout.RowStyles.Clear();
                mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
                mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
                mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
                mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

                // Create panels for the PictureBoxes
                xyPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
                xzPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
                yzPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
                infoPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

                // Create the PictureBoxes with specific settings for maintaining aspect ratio
                xyView = new ScrollablePictureBox
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.Black,
                    SizeMode = PictureBoxSizeMode.Zoom  // This helps maintain aspect ratio
                };

                xzView = new ScrollablePictureBox
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.Black,
                    SizeMode = PictureBoxSizeMode.Zoom
                };

                yzView = new ScrollablePictureBox
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.Black,
                    SizeMode = PictureBoxSizeMode.Zoom
                };

                // Create labels
                xyLabel = new Label
                {
                    Text = "XY View",
                    ForeColor = Color.Yellow,
                    Dock = DockStyle.Top,
                    Height = 20,
                    Font = new Font("Arial", 12, FontStyle.Bold)
                };

                xzLabel = new Label
                {
                    Text = "XZ View",
                    ForeColor = Color.Red,
                    Dock = DockStyle.Top,
                    Height = 20,
                    Font = new Font("Arial", 12, FontStyle.Bold)
                };

                yzLabel = new Label
                {
                    Text = "YZ View",
                    ForeColor = Color.Green,
                    Dock = DockStyle.Top,
                    Height = 20,
                    Font = new Font("Arial", 12, FontStyle.Bold)
                };

                // Add labels to panels
                xyPanel.Controls.Add(xyLabel);
                xzPanel.Controls.Add(xzLabel);
                yzPanel.Controls.Add(yzLabel);

                // Add PictureBoxes to panels
                xyPanel.Controls.Add(xyView);
                xzPanel.Controls.Add(xzView);
                yzPanel.Controls.Add(yzView);

                // Add panels to the main layout
                mainLayout.Controls.Add(xyPanel, 0, 0);
                mainLayout.Controls.Add(yzPanel, 1, 0);
                mainLayout.Controls.Add(xzPanel, 0, 1);
                mainLayout.Controls.Add(infoPanel, 1, 1);

                // Create orthogonal view panel
                orthogonalView = new OrthogonalViewPanel(this) { Dock = DockStyle.Fill };
                infoPanel.Controls.Add(orthogonalView);

                // Set up event handlers
                xyView.MouseDown += (s, e) => ViewMouseDown(ViewType.XY, e);
                xyView.MouseMove += (s, e) => ViewMouseMove(ViewType.XY, e);
                xyView.MouseUp += (s, e) => ViewMouseUp(ViewType.XY, e);
                xyView.MouseWheel += (s, e) => ViewMouseWheel(ViewType.XY, e);
                xyView.Paint += (s, e) => ViewPaint(ViewType.XY, e);

                xzView.MouseDown += (s, e) => ViewMouseDown(ViewType.XZ, e);
                xzView.MouseMove += (s, e) => ViewMouseMove(ViewType.XZ, e);
                xzView.MouseUp += (s, e) => ViewMouseUp(ViewType.XZ, e);
                xzView.MouseWheel += (s, e) => ViewMouseWheel(ViewType.XZ, e);
                xzView.Paint += (s, e) => ViewPaint(ViewType.XZ, e);

                yzView.MouseDown += (s, e) => ViewMouseDown(ViewType.YZ, e);
                yzView.MouseMove += (s, e) => ViewMouseMove(ViewType.YZ, e);
                yzView.MouseUp += (s, e) => ViewMouseUp(ViewType.YZ, e);
                yzView.MouseWheel += (s, e) => ViewMouseWheel(ViewType.YZ, e);
                yzView.Paint += (s, e) => ViewPaint(ViewType.YZ, e);

                // Set resize handler for the form
                this.Resize += (s, e) =>
                {
                    // Clamp pan values when resizing
                    foreach (ViewType viewType in Enum.GetValues(typeof(ViewType)))
                    {
                        if (viewType != ViewType.All)
                            ClampPanForView(viewType);
                    }
                    this.Invalidate(true);
                };

                Logger.Log("[SetupMainLayout] Layout successfully created.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SetupMainLayout] Error: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Error setting up layout: {ex.Message}", "Layout Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void InitializeComponent()
        {
            this.SetShowProjections(true);
            this.Text = "CT Segmentation Suite - Main Viewer";
            this.Size = new Size(1000, 800);
            this.DoubleBuffered = true;

            try
            {
                string iconPath = Path.Combine(Application.StartupPath, "favicon.ico");
                if (File.Exists(iconPath))
                    this.Icon = new Icon(iconPath);
            }
            catch (Exception ex)
            {
                Logger.Log("[InitializeComponent] Error setting icon: " + ex);
            }

            // Create the main layout table (2x2 grid)
            mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
            };

            // Setup equal sizing for all 4 panels
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            // Setup panels for each view with a label
            xyPanel = CreateViewPanel(out xyView, out xyLabel, "XY View", Color.Yellow);
            xzPanel = CreateViewPanel(out xzView, out xzLabel, "XZ View", Color.Red);
            yzPanel = CreateViewPanel(out yzView, out yzLabel, "YZ View", Color.Green);

            // Add panels to the layout
            mainLayout.Controls.Add(xyPanel, 0, 0);
            mainLayout.Controls.Add(yzPanel, 1, 0);
            mainLayout.Controls.Add(xzPanel, 0, 1);

            // Info panel for bottom-right
            infoPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            // Create and add the 3D orthogonal view panel
            orthogonalView = new OrthogonalViewPanel(this)
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            // Add the orthogonal view to the info panel
            infoPanel.Controls.Add(orthogonalView);
            mainLayout.Controls.Add(infoPanel, 1, 1);

            // Connect events for the main views
            SetupViewEvents(xyView, ViewType.XY);
            SetupViewEvents(xzView, ViewType.XZ);
            SetupViewEvents(yzView, ViewType.YZ);

            // Set up form resize event
            this.Resize += (s, e) =>
            {
                foreach (ViewType viewType in Enum.GetValues(typeof(ViewType)))
                {
                    if (viewType != ViewType.All)
                        ClampPanForView(viewType);
                }
                this.Invalidate(true);
            };

            this.SetStyle(ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint, true);
        }

        private Panel CreateViewPanel(out ScrollablePictureBox pictureBox, out Label label, string labelText, Color labelColor)
        {
            Panel panel = new Panel { Dock = DockStyle.Fill };

            // Create label at top
            label = new Label
            {
                Text = labelText,
                ForeColor = labelColor,
                BackColor = Color.Black,
                Font = new Font("Arial", 10, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = 20,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(5, 0, 0, 0)
            };

            // Create scrollable picture box
            pictureBox = new ScrollablePictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            panel.Controls.Add(pictureBox);
            panel.Controls.Add(label);

            return panel;
        }

        private void SetupViewEvents(ScrollablePictureBox pictureBox, ViewType viewType)
        {
            // Mouse events
            pictureBox.MouseDown += (s, e) => ViewMouseDown(viewType, e);
            pictureBox.MouseMove += (s, e) => ViewMouseMove(viewType, e);
            pictureBox.MouseUp += (s, e) => ViewMouseUp(viewType, e);
            pictureBox.MouseWheel += (s, e) => ViewMouseWheel(viewType, e);

            // Paint event
            pictureBox.Paint += (s, e) => ViewPaint(viewType, e);
        }

        private void InitializeMaterials()
        {
            Materials.Clear();
            Materials.Add(new Material("Exterior", Color.Transparent, 0, 0, 0) { IsExterior = true });
            Materials.Add(new Material("Material1", Color.Blue, 0, 0, GetNextMaterialID()));
            SaveLabelsChk();
        }
        #endregion

        #region Public Interface Methods
        // Maintain public API for ControlForm compatibility

        public int GetWidth() => width;
        public int GetHeight() => height;
        public int GetDepth() => depth;
        public double GetPixelSize() => pixelSize;

        public void SetShowProjections(bool enable)
        {
            ShowProjections = enable;

            // Update visibility of the ortho views
            if (xzPanel != null) xzPanel.Visible = enable;
            if (yzPanel != null) yzPanel.Visible = enable;

            if (enable)
                RenderViews(ViewType.All);
            else
                RenderViews(ViewType.XY);
        }

        public bool GetShowProjections() => ShowProjections;

        public void SetUseMemoryMapping(bool useIt)
        {
            useMemoryMapping = useIt;
            Logger.Log($"[SetUseMemoryMapping] useMemoryMapping set to {useMemoryMapping}");
        }

        public void SetSegmentationTool(SegmentationTool tool)
        {
            currentTool = tool;

            // Clear any threshold preview if switching from thresholding
            if (tool != SegmentationTool.Thresholding)
            {
                PreviewMin = 0;
                PreviewMax = 0;
                EnableThresholdMask = false;
            }

            // Update the views
            RenderViews(ViewType.All);
        }

        public void ShowBrushOverlay(int size)
        {
            currentBrushSize = size;

            // Center the overlay on the current view
            brushOverlayCenter = new Point(xyView.ClientSize.Width / 2, xyView.ClientSize.Height / 2);
            showBrushOverlay = true;
            xyView.Invalidate();

            // Auto-hide overlay after a delay
            Task.Delay(1000).ContinueWith(_ =>
            {
                showBrushOverlay = false;
                xyView.Invalidate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void HideBrushOverlay()
        {
            showBrushOverlay = false;
            xyView.Invalidate();
        }

        public void ResetView()
        {
            // Reset zoom levels
            xyZoom = 1.0f;
            xzZoom = 1.0f;
            yzZoom = 1.0f;

            // Reset pan positions
            xyPan = PointF.Empty;
            xzPan = PointF.Empty;
            yzPan = PointF.Empty;

            // Re-render all views
            RenderViews(ViewType.All);
        }

        public byte GetNextMaterialID()
        {
            if (MaterialOps == null)
            {
                // Fallback implementation
                byte nextID = 1;
                while (Materials.Any(m => m.ID == nextID))
                {
                    nextID++;
                    if (nextID == 0) throw new InvalidOperationException("No available material IDs.");
                }
                return nextID;
            }

            return MaterialOps.GetNextMaterialID();
        }

        // Callback registration
        public void RegisterSliceChangeCallback(Action<int> callback)
        {
            if (callback != null && !sliceChangeCallbacks.Contains(callback))
                sliceChangeCallbacks.Add(callback);
        }

        public void UnregisterSliceChangeCallback(Action<int> callback)
        {
            if (callback != null)
                sliceChangeCallbacks.Remove(callback);
        }

        public void RegisterXZRowChangeCallback(Action<int> callback)
        {
            if (callback != null && !xzRowChangeCallbacks.Contains(callback))
                xzRowChangeCallbacks.Add(callback);
        }

        public void UnregisterXZRowChangeCallback(Action<int> callback)
        {
            if (callback != null)
                xzRowChangeCallbacks.Remove(callback);
        }

        public void RegisterYZColChangeCallback(Action<int> callback)
        {
            if (callback != null && !yzColChangeCallbacks.Contains(callback))
                yzColChangeCallbacks.Add(callback);
        }

        public void UnregisterYZColChangeCallback(Action<int> callback)
        {
            if (callback != null)
                yzColChangeCallbacks.Remove(callback);
        }

        private void NotifySliceChangeCallbacks(int slice)
        {
            foreach (var callback in sliceChangeCallbacks)
            {
                try { callback(slice); } catch { }
            }
        }

        private void NotifyXZRowChangeCallbacks(int row)
        {
            foreach (var callback in xzRowChangeCallbacks)
            {
                try { callback(row); } catch { }
            }
        }

        private void NotifyYZColChangeCallbacks(int col)
        {
            foreach (var callback in yzColChangeCallbacks)
            {
                try { callback(col); } catch { }
            }
        }
        #endregion

        #region Data Loading and Saving
        public async Task LoadDatasetAsync(string path)
        {
            Logger.Log($"[LoadDatasetAsync] Loading dataset from path: {path}");
            ProgressFormWithProgress progressForm = null;

            try
            {
                await this.SafeInvokeAsync(() =>
                {
                    progressForm = new ProgressFormWithProgress("Loading dataset...");
                    progressForm.Show();
                });

                CurrentPath = path;
                renderCts.Cancel();
                renderCts = new CancellationTokenSource();

                // Dispose of any previously loaded volume and labels
                lock (datasetLock)
                {
                    volumeData?.Dispose();
                    volumeData = null;
                    if (volumeLabels != null)
                    {
                        volumeLabels.ReleaseFileLock();
                        volumeLabels.Dispose();
                        volumeLabels = null;
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();

                bool folderMode = Directory.Exists(path);
                string labelsChkPath;

                // Determine the correct path to look for labels.chk
                if (folderMode)
                {
                    // Path is a directory - look for labels.chk directly inside
                    labelsChkPath = Path.Combine(path, "labels.chk");
                }
                else
                {
                    // Path is a file - look for labels.chk in the parent directory
                    string directoryPath = Path.GetDirectoryName(path);
                    labelsChkPath = Path.Combine(directoryPath, "labels.chk");
                }

                bool labelsChkExists = File.Exists(labelsChkPath);
                Logger.Log($"[LoadDatasetAsync] Checking for materials file: {labelsChkPath}, exists: {labelsChkExists}");

                // Ask for pixel size only in folder mode
                if (folderMode)
                {
                    double? userPixelSize = await Task.Run(() => AskUserPixelSize());
                    if (!userPixelSize.HasValue || userPixelSize.Value <= 0)
                    {
                        Logger.Log("[LoadDatasetAsync] Pixel size not provided or invalid.");
                        return;
                    }
                    pixelSize = userPixelSize.Value;
                    Logger.Log($"[LoadDatasetAsync] Using pixel size from user input: {pixelSize}");
                }

                // First load materials from labels.chk if it exists
                if (labelsChkExists)
                {
                    try
                    {
                        // Use the directory path for FileOperations.ReadLabelsChk
                        string directoryPath = folderMode ? path : Path.GetDirectoryName(path);
                        List<Material> loadedMaterials = FileOperations.ReadLabelsChk(directoryPath);

                        if (loadedMaterials != null && loadedMaterials.Count > 0)
                        {
                            Materials = loadedMaterials;
                            Logger.Log($"[LoadDatasetAsync] Successfully loaded {Materials.Count} materials from labels.chk");
                            foreach (var material in Materials)
                            {
                                Logger.Log($"[LoadDatasetAsync] Loaded Material - ID: {material.ID}; Name: \"{material.Name}\"; Color: {material.Color}");
                            }
                        }
                        else
                        {
                            Logger.Log("[LoadDatasetAsync] No materials found in labels.chk, initializing defaults");
                            InitializeMaterials();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[LoadDatasetAsync] Error loading materials from labels.chk: {ex.Message}");
                        InitializeMaterials();
                    }
                }
                else
                {
                    // Initialize default materials if no labels.chk exists
                    Logger.Log("[LoadDatasetAsync] No labels.chk found, using default materials");
                    InitializeMaterials();
                }

                // Now load the dataset (volume.bin and labels.bin)
                var result = await FileOperations.LoadDatasetAsync(
                    path,
                    useMemoryMapping,
                    pixelSize,
                    SelectedBinningFactor,
                    progressForm);

                // Update MainForm state with the result
                lock (datasetLock)
                {
                    volumeData = result.volumeData;
                    volumeLabels = result.volumeLabels;
                    width = result.width;
                    height = result.height;
                    depth = result.depth;
                    pixelSize = result.pixelSize;

                    // Initialize selections
                    currentSelection = new byte[width, height];
                    currentSelectionXZ = new byte[width, depth];
                    currentSelectionYZ = new byte[depth, height];
                }

                // Create labels.chk if it doesn't exist but we now have a valid dataset
                if (!labelsChkExists && width > 0 && height > 0 && depth > 0)
                {
                    try
                    {
                        string directoryPath = folderMode ? path : Path.GetDirectoryName(path);
                        FileOperations.CreateLabelsChk(directoryPath, Materials);
                        Logger.Log("[LoadDatasetAsync] Created new labels.chk with current materials");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[LoadDatasetAsync] Warning: Failed to create labels.chk: {ex.Message}");
                    }
                }

                // Update UI and trigger initial render
                if ((volumeData != null || volumeLabels != null) && !this.IsDisposed)
                {
                    await this.SafeInvokeAsync(() =>
                    {
                        // Set initial view positions to center of the volume
                        CurrentSlice = depth / 2;
                        XzSliceY = height / 2;
                        YzSliceX = width / 2;

                        // Update labels
                        UpdateViewLabels();
                        orthogonalView.UpdateDimensions(width, height, depth);
                        orthogonalView.UpdatePosition(YzSliceX, XzSliceY, CurrentSlice);

                        // Render all views
                        RenderViews(ViewType.All);
                    });
                }

                // Initialize MaterialOperations with loaded materials
                if (volumeLabels != null)
                {
                    // Ensure unique material IDs before initializing MaterialOperations
                    HashSet<byte> materialIds = new HashSet<byte>();
                    foreach (var material in Materials.ToList())
                    {
                        if (material.ID == 0 && !material.IsExterior)
                        {
                            // Invalid ID for non-exterior material
                            material.ID = GetNextMaterialID();
                            Logger.Log($"[LoadDatasetAsync] Fixed zero ID for material {material.Name}, new ID: {material.ID}");
                        }

                        if (materialIds.Contains(material.ID) && !material.IsExterior)
                        {
                            // Duplicate ID found
                            byte oldId = material.ID;
                            material.ID = GetNextMaterialID();
                            Logger.Log($"[LoadDatasetAsync] Fixed duplicate ID {oldId} for material {material.Name}, new ID: {material.ID}");
                        }

                        materialIds.Add(material.ID);
                    }

                    // Create MaterialOperations with validated materials
                    Logger.Log($"[LoadDatasetAsync] Initializing MaterialOperations with {Materials.Count} materials for volume {width}x{height}x{depth}");
                    MaterialOps = new MaterialOperations(volumeLabels, Materials, width, height, depth);
                    Logger.Log("[LoadDatasetAsync] MaterialOperations initialization complete");
                }
                else
                {
                    Logger.Log("[LoadDatasetAsync] Warning: volumeLabels is null, cannot initialize MaterialOperations");
                }

                // Center all views
                xyPan = new PointF(
                    (xyView.ClientSize.Width - width * xyZoom) / 2,
                    (xyView.ClientSize.Height - height * xyZoom) / 2);

                xzPan = new PointF(
                    (xzView.ClientSize.Width - width * xzZoom) / 2,
                    (xzView.ClientSize.Height - depth * xzZoom) / 2);

                yzPan = new PointF(
                    (yzView.ClientSize.Width - depth * yzZoom) / 2,
                    (yzView.ClientSize.Height - height * yzZoom) / 2);

                // Apply the panning constraints to ensure they're valid
                ClampPanForView(ViewType.XY);
                ClampPanForView(ViewType.XZ);
                ClampPanForView(ViewType.YZ);
            }
            catch (AggregateException ae)
            {
                string err = string.Join("\n• ", ae.Flatten().InnerExceptions.Select(e => e.Message));
                Logger.Log("[LoadDatasetAsync] AggregateException: " + err);
                ShowError("Loading failed", err);
            }
            catch (Exception ex)
            {
                Logger.Log("[LoadDatasetAsync] Exception: " + ex);
                ShowError("Loading failed", ex.Message);
            }
            finally
            {
                await this.SafeInvokeAsync(() => progressForm?.Close());
            }
        }
        private void ShowError(string title, string message)
        {
            if (!this.IsDisposed && this.IsHandleCreated)
            {
                this.BeginInvoke((Action)(() =>
                {
                    MessageBox.Show($"{title}:\n\n{message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
            }
        }

        private double? AskUserPixelSize()
        {
            using (Form form = new Form()
            {
                Text = "Enter Pixel Size and Binning",
                Width = 350,
                Height = 220,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                TopMost = true,
                Icon = this.Icon
            })
            {
                Label lbl = new Label() { Text = "Pixel size:", Left = 10, Top = 20, AutoSize = true };
                TextBox txtVal = new TextBox() { Left = 80, Top = 18, Width = 80, Text = "1" };
                ComboBox cbUnits = new ComboBox() { Left = 170, Top = 18, Width = 80 };
                cbUnits.Items.Add("µm");
                cbUnits.Items.Add("mm");
                cbUnits.SelectedIndex = 0;

                Label lblBinning = new Label() { Text = "Binning:", Left = 10, Top = 60, AutoSize = true };
                ComboBox cbBinning = new ComboBox() { Left = 80, Top = 58, Width = 170 };
                cbBinning.Items.Add("1 (disabled)");
                cbBinning.Items.Add("2x2");
                cbBinning.Items.Add("4x4");
                cbBinning.Items.Add("8x8");
                cbBinning.Items.Add("16x16");
                cbBinning.SelectedIndex = 0;

                Button ok = new Button() { Text = "OK", Left = 130, Top = 100, Width = 80, DialogResult = DialogResult.OK };
                ok.Click += (s, e) => form.Close();

                form.Controls.Add(lbl);
                form.Controls.Add(txtVal);
                form.Controls.Add(cbUnits);
                form.Controls.Add(lblBinning);
                form.Controls.Add(cbBinning);
                form.Controls.Add(ok);
                form.AcceptButton = ok;

                if (form.ShowDialog() == DialogResult.OK)
                {
                    if (double.TryParse(txtVal.Text, out double val))
                    {
                        string unit = cbUnits.SelectedItem.ToString();
                        if (unit == "mm")
                            val *= 1e-3;
                        else
                            val *= 1e-6;

                        int binFactor = 1;
                        string binText = cbBinning.SelectedItem.ToString();
                        if (binText != "1 (disabled)")
                        {
                            string factorStr = binText.Split('x')[0];
                            if (int.TryParse(factorStr, out int parsedFactor))
                                binFactor = parsedFactor;
                        }

                        // Adjust the pixel size based on binning
                        double adjustedPixelSize = val * binFactor;

                        // Store the binning factor for later processing
                        SelectedBinningFactor = binFactor;

                        return adjustedPixelSize;
                    }
                }
            }
            return null;
        }

        public void SaveBinary(string path)
        {
            if (volumeLabels == null)
            {
                MessageBox.Show("No label volume to save.");
                return;
            }

            try
            {
                FileOperations.SaveBinary(path, volumeData, volumeLabels, Materials, width, height, depth, pixelSize);
            }
            catch (Exception ex)
            {
                Logger.Log("[SaveBinary] Error: " + ex);
                ShowError("Save failed", ex.Message);
            }
        }

        public void ExportImages()
        {
            if (volumeLabels == null)
            {
                MessageBox.Show("No label volume loaded.");
                return;
            }

            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        FileOperations.ExportImages(
                            dialog.SelectedPath,
                            volumeData,
                            volumeLabels,
                            Materials,
                            width,
                            height,
                            depth);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("[ExportImages] Error: " + ex);
                        ShowError("Export failed", ex.Message);
                    }
                }
            }
        }

        public void SaveLabelsChk()
        {
            string folder;

            if (Directory.Exists(CurrentPath))
            {
                // CurrentPath is a directory
                folder = CurrentPath;
            }
            else if (File.Exists(CurrentPath))
            {
                // CurrentPath is a file, use its directory
                folder = Path.GetDirectoryName(CurrentPath);
            }
            else
            {
                Logger.Log($"[SaveLabelsChk] Invalid path: {CurrentPath}");
                return;
            }

            Logger.Log($"[SaveLabelsChk] Saving materials to: {Path.Combine(folder, "labels.chk")}");
            foreach (var material in Materials)
            {
                Logger.Log($"[SaveLabelsChk] Saving Material - ID: {material.ID}; Name: \"{material.Name}\"; Color: {material.Color}");
            }

            FileOperations.CreateLabelsChk(folder, Materials);
            Logger.Log($"[SaveLabelsChk] Successfully saved {Materials.Count} materials");
        }

        /// <summary>
        /// Enhanced screenshot functionality with multiple options
        /// </summary>
        public void SaveScreenshot()
        {
            try
            {
                // Create a screenshot options form
                using (Form optionsForm = new Form
                {
                    Text = "Screenshot Options",
                    Size = new Size(400, 300),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterScreen,
                    MaximizeBox = false,
                    MinimizeBox = false
                })
                {
                    // Content Only vs Full Window
                    CheckBox chkContentOnly = new CheckBox
                    {
                        Text = "Content area only (no window frame)",
                        Checked = true,
                        Location = new Point(20, 20),
                        Width = 300
                    };

                    // High quality rendering
                    CheckBox chkHighQuality = new CheckBox
                    {
                        Text = "High quality rendering",
                        Checked = true,
                        Location = new Point(20, 50),
                        Width = 300
                    };

                    // Add overlay information
                    CheckBox chkOverlayInfo = new CheckBox
                    {
                        Text = "Include information overlay",
                        Checked = true,
                        Location = new Point(20, 80),
                        Width = 300
                    };

                    // Panel selection
                    GroupBox panelGroup = new GroupBox
                    {
                        Text = "Panels to include",
                        Location = new Point(20, 110),
                        Size = new Size(340, 100)
                    };

                    CheckBox chkXY = new CheckBox
                    {
                        Text = "XY View",
                        Checked = true,
                        Location = new Point(20, 20),
                        Width = 140
                    };

                    CheckBox chkXZ = new CheckBox
                    {
                        Text = "XZ View",
                        Checked = true,
                        Location = new Point(20, 50),
                        Width = 140
                    };

                    CheckBox chkYZ = new CheckBox
                    {
                        Text = "YZ View",
                        Checked = true,
                        Location = new Point(180, 20),
                        Width = 140
                    };

                    CheckBox chk3D = new CheckBox
                    {
                        Text = "3D Orthogonal View",
                        Checked = true,
                        Location = new Point(180, 50),
                        Width = 140
                    };

                    panelGroup.Controls.Add(chkXY);
                    panelGroup.Controls.Add(chkXZ);
                    panelGroup.Controls.Add(chkYZ);
                    panelGroup.Controls.Add(chk3D);

                    // Buttons
                    Button btnOK = new Button
                    {
                        Text = "Capture",
                        DialogResult = DialogResult.OK,
                        Location = new Point(200, 220),
                        Width = 80
                    };

                    Button btnCancel = new Button
                    {
                        Text = "Cancel",
                        DialogResult = DialogResult.Cancel,
                        Location = new Point(290, 220),
                        Width = 80
                    };

                    // Add controls to form
                    optionsForm.Controls.Add(chkContentOnly);
                    optionsForm.Controls.Add(chkHighQuality);
                    optionsForm.Controls.Add(chkOverlayInfo);
                    optionsForm.Controls.Add(panelGroup);
                    optionsForm.Controls.Add(btnOK);
                    optionsForm.Controls.Add(btnCancel);

                    optionsForm.AcceptButton = btnOK;
                    optionsForm.CancelButton = btnCancel;

                    // Show dialog and capture if OK
                    if (optionsForm.ShowDialog() == DialogResult.OK)
                    {
                        Rectangle captureRect;
                        if (chkContentOnly.Checked)
                        {
                            // Capture only the content area (ClientRectangle)
                            captureRect = this.ClientRectangle;
                        }
                        else
                        {
                            // Capture the entire form including borders
                            captureRect = new Rectangle(0, 0, this.Width, this.Height);
                        }

                        // Create the composite bitmap
                        using (Bitmap composite = new Bitmap(
                            chkContentOnly.Checked ? this.ClientSize.Width : this.Width,
                            chkContentOnly.Checked ? this.ClientSize.Height : this.Height,
                            PixelFormat.Format32bppArgb))
                        {
                            using (Graphics g = Graphics.FromImage(composite))
                            {
                                // Set high quality if requested
                                if (chkHighQuality.Checked)
                                {
                                    g.CompositingQuality = CompositingQuality.HighQuality;
                                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    g.SmoothingMode = SmoothingMode.AntiAlias;
                                }

                                // Create a copy of mainLayout for screenshot
                                using (TableLayoutPanel screenshotLayout = new TableLayoutPanel
                                {
                                    ColumnCount = 2,
                                    RowCount = 2,
                                    CellBorderStyle = mainLayout.CellBorderStyle,
                                    Size = mainLayout.Size
                                })
                                {
                                    screenshotLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
                                    screenshotLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
                                    screenshotLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
                                    screenshotLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

                                    // Selectively draw the enabled panels
                                    if (chkXY.Checked)
                                    {
                                        DrawPanel(g, screenshotLayout, 0, 0, xyView, xyLabel, xyBitmap);
                                    }

                                    if (chkYZ.Checked)
                                    {
                                        DrawPanel(g, screenshotLayout, 1, 0, yzView, yzLabel, yzBitmap);
                                    }

                                    if (chkXZ.Checked)
                                    {
                                        DrawPanel(g, screenshotLayout, 0, 1, xzView, xzLabel, xzBitmap);
                                    }

                                    if (chk3D.Checked)
                                    {
                                        // Draw the 3D panel
                                        Rectangle cell = GetCellBounds(screenshotLayout, 1, 1);

                                        // Create a temporary bitmap to capture the 3D view
                                        using (Bitmap orthoBmp = new Bitmap(orthogonalView.Width, orthogonalView.Height))
                                        {
                                            orthogonalView.DrawToBitmap(orthoBmp, new Rectangle(0, 0, orthogonalView.Width, orthogonalView.Height));
                                            g.DrawImage(orthoBmp, cell);
                                        }
                                    }

                                    // Add information overlay if requested
                                    if (chkOverlayInfo.Checked)
                                    {
                                        AddInformationOverlay(g, composite.Size);
                                    }
                                }
                            }

                            // Save the screenshot
                            using (SaveFileDialog sfd = new SaveFileDialog())
                            {
                                sfd.Filter = "PNG Image|*.png|JPEG Image|*.jpg|TIFF Image|*.tif";
                                sfd.Title = "Save Enhanced Screenshot";
                                sfd.DefaultExt = "png";

                                if (sfd.ShowDialog() == DialogResult.OK)
                                {
                                    ImageFormat format = ImageFormat.Png;
                                    string ext = Path.GetExtension(sfd.FileName).ToLower();

                                    if (ext == ".jpg" || ext == ".jpeg")
                                        format = ImageFormat.Jpeg;
                                    else if (ext == ".tif" || ext == ".tiff")
                                        format = ImageFormat.Tiff;

                                    composite.Save(sfd.FileName, format);
                                    Logger.Log($"[SaveScreenshot] Enhanced screenshot saved to {sfd.FileName}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SaveScreenshot] Error: {ex.Message}");
                MessageBox.Show($"Error saving screenshot: {ex.Message}", "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Helper method to draw a panel into the screenshot
        private void DrawPanel(Graphics g, TableLayoutPanel layout, int col, int row, Control view, Label label, Bitmap bitmap)
        {
            Rectangle cell = GetCellBounds(layout, col, row);

            // Fill background
            g.FillRectangle(Brushes.Black, cell);

            // Draw label
            Rectangle labelRect = new Rectangle(cell.X, cell.Y, cell.Width, label.Height);
            using (SolidBrush brush = new SolidBrush(label.ForeColor))
            using (Font font = label.Font)
            {
                g.DrawString(label.Text, font, brush, labelRect);
            }

            // Draw image content
            if (bitmap != null)
            {
                Rectangle imageRect = new Rectangle(
                    cell.X, cell.Y + label.Height,
                    cell.Width, cell.Height - label.Height);

                // Calculate positioning (centered)
                float imageAspect = (float)bitmap.Width / bitmap.Height;
                float rectAspect = (float)imageRect.Width / imageRect.Height;

                Rectangle destRect;
                if (imageAspect > rectAspect)
                {
                    // Image is wider than rectangle
                    int height = (int)(imageRect.Width / imageAspect);
                    int y = imageRect.Y + (imageRect.Height - height) / 2;
                    destRect = new Rectangle(imageRect.X, y, imageRect.Width, height);
                }
                else
                {
                    // Image is taller than rectangle
                    int width = (int)(imageRect.Height * imageAspect);
                    int x = imageRect.X + (imageRect.Width - width) / 2;
                    destRect = new Rectangle(x, imageRect.Y, width, imageRect.Height);
                }

                g.DrawImage(bitmap, destRect);

                // Draw scale bar on the image
                DrawScaleBarForScreenshot(g, destRect);
            }
        }

        // Helper to get cell bounds in a TableLayoutPanel
        private Rectangle GetCellBounds(TableLayoutPanel layout, int col, int row)
        {
            float colWidth = layout.Width / layout.ColumnCount;
            float rowHeight = layout.Height / layout.RowCount;

            // Account for cell spacing
            int spacing = layout.CellBorderStyle == TableLayoutPanelCellBorderStyle.None ? 0 : 1;

            return new Rectangle(
                (int)(col * colWidth) + spacing,
                (int)(row * rowHeight) + spacing,
                (int)colWidth - spacing * 2,
                (int)rowHeight - spacing * 2
            );
        }

        // Draw scale bar for the screenshot
        private void DrawScaleBarForScreenshot(Graphics g, Rectangle imageRect)
        {
            const float baseScreenLength = 80f;
            double candidateLengthMeters = baseScreenLength * pixelSize;
            string labelText;

            if (candidateLengthMeters < 1e-3)
            {
                double micrometers = candidateLengthMeters * 1e6;
                double rounded = Math.Round(micrometers / 10.0) * 10;
                labelText = $"{rounded:0} µm";
            }
            else
            {
                double millimeters = candidateLengthMeters * 1e3;
                double rounded = Math.Round(millimeters / 10.0) * 10;
                labelText = $"{rounded:0} mm";
            }

            using (SolidBrush brush = new SolidBrush(Color.White))
            using (Font font = new Font("Arial", 8, FontStyle.Bold))
            {
                float x = imageRect.X + 10;
                float y = imageRect.Bottom - 25;
                g.FillRectangle(brush, x, y, baseScreenLength, 3);
                g.DrawString(labelText, font, brush, x, y + 5);
            }
        }

        // Add information overlay to the screenshot
        private void AddInformationOverlay(Graphics g, Size size)
        {
            // Calculate the position (top of the 3D orthogonal panel)
            Rectangle orthoPanelRect = GetCellBounds(mainLayout, 1, 1);

            string info = $"CT Segmentation Suite - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
            info += $"Dimensions: {width}×{height}×{depth}, Pixel Size: {pixelSize * 1e6:0.00} µm\n";
            //Don't really need the following as it's already present in the other views.
            //info += $"Slice XY: {currentSlice + 1}/{depth}, Row XZ: {XzSliceY + 1}/{height}, Column YZ: {YzSliceX + 1}/{width}";

            // Measure text size to ensure proper overlay height
            SizeF textSize;
            using (Font font = new Font("Arial", 9, FontStyle.Bold))
            {
                textSize = g.MeasureString(info, font);
            }

            int overlayHeight = (int)textSize.Height + 10; // Add padding

            // Draw semi-transparent background at the top of the 3D panel
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(180, Color.Black)))
            {
                g.FillRectangle(brush, orthoPanelRect.X, orthoPanelRect.Y, orthoPanelRect.Width, overlayHeight);
            }

            // Draw text
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (Font font = new Font("Arial", 9, FontStyle.Bold))
            {
                g.DrawString(info, font, textBrush, orthoPanelRect.X + 5, orthoPanelRect.Y + 5);
            }
        }

        public void CloseDataset()
        {
            if (volumeData != null)
            {
                volumeData.Dispose();
                volumeData = null;

                // Also clear labels
                if (volumeLabels != null)
                {
                    volumeLabels.ReleaseFileLock();
                    volumeLabels.Dispose();
                    volumeLabels = null;
                }

                // Clear cached bitmaps
                lock (bitmapLock)
                {
                    xyBitmap?.Dispose();
                    xzBitmap?.Dispose();
                    yzBitmap?.Dispose();
                    xyBitmap = null;
                    xzBitmap = null;
                    yzBitmap = null;
                }

                // Update UI
                xyView.Image = null;
                xzView.Image = null;
                yzView.Image = null;

                // Reset dimensions
                width = 0;
                height = 0;
                depth = 0;

                Logger.Log("[CloseDataset] Dataset closed and resources released.");
            }
        }
        #endregion

        #region Rendering System
        public void RenderViews(ViewType viewType = ViewType.All)
        {
            if (width <= 0 || height <= 0 || depth <= 0)
                return;

            // Cancel any existing rendering
            renderCts.Cancel();
            renderCts = new CancellationTokenSource();
            var ct = renderCts.Token;

            // Update the view labels first
            UpdateViewLabels();

            // Perform the appropriate renders
            if (viewType == ViewType.XY || viewType == ViewType.All)
            {
                // Render XY view (current slice)
                Task.Run(() =>
                {
                    try
                    {
                        var bitmap = RenderSlice(ViewType.XY, ct);
                        if (!ct.IsCancellationRequested)
                        {
                            this.SafeInvokeAsync(() =>
                            {
                                UpdateView(xyView, bitmap, ref xyBitmap);
                            });
                        }
                        else
                        {
                            bitmap?.Dispose();
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Logger.Log($"[RenderViews XY] Error: {ex.Message}");
                    }
                }, ct);
            }

            if (ShowProjections && (viewType == ViewType.XZ || viewType == ViewType.All))
            {
                // Render XZ view
                Task.Run(() =>
                {
                    try
                    {
                        var bitmap = RenderSlice(ViewType.XZ, ct);
                        if (!ct.IsCancellationRequested)
                        {
                            this.SafeInvokeAsync(() =>
                            {
                                UpdateView(xzView, bitmap, ref xzBitmap);
                            });
                        }
                        else
                        {
                            bitmap?.Dispose();
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Logger.Log($"[RenderViews XZ] Error: {ex.Message}");
                    }
                }, ct);
            }

            if (ShowProjections && (viewType == ViewType.YZ || viewType == ViewType.All))
            {
                // Render YZ view
                Task.Run(() =>
                {
                    try
                    {
                        var bitmap = RenderSlice(ViewType.YZ, ct);
                        if (!ct.IsCancellationRequested)
                        {
                            this.SafeInvokeAsync(() =>
                            {
                                UpdateView(yzView, bitmap, ref yzBitmap);
                            });
                        }
                        else
                        {
                            bitmap?.Dispose();
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Logger.Log($"[RenderViews YZ] Error: {ex.Message}");
                    }
                }, ct);
            }
        }

        private void UpdateView(ScrollablePictureBox view, Bitmap newBitmap, ref Bitmap cachedBitmap)
        {
            lock (bitmapLock)
            {
                Bitmap oldBitmap = cachedBitmap;
                cachedBitmap = newBitmap;
                view.Image = newBitmap;
                oldBitmap?.Dispose();
            }

            view.Invalidate();
        }

        private Bitmap RenderSlice(ViewType viewType, CancellationToken ct)
        {
            if (volumeData == null)
                return null;

            // Determine dimensions and rendering coordinates
            int bmpWidth, bmpHeight, slice;
            byte[,] selectionMask = null;

            switch (viewType)
            {
                case ViewType.XY:
                    bmpWidth = width;
                    bmpHeight = height;
                    slice = currentSlice;
                    selectionMask = currentSelection;
                    break;

                case ViewType.XZ:
                    bmpWidth = width;
                    bmpHeight = depth;
                    slice = XzSliceY; // Use Y position as the "slice"
                    selectionMask = currentSelectionXZ;
                    break;

                case ViewType.YZ:
                    bmpWidth = depth;
                    bmpHeight = height;
                    slice = YzSliceX; // Use X position as the "slice"
                    selectionMask = currentSelectionYZ;
                    break;

                default:
                    return null;
            }

            // Create the bitmap and lock bits for direct manipulation
            Bitmap bmp = new Bitmap(bmpWidth, bmpHeight, PixelFormat.Format24bppRgb);
            Rectangle rect = new Rectangle(0, 0, bmpWidth, bmpHeight);
            BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
            int stride = bmpData.Stride;

            // Process the image using the unified rendering approach
            try
            {
                unsafe
                {
                    byte* ptr = (byte*)bmpData.Scan0;

                    // Use parallel processing for performance
                    Parallel.For(0, bmpHeight, y =>
                    {
                        if (ct.IsCancellationRequested)
                            return;

                        byte* rowPtr = ptr + (y * stride);

                        for (int x = 0; x < bmpWidth; x++)
                        {
                            int offset = x * 3;

                            // Define coordinates based on the view type
                            int voxelX, voxelY, voxelZ;
                            byte selectionValue = 0;

                            switch (viewType)
                            {
                                case ViewType.XY:
                                    voxelX = x;
                                    voxelY = y;
                                    voxelZ = slice;
                                    if (selectionMask != null && x < selectionMask.GetLength(0) && y < selectionMask.GetLength(1))
                                        selectionValue = selectionMask[x, y];
                                    break;

                                case ViewType.XZ:
                                    voxelX = x;
                                    voxelY = slice;
                                    voxelZ = y;
                                    if (selectionMask != null && x < selectionMask.GetLength(0) && y < selectionMask.GetLength(1))
                                        selectionValue = selectionMask[x, y];
                                    break;

                                case ViewType.YZ:
                                    voxelX = slice;
                                    voxelY = y;
                                    voxelZ = x;
                                    if (selectionMask != null && x < selectionMask.GetLength(0) && y < selectionMask.GetLength(1))
                                        selectionValue = selectionMask[x, y];
                                    break;

                                default:
                                    continue;
                            }

                            // Get grayscale value
                            byte gVal = SafeGetVoxel(voxelX, voxelY, voxelZ);

                            // Start with grayscale as base color
                            Color pixelColor = Color.FromArgb(gVal, gVal, gVal);

                            // Apply material color if there's a segmentation label
                            if (volumeLabels != null)
                            {
                                byte segID = SafeGetLabel(voxelX, voxelY, voxelZ);
                                if (segID != 0 && ShowMask)
                                {
                                    Material mat = Materials.FirstOrDefault(m => m.ID == segID);
                                    if (mat != null)
                                    {
                                        if (RenderMaterials)
                                            pixelColor = mat.Color;
                                        else
                                            pixelColor = BlendColors(pixelColor, mat.Color, 0.5f);
                                    }
                                }

                                // Process interpolated mask if available for XY view
                                if (viewType == ViewType.XY && interpolatedMask != null)
                                {
                                    // Current Z position for this slice
                                    int z = voxelZ;

                                    // Find all Y positions that have selections for this X coordinate
                                    List<int> yIndices = new List<int>();

                                    // Check sparse selections along Y axis for this X and Z
                                    foreach (var entry in sparseSelectionsZ)
                                    {
                                        int sliceIndex = entry.Key;
                                        byte[,] sliceData = entry.Value;

                                        // Skip current slice to avoid including it twice
                                        if (sliceIndex == z)
                                            continue;

                                        // Check if this X position is selected in any slice
                                        if (x < sliceData.GetLength(0))
                                        {
                                            for (int checkY = 0; checkY < Math.Min(height, sliceData.GetLength(1)); checkY++)
                                            {
                                                if (sliceData[x, checkY] != 0)
                                                {
                                                    // Found a selection at this Y position
                                                    yIndices.Add(checkY);
                                                }
                                            }
                                        }
                                    }

                                    // Also check sparse selections for orthogonal views
                                    foreach (var entry in sparseSelectionsY)
                                    {
                                        int yKey = entry.Key;
                                        byte[,] xzPlane = entry.Value;

                                        if (x < xzPlane.GetLength(0) && z < xzPlane.GetLength(1) && xzPlane[x, z] != 0)
                                        {
                                            yIndices.Add(yKey);
                                        }
                                    }

                                    // If we found Y positions with selections, interpolate between them
                                    if (yIndices.Count > 0)
                                    {
                                        int minY = yIndices.Min();
                                        int maxY = yIndices.Max();
                                        minY = Math.Max(0, minY);
                                        maxY = Math.Min(height - 1, maxY);

                                        // Fill in all Y positions between min and max
                                        for (int localY = minY; localY <= maxY; localY++)
                                        {
                                            interpolatedMask[x, localY, z] = true;
                                        }
                                    }
                                }

                                // Apply threshold preview if in thresholding mode
                                bool thresholdActive = currentTool == SegmentationTool.Thresholding &&
                                                       SelectedMaterialIndex >= 0 &&
                                                       PreviewMax > PreviewMin;

                                if (thresholdActive && ShowMask && gVal >= PreviewMin && gVal <= PreviewMax)
                                {
                                    Color selColor = GetComplementaryColor(Materials[SelectedMaterialIndex].Color);
                                    pixelColor = BlendColors(pixelColor, selColor, 0.5f);
                                }

                                // Apply selection overlay if present - Using complementary color
                                if (selectionValue != 0)
                                {
                                    Material selMat = Materials.FirstOrDefault(m => m.ID == selectionValue);
                                    if (selMat != null)
                                    {
                                        // Use complementary color for selections to make them more visible
                                        Color complementaryColor = GetComplementaryColor(selMat.Color);
                                        pixelColor = BlendColors(pixelColor, complementaryColor, 0.7f);
                                    }
                                }

                                // Write the final color to the bitmap
                                rowPtr[offset] = pixelColor.B;
                                rowPtr[offset + 1] = pixelColor.G;
                                rowPtr[offset + 2] = pixelColor.R;
                            }
                        }
                    });
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return bmp;
        }



        private byte SafeGetVoxel(int x, int y, int z)
        {
            if (volumeData == null || x < 0 || y < 0 || z < 0 || x >= width || y >= height || z >= depth)
                return 128; // Default gray if out of bounds

            return volumeData[x, y, z];
        }

        private byte SafeGetLabel(int x, int y, int z)
        {
            if (volumeLabels == null || x < 0 || y < 0 || z < 0 || x >= width || y >= height || z >= depth)
                return 0; // Default exterior if out of bounds

            return volumeLabels[x, y, z];
        }

        private void UpdateViewLabels()
        {
            if (xyLabel != null)
                xyLabel.Text = $"XY View - Slice: {currentSlice + 1}/{depth}";

            if (xzLabel != null)
                xzLabel.Text = $"XZ View - Row: {XzSliceY + 1}/{height}";

            if (yzLabel != null)
                yzLabel.Text = $"YZ View - Column: {YzSliceX + 1}/{width}";
        }

        private Color BlendColors(Color baseColor, Color overlay, float alpha)
        {
            return Color.FromArgb(
                (int)(baseColor.R * (1 - alpha) + overlay.R * alpha),
                (int)(baseColor.G * (1 - alpha) + overlay.G * alpha),
                (int)(baseColor.B * (1 - alpha) + overlay.B * alpha)
            );
        }

        private Color GetComplementaryColor(Color color)
        {
            return Color.FromArgb(255 - color.R, 255 - color.G, 255 - color.B);
        }

        public async Task RenderOrthoViewsAsync()
        {
            await Task.Run(() => RenderViews(ViewType.All));
        }

        // Helper for rendering the scale bar
        private void DrawScaleBar(Graphics g, Rectangle clientRect, float zoom)
        {
            const float baseScreenLength = 100f;
            double candidateLengthMeters = baseScreenLength / zoom * pixelSize;
            double labelInMeters;
            string labelText;

            if (candidateLengthMeters < 1e-3)
            {
                double candidateMicrometers = candidateLengthMeters * 1e6;
                double roundedMicrometers = Math.Max(10, Math.Round(candidateMicrometers / 10.0) * 10);
                labelInMeters = roundedMicrometers / 1e6;
                labelText = $"{roundedMicrometers:0} µm";
            }
            else
            {
                double candidateMillimeters = candidateLengthMeters * 1e3;
                double roundedMillimeters = Math.Max(1, Math.Round(candidateMillimeters / 10.0) * 10);
                labelInMeters = roundedMillimeters / 1e3;
                labelText = $"{roundedMillimeters:0} mm";
            }

            float screenLength = (float)(labelInMeters / pixelSize * zoom);
            using (SolidBrush brush = new SolidBrush(Color.White))
            using (Font font = new Font("Arial", 10))
            {
                float x = 20;
                float y = clientRect.Height - 40;
                g.FillRectangle(brush, x, y, screenLength, 3);
                g.DrawString(labelText, font, brush, x, y + 5);
            }
        }
        #endregion

        #region Input Handling
        private void ViewMouseDown(ViewType viewType, MouseEventArgs e)
        {
            ScrollablePictureBox view = GetViewForType(viewType);
            if (view == null) return;

            if (e.Button == MouseButtons.Middle)
            {
                // Pan with middle mouse button
                view.Tag = e.Location; // Store start point for pan
            }
            else if (e.Button == MouseButtons.Left)
            {
                if (currentTool == SegmentationTool.Brush)
                {
                    // Left button draws with brush
                    PaintMaskAt(viewType, e.Location, currentBrushSize);
                }
                else
                {
                    // Normal panning for other tools
                    view.Tag = e.Location;
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                switch (viewType)
                {
                    case ViewType.XY:
                        if (currentTool == SegmentationTool.Point)
                        {
                            // Start box drawing in XY view
                            isBoxDrawingPossible = true;
                            boxStartPoint = e.Location;
                            boxCurrentPoint = e.Location;
                            isActuallyDrawingBox = false;
                        }
                        else if (currentTool == SegmentationTool.Brush)
                        {
                            // Right button erases when using brush tool
                            EraseMaskAt(viewType, e.Location, currentBrushSize);
                        }
                        else if (currentTool == SegmentationTool.Eraser)
                        {
                            EraseMaskAt(viewType, e.Location, currentBrushSize);
                        }
                        else if (currentTool == SegmentationTool.Thresholding)
                        {
                            UpdateThresholdPreview();
                        }
                        break;

                    case ViewType.XZ:
                        if (currentTool == SegmentationTool.Point)
                        {
                            // Start box drawing in XZ view
                            isXzBoxDrawingPossible = true;
                            xzBoxStartPoint = e.Location;
                            xzBoxCurrentPoint = e.Location;
                            isXzActuallyDrawingBox = false;
                        }
                        else if (currentTool == SegmentationTool.Brush)
                        {
                            // Right button erases in XZ view
                            EraseOrthoMaskAt(viewType, e.Location, currentBrushSize);
                        }
                        else if (currentTool == SegmentationTool.Eraser)
                        {
                            EraseOrthoMaskAt(viewType, e.Location, currentBrushSize);
                        }
                        break;

                    case ViewType.YZ:
                        if (currentTool == SegmentationTool.Point)
                        {
                            // Start box drawing in YZ view
                            isYzBoxDrawingPossible = true;
                            yzBoxStartPoint = e.Location;
                            yzBoxCurrentPoint = e.Location;
                            isYzActuallyDrawingBox = false;
                        }
                        else if (currentTool == SegmentationTool.Brush)
                        {
                            // Right button erases in YZ view
                            EraseOrthoMaskAt(viewType, e.Location, currentBrushSize);
                        }
                        else if (currentTool == SegmentationTool.Eraser)
                        {
                            EraseOrthoMaskAt(viewType, e.Location, currentBrushSize);
                        }
                        break;
                }

                view.Invalidate();
            }
        }

        private void ViewMouseMove(ViewType viewType, MouseEventArgs e)
        {
            ScrollablePictureBox view = GetViewForType(viewType);
            if (view == null) return;

            // Handle brush overlay preview
            if (e.Button == MouseButtons.None &&
                (currentTool == SegmentationTool.Brush || currentTool == SegmentationTool.Eraser))
            {
                // Update brush overlay position
                switch (viewType)
                {
                    case ViewType.XY:
                        brushOverlayCenter = e.Location;
                        showBrushOverlay = true;
                        break;

                    case ViewType.XZ:
                        xzOverlayCenter = e.Location;
                        showBrushOverlay = true;
                        break;

                    case ViewType.YZ:
                        yzOverlayCenter = e.Location;
                        showBrushOverlay = true;
                        break;
                }

                view.Invalidate();
            }

            // Handle panning - now supports both middle mouse button and left mouse button (for non-brush tools)
            if ((e.Button == MouseButtons.Middle ||
                (e.Button == MouseButtons.Left && currentTool != SegmentationTool.Brush))
                && view.Tag is Point startPoint)
            {
                int dx = e.X - startPoint.X;
                int dy = e.Y - startPoint.Y;

                switch (viewType)
                {
                    case ViewType.XY:
                        xyPan = new PointF(xyPan.X + dx, xyPan.Y + dy);
                        ClampPanForView(viewType);
                        break;

                    case ViewType.XZ:
                        xzPan = new PointF(xzPan.X + dx, xzPan.Y + dy);
                        ClampPanForView(viewType);
                        break;

                    case ViewType.YZ:
                        yzPan = new PointF(yzPan.X + dx, yzPan.Y + dy);
                        ClampPanForView(viewType);
                        break;
                }

                view.Tag = e.Location; // Update start point for next move
                view.Invalidate();
            }

            // Handle box drawing
            if (viewType == ViewType.XY && isBoxDrawingPossible && e.Button == MouseButtons.Right)
            {
                boxCurrentPoint = e.Location;

                // If moved significantly, we're drawing a box
                if (Math.Abs(boxCurrentPoint.X - boxStartPoint.X) > 5 ||
                    Math.Abs(boxCurrentPoint.Y - boxStartPoint.Y) > 5)
                {
                    isActuallyDrawingBox = true;
                    view.Invalidate();
                }
            }
            else if (viewType == ViewType.XZ && isXzBoxDrawingPossible && e.Button == MouseButtons.Right)
            {
                xzBoxCurrentPoint = e.Location;

                if (Math.Abs(xzBoxCurrentPoint.X - xzBoxStartPoint.X) > 5 ||
                    Math.Abs(xzBoxCurrentPoint.Y - xzBoxStartPoint.Y) > 5)
                {
                    isXzActuallyDrawingBox = true;
                    view.Invalidate();
                }
            }
            else if (viewType == ViewType.YZ && isYzBoxDrawingPossible && e.Button == MouseButtons.Right)
            {
                yzBoxCurrentPoint = e.Location;

                if (Math.Abs(yzBoxCurrentPoint.X - yzBoxStartPoint.X) > 5 ||
                    Math.Abs(yzBoxCurrentPoint.Y - yzBoxStartPoint.Y) > 5)
                {
                    isYzActuallyDrawingBox = true;
                    view.Invalidate();
                }
            }

            // Handle brush painting with left mouse button
            if (e.Button == MouseButtons.Left && currentTool == SegmentationTool.Brush)
            {
                PaintMaskAt(viewType, e.Location, currentBrushSize);
                view.Invalidate();
            }

            // Handle brush erasing with right mouse button
            if (e.Button == MouseButtons.Right && currentTool == SegmentationTool.Brush)
            {
                switch (viewType)
                {
                    case ViewType.XY:
                        EraseMaskAt(viewType, e.Location, currentBrushSize);
                        break;

                    case ViewType.XZ:
                    case ViewType.YZ:
                        EraseOrthoMaskAt(viewType, e.Location, currentBrushSize);
                        break;
                }
                view.Invalidate();
            }

            // Handle eraser tool with right mouse button (keep original behavior)
            if (e.Button == MouseButtons.Right && currentTool == SegmentationTool.Eraser)
            {
                switch (viewType)
                {
                    case ViewType.XY:
                        EraseMaskAt(viewType, e.Location, currentBrushSize);
                        break;

                    case ViewType.XZ:
                    case ViewType.YZ:
                        EraseOrthoMaskAt(viewType, e.Location, currentBrushSize);
                        break;
                }
                view.Invalidate();
            }
        }

        private void ViewMouseUp(ViewType viewType, MouseEventArgs e)
        {
            ScrollablePictureBox view = GetViewForType(viewType);
            if (view == null) return;

            // End panning with either middle mouse or left mouse (for non-brush tools)
            if (e.Button == MouseButtons.Middle ||
                (e.Button == MouseButtons.Left && currentTool != SegmentationTool.Brush))
            {
                view.Tag = null;
            }

            // Handle box drawing completion
            if (e.Button == MouseButtons.Right)
            {
                if (viewType == ViewType.XY && isBoxDrawingPossible)
                {
                    if (isActuallyDrawingBox)
                    {
                        // Create box annotation
                        float x1 = (boxStartPoint.X - xyPan.X) / xyZoom;
                        float y1 = (boxStartPoint.Y - xyPan.Y) / xyZoom;
                        float x2 = (boxCurrentPoint.X - xyPan.X) / xyZoom;
                        float y2 = (boxCurrentPoint.Y - xyPan.Y) / xyZoom;

                        // Clamp to image boundaries
                        x1 = Math.Max(0, Math.Min(x1, width - 1));
                        y1 = Math.Max(0, Math.Min(y1, height - 1));
                        x2 = Math.Max(0, Math.Min(x2, width - 1));
                        y2 = Math.Max(0, Math.Min(y2, height - 1));

                        Material selectedMaterial = GetSelectedMaterial();
                        AnnotationPoint box = AnnotationMgr.AddBox(x1, y1, x2, y2, CurrentSlice, selectedMaterial.Name);
                    }
                    else
                    {
                        // Create point annotation
                        float sliceX = (boxStartPoint.X - xyPan.X) / xyZoom;
                        float sliceY = (boxStartPoint.Y - xyPan.Y) / xyZoom;

                        if (sliceX >= 0 && sliceX < width && sliceY >= 0 && sliceY < height)
                        {
                            Material selectedMaterial = GetSelectedMaterial();
                            AnnotationPoint point = AnnotationMgr.AddPoint(sliceX, sliceY, CurrentSlice, selectedMaterial.Name);
                        }
                    }

                    isBoxDrawingPossible = false;
                    isActuallyDrawingBox = false;
                    view.Invalidate();
                }
                else if (viewType == ViewType.XZ && isXzBoxDrawingPossible)
                {
                    // Keep existing XZ handling code
                    if (isXzActuallyDrawingBox)
                    {
                        // Create box annotation in XZ plane
                        float x1 = (xzBoxStartPoint.X - xzPan.X) / xzZoom;
                        float z1 = (xzBoxStartPoint.Y - xzPan.Y) / xzZoom;
                        float x2 = (xzBoxCurrentPoint.X - xzPan.X) / xzZoom;
                        float z2 = (xzBoxCurrentPoint.Y - xzPan.Y) / xzZoom;

                        // Clamp to boundaries
                        x1 = Math.Max(0, Math.Min(x1, width - 1));
                        z1 = Math.Max(0, Math.Min(z1, depth - 1));
                        x2 = Math.Max(0, Math.Min(x2, width - 1));
                        z2 = Math.Max(0, Math.Min(z2, depth - 1));

                        Material selectedMaterial = GetSelectedMaterial();
                        AnnotationPoint box = AnnotationMgr.AddBox(x1, XzSliceY, x2, XzSliceY, (int)((z1 + z2) / 2), selectedMaterial.Name);
                        box.X2 = x2;
                        box.Y2 = z2;
                    }
                    else
                    {
                        // Create point annotation in XZ plane
                        float x = (xzBoxStartPoint.X - xzPan.X) / xzZoom;
                        float z = (xzBoxStartPoint.Y - xzPan.Y) / xzZoom;

                        if (x >= 0 && x < width && z >= 0 && z < depth)
                        {
                            Material selectedMaterial = GetSelectedMaterial();
                            AnnotationPoint point = AnnotationMgr.AddPoint(x, XzSliceY, (int)z, selectedMaterial.Name);
                        }
                    }

                    isXzBoxDrawingPossible = false;
                    isXzActuallyDrawingBox = false;
                    view.Invalidate();
                }
                else if (viewType == ViewType.YZ && isYzBoxDrawingPossible)
                {
                    // Keep existing YZ handling code
                    if (isYzActuallyDrawingBox)
                    {
                        // Create box annotation in YZ plane
                        float z1 = (yzBoxStartPoint.X - yzPan.X) / yzZoom;
                        float y1 = (yzBoxStartPoint.Y - yzPan.Y) / yzZoom;
                        float z2 = (yzBoxCurrentPoint.X - yzPan.X) / yzZoom;
                        float y2 = (yzBoxCurrentPoint.Y - yzPan.Y) / yzZoom;

                        // Clamp to boundaries
                        z1 = Math.Max(0, Math.Min(z1, depth - 1));
                        y1 = Math.Max(0, Math.Min(y1, height - 1));
                        z2 = Math.Max(0, Math.Min(z2, depth - 1));
                        y2 = Math.Max(0, Math.Min(y2, height - 1));

                        Material selectedMaterial = GetSelectedMaterial();
                        AnnotationPoint box = AnnotationMgr.AddBox(YzSliceX, y1, YzSliceX, y2, (int)((z1 + z2) / 2), selectedMaterial.Name);
                        box.X2 = z1;
                        box.Y2 = z2;
                    }
                    else
                    {
                        // Create point annotation in YZ plane
                        float z = (yzBoxStartPoint.X - yzPan.X) / yzZoom;
                        float y = (yzBoxStartPoint.Y - yzPan.Y) / yzZoom;

                        if (z >= 0 && z < depth && y >= 0 && y < height)
                        {
                            Material selectedMaterial = GetSelectedMaterial();
                            AnnotationPoint point = AnnotationMgr.AddPoint(YzSliceX, y, (int)z, selectedMaterial.Name);
                        }
                    }

                    isYzBoxDrawingPossible = false;
                    isYzActuallyDrawingBox = false;
                    view.Invalidate();
                }
            }
        }

        private void ViewMouseWheel(ViewType viewType, MouseEventArgs e)
        {
            // Calculate zoom factor based on wheel delta
            float factor = (e.Delta > 0) ? 1.1f : 0.9f;

            switch (viewType)
            {
                case ViewType.XY:
                    xyZoom = Math.Max(0.1f, Math.Min(5f, xyZoom * factor));
                    ClampPanForView(viewType);
                    xyView.Invalidate();
                    break;

                case ViewType.XZ:
                    xzZoom = Math.Max(0.1f, Math.Min(5f, xzZoom * factor));
                    ClampPanForView(viewType);
                    xzView.Invalidate();
                    break;

                case ViewType.YZ:
                    yzZoom = Math.Max(0.1f, Math.Min(5f, yzZoom * factor));
                    ClampPanForView(viewType);
                    yzView.Invalidate();
                    break;
            }
        }

        private void ViewPaint(ViewType viewType, PaintEventArgs e)
        {
            ScrollablePictureBox view = GetViewForType(viewType);
            if (view == null || view.Image == null) return;

            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Get the appropriate pan and zoom for this view
            PointF pan;
            float zoom;
            Bitmap bitmap;

            switch (viewType)
            {
                case ViewType.XY:
                    pan = xyPan;
                    zoom = xyZoom;
                    bitmap = xyBitmap;
                    break;

                case ViewType.XZ:
                    pan = xzPan;
                    zoom = xzZoom;
                    bitmap = xzBitmap;
                    break;

                case ViewType.YZ:
                    pan = yzPan;
                    zoom = yzZoom;
                    bitmap = yzBitmap;
                    break;

                default:
                    return;
            }

            // Clear background
            g.Clear(Color.Black);

            // Draw the scaled and panned image
            if (bitmap != null)
            {
                float destWidth = bitmap.Width * zoom;
                float destHeight = bitmap.Height * zoom;

                RectangleF destRect = new RectangleF(pan.X, pan.Y, destWidth, destHeight);
                g.DrawImage(bitmap, destRect);
            }

            // Draw the scale bar
            DrawScaleBar(g, view.ClientRectangle, zoom);

            // Draw view-specific overlays
            switch (viewType)
            {
                case ViewType.XY:
                    // Draw XY slice info
                    using (Font font = new Font("Arial", 10, FontStyle.Bold))
                    using (SolidBrush brush = new SolidBrush(Color.White))
                    {
                        string sliceInfo = $"Slice: {CurrentSlice + 1}/{depth}";
                        SizeF textSize = g.MeasureString(sliceInfo, font);
                        g.DrawString(sliceInfo, font, brush,
                            view.ClientSize.Width - textSize.Width - 10,
                            view.ClientSize.Height - textSize.Height - 10);
                    }

                    // Draw box preview in XY view
                    if (isBoxDrawingPossible && isActuallyDrawingBox)
                    {
                        int boxX = Math.Min(boxStartPoint.X, boxCurrentPoint.X);
                        int boxY = Math.Min(boxStartPoint.Y, boxCurrentPoint.Y);
                        int boxWidth = Math.Abs(boxCurrentPoint.X - boxStartPoint.X);
                        int boxHeight = Math.Abs(boxCurrentPoint.Y - boxStartPoint.Y);

                        using (Pen pen = new Pen(Color.Yellow, 2))
                        {
                            g.DrawRectangle(pen, boxX, boxY, boxWidth, boxHeight);
                        }
                    }

                    // Draw brush overlay in XY view
                    if (showBrushOverlay && (currentTool == SegmentationTool.Brush || currentTool == SegmentationTool.Eraser))
                    {
                        float radius = currentBrushSize * zoom / 2;
                        using (Pen pen = new Pen(Color.Red, 2))
                        {
                            g.DrawEllipse(pen,
                                brushOverlayCenter.X - radius,
                                brushOverlayCenter.Y - radius,
                                radius * 2, radius * 2);
                        }
                    }

                    // Draw annotations in XY view
                    DrawAnnotations(g, viewType);
                    break;

                case ViewType.XZ:
                    // Draw box preview in XZ view
                    if (isXzBoxDrawingPossible && isXzActuallyDrawingBox)
                    {
                        int boxX = Math.Min(xzBoxStartPoint.X, xzBoxCurrentPoint.X);
                        int boxY = Math.Min(xzBoxStartPoint.Y, xzBoxCurrentPoint.Y);
                        int boxWidth = Math.Abs(xzBoxCurrentPoint.X - xzBoxStartPoint.X);
                        int boxHeight = Math.Abs(xzBoxCurrentPoint.Y - xzBoxStartPoint.Y);

                        using (Pen pen = new Pen(Color.Yellow, 2))
                        {
                            g.DrawRectangle(pen, boxX, boxY, boxWidth, boxHeight);
                        }
                    }

                    // Draw brush overlay in XZ view
                    if (showBrushOverlay && (currentTool == SegmentationTool.Brush || currentTool == SegmentationTool.Eraser))
                    {
                        float radius = currentBrushSize * zoom / 2;
                        using (Pen pen = new Pen(Color.Red, 2))
                        {
                            g.DrawEllipse(pen,
                                xzOverlayCenter.X - radius,
                                xzOverlayCenter.Y - radius,
                                radius * 2, radius * 2);
                        }
                    }

                    // Draw annotations in XZ view
                    DrawAnnotations(g, viewType);
                    break;

                case ViewType.YZ:
                    // Draw box preview in YZ view
                    if (isYzBoxDrawingPossible && isYzActuallyDrawingBox)
                    {
                        int boxX = Math.Min(yzBoxStartPoint.X, yzBoxCurrentPoint.X);
                        int boxY = Math.Min(yzBoxStartPoint.Y, yzBoxCurrentPoint.Y);
                        int boxWidth = Math.Abs(yzBoxCurrentPoint.X - yzBoxStartPoint.X);
                        int boxHeight = Math.Abs(yzBoxCurrentPoint.Y - yzBoxStartPoint.Y);

                        using (Pen pen = new Pen(Color.Yellow, 2))
                        {
                            g.DrawRectangle(pen, boxX, boxY, boxWidth, boxHeight);
                        }
                    }

                    // Draw brush overlay in YZ view
                    if (showBrushOverlay && (currentTool == SegmentationTool.Brush || currentTool == SegmentationTool.Eraser))
                    {
                        float radius = currentBrushSize * zoom / 2;
                        using (Pen pen = new Pen(Color.Red, 2))
                        {
                            g.DrawEllipse(pen,
                                yzOverlayCenter.X - radius,
                                yzOverlayCenter.Y - radius,
                                radius * 2, radius * 2);
                        }
                    }

                    // Draw annotations in YZ view
                    DrawAnnotations(g, viewType);
                    break;
            }
        }

        private ScrollablePictureBox GetViewForType(ViewType viewType)
        {
            switch (viewType)
            {
                case ViewType.XY: return xyView;
                case ViewType.XZ: return xzView;
                case ViewType.YZ: return yzView;
                default: return null;
            }
        }

        private void ClampPanForView(ViewType viewType)
        {
            switch (viewType)
            {
                case ViewType.XY:
                    if (xyBitmap == null) return;

                    float xyImageWidth = xyBitmap.Width * xyZoom;
                    float xyImageHeight = xyBitmap.Height * xyZoom;

                    // If image is smaller than view, center it
                    if (xyImageWidth <= xyView.ClientSize.Width)
                        xyPan.X = (xyView.ClientSize.Width - xyImageWidth) / 2;
                    else
                        xyPan.X = Math.Max(xyView.ClientSize.Width - xyImageWidth, Math.Min(0, xyPan.X));

                    if (xyImageHeight <= xyView.ClientSize.Height)
                        xyPan.Y = (xyView.ClientSize.Height - xyImageHeight) / 2;
                    else
                        xyPan.Y = Math.Max(xyView.ClientSize.Height - xyImageHeight, Math.Min(0, xyPan.Y));
                    break;

                case ViewType.XZ:
                    if (xzBitmap == null) return;

                    float xzImageWidth = xzBitmap.Width * xzZoom;
                    float xzImageHeight = xzBitmap.Height * xzZoom;

                    if (xzImageWidth <= xzView.ClientSize.Width)
                        xzPan.X = (xzView.ClientSize.Width - xzImageWidth) / 2;
                    else
                        xzPan.X = Math.Max(xzView.ClientSize.Width - xzImageWidth, Math.Min(0, xzPan.X));

                    if (xzImageHeight <= xzView.ClientSize.Height)
                        xzPan.Y = (xzView.ClientSize.Height - xzImageHeight) / 2;
                    else
                        xzPan.Y = Math.Max(xzView.ClientSize.Height - xzImageHeight, Math.Min(0, xzPan.Y));
                    break;

                case ViewType.YZ:
                    if (yzBitmap == null) return;

                    float yzImageWidth = yzBitmap.Width * yzZoom;
                    float yzImageHeight = yzBitmap.Height * yzZoom;

                    if (yzImageWidth <= yzView.ClientSize.Width)
                        yzPan.X = (yzView.ClientSize.Width - yzImageWidth) / 2;
                    else
                        yzPan.X = Math.Max(yzView.ClientSize.Width - yzImageWidth, Math.Min(0, yzPan.X));

                    if (yzImageHeight <= yzView.ClientSize.Height)
                        yzPan.Y = (yzView.ClientSize.Height - yzImageHeight) / 2;
                    else
                        yzPan.Y = Math.Max(yzView.ClientSize.Height - yzImageHeight, Math.Min(0, yzPan.Y));
                    break;
            }
        }

        private Material GetSelectedMaterial()
        {
            if (SelectedMaterialIndex >= 0 && SelectedMaterialIndex < Materials.Count)
                return Materials[SelectedMaterialIndex];

            return Materials.Count > 1 ? Materials[1] : Materials[0];
        }
        #endregion

        #region Segmentation Operations
        public void OnThresholdRangeChanged(byte newMin, byte newMax)
        {
            if (SelectedMaterialIndex < 0 || Materials[SelectedMaterialIndex].IsExterior)
                return;

            PreviewMin = newMin;
            PreviewMax = newMax;

            if (currentTool == SegmentationTool.Thresholding)
            {
                UpdateThresholdPreview();
                RenderViews(ViewType.All);
            }
        }

        private void UpdateThresholdPreview()
        {
            if (currentTool != SegmentationTool.Thresholding ||
                SelectedMaterialIndex < 0 ||
                volumeData == null)
            {
                return;
            }

            currentSelection = new byte[width, height];
            int currentZ = CurrentSlice;
            byte materialID = Materials[SelectedMaterialIndex].ID;

            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    byte gVal = volumeData[x, y, currentZ];
                    if (gVal >= PreviewMin && gVal <= PreviewMax)
                    {
                        currentSelection[x, y] = materialID;
                    }
                }
            });

            RenderViews(ViewType.XY);
        }

        public void AddThresholdSelection(byte minVal, byte maxVal, byte materialID)
        {
            if (volumeData == null || volumeLabels == null || MaterialOps == null) return;

            MaterialOps.AddVoxelsByThreshold(volumeData, materialID, minVal, maxVal);
            RenderViews(ViewType.All);
        }

        public void RemoveThresholdSelection(byte minVal, byte maxVal, byte materialID)
        {
            if (volumeData == null || volumeLabels == null || MaterialOps == null) return;

            MaterialOps.RemoveVoxelsByThreshold(volumeData, materialID, minVal, maxVal);
            RenderViews(ViewType.All);
        }

        public void ApplyCurrentSelection()
        {
            int slice = CurrentSlice;
            if (currentSelection == null || MaterialOps == null) return;

            MaterialOps.ApplySelection(currentSelection, slice);

            // Save to sparseSelectionsZ (XY slices)
            byte[,] copy = new byte[width, height];
            Array.Copy(currentSelection, copy, width * height);
            sparseSelectionsZ[slice] = copy;

            currentSelection = new byte[width, height];
            RenderViews(ViewType.All);
        }

        public void SubtractCurrentSelection()
        {
            int slice = CurrentSlice;
            if (currentSelection == null || MaterialOps == null) return;

            MaterialOps.SubtractSelection(currentSelection, slice);

            currentSelection = new byte[width, height];
            RenderViews(ViewType.All);
        }

        public void ApplyOrthoSelections()
        {
            if (MaterialOps == null) return;

            // For XZ view
            if (currentSelectionXZ != null)
            {
                MaterialOps.ApplyOrthogonalSelection(currentSelectionXZ, XzSliceY, (CTSegmenter.OrthogonalView)OrthogonalView.XZ);

                // Store in sparseSelectionsY
                byte[,] copyXZ = new byte[width, depth];
                Array.Copy(currentSelectionXZ, copyXZ, width * depth);
                sparseSelectionsY[XzSliceY] = copyXZ;

                currentSelectionXZ = new byte[width, depth];
            }

            // For YZ view
            if (currentSelectionYZ != null)
            {
                MaterialOps.ApplyOrthogonalSelection(currentSelectionYZ, YzSliceX, (CTSegmenter.OrthogonalView)OrthogonalView.YZ);

                // Store in sparseSelectionsX
                byte[,] copyYZ = new byte[depth, height];
                Array.Copy(currentSelectionYZ, copyYZ, depth * height);
                sparseSelectionsX[YzSliceX] = copyYZ;

                currentSelectionYZ = new byte[depth, height];
            }

            RenderViews(ViewType.All);
        }

        public void SubtractOrthoSelections()
        {
            if (MaterialOps == null) return;

            // For XZ view
            if (currentSelectionXZ != null)
            {
                MaterialOps.SubtractOrthogonalSelection(currentSelectionXZ, XzSliceY, (CTSegmenter.OrthogonalView)OrthogonalView.XZ);
                currentSelectionXZ = new byte[width, depth];
            }

            // For YZ view
            if (currentSelectionYZ != null)
            {
                MaterialOps.SubtractOrthogonalSelection(currentSelectionYZ, YzSliceX, (CTSegmenter.OrthogonalView)OrthogonalView.YZ);
                currentSelectionYZ = new byte[depth, height];
            }

            RenderViews(ViewType.All);
        }

        private void PaintMaskAt(ViewType viewType, Point screenPoint, int brushSize)
        {
            interpolatedMask = null;
            float zoom;
            PointF pan;
            byte[,] selection;
            int radius = brushSize / 2;
            byte labelToSet = (byte)((SelectedMaterialIndex > 0) ? Materials[SelectedMaterialIndex].ID : 1);

            switch (viewType)
            {
                case ViewType.XY:
                    zoom = xyZoom;
                    pan = xyPan;
                    selection = currentSelection;

                    if (selection == null || selection.GetLength(0) != width || selection.GetLength(1) != height)
                        selection = new byte[width, height];

                    // Convert screen coordinates to image coordinates
                    int imageX = (int)((screenPoint.X - pan.X) / zoom);
                    int imageY = (int)((screenPoint.Y - pan.Y) / zoom);

                    // Paint a circle in the selection mask
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int y = imageY + dy;
                        if (y < 0 || y >= height) continue;

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int x = imageX + dx;
                            if (x < 0 || x >= width) continue;

                            if (dx * dx + dy * dy <= radius * radius)
                            {
                                selection[x, y] = labelToSet;
                            }
                        }
                    }

                    currentSelection = selection;
                    break;

                case ViewType.XZ:
                    PaintOrthoMaskAt(viewType, screenPoint, brushSize);
                    break;

                case ViewType.YZ:
                    PaintOrthoMaskAt(viewType, screenPoint, brushSize);
                    break;
            }

            RenderViews(viewType);
        }

        private void EraseMaskAt(ViewType viewType, Point screenPoint, int brushSize)
        {
            interpolatedMask = null;
            float zoom;
            PointF pan;
            byte[,] selection;
            int radius = brushSize / 2;

            switch (viewType)
            {
                case ViewType.XY:
                    zoom = xyZoom;
                    pan = xyPan;
                    selection = currentSelection;

                    if (selection == null || selection.GetLength(0) != width || selection.GetLength(1) != height)
                        selection = new byte[width, height];

                    // Convert screen coordinates to image coordinates
                    int imageX = (int)((screenPoint.X - pan.X) / zoom);
                    int imageY = (int)((screenPoint.Y - pan.Y) / zoom);

                    // Erase a circle in the selection mask
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int y = imageY + dy;
                        if (y < 0 || y >= height) continue;

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int x = imageX + dx;
                            if (x < 0 || x >= width) continue;

                            if (dx * dx + dy * dy <= radius * radius)
                            {
                                selection[x, y] = 0;
                            }
                        }
                    }

                    currentSelection = selection;
                    break;

                case ViewType.XZ:
                    EraseOrthoMaskAt(viewType, screenPoint, brushSize);
                    break;

                case ViewType.YZ:
                    EraseOrthoMaskAt(viewType, screenPoint, brushSize);
                    break;
            }

            RenderViews(viewType);
        }

        private void PaintOrthoMaskAt(ViewType viewType, Point screenPoint, int brushSize)
        {
            float zoom;
            PointF pan;
            byte[,] selection;
            int radius = brushSize / 2;
            byte labelToSet = (byte)((SelectedMaterialIndex > 0) ? Materials[SelectedMaterialIndex].ID : 1);
            int imageZ;
            int imageY;
            int imageX;
            switch (viewType)
            {
                case ViewType.XZ:
                    zoom = xzZoom;
                    pan = xzPan;
                    selection = currentSelectionXZ;

                    if (selection == null || selection.GetLength(0) != width || selection.GetLength(1) != depth)
                        selection = new byte[width, depth];

                    // Convert screen coordinates to image coordinates
                    imageX = (int)((screenPoint.X - pan.X) / zoom);
                    imageZ = (int)((screenPoint.Y - pan.Y) / zoom);

                    // Paint a circle in the selection mask
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int z = imageZ + dz;
                        if (z < 0 || z >= depth) continue;

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int x = imageX + dx;
                            if (x < 0 || x >= width) continue;

                            if (dx * dx + dz * dz <= radius * radius)
                            {
                                selection[x, z] = labelToSet;
                            }
                        }
                    }

                    currentSelectionXZ = selection;
                    break;

                case ViewType.YZ:
                    zoom = yzZoom;
                    pan = yzPan;
                    selection = currentSelectionYZ;

                    if (selection == null || selection.GetLength(0) != depth || selection.GetLength(1) != height)
                        selection = new byte[depth, height];

                    // Convert screen coordinates to image coordinates
                    imageZ = (int)((screenPoint.X - pan.X) / zoom);
                    imageY = (int)((screenPoint.Y - pan.Y) / zoom);

                    // Paint a circle in the selection mask
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int y = imageY + dy;
                        if (y < 0 || y >= height) continue;

                        for (int dz = -radius; dz <= radius; dz++)
                        {
                            int z = imageZ + dz;
                            if (z < 0 || z >= depth) continue;

                            if (dy * dy + dz * dz <= radius * radius)
                            {
                                selection[z, y] = labelToSet;
                            }
                        }
                    }

                    currentSelectionYZ = selection;
                    break;
            }
        }

        private void EraseOrthoMaskAt(ViewType viewType, Point screenPoint, int brushSize)
        {
            float zoom;
            PointF pan;
            byte[,] selection;
            int radius = brushSize / 2;

            switch (viewType)
            {
                case ViewType.XZ:
                    zoom = xzZoom;
                    pan = xzPan;
                    selection = currentSelectionXZ;

                    if (selection == null || selection.GetLength(0) != width || selection.GetLength(1) != depth)
                        selection = new byte[width, depth];

                    // Convert screen coordinates to image coordinates
                    int imageX = (int)((screenPoint.X - pan.X) / zoom);
                    int imageZ = (int)((screenPoint.Y - pan.Y) / zoom);

                    // Erase a circle in the selection mask
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int z = imageZ + dz;
                        if (z < 0 || z >= depth) continue;

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int x = imageX + dx;
                            if (x < 0 || x >= width) continue;

                            if (dx * dx + dz * dz <= radius * radius)
                            {
                                selection[x, z] = 0;
                            }
                        }
                    }

                    currentSelectionXZ = selection;
                    break;

                case ViewType.YZ:
                    zoom = yzZoom;
                    pan = yzPan;
                    selection = currentSelectionYZ;

                    if (selection == null || selection.GetLength(0) != depth || selection.GetLength(1) != height)
                        selection = new byte[depth, height];

                    // Convert screen coordinates to image coordinates
                    imageZ = (int)((screenPoint.X - pan.X) / zoom);
                    int imageY = (int)((screenPoint.Y - pan.Y) / zoom);

                    // Erase a circle in the selection mask
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int y = imageY + dy;
                        if (y < 0 || y >= height) continue;

                        for (int dz = -radius; dz <= radius; dz++)
                        {
                            int z = imageZ + dz;
                            if (z < 0 || z >= depth) continue;

                            if (dy * dy + dz * dz <= radius * radius)
                            {
                                selection[z, y] = 0;
                            }
                        }
                    }

                    currentSelectionYZ = selection;
                    break;
            }
        }

        public void InterpolateSelection(byte materialID)
        {
            // Create a new 3D mask
            interpolatedMask = new bool[width, height, depth];

            // Get all slices with brush strokes
            List<int> slicesWithStrokes = sparseSelectionsZ.Keys.ToList();

            if (slicesWithStrokes.Count < 2)
            {
                MessageBox.Show("You need at least two slices with brush strokes to interpolate.");
                return;
            }

            // Sort slices by z-position
            slicesWithStrokes.Sort();

            Logger.Log($"[InterpolateSelection] Creating 3D interpolation between {slicesWithStrokes.Count} slices");

            // Create a list of all points in each slice
            Dictionary<int, List<Point3D>> pointsBySlice = new Dictionary<int, List<Point3D>>();

            foreach (int z in slicesWithStrokes)
            {
                byte[,] selection = sparseSelectionsZ[z];
                List<Point3D> points = new List<Point3D>();

                // Find all selected points in this slice
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (x < selection.GetLength(0) && y < selection.GetLength(1) && selection[x, y] != 0)
                        {
                            points.Add(new Point3D(x, y, z));
                            // Also fill the original selection in the result
                            interpolatedMask[x, y, z] = true;
                        }
                    }
                }

                pointsBySlice[z] = points;
            }

            // For every pair of slices, connect their points in 3D space
            for (int i = 0; i < slicesWithStrokes.Count - 1; i++)
            {
                int z1 = slicesWithStrokes[i];
                int z2 = slicesWithStrokes[i + 1];

                if (pointsBySlice[z1].Count == 0 || pointsBySlice[z2].Count == 0)
                    continue;

                // Calculate centroids for cleaner interpolation
                Point3D centroid1 = CalculateCentroid(pointsBySlice[z1]);
                Point3D centroid2 = CalculateCentroid(pointsBySlice[z2]);

                // Draw main path between centroids with double thickness
                DrawLine3D(
                    (int)centroid1.X, (int)centroid1.Y, (int)centroid1.Z,
                    (int)centroid2.X, (int)centroid2.Y, (int)centroid2.Z,
                    interpolatedMask, thickness: 4);

                // Connect each point to opposite centroid for fan-like structure
                foreach (var point in pointsBySlice[z1])
                {
                    DrawLine3D(
                        (int)point.X, (int)point.Y, (int)point.Z,
                        (int)centroid2.X, (int)centroid2.Y, (int)centroid2.Z,
                        interpolatedMask, thickness: 2);
                }

                foreach (var point in pointsBySlice[z2])
                {
                    DrawLine3D(
                        (int)centroid1.X, (int)centroid1.Y, (int)centroid1.Z,
                        (int)point.X, (int)point.Y, (int)point.Z,
                        interpolatedMask, thickness: 2);
                }
            }

            // Expand and smooth the interpolated shape
            interpolatedMask = ExpandVolume(interpolatedMask, radius: 3);
            interpolatedMask = SmoothMask(interpolatedMask, kernelSize: 3, threshold: 0.4);

            // Apply the interpolated mask to the labels
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (interpolatedMask[x, y, z])
                        {
                            volumeLabels[x, y, z] = materialID;
                        }
                    }
                }
            }

            Logger.Log("[InterpolateSelection] 3D interpolation completed");
            RenderViews(ViewType.All);
        }
        private bool[,,] InterpolateAlongAxis(Dictionary<int, byte[,]> sparseDict, int dimA, int dimB, int fullDim, Axis axis)
        {
            bool[,,] mask = new bool[width, height, depth];

            switch (axis)
            {
                case Axis.Z:
                    // For each (x,y) coordinate in the XY plane
                    Parallel.For(0, width, x =>
                    {
                        for (int y = 0; y < height; y++)
                        {
                            List<int> zIndices = new List<int>();
                            foreach (var kvp in sparseDict)
                            {
                                int zKey = kvp.Key;
                                byte[,] plane = kvp.Value; // Expected size: [width, height]

                                // Make sure we're within bounds
                                if (x < plane.GetLength(0) && y < plane.GetLength(1) && plane[x, y] != 0)
                                {
                                    zIndices.Add(zKey);
                                }
                            }

                            // Sort zIndices to ensure proper interpolation
                            if (zIndices.Count >= 2)
                            {
                                zIndices.Sort();

                                // Interpolate between each consecutive pair of z indices
                                for (int i = 0; i < zIndices.Count - 1; i++)
                                {
                                    int z1 = zIndices[i];
                                    int z2 = zIndices[i + 1];

                                    // Fill in all z values between z1 and z2 (inclusive)
                                    for (int z = z1; z <= z2; z++)
                                    {
                                        mask[x, y, z] = true;
                                    }
                                }
                            }
                            else if (zIndices.Count == 1)
                            {
                                // Single point, don't interpolate
                                mask[x, y, zIndices[0]] = true;
                            }
                        }
                    });
                    break;

                case Axis.Y:
                    // For each (x,z) coordinate in the XZ plane
                    Parallel.For(0, width, x =>
                    {
                        for (int z = 0; z < depth; z++)
                        {
                            List<int> yIndices = new List<int>();
                            foreach (var kvp in sparseDict)
                            {
                                int yKey = kvp.Key;
                                byte[,] plane = kvp.Value; // Expected size: [width, depth]

                                // Make sure we're within bounds
                                if (x < plane.GetLength(0) && z < plane.GetLength(1) && plane[x, z] != 0)
                                {
                                    yIndices.Add(yKey);
                                }
                            }

                            // Sort yIndices to ensure proper interpolation
                            if (yIndices.Count >= 2)
                            {
                                yIndices.Sort();

                                // Interpolate between each consecutive pair of y indices
                                for (int i = 0; i < yIndices.Count - 1; i++)
                                {
                                    int y1 = yIndices[i];
                                    int y2 = yIndices[i + 1];

                                    // Fill in all y values between y1 and y2 (inclusive)
                                    for (int y = y1; y <= y2; y++)
                                    {
                                        mask[x, y, z] = true;
                                    }
                                }
                            }
                            else if (yIndices.Count == 1)
                            {
                                // Single point, don't interpolate
                                mask[x, yIndices[0], z] = true;
                            }
                        }
                    });
                    break;

                case Axis.X:
                    // For each (y,z) coordinate in the YZ plane
                    Parallel.For(0, height, y =>
                    {
                        for (int z = 0; z < depth; z++)
                        {
                            List<int> xIndices = new List<int>();
                            foreach (var kvp in sparseDict)
                            {
                                int xKey = kvp.Key;
                                byte[,] plane = kvp.Value; // Expected size: [depth, height] for YZ plane

                                // For YZ plane, first index is z and second is y
                                if (z < plane.GetLength(0) && y < plane.GetLength(1) && plane[z, y] != 0)
                                {
                                    xIndices.Add(xKey);
                                }
                            }

                            // Sort xIndices to ensure proper interpolation
                            if (xIndices.Count >= 2)
                            {
                                xIndices.Sort();

                                // Interpolate between each consecutive pair of x indices
                                for (int i = 0; i < xIndices.Count - 1; i++)
                                {
                                    int x1 = xIndices[i];
                                    int x2 = xIndices[i + 1];

                                    // Fill in all x values between x1 and x2 (inclusive)
                                    for (int x = x1; x <= x2; x++)
                                    {
                                        mask[x, y, z] = true;
                                    }
                                }
                            }
                            else if (xIndices.Count == 1)
                            {
                                // Single point, don't interpolate
                                mask[xIndices[0], y, z] = true;
                            }
                        }
                    });
                    break;
            }

            return mask;
        }

        private void CleanupSparseSelections()
        {
            const int maxEntries = 100; // Maximum number of slices to store

            // Clean up Z selections if needed
            if (sparseSelectionsZ.Count > maxEntries)
            {
                var keysToKeep = sparseSelectionsZ.Keys.OrderByDescending(k => k).Take(maxEntries).ToList();
                var keysToRemove = sparseSelectionsZ.Keys.Except(keysToKeep).ToList();
                foreach (var key in keysToRemove)
                {
                    sparseSelectionsZ.Remove(key);
                }
            }

            // Clean up Y selections if needed
            if (sparseSelectionsY.Count > maxEntries)
            {
                var keysToKeep = sparseSelectionsY.Keys.OrderByDescending(k => k).Take(maxEntries).ToList();
                var keysToRemove = sparseSelectionsY.Keys.Except(keysToKeep).ToList();
                foreach (var key in keysToRemove)
                {
                    sparseSelectionsY.Remove(key);
                }
            }

            // Clean up X selections if needed
            if (sparseSelectionsX.Count > maxEntries)
            {
                var keysToKeep = sparseSelectionsX.Keys.OrderByDescending(k => k).Take(maxEntries).ToList();
                var keysToRemove = sparseSelectionsX.Keys.Except(keysToKeep).ToList();
                foreach (var key in keysToRemove)
                {
                    sparseSelectionsX.Remove(key);
                }
            }
        }
        public void ApplyInterpolatedSelection(byte materialID)
        {
            if (interpolatedMask == null)
            {
                Logger.Log("[ApplyInterpolatedSelection] No interpolated mask available.");
                return;
            }

            // If in thresholding mode, update only the temporary preview
            if (currentTool == SegmentationTool.Thresholding)
            {
                int currentZ = CurrentSlice;
                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        currentSelection[x, y] = interpolatedMask[x, y, currentZ] ? materialID : (byte)0;
                    }
                }
                Logger.Log("[ApplyInterpolatedSelection] Updated preview in thresholding mode.");
            }
            else
            {
                // For non-thresholding modes, apply the interpolated mask to the entire label volume
                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (interpolatedMask[x, y, z])
                            {
                                volumeLabels[x, y, z] = materialID;
                            }
                        }
                    }
                }

                // Update the temporary overlay for the current slice
                int currentZ = CurrentSlice;
                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        currentSelection[x, y] = interpolatedMask[x, y, currentZ] ? materialID : (byte)0;
                    }
                }
                Logger.Log("[ApplyInterpolatedSelection] 3D interpolated mask applied to volumeLabels.");
            }

            RenderViews(ViewType.All);
        }

        public void SubtractInterpolatedSelection(byte materialID)
        {
            if (interpolatedMask == null)
            {
                Logger.Log("[SubtractInterpolatedSelection] No interpolated mask available.");
                return;
            }

            // Subtract from ALL slices in the volume
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (interpolatedMask[x, y, z] && volumeLabels[x, y, z] == materialID)
                        {
                            volumeLabels[x, y, z] = 0;
                        }
                    }
                }
            }

            Logger.Log($"[SubtractInterpolatedSelection] Subtracted 3D interpolated mask.");
            RenderViews(ViewType.All);
        }




        private void DrawAnnotations(Graphics g, ViewType viewType)
        {
            if (AnnotationMgr == null) return;

            // Parameters for drawing annotations
            float zoom;
            PointF pan;
            float tolerance = 5.0f;

            switch (viewType)
            {
                case ViewType.XY:
                    zoom = xyZoom;
                    pan = xyPan;

                    // Draw annotations for XY view (current slice)
                    foreach (var point in AnnotationMgr.GetPointsForSlice(CurrentSlice))
                    {
                        Color materialColor = Materials.FirstOrDefault(m => m.Name == point.Label)?.Color ?? Color.Red;

                        if (point.Type == "Box")
                        {
                            // Draw box annotation
                            float x1 = point.X * zoom + pan.X;
                            float y1 = point.Y * zoom + pan.Y;
                            float x2 = point.X2 * zoom + pan.X;
                            float y2 = point.Y2 * zoom + pan.Y;

                            float boxX = Math.Min(x1, x2);
                            float boxY = Math.Min(y1, y2);
                            float boxWidth = Math.Abs(x2 - x1);
                            float boxHeight = Math.Abs(y2 - y1);

                            using (var pen = new Pen(materialColor, 2))
                            {
                                g.DrawRectangle(pen, boxX, boxY, boxWidth, boxHeight);
                                g.DrawString(point.ID.ToString(), Font, Brushes.Yellow, boxX, boxY - 15);
                            }
                        }
                        else // Draw point
                        {
                            float x = point.X * zoom + pan.X;
                            float y = point.Y * zoom + pan.Y;

                            using (var pen = new Pen(materialColor, 2))
                            {
                                g.DrawLine(pen, x - 5, y, x + 5, y);
                                g.DrawLine(pen, x, y - 5, x, y + 5);
                                g.DrawString(point.ID.ToString(), Font, Brushes.Yellow, x + 5, y + 5);
                            }
                        }
                    }
                    break;

                case ViewType.XZ:
                    zoom = xzZoom;
                    pan = xzPan;

                    // Draw annotations in XZ view
                    foreach (var point in AnnotationMgr.Points)
                    {
                        if (Math.Abs(point.Y - XzSliceY) <= tolerance)
                        {
                            Color pointColor = Materials.FirstOrDefault(m => m.Name == point.Label)?.Color ?? Color.Red;

                            if (point.Type == "Box")
                            {
                                // In XZ view, draw box using X and Z coordinates
                                float x1 = point.X * zoom + pan.X;
                                float z1 = point.Z * zoom + pan.Y;
                                float x2 = point.X2 * zoom + pan.X;
                                float z2 = point.Y2 * zoom + pan.Y; // Y2 is storing Z2 for XZ boxes

                                float boxX = Math.Min(x1, x2);
                                float boxY = Math.Min(z1, z2);
                                float boxWidth = Math.Abs(x2 - x1);
                                float boxHeight = Math.Abs(z2 - z1);

                                using (Pen pen = new Pen(pointColor, 2))
                                {
                                    g.DrawRectangle(pen, boxX, boxY, boxWidth, boxHeight);
                                }
                                using (Font font = new Font("Arial", 8))
                                using (SolidBrush idBrush = new SolidBrush(Color.Yellow))
                                {
                                    g.DrawString(point.ID.ToString(), font, idBrush, boxX, boxY - 10);
                                }
                            }
                            else // Draw point
                            {
                                float drawX = point.X * zoom + pan.X;
                                float drawZ = point.Z * zoom + pan.Y;

                                using (Pen pen = new Pen(pointColor, 2))
                                {
                                    g.DrawLine(pen, drawX - 5, drawZ, drawX + 5, drawZ);
                                    g.DrawLine(pen, drawX, drawZ - 5, drawX, drawZ + 5);
                                }
                                using (Font font = new Font("Arial", 8))
                                using (SolidBrush idBrush = new SolidBrush(Color.Yellow))
                                {
                                    g.DrawString(point.ID.ToString(), font, idBrush, drawX + 5, drawZ + 5);
                                }
                            }
                        }
                    }
                    break;

                case ViewType.YZ:
                    zoom = yzZoom;
                    pan = yzPan;

                    // Draw annotations in YZ view
                    foreach (var point in AnnotationMgr.Points)
                    {
                        if (Math.Abs(point.X - YzSliceX) <= tolerance)
                        {
                            Color pointColor = Materials.FirstOrDefault(m => m.Name == point.Label)?.Color ?? Color.Red;

                            if (point.Type == "Box")
                            {
                                // In YZ view, draw box using Z and Y coordinates
                                float z1 = point.X2 * zoom + pan.X; // X2 is storing Z1 for YZ boxes
                                float y1 = point.Y * zoom + pan.Y;
                                float z2 = point.Y2 * zoom + pan.X; // Y2 is storing Z2 for YZ boxes
                                float y2 = point.Y2 * zoom + pan.Y;

                                float boxX = Math.Min(z1, z2);
                                float boxY = Math.Min(y1, y2);
                                float boxWidth = Math.Abs(z2 - z1);
                                float boxHeight = Math.Abs(y2 - y1);

                                using (Pen pen = new Pen(pointColor, 2))
                                {
                                    g.DrawRectangle(pen, boxX, boxY, boxWidth, boxHeight);
                                }
                                using (Font font = new Font("Arial", 8))
                                using (SolidBrush idBrush = new SolidBrush(Color.Yellow))
                                {
                                    g.DrawString(point.ID.ToString(), font, idBrush, boxX, boxY - 10);
                                }
                            }
                            else // Draw point
                            {
                                float drawZ = point.Z * zoom + pan.X;
                                float drawY = point.Y * zoom + pan.Y;

                                using (Pen pen = new Pen(pointColor, 2))
                                {
                                    g.DrawLine(pen, drawZ - 5, drawY, drawZ + 5, drawY);
                                    g.DrawLine(pen, drawZ, drawY - 5, drawZ, drawY + 5);
                                }
                                using (Font font = new Font("Arial", 8))
                                using (SolidBrush idBrush = new SolidBrush(Color.Yellow))
                                {
                                    g.DrawString(point.ID.ToString(), font, idBrush, drawZ + 5, drawY + 5);
                                }
                            }
                        }
                    }
                    break;
            }
        }

        public void RemoveMaterialAndReindex(int removeMaterialID)
        {
            // Do not allow deletion of the Exterior material (ID 0)
            if (removeMaterialID == 0 || MaterialOps == null)
                return;

            // Use MaterialOps to remove the material and clear associated voxels
            MaterialOps.RemoveMaterial((byte)removeMaterialID);

            // Clear any temporary selection voxels using this material
            if (currentSelection != null)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (currentSelection[x, y] == removeMaterialID)
                            currentSelection[x, y] = 0;
                    }
                }
            }

            // Clear temporary XZ and YZ selections as well
            if (currentSelectionXZ != null)
            {
                for (int z = 0; z < depth; z++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (currentSelectionXZ[x, z] == removeMaterialID)
                            currentSelectionXZ[x, z] = 0;
                    }
                }
            }

            if (currentSelectionYZ != null)
            {
                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        if (currentSelectionYZ[z, y] == removeMaterialID)
                            currentSelectionYZ[z, y] = 0;
                    }
                }
            }

            // Adjust the SelectedMaterialIndex if needed
            if (SelectedMaterialIndex >= Materials.Count)
                SelectedMaterialIndex = Materials.Count - 1;

            // Re-render all views
            RenderViews(ViewType.All);
        }

        // Custom scrollable picture box class for better rendering performance
        public class ScrollablePictureBox : PictureBox
        {
            public ScrollablePictureBox()
            {
                this.SetStyle(ControlStyles.OptimizedDoubleBuffer |
                              ControlStyles.AllPaintingInWmPaint |
                              ControlStyles.UserPaint, true);
            }
        }

        // Make sure to clean up resources when the form is closed
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            // Cancel any pending rendering operations
            renderCts.Cancel();

            // Dispose bitmaps
            xyBitmap?.Dispose();
            xzBitmap?.Dispose();
            yzBitmap?.Dispose();
        }

        // IDisposable implementation
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose managed resources
                bitmapLock?.Dispose();
                renderCts?.Cancel();

                // Dispose bitmaps
                xyBitmap?.Dispose();
                xzBitmap?.Dispose();
                yzBitmap?.Dispose();

                // Dispose volume data
                volumeData?.Dispose();
                if (volumeLabels != null)
                {
                    volumeLabels.ReleaseFileLock();
                    volumeLabels.Dispose();
                }

                // Dispose docking managers
                if (_dockingManager != null)
                {
                    _dockingManager.Dispose();
                    _dockingManager = null;
                }

                if (_dock != null)
                {
                    _dock.Dispose();
                    _dock = null;
                }
            }

            base.Dispose(disposing);
        }

        // Get bitmaps for other classes that need them
        public Bitmap GetSliceBitmap(int z)
        {
            if (volumeData == null) return null;

            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte intensity = volumeData[x, y, z];
                    bmp.SetPixel(x, y, Color.FromArgb(intensity, intensity, intensity));
                }
            }

            return bmp;
        }

        public Bitmap GetXZSliceBitmap(int fixedY)
        {
            if (volumeData == null) return null;

            Bitmap bmp = new Bitmap(width, depth, PixelFormat.Format24bppRgb);

            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte intensity = volumeData[x, fixedY, z];
                    bmp.SetPixel(x, z, Color.FromArgb(intensity, intensity, intensity));
                }
            }

            return bmp;
        }

        public Bitmap GetYZSliceBitmap(int fixedX)
        {
            if (volumeData == null) return null;

            Bitmap bmp = new Bitmap(depth, height, PixelFormat.Format24bppRgb);

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    byte intensity = volumeData[fixedX, y, z];
                    bmp.SetPixel(z, y, Color.FromArgb(intensity, intensity, intensity));
                }
            }

            return bmp;
        }

        // Update dataset after processing
        public void UpdateVolumeData(ChunkedVolume newVolumeData)
        {
            if (newVolumeData == null)
                throw new ArgumentNullException(nameof(newVolumeData));

            // Get the new dimensions
            int newWidth = newVolumeData.Width;
            int newHeight = newVolumeData.Height;
            int newDepth = newVolumeData.Depth;

            Logger.Log($"[MainForm] Updating volume data to {newWidth}x{newHeight}x{newDepth}");

            // Replace the existing volume data with the new one
            volumeData = newVolumeData;

            // Update dimensions
            width = newWidth;
            height = newHeight;
            depth = newDepth;

            // Refresh the views
            CurrentSlice = Math.Min(CurrentSlice, newDepth - 1);
            XzSliceY = Math.Min(XzSliceY, newHeight - 1);
            YzSliceX = Math.Min(YzSliceX, newWidth - 1);

            // Render the updated volume
            RenderViews(ViewType.All);

            Logger.Log("[MainForm] Volume data successfully updated");
        }
        public void UpdateXzSliceY(int y)
        {
            if (XzSliceY != y)
            {
                XzSliceY = Math.Max(0, Math.Min(y, height - 1));
                RenderViews(ViewType.XZ);
                NotifyXZRowChangeCallbacks(XzSliceY);

                // Update the 3D orthogonal view
                if (orthogonalView != null)
                    orthogonalView.UpdatePosition(YzSliceX, XzSliceY, currentSlice);
            }
        }

        public void UpdateYzSliceX(int x)
        {
            if (YzSliceX != x)
            {
                YzSliceX = Math.Max(0, Math.Min(x, width - 1));
                RenderViews(ViewType.YZ);
                NotifyYZColChangeCallbacks(YzSliceX);

                // Update the 3D orthogonal view
                if (orthogonalView != null)
                    orthogonalView.UpdatePosition(YzSliceX, XzSliceY, currentSlice);
            }
        }
        public void OnDatasetChanged()
        {
            // Update UI to reflect the new dataset dimensions
            width = volumeData.Width;
            height = volumeData.Height;
            depth = volumeData.Depth;

            // Make sure the current slice is valid for the new volume
            CurrentSlice = Math.Min(CurrentSlice, depth - 1);

            // Update XZ and YZ slice positions
            XzSliceY = Math.Min(XzSliceY, height - 1);
            YzSliceX = Math.Min(YzSliceX, width - 1);

            // Update the window title with new dimensions
            this.Text = $"CT Segmentation Suite - {width}x{height}x{depth} (Pixel Size: {pixelSize:0.000000} m)";

            // Refresh all the views
            RenderViews(ViewType.All);

            Logger.Log($"[MainForm] Dataset updated: {width}x{height}x{depth}, PixelSize: {pixelSize}");
        }
        #endregion
        #region 3D interpolation
        private bool[,,] SmoothMask(bool[,,] mask, int kernelSize, double threshold)
        {
            int w = mask.GetLength(0), h = mask.GetLength(1), d = mask.GetLength(2);
            double[,,] density = new double[w, h, d];
            int radius = kernelSize / 2;

            // Calculate density values with distance-based weighting
            Parallel.For(0, w, x =>
            {
                for (int y = 0; y < h; y++)
                {
                    for (int z = 0; z < d; z++)
                    {
                        double sum = 0;
                        double totalWeight = 0;

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            for (int dy = -radius; dy <= radius; dy++)
                            {
                                for (int dz = -radius; dz <= radius; dz++)
                                {
                                    int nx = x + dx, ny = y + dy, nz = z + dz;

                                    if (nx >= 0 && nx < w && ny >= 0 && ny < h && nz >= 0 && nz < d)
                                    {
                                        // Calculate inverse-square distance weight
                                        double distSq = dx * dx + dy * dy + dz * dz;
                                        double weight = 1.0 / (1.0 + distSq); // Avoid division by zero

                                        sum += mask[nx, ny, nz] ? weight : 0;
                                        totalWeight += weight;
                                    }
                                }
                            }
                        }

                        if (totalWeight > 0)
                            density[x, y, z] = sum / totalWeight;
                    }
                }
            });

            // Threshold the density to create the smoothed mask
            bool[,,] smoothed = new bool[w, h, d];
            Parallel.For(0, w, x =>
            {
                for (int y = 0; y < h; y++)
                {
                    for (int z = 0; z < d; z++)
                    {
                        smoothed[x, y, z] = density[x, y, z] >= threshold;
                    }
                }
            });

            return smoothed;
        }

        private Point3D CalculateCentroid(List<Point3D> points)
        {
            if (points.Count == 0)
                return new Point3D(0, 0, 0);

            double sumX = 0, sumY = 0, sumZ = 0;
            foreach (var point in points)
            {
                sumX += point.X;
                sumY += point.Y;
                sumZ += point.Z;
            }

            return new Point3D(
                sumX / points.Count,
                sumY / points.Count,
                sumZ / points.Count);
        }

        private void DrawLine3D(int x1, int y1, int z1, int x2, int y2, int z2, bool[,,] volume, int thickness)
        {
            // Calculate step count based on the longest dimension
            int dx = x2 - x1;
            int dy = y2 - y1;
            int dz = z2 - z1;
            int steps = Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy)), Math.Abs(dz));

            // Calculate increments for each dimension
            double xInc = dx / (double)steps;
            double yInc = dy / (double)steps;
            double zInc = dz / (double)steps;

            double x = x1, y = y1, z = z1;
            double radius = thickness / 2.0;

            // Draw each point along the line with thickness
            for (int i = 0; i <= steps; i++)
            {
                int ix = (int)Math.Round(x);
                int iy = (int)Math.Round(y);
                int iz = (int)Math.Round(z);

                // Draw a sphere at this point
                for (int offZ = -(int)radius; offZ <= radius; offZ++)
                {
                    for (int offY = -(int)radius; offY <= radius; offY++)
                    {
                        for (int offX = -(int)radius; offX <= radius; offX++)
                        {
                            // Check if point is within the sphere
                            if (offX * offX + offY * offY + offZ * offZ <= radius * radius)
                            {
                                int nx = ix + offX;
                                int ny = iy + offY;
                                int nz = iz + offZ;

                                // Check bounds
                                if (nx >= 0 && nx < width && ny >= 0 && ny < height && nz >= 0 && nz < depth)
                                {
                                    volume[nx, ny, nz] = true;
                                }
                            }
                        }
                    }
                }

                x += xInc;
                y += yInc;
                z += zInc;
            }
        }
        private bool[,,] ExpandVolume(bool[,,] input, int radius)
        {
            bool[,,] output = new bool[width, height, depth];

            // Copy input to output
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        output[x, y, z] = input[x, y, z];
                    }
                }
            }

            // For each 'true' voxel, expand in a sphere
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (input[x, y, z])
                        {
                            for (int dz = -radius; dz <= radius; dz++)
                            {
                                for (int dy = -radius; dy <= radius; dy++)
                                {
                                    for (int dx = -radius; dx <= radius; dx++)
                                    {
                                        if (dx * dx + dy * dy + dz * dz <= radius * radius)
                                        {
                                            int nx = x + dx;
                                            int ny = y + dy;
                                            int nz = z + dz;

                                            if (nx >= 0 && nx < width && ny >= 0 && ny < height && nz >= 0 && nz < depth)
                                            {
                                                output[nx, ny, nz] = true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            });

            return output;
        }
        private class Point3D
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }

            public Point3D(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        #endregion
        #region Material Merging
        public void MergeMaterials(byte targetID, byte sourceID)
        {
            Logger.Log($"[MainForm] Merging material {sourceID} into {targetID}");
            int w = width, h = height, d = depth;

            // Reassign all voxels labeled sourceID → targetID
            for (int z = 0; z < d; z++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (volumeLabels[x, y, z] == sourceID)
                            volumeLabels[x, y, z] = targetID;

            // Remove the material entry
            var mat = Materials.FirstOrDefault(m => m.ID == sourceID);
            if (mat != null)
                Materials.Remove(mat);

            // Persist the updated materials list
            SaveLabelsChk();

            // Refresh all views so the change is visible immediately
            RenderViews(ViewType.All);
        }
        #endregion
    }
}
