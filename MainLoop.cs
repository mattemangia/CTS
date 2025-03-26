using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using static CTSegmenter.SAMForm;
using System.Threading;
using System.Runtime.InteropServices;

namespace CTSegmenter
{
    public enum SegmentationTool { Pan, Brush, Eraser, Thresholding, Point }
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string logPath = Path.Combine(Application.StartupPath, "log.txt");
            if (File.Exists(logPath))
                File.Delete(logPath); // Clear old logs
            Logger.Log("Application started.");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MainForm mainForm = new MainForm(args);
            ControlForm controlForm = new ControlForm(mainForm);
            controlForm.Show();
            Application.Run(mainForm);
            Logger.Log("Application ended.");
        }
    }

    // ------------------------------------------------------------------------
    // MainForm – main viewer and controller of segmentation
    // ------------------------------------------------------------------------
    public partial class MainForm : Form
    {
        #region Fields

        // Volume metadata
        private int width, height, depth;
        public double pixelSize = 1e-6;
        public int SelectedBinningFactor { get; set; } = 1;

        public string CurrentPath { get; private set; } = "";

        // Flag: if true use memory mapping (default false = load full volume in RAM)
        private bool useMemoryMapping = true;

        // Materials: index 0 is reserved for the exterior.
        public List<Material> Materials = new List<Material>();

        // The volume data (grayscale) and segmentation (labels)
        public ChunkedVolume volumeData;         // grayscale volume
        public ChunkedLabelVolume volumeLabels;  // label volume
        private readonly object datasetLock = new object();
        

        // Rendering fields
        private System.Windows.Forms.Timer renderTimer;

        // Main view zoom/pan
        public float globalZoom = 1.0f;
        private float zoomLevel = 1.0f;
        public PointF panOffset = PointF.Empty;
        private Bitmap currentBitmap;

        // PictureBoxes for main view + orthoviews
        private PictureBox mainView;
        private PictureBox xzView;
        private PictureBox yzView;

        // Cache for precomputed lateral views.
        private bool isRenderingOrtho = false;
        private ConcurrentDictionary<string, Bitmap> orthoViewCache = new ConcurrentDictionary<string, Bitmap>();

        private DateTime lastOrthoRenderTime = DateTime.MinValue;
        // For orthoview toggling
        private bool showProjections = true;
        private Bitmap xzCache, yzCache;

        // Orthoview bitmaps and states
        public bool ShowMask { get; set; } = false;
        public bool EnableThresholdMask { get; set; } = true;
        // New property: when true, full material colors are rendered (the "Render Materials" mode)
        public bool RenderMaterials { get; set; } = false;
        private Bitmap xzProjection, yzProjection;

        // Orthoview slice positions
        public int XzSliceY { get; set; }
        public int YzSliceX { get; set; }

        // Orthoview zoom/pan states
        private float xzZoom = 1.0f;
        public PointF xzPan = PointF.Empty;
        private float yzZoom = 1.0f;
        public PointF yzPan = PointF.Empty;

        // Accessors
        public int GetWidth() => width;
        public int GetHeight() => height;

        // Current slice index (0-based)
        private int currentSlice;
        public int CurrentSlice
        {
            get => currentSlice;
            set
            {
                currentSlice = Math.Max(0, Math.Min(value, depth - 1));
                // Start the timer to re-render the main slice
                renderTimer?.Start();
            }
        }
        //SAM2 Annotations
        public AnnotationManager AnnotationMgr { get; set; }
        // Demonstration: chunk dimension
        private const int CHUNK_DIM = 256;
        public CTMemorySegmenter LiveSegmenter
        {
            get { return _liveSegmenter; }
        }
        // Cache for rendered slices
        private Dictionary<int, byte[]> sliceCache = new Dictionary<int, byte[]>();
        private const int MAX_CACHE_SIZE = 5;
        private readonly object sliceCacheLock = new object(); // Protects sliceCache
        private Bitmap _backingStore;
        public int SelectedMaterialIndex { get; set; } = -1;
        public byte PreviewMin { get; set; }
        public byte PreviewMax { get; set; }

        // For panning the main view
        private Point lastMousePosition;

        // A flag to prevent re-entrancy in RenderOrthoViewsAsync
        private System.Windows.Forms.Timer thresholdUpdateTimer;
        private int pendingMin = -1;
        private int pendingMax = -1;

        //Tools
        public SegmentationTool currentTool = SegmentationTool.Pan;
        private int currentBrushSize = 50;
        private bool showBrushOverlay = false;
        // New overlay centers for orthoviews:
        private PointF xzOverlayCenter = new PointF(0, 0);
        private PointF yzOverlayCenter = new PointF(0, 0);

        private Point brushOverlayCenter; // might choose to store the last mouse location
        public bool[,,] interpolatedMask;
        // Stores sparse brush annotations for slices the user has applied.
        public Dictionary<int, byte[,]> sparseSelectionsZ = new Dictionary<int, byte[,]>(); // XY slices (Z-axis)
        public Dictionary<int, byte[,]> sparseSelectionsY = new Dictionary<int, byte[,]>(); // XZ slices (Y-axis)
        public Dictionary<int, byte[,]> sparseSelectionsX = new Dictionary<int, byte[,]>(); // YZ slices (X-axis)
        public Dictionary<int, byte[,]> sparseSelections = new Dictionary<int, byte[,]>();
        public enum Axis { X, Y, Z }
        
        public SAMForm SamFormInstance { get; set; }

        private byte nextMaterialID = 1; // 0 is reserved for Exterior.
        public byte[,] currentSelection;  // dimensions: [width, height] for current slice temporary selection
                                          // For orthoview temporary selections.
        public byte[,] currentSelectionXZ; // dimensions: [width, depth] for XZ view (fixed Y = XzSliceY)
        public byte[,] currentSelectionYZ; // dimensions: [depth, height] for YZ view (fixed X = YzSliceX)

        private bool isBoxDrawingPossible = false;
        private Point boxStartPoint;
        private Point boxCurrentPoint;
        private bool isActuallyDrawingBox = false;
        public bool RealTimeProcessing { get; set; } = false;
        private DateTime _lastRenderRequest = DateTime.MinValue;
        private const int RENDER_THROTTLE_MS = 50;
        private CancellationTokenSource xyRenderCts = new CancellationTokenSource();

        private bool isXzBoxDrawingPossible = false;
        private Point xzBoxStartPoint;
        private Point xzBoxCurrentPoint;
        private bool isXzActuallyDrawingBox = false;

        private bool isYzBoxDrawingPossible = false;
        private Point yzBoxStartPoint;
        private Point yzBoxCurrentPoint;
        private bool isYzActuallyDrawingBox = false;

        #endregion

        public MainForm(string[] args)
        {
            Logger.Log("[MainForm] Constructor start.");
            InitializeComponent();
            InitializeTimers();
            InitializeMaterials();
            mainView.Paint += MainView_Paint;
            //mainView.MouseDown += MainView_MouseDown;
            mainView.MouseMove += MainView_MouseMove;
            mainView.MouseWheel += MainView_MouseWheel;

            // If started with an argument, load dataset
            if (args.Length > 0)
            {
                _ = LoadDatasetAsync(args[0]);
            }
            this.Resize += MainForm_Resize;
            Logger.Log("[MainForm] Constructor end.");
        }

        #region Setup
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

            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 70F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            mainView = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);

            // Main view – using Paint event.
            mainView = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };
            mainLayout.Controls.Add(mainView, 0, 0);
            mainLayout.SetColumnSpan(mainView, 1);
            mainLayout.SetRowSpan(mainView, 1);
            mainView.Paint += MainView_Paint;
            mainView.MouseWheel += MainView_MouseWheel;
            mainView.MouseDown += MainView_MouseDown;
            mainView.MouseMove += MainView_MouseMove;
            mainView.MouseUp += MainView_MouseUp;
            // XZ view
            xzView = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            mainLayout.Controls.Add(xzView, 0, 1);
            xzView.MouseWheel += XzView_MouseWheel;
            xzView.Paint += XzView_Paint;
            xzView.MouseDown += XzView_MouseDown;
            xzView.MouseMove += XzView_MouseMove;

            // YZ view
            yzView = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            mainLayout.Controls.Add(yzView, 1, 0);
            yzView.MouseWheel += YzView_MouseWheel;
            yzView.Paint += YzView_Paint;
            yzView.MouseDown += YzView_MouseDown;
            yzView.MouseMove += YzView_MouseMove;
            xzView.MouseUp += XzView_MouseUp;
            yzView.MouseUp += YzView_MouseUp;

            this.Controls.Add(mainLayout);
        }
        // Cached segmenter instance for live preview
        private CTMemorySegmenter _liveSegmenter = null;
        // Add to class fields
        private readonly ReaderWriterLockSlim _bitmapLock = new ReaderWriterLockSlim();
        private Bitmap _scaledBitmap;
        private float _lastZoom = -1;
        // Clears the cached segmenter (for example, when live preview is turned off or settings change)
        public void ClearLiveSegmenter()
        {
            if (_liveSegmenter != null)
            {
                _liveSegmenter.Dispose();
                _liveSegmenter = null;
            }
        }
        private void UpdateBackingStore()
        {
            if (currentBitmap == null) return;

            _backingStore?.Dispose();
            _backingStore = new Bitmap(
                (int)(currentBitmap.Width * zoomLevel),
                (int)(currentBitmap.Height * zoomLevel),
                PixelFormat.Format24bppRgb);

            using (var g = Graphics.FromImage(_backingStore))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(currentBitmap,
                    new Rectangle(0, 0, _backingStore.Width, _backingStore.Height),
                    new Rectangle(0, 0, currentBitmap.Width, currentBitmap.Height),
                    GraphicsUnit.Pixel);
            }
        }

        public void SetSegmentationTool(SegmentationTool tool)
        {
            currentTool = tool;
            // Update event behavior as needed.
        }

        // This method now immediately shows the red circle overlay and automatically hides it after 500ms.
        // Call this from the tool size slider scroll event.
        public void ShowBrushOverlay(int size)
        {
            currentBrushSize = size;
            // Center the overlay relative to the mainView PictureBox.
            brushOverlayCenter = new Point(mainView.ClientSize.Width / 2, mainView.ClientSize.Height / 2);
            showBrushOverlay = true;
            mainView.Invalidate();

            // Create a timer that hides the overlay after 1000ms.
            System.Windows.Forms.Timer overlayTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            overlayTimer.Tick += (s, e) =>
            {
                showBrushOverlay = false;
                mainView.Invalidate();
                overlayTimer.Stop();
                overlayTimer.Dispose();
            };
            overlayTimer.Start();
        }


        // This remains as a manual way to force hiding the overlay.
        public void HideBrushOverlay()
        {
            showBrushOverlay = false;
            mainView.Invalidate();
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            ClampPanOffset();
            mainView.Invalidate();
        }
        private void InitializeTimers()
        {
            renderTimer = new System.Windows.Forms.Timer { Interval = RENDER_THROTTLE_MS };
            renderTimer.Tick += (s, e) => {
                renderTimer.Stop();
                RenderViews();
            };
        }

        // When loading a dataset from a folder, we want to reset Materials to only Exterior and one empty Material1.
        private void InitializeMaterials()
        {
            Materials.Clear();
            Materials.Add(new Material("Exterior", Color.Transparent, 0, 0, 0) { IsExterior = true });
            Materials.Add(new Material("Material1", Color.Blue, 0, 0, GetNextMaterialID()));
            SaveLabelsChk();
        }
        #endregion

        #region Public Interface for ControlForm
        public int GetDepth() => depth;
        public double GetPixelSize() => pixelSize;
        private Color GetComplementaryColor(Color color)
        {
            return Color.FromArgb(255 - color.R, 255 - color.G, 255 - color.B);
        }
        public void SetShowProjections(bool enable)
        {
            showProjections = enable;
            if (!showProjections)
            {
                xzView.Image?.Dispose();
                xzView.Image = null;
                yzView.Image?.Dispose();
                yzView.Image = null;
            }
            if (showProjections)
                _ = RenderOrthoViewsAsync();
            else
                RenderViews();
        }
        public bool GetShowProjections() => showProjections;
        public void SetUseMemoryMapping(bool useIt)
        {
            useMemoryMapping = useIt;
            Logger.Log($"[SetUseMemoryMapping] useMemoryMapping set to {useMemoryMapping}");
        }
        #endregion

        #region Load / Save

        public async Task LoadDatasetAsync(string path)
        {
            Logger.Log($"[LoadDatasetAsync] Loading dataset from path: {path}");
            ProgressForm progressForm = null;
            try
            {
                await this.SafeInvokeAsync(() =>
                {
                    progressForm = new ProgressForm("Loading dataset...");
                    progressForm.Show();
                });

                CurrentPath = path;
                renderTimer?.Stop();

                // Dispose of any previously loaded volume and labels.
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
                // Ask for pixel size only if in folder mode.
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
                // --- Folder Mode ---
                if (folderMode)
                {
                    Logger.Log("[LoadDatasetAsync] Detected folder mode.");
                    lock (datasetLock)
                    {
                        InitializeMaterials();
                        SaveLabelsChk();
                    }

                    string volumeBinPath = Path.Combine(path, "volume.bin");
                    string labelsBinPath = Path.Combine(path, "labels.bin");
                    string volumeChkPath = Path.Combine(path, "volume.chk");

                    // --- BINNING Branch ---
                    if (SelectedBinningFactor > 1)
                    {
                        if (!File.Exists(volumeBinPath) || !File.Exists(labelsBinPath) || !File.Exists(volumeChkPath))
                        {
                            Logger.Log("[LoadDatasetAsync] Binary files missing. Generating volume.bin from folder images.");
                            lock (datasetLock)
                            {
                                if (useMemoryMapping)
                                    volumeData = ChunkedVolume.FromFolder(path, CHUNK_DIM, progressForm, true);
                                else
                                    volumeData = ChunkedVolume.FromFolder(path, CHUNK_DIM, progressForm, false);
                                width = volumeData.Width;
                                height = volumeData.Height;
                                depth = volumeData.Depth;
                            }
                            Logger.Log($"[LoadDatasetAsync] Loaded original volume: {width}x{height}x{depth}");
                            CreateVolumeChk(path, width, height, depth, CHUNK_DIM, pixelSize);
                            if (!File.Exists(labelsBinPath))
                            {
                                CreateBlankLabelsFile(labelsBinPath, width, height, depth, CHUNK_DIM);
                            }
                            volumeData.Dispose();
                            volumeData = null;
                        }

                        string backupVolPath = Path.Combine(path, "temp_volume.bin");
                        Logger.Log($"[LoadDatasetAsync] Creating backup copy of volume.bin: {backupVolPath}");
                        File.Copy(volumeBinPath, backupVolPath, true);

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

                        Logger.Log($"[LoadDatasetAsync] Running binning process with factor {SelectedBinningFactor}...");
                        Progress<int> binProgress = new Progress<int>(p => progressForm.UpdateProgress(p));
                        await Binning.ProcessBinningAsync(path, SelectedBinningFactor, (float)pixelSize, useMemoryMapping);
                        Logger.Log("[LoadDatasetAsync] Binning complete. Loading binned volume.");

                        if (useMemoryMapping)
                            volumeData = LoadVolumeBin(volumeBinPath, true);
                        else
                            volumeData = LoadVolumeBin(volumeBinPath, false);
                        width = volumeData.Width;
                        height = volumeData.Height;
                        depth = volumeData.Depth;
                        Logger.Log($"[LoadDatasetAsync] Loaded binned volume: {width}x{height}x{depth}");

                        if (File.Exists(labelsBinPath))
                        {
                            Logger.Log($"[LoadDatasetAsync] Found labels file at: {labelsBinPath}");
                            try
                            {
                                volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log("[LoadDatasetAsync] Error loading labels.bin: " + ex.Message + ". Recreating blank labels file.");
                                if (useMemoryMapping)
                                {
                                    volumeLabels?.ReleaseFileLock();
                                    volumeLabels?.Dispose();
                                    volumeLabels = null;
                                }
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                                CreateBlankLabelsFile(labelsBinPath, width, height, depth, CHUNK_DIM);
                                volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);
                                Logger.Log("[LoadDatasetAsync] Reloaded new labels.bin.");
                            }
                        }
                        else
                        {
                            Logger.Log("[LoadDatasetAsync] labels.bin not found. Creating blank labels file.");
                            CreateBlankLabelsFile(labelsBinPath, width, height, depth, CHUNK_DIM);
                            volumeLabels = LoadLabelsBin(labelsBinPath, useMemoryMapping);
                        }
                    }
                    // --- NO BINNING Branch ---
                    else
                    {
                        Logger.Log("[LoadDatasetAsync] Binning factor = 1. Loading volume directly from folder images.");
                        if (useMemoryMapping)
                            volumeData = ChunkedVolume.FromFolder(path, CHUNK_DIM, progressForm, true);
                        else
                            volumeData = ChunkedVolume.FromFolder(path, CHUNK_DIM, progressForm, false);
                        width = volumeData.Width;
                        height = volumeData.Height;
                        depth = volumeData.Depth;
                        Logger.Log($"[LoadDatasetAsync] Loaded volume: {width}x{height}x{depth}");
                        CreateVolumeChk(path, width, height, depth, CHUNK_DIM, pixelSize);

                        if (File.Exists(Path.Combine(path, "labels.bin")))
                        {
                            Logger.Log($"[LoadDatasetAsync] Found labels file at: {Path.Combine(path, "labels.bin")}");
                            volumeLabels = LoadLabelsBin(Path.Combine(path, "labels.bin"), useMemoryMapping);
                        }
                        else
                        {
                            Logger.Log("[LoadDatasetAsync] labels.bin not found. Creating blank labels file.");
                            CreateBlankLabelsFile(Path.Combine(path, "labels.bin"), width, height, depth, CHUNK_DIM);
                            volumeLabels = LoadLabelsBin(Path.Combine(path, "labels.bin"), useMemoryMapping);
                        }
                    }
                }
                // --- File Mode ---
                else if (File.Exists(path))
                {
                    string fileName = Path.GetFileName(path).ToLower();
                    string baseFolder = Path.GetDirectoryName(path);
                    // Case: Volume file (volume.bin) only.
                    if (fileName.Contains("volume") && !fileName.Contains("labels"))
                    {
                        string volChk = Path.Combine(baseFolder, "volume.chk");
                        if (File.Exists(volChk))
                        {
                            var header = ReadVolumeChk(baseFolder);
                            int volWidth = header.volWidth;
                            int volHeight = header.volHeight;
                            int volDepth = header.volDepth;
                            int chkChunkDim = header.chunkDim;
                            double chkPixelSize = header.pixelSize;
                            pixelSize = chkPixelSize;
                            Logger.Log($"[LoadDatasetAsync] Loaded header from volume.chk: {volWidth}x{volHeight}x{volDepth}, chunkDim={chkChunkDim}, pixelSize={pixelSize}");
                            // Use raw-loading since the volume.bin in file mode (when exported from folder mode) does not include a header.
                            if (useMemoryMapping)
                                volumeData = LoadVolumeBinRaw(path, true, volWidth, volHeight, volDepth, chkChunkDim);
                            else
                                volumeData = LoadVolumeBinRaw(path, false, volWidth, volHeight, volDepth, chkChunkDim);
                            width = volumeData.Width;
                            height = volumeData.Height;
                            depth = volumeData.Depth;
                            string labelsPath = Path.Combine(baseFolder, "labels.bin");
                            if (File.Exists(labelsPath))
                            {
                                volumeLabels = LoadLabelsBin(labelsPath, useMemoryMapping);
                            }
                            else
                            {
                                CreateBlankLabelsFile(labelsPath, width, height, depth, CHUNK_DIM);
                                volumeLabels = LoadLabelsBin(labelsPath, useMemoryMapping);
                            }
                        }
                        else
                        {
                            Logger.Log("[LoadDatasetAsync] volume.chk not found. Assuming combined file mode.");
                            LoadCombinedBinary(path);
                        }
                    }
                    // Case: Labels file only.
                    else if (fileName.Contains("labels"))
                    {
                        string labChk = Path.Combine(baseFolder, "labels.chk");
                        if (!File.Exists(labChk))
                            throw new Exception("Labels header file (labels.chk) not found.");
                        volumeLabels = LoadLabelsBin(path, useMemoryMapping);
                        volumeData = null;
                        width = volumeLabels.Width;
                        height = volumeLabels.Height;
                        depth = volumeLabels.Depth;
                    }
                    // Otherwise, assume Combined File mode.
                    else
                    {
                        Logger.Log("[LoadDatasetAsync] File mode: assuming combined file mode.");
                        LoadCombinedBinary(path);
                    }
                }
                else
                {
                    Logger.Log("[LoadDatasetAsync] Provided path is neither a folder nor an existing file.");
                    return;
                }

                // Update UI and trigger initial render.
                if ((volumeData != null || volumeLabels != null) && !this.IsDisposed && mainView != null && mainView.IsHandleCreated)
                {
                    await mainView.SafeInvokeAsync(() =>
                    {
                        CurrentSlice = (volumeData != null ? depth / 2 : 0);
                        XzSliceY = (height > 0 ? height / 2 : 0);
                        YzSliceX = (width > 0 ? width / 2 : 0);
                        RenderViews();
                    });
                    _ = RenderOrthoViewsAsync();
                }
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







        public void CloseDataset()
        {
            if (volumeData != null)
            {
                volumeData.Dispose();
                volumeData = null;
                Logger.Log("[CloseDataset] Dataset closed and released from memory.");
            }
        }

        private void CreateVolumeChk(string folder, int volWidth, int volHeight, int volDepth, int chunkDim, double pixelSize)
        {
            string chkPath = Path.Combine(folder, "volume.chk");
            using (FileStream fs = new FileStream(chkPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(volWidth);
                bw.Write(volHeight);
                bw.Write(volDepth);
                bw.Write(chunkDim);
                bw.Write(pixelSize); // Store pixel size
            }
            Logger.Log($"[CreateVolumeChk] Created header file at {chkPath} with pixel size {pixelSize}");
        }

        private (int volWidth, int volHeight, int volDepth, int chunkDim, double pixelSize) ReadVolumeChk(string folder)
        {
            string chkPath = Path.Combine(folder, "volume.chk");
            if (!File.Exists(chkPath))
                throw new Exception("Volume header file (volume.chk) not found.");

            using (FileStream fs = new FileStream(chkPath, FileMode.Open, FileAccess.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                int volWidth = br.ReadInt32();
                int volHeight = br.ReadInt32();
                int volDepth = br.ReadInt32();
                int chunkDim = br.ReadInt32();
                double pixelSize = br.ReadDouble();
                return (volWidth, volHeight, volDepth, chunkDim, pixelSize);
            }
        }

        private ChunkedVolume LoadVolumeBin(string path, bool useMM)
        {
            int volWidth, volHeight, volDepth, chunkDim, cntX, cntY, cntZ;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (BinaryReader br = new BinaryReader(fs))
            {
                volWidth = br.ReadInt32();
                volHeight = br.ReadInt32();
                volDepth = br.ReadInt32();
                chunkDim = br.ReadInt32();
                cntX = br.ReadInt32();
                cntY = br.ReadInt32();
                cntZ = br.ReadInt32();
            }
            int headerSize = 7 * sizeof(int);
            long chunkSize = (long)chunkDim * chunkDim * chunkDim;
            int totalChunks = cntX * cntY * cntZ;
            long expectedSize = headerSize + totalChunks * chunkSize;
            long fileSize = new FileInfo(path).Length;
            if (fileSize < expectedSize)
                throw new Exception($"volume.bin file is incomplete: expected {expectedSize} bytes but got {fileSize} bytes.");
            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            MemoryMappedViewAccessor[] accessors = new MemoryMappedViewAccessor[totalChunks];
            for (int i = 0; i < totalChunks; i++)
            {
                long offset = headerSize + i * chunkSize;
                accessors[i] = mmf.CreateViewAccessor(offset, chunkSize, MemoryMappedFileAccess.Read);
            }
            return new ChunkedVolume(volWidth, volHeight, volDepth, chunkDim, mmf, accessors);
        }

        private ChunkedVolume LoadVolumeBinRaw(string path, bool useMM, int volWidth, int volHeight, int volDepth, int chunkDim)
        {
            int cntX = (volWidth + chunkDim - 1) / chunkDim;
            int cntY = (volHeight + chunkDim - 1) / chunkDim;
            int cntZ = (volDepth + chunkDim - 1) / chunkDim;
            int totalChunks = cntX * cntY * cntZ;
            long chunkSize = (long)chunkDim * chunkDim * chunkDim;
            long expectedSize = totalChunks * chunkSize;
            long fileSize = new FileInfo(path).Length;
            if (fileSize != expectedSize)
                throw new Exception($"Raw volume.bin file size mismatch: expected {expectedSize} bytes but got {fileSize} bytes.");
            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            MemoryMappedViewAccessor[] accessors = new MemoryMappedViewAccessor[totalChunks];
            for (int i = 0; i < totalChunks; i++)
            {
                long offset = i * chunkSize;
                accessors[i] = mmf.CreateViewAccessor(offset, chunkSize, MemoryMappedFileAccess.Read);
            }
            return new ChunkedVolume(volWidth, volHeight, volDepth, chunkDim, mmf, accessors);
        }

        private ChunkedLabelVolume LoadLabelsBin(string path, bool useMM)
        {
            if (!File.Exists(path))
            {
                Logger.Log("[LoadLabelsBin] File not found; creating blank labels volume.");
                if (useMM)
                    throw new FileNotFoundException("labels.bin not found and memory mapping is enabled.");
                return new ChunkedLabelVolume(width, height, depth, CHUNK_DIM, false, null);
            }

            if (useMM)
            {
                MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
                int headerSize = sizeof(int) * 4;
                int chunkDim, cntX, cntY, cntZ;
                using (var headerStream = mmf.CreateViewStream(0, headerSize, MemoryMappedFileAccess.Read))
                using (BinaryReader br = new BinaryReader(headerStream))
                {
                    chunkDim = br.ReadInt32();
                    cntX = br.ReadInt32();
                    cntY = br.ReadInt32();
                    cntZ = br.ReadInt32();
                }
                int labWidth = cntX * chunkDim;
                int labHeight = cntY * chunkDim;
                int labDepth = cntZ * chunkDim;
                ChunkedLabelVolume labVol = new ChunkedLabelVolume(labWidth, labHeight, labDepth, chunkDim, mmf);
                using (var dataStream = mmf.CreateViewStream(headerSize, 0, MemoryMappedFileAccess.ReadWrite))
                using (BinaryReader dataReader = new BinaryReader(dataStream))
                {
                    labVol.ReadChunksHeaderAndData(dataReader, mmf, headerSize);
                }
                return labVol;
            }
            else
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    int chunkDim = br.ReadInt32();
                    int cntX = br.ReadInt32();
                    int cntY = br.ReadInt32();
                    int cntZ = br.ReadInt32();
                    if (chunkDim <= 0)
                        throw new Exception("Invalid header in labels file: chunkDim is zero.");
                    int labWidth = cntX * chunkDim;
                    int labHeight = cntY * chunkDim;
                    int labDepth = cntZ * chunkDim;
                    ChunkedLabelVolume labVol = new ChunkedLabelVolume(labWidth, labHeight, labDepth, chunkDim, false, path);
                    labVol.ReadChunksHeaderAndData(br);
                    return labVol;
                }
            }
        }

        private void CreateBlankLabelsFile(string path, int volWidth, int volHeight, int volDepth, int chunkDim)
        {
            int cntX = (volWidth + chunkDim - 1) / chunkDim;
            int cntY = (volHeight + chunkDim - 1) / chunkDim;
            int cntZ = (volDepth + chunkDim - 1) / chunkDim;
            int totalChunks = cntX * cntY * cntZ;
            long chunkSize = (long)chunkDim * chunkDim * chunkDim;
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(chunkDim);
                bw.Write(cntX);
                bw.Write(cntY);
                bw.Write(cntZ);
                byte[] emptyChunk = new byte[chunkSize];
                for (int i = 0; i < totalChunks; i++)
                {
                    bw.Write(emptyChunk, 0, emptyChunk.Length);
                }
            }
            Logger.Log($"[CreateBlankLabelsFile] Created blank labels.bin at {path}");
        }

        /// <summary>
        /// Loads a combined volume+labels file (saved via SaveBinary) from the given path.
        /// Assumes the file header was written in SaveBinary.
        /// </summary>
        private void LoadCombinedBinary(string path)
        {
            // This implementation loads everything into RAM.
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                // Read volume header
                width = br.ReadInt32();
                height = br.ReadInt32();
                depth = br.ReadInt32();
                pixelSize = br.ReadDouble();

                // Read materials
                int materialCount = br.ReadInt32();
                Materials = new List<Material>();
                for (int i = 0; i < materialCount; i++)
                {
                    string name = br.ReadString();
                    int argb = br.ReadInt32();
                    byte min = br.ReadByte();
                    byte max = br.ReadByte();
                    bool isExterior = br.ReadBoolean();
                    byte id = br.ReadByte();
                    Materials.Add(new Material(name, Color.FromArgb(argb), min, max, id) { IsExterior = isExterior });
                }

                // Read a flag that indicates whether volume data (grayscale) is present.
                bool hasGrayscale = br.ReadBoolean();
                if (hasGrayscale)
                {
                    // Create the volume in RAM
                    volumeData = new ChunkedVolume(width, height, depth, CHUNK_DIM);
                    volumeData.ReadChunks(br);
                }
                else
                {
                    volumeData = null;
                }

                // Now load the labels.
                // Create a new ChunkedLabelVolume (RAM mode) and read its chunks.
                volumeLabels = new ChunkedLabelVolume(width, height, depth, CHUNK_DIM, false, null);
                volumeLabels.ReadChunksHeaderAndData(br);
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

        private void InitializeLabels(ProgressForm progress)
        {
            Logger.Log("[InitializeLabels] Starting label initialization.");
            // Iterate through every voxel and set the label to 0 (Exterior).
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        volumeLabels[x, y, z] = 0;
                    }
                }
                progress?.SafeUpdateProgress(z + 1, depth);
            }
            Logger.Log("[InitializeLabels] Completed label initialization.");
        }
        public byte GetNextMaterialID()
        {
            // Find the smallest available ID starting from 1
            for (byte candidate = 1; candidate < byte.MaxValue; candidate++)
            {
                if (!Materials.Any(m => m.ID == candidate))
                {
                    nextMaterialID = (byte)(candidate + 1);
                    return candidate;
                }
            }
            throw new InvalidOperationException("No available material IDs.");
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
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(width);
                    bw.Write(height);
                    bw.Write(depth);
                    bw.Write(pixelSize);

                    bw.Write(Materials.Count);
                    foreach (var mat in Materials)
                    {
                        bw.Write(mat.Name);
                        bw.Write(mat.Color.ToArgb());
                        bw.Write(mat.Min);
                        bw.Write(mat.Max);
                        bw.Write(mat.IsExterior);
                    }

                    bool hasGrayscale = (volumeData != null);
                    bw.Write(hasGrayscale);
                    if (hasGrayscale)
                        volumeData.WriteChunks(bw);
                    volumeLabels.WriteChunks(bw);
                }
                Logger.Log($"[SaveBinary] Volume saved successfully to {path}");
            }
            catch (Exception ex)
            {
                Logger.Log("[SaveBinary] Error: " + ex);
                throw;
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
                    Parallel.For(0, depth, z =>
                    {
                        using (Bitmap bmp = CreateBitmapFromData(z,CancellationToken.None))
                        {
                            bmp.Save(Path.Combine(dialog.SelectedPath, $"{z:00000}.bmp"));
                        }
                    });
                    Logger.Log("[ExportImages] Exported label images.");
                }
            }
        }
        #endregion

        #region Thresholding and Material Manipulation

        public void AddThresholdSelection(byte minVal, byte maxVal, byte materialID)
        {
            if (volumeData == null || volumeLabels == null) return;
            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        byte g = volumeData[x, y, z];
                        if (g >= minVal && g <= maxVal)
                            volumeLabels[x, y, z] = materialID;
                    }
            RenderViews();
            _ = RenderOrthoViewsAsync();
        }

        public void AddThresholdSelectionForSlice(byte minVal, byte maxVal, byte materialID, int slice)
        {
            if (volumeData == null || volumeLabels == null) return;

            // First pass: Clear existing material labels that are outside the new range
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (volumeLabels[x, y, slice] == materialID)
                    {
                        byte g = volumeData[x, y, slice];
                        if (g < minVal || g > maxVal)
                        {
                            volumeLabels[x, y, slice] = 0;
                        }
                    }
                }
            }

            // Second pass: Set new labels for voxels in range
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte g = volumeData[x, y, slice];
                    if (g >= minVal && g <= maxVal)
                    {
                        volumeLabels[x, y, slice] = materialID;
                    }
                }
            }

            RenderViews();
            _ = RenderOrthoViewsAsync();
        }

        // Remove the threshold selection only from the specified XY slice.
        public void RemoveThresholdSelectionForSlice(byte minVal, byte maxVal, byte materialID, int slice)
        {
            if (volumeData == null || volumeLabels == null)
                return;
            int w = GetWidth();
            int h = GetHeight();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (volumeLabels[x, y, slice] == materialID)
                    {
                        byte g = volumeData[x, y, slice];
                        if (g >= minVal && g <= maxVal)
                            volumeLabels[x, y, slice] = 0;
                    }
                }
            }
            RenderViews();
            _ = RenderOrthoViewsAsync();
        }

        public void RemoveThresholdSelection(byte minVal, byte maxVal, byte materialID)
        {
            if (volumeData == null || volumeLabels == null) return;
            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        if (volumeLabels[x, y, z] == materialID)
                        {
                            byte g = volumeData[x, y, z];
                            if (g >= minVal && g <= maxVal)
                                volumeLabels[x, y, z] = 0;
                        }
                    }
            RenderViews();
        }

        public void ApplyCurrentSelection()
        {
            int slice = CurrentSlice;
            if (currentSelection == null)
                return;

            // Apply the temporary selection for the current slice.
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte sel = currentSelection[x, y];
                    if (sel != 0)
                        volumeLabels[x, y, slice] = sel;
                }
            }

            // Save to sparseSelectionsZ (XY slices)
            byte[,] copy = new byte[width, height];
            Array.Copy(currentSelection, copy, width * height);
            sparseSelectionsZ[slice] = copy;

            currentSelection = new byte[width, height];
            RenderViews();
            _ = RenderOrthoViewsAsync();
        }



        public void SubtractCurrentSelection()
        {
            int slice = CurrentSlice;
            if (currentSelection == null) return;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte sel = currentSelection[x, y];
                    if (sel != 0 && volumeLabels[x, y, slice] == sel)
                        volumeLabels[x, y, slice] = 0;
                }
            }

            currentSelection = new byte[width, height];
            RenderViews();
            _ = RenderOrthoViewsAsync(); // Update orthoviews
        }


        public void ApplyMaterialThresholdRange(int matIndex, byte newMin, byte newMax)
        {
            if (matIndex < 0 || matIndex >= Materials.Count) return;
            if (volumeData == null || volumeLabels == null) return;
            if (Materials[matIndex].IsExterior) return;

            pendingMin = newMin;
            pendingMax = newMax;
            if (thresholdUpdateTimer == null)
            {
                thresholdUpdateTimer = new System.Windows.Forms.Timer { Interval = 50 };
                thresholdUpdateTimer.Tick += (s, e) =>
                {
                    thresholdUpdateTimer.Stop();
                    ApplyThresholdToVolume(matIndex, (byte)pendingMin, (byte)pendingMax);
                };
            }
            thresholdUpdateTimer.Stop();
            thresholdUpdateTimer.Start();
        }
        /// <summary>
        /// Applies the entire interpolated selection (a 3D binary mask) to the label volume,
        /// setting voxels to the provided materialID.
        /// </summary>
        /// <param name="materialID">The material label to apply.</param>
        public void ApplyInterpolatedSelection(byte materialID)
        {
            if (interpolatedMask == null)
            {
                Logger.Log("[ApplyInterpolatedSelection] No interpolated mask available.");
                return;
            }

            // If in thresholding mode, we update only the temporary preview (currentSelection)
            // so that your threshold overlay (computed in RenderViews) continues to be used.
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
                // For non-thresholding modes, apply the interpolated mask to the entire label volume.
                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            // Only update voxels where the mask is true.
                            if (interpolatedMask[x, y, z])
                            {
                                volumeLabels[x, y, z] = materialID;
                            }
                        }
                    }
                }
                // Update the temporary overlay for the current slice.
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

            // Clear cached slice data to force a full re-render.
            lock (sliceCacheLock)
            {
                sliceCache.Clear();
            }

            RenderViews();
            _ = RenderOrthoViewsAsync();
        }
        /// <summary>
        /// Subtracts (removes) the selection for the given material by using the full 3D interpolated mask.
        /// For every voxel in the volume, if the interpolated mask is true and the voxel currently
        /// has the given material ID, then set the label to 0 (Exterior).
        /// </summary>
        /// <param name="materialID">The material label to subtract.</param>
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
            RenderViews();
            _ = RenderOrthoViewsAsync();
        }
        private void ApplyThresholdToVolume(int matIndex, byte newMin, byte newMax)
        {
            if (matIndex < 0 || matIndex >= Materials.Count) return;
            if (Materials[matIndex].IsExterior) return;

            byte materialID = Materials[matIndex].ID;
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte g = volumeData[x, y, z];
                        if (g >= newMin && g <= newMax)
                            volumeLabels[x, y, z] = materialID;
                        else if (volumeLabels[x, y, z] == materialID)
                            volumeLabels[x, y, z] = 0;
                    }
                }
            });

            RenderViews();
            _ = RenderOrthoViewsAsync();
        }


        public void RemoveMaterialAndReindex(int removeMaterialID)
        {
            // Do not allow deletion of the Exterior material (ID 0).
            if (removeMaterialID == 0)
                return;

            // Clear segmentation voxels assigned to this material across the entire volume.
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (volumeLabels[x, y, z] == removeMaterialID)
                            volumeLabels[x, y, z] = 0;
                    }
                }
            }

            // Clear any temporary selection voxels using this material.
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

            // Remove the material from the list by its unique ID.
            Material toRemove = Materials.FirstOrDefault(m => m.ID == removeMaterialID);
            if (toRemove != null)
            {
                Materials.Remove(toRemove);
            }

            // Optionally adjust the SelectedMaterialIndex.
            if (SelectedMaterialIndex >= Materials.Count)
                SelectedMaterialIndex = Materials.Count - 1;

            // Clear the slice cache so the current XY view reflects the changes.
            lock (sliceCacheLock)
            {
                sliceCache.Clear();
            }

            RenderViews();
            _ = RenderOrthoViewsAsync();
        }



        // Methods to save and load the companion labels.chk file.
        private void CreateLabelsChk(string folder, List<Material> materials)
        {
            string chkPath = Path.Combine(folder, "labels.chk");
            using (FileStream fs = new FileStream(chkPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(materials.Count);
                foreach (var mat in materials)
                {
                    bw.Write(mat.Name);
                    bw.Write(mat.Color.ToArgb());
                    bw.Write(mat.Min);
                    bw.Write(mat.Max);
                    bw.Write(mat.IsExterior);
                    // Write the material's unique ID.
                    bw.Write(mat.ID);
                }
            }
            Logger.Log($"[CreateLabelsChk] Created labels.chk at {chkPath}");
        }


        private List<Material> ReadLabelsChk(string chkPath)
        {
            List<Material> mats = new List<Material>();
            using (FileStream fs = new FileStream(chkPath, FileMode.Open, FileAccess.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string name = br.ReadString();
                    int argb = br.ReadInt32();
                    byte min = br.ReadByte();
                    byte max = br.ReadByte();
                    bool isExterior = br.ReadBoolean();
                    // Read the material ID from the file.
                    byte id = br.ReadByte();
                    mats.Add(new Material(name, Color.FromArgb(argb), min, max, id) { IsExterior = isExterior });
                }
            }
            Logger.Log($"[ReadLabelsChk] Loaded {mats.Count} materials from labels.chk");
            return mats;
        }


        // Call this after any modification to Materials.
        public void SaveLabelsChk()
        {
            string folder = CurrentPath;
            if (!Directory.Exists(folder))
                return;
            CreateLabelsChk(folder, Materials);
        }

        #endregion

        #region Rendering

        public void RenderViews()
        {
            // Cancel any in-progress XY render.
            xyRenderCts.Cancel();
            xyRenderCts = new CancellationTokenSource();
            CancellationToken ct = xyRenderCts.Token;

            // Throttle renders if needed.
            if ((DateTime.Now - _lastRenderRequest).TotalMilliseconds < RENDER_THROTTLE_MS)
                return;
            _lastRenderRequest = DateTime.Now;
            if (width <= 0 || height <= 0)
                return;

            // Offload XY bitmap creation to a background task.
            Task.Run(() =>
            {
                // Create the bitmap for the current XY slice.
                Bitmap bmp = CreateBitmapFromData(CurrentSlice, ct);
                ct.ThrowIfCancellationRequested();
                this.Invoke(new Action(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        currentBitmap?.Dispose();
                        currentBitmap = bmp;
                        ClampPanOffset();
                        UpdateScaledBitmap(currentBitmap);
                        mainView.Invalidate();
                    }
                    else
                    {
                        bmp.Dispose();
                    }
                }));
            }, ct);
        }

        private byte[] GetSliceData(int slice)
        {
            lock (datasetLock)
            {
                if (volumeData == null || slice < 0 || slice >= depth)
                    return new byte[width * height];

                if (sliceCache.TryGetValue(slice, out byte[] cached))
                    return cached;

                byte[] data = new byte[width * height];
                int idx = 0;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        data[idx++] = volumeLabels?[x, y, slice] ?? (byte)0;

                if (sliceCache.Count >= MAX_CACHE_SIZE)
                {
                    int keyToRemove = sliceCache.Keys.First();
                    sliceCache.Remove(keyToRemove);
                }
                sliceCache[slice] = data;
                return data;
            }
        }
        public void ClearSliceCache()
        {
            lock (sliceCacheLock)
            {
                sliceCache.Clear();
            }
        }

        private CancellationTokenSource orthoRenderCts = new CancellationTokenSource();

        // Modified CreateBitmapFromData using RenderMaterials property.
        // Modified CreateBitmapFromData with LockBits and parallelization
        private Bitmap CreateBitmapFromData(int sliceIndex, CancellationToken ct)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            Rectangle rect = new Rectangle(0, 0, width, height);
            BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
            int stride = bmpData.Stride;

            unsafe
            {
                byte* basePtr = (byte*)bmpData.Scan0;
                Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
                {
                    byte* rowPtr = basePtr + y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        // Start with the grayscale value from the volume.
                        byte gVal = volumeData?[x, y, sliceIndex] ?? (byte)128;
                        Color finalColor = Color.FromArgb(gVal, gVal, gVal);

                        // If a segmentation has been applied (volumeLabels), use it.
                        byte appliedLabel = volumeLabels?[x, y, sliceIndex] ?? (byte)0;
                        if (appliedLabel != 0)
                        {
                            Material mat = Materials.FirstOrDefault(m => m.ID == appliedLabel);
                            if (mat != null)
                            {
                                finalColor = RenderMaterials
                                    ? mat.Color
                                    : BlendColors(finalColor, mat.Color, 0.5f);
                            }
                        }
                        // If a SAM segmentation mask is present (stored in currentSelection),
                        // override with a higher opacity blend to make it stand out.
                        if (currentSelection != null && currentSelection[x, y] != 0)
                        {
                            Material selMat = Materials.FirstOrDefault(m => m.ID == currentSelection[x, y]);
                            if (selMat != null)
                            {
                                // Using 80% overlay so that the SAM mask becomes clearly visible.
                                finalColor = BlendColors(finalColor, selMat.Color, 0.8f);
                            }
                        }

                        // Write the final color into the bitmap.
                        int offset = x * 3;
                        rowPtr[offset] = finalColor.B;
                        rowPtr[offset + 1] = finalColor.G;
                        rowPtr[offset + 2] = finalColor.R;
                    }
                });
            }
            bmp.UnlockBits(bmpData);
            return bmp;
        }





        private Color BlendColors(Color baseColor, Color overlay, float alpha)
        {
            return Color.FromArgb(
                (int)(baseColor.R * (1 - alpha) + overlay.R * alpha),
                (int)(baseColor.G * (1 - alpha) + overlay.G * alpha),
                (int)(baseColor.B * (1 - alpha) + overlay.B * alpha)
            );
        }

        public async Task RenderOrthoViewsAsync(bool forceUpdate = false)
        {
            if (!showProjections || width == 0 || height == 0 || depth == 0)
                return;

            // Cancel previous operations
            orthoRenderCts.Cancel();
            orthoRenderCts = new CancellationTokenSource();
            var ct = orthoRenderCts.Token;

            Task<Bitmap> xzTask = null;
            Task<Bitmap> yzTask = null;

            try
            {
                int fixedY, fixedX;
                bool showMaskLocal;
                bool renderMaterialsLocal;
                bool thresholdActiveLocal;
                ChunkedVolume localVolumeData;
                ChunkedLabelVolume localVolumeLabels;

                // Capture current properties under lock to prevent mid-render changes
                lock (datasetLock)
                {
                    fixedY = XzSliceY;
                    fixedX = YzSliceX;
                    showMaskLocal = this.ShowMask;
                    renderMaterialsLocal = this.RenderMaterials;
                    thresholdActiveLocal = currentTool == SegmentationTool.Thresholding &&
                                            SelectedMaterialIndex >= 0 &&
                                            PreviewMax > PreviewMin;
                    localVolumeData = volumeData;
                    localVolumeLabels = volumeLabels;
                }

                xzTask = Task.Run(() => RenderXZProjection(fixedY, showMaskLocal, renderMaterialsLocal, thresholdActiveLocal, localVolumeData, localVolumeLabels, ct), ct);
                yzTask = Task.Run(() => RenderYZProjection(fixedX, showMaskLocal, renderMaterialsLocal, thresholdActiveLocal, localVolumeData, localVolumeLabels, ct), ct);

                await Task.WhenAll(xzTask, yzTask).ConfigureAwait(false);

                if (!ct.IsCancellationRequested && !this.IsDisposed)
                {
                    this.Invoke(new Action(() =>
                    {
                        // Update XZ view
                        if (xzProjection != null) xzProjection.Dispose();
                        xzProjection = xzTask.Result;
                        xzView.Invalidate();

                        // Update YZ view
                        if (yzProjection != null) yzProjection.Dispose();
                        yzProjection = yzTask.Result;
                        yzView.Invalidate();
                    }));
                }
            }
            catch (OperationCanceledException)
            {
                // Cleanup if cancelled
                xzTask?.Dispose();
                yzTask?.Dispose();
            }
            catch (Exception ex)
            {
                // Handle AggregateException to log all inner exceptions
                if (ex is AggregateException aggEx)
                {
                    foreach (var innerEx in aggEx.Flatten().InnerExceptions)
                    {
                        Logger.Log($"[RenderOrthoViewsAsync] Inner Error: {innerEx.Message}\n{innerEx.StackTrace}");
                    }
                }
                else
                {
                    Logger.Log($"[RenderOrthoViewsAsync] Error: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        private Bitmap RenderXZProjection(int fixedY, bool showMask, bool renderMaterials, bool thresholdActive,
                                  ChunkedVolume localVolumeData, ChunkedLabelVolume localVolumeLabels,
                                  CancellationToken ct)
        {
            if (width <= 0 || depth <= 0) return new Bitmap(1, 1);

            Bitmap bmp = new Bitmap(width, depth, PixelFormat.Format24bppRgb);
            Rectangle rect = new Rectangle(0, 0, width, depth);
            BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
            int stride = bmpData.Stride;

            unsafe
            {
                byte* ptr = (byte*)bmpData.Scan0;

                Parallel.For(0, depth, z =>
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        for (int x = 0; x < width; x++)
                        {
                            byte gVal = localVolumeData?[x, fixedY, z] ?? (byte)128;
                            Color finalColor = Color.FromArgb(gVal, gVal, gVal);

                            // Segmentation
                            if (localVolumeLabels != null && showMask)
                            {
                                byte segID = localVolumeLabels[x, fixedY, z];
                                if (segID != 0)
                                {
                                    var mat = Materials.FirstOrDefault(m => m.ID == segID);
                                    if (mat != null)
                                    {
                                        finalColor = renderMaterials ? mat.Color :
                                            BlendColors(finalColor, mat.Color, 0.5f);
                                    }
                                }
                            }

                            // Threshold overlay
                            if (thresholdActive && gVal >= PreviewMin && gVal <= PreviewMax)
                            {
                                var selColor = GetComplementaryColor(Materials[SelectedMaterialIndex].Color);
                                finalColor = BlendColors(finalColor, selColor, 0.5f);
                            }

                            // Selection overlay
                            if (currentSelectionXZ != null && x < currentSelectionXZ.GetLength(0) &&
                                z < currentSelectionXZ.GetLength(1) && currentSelectionXZ[x, z] != 0)
                            {
                                var selMat = Materials.FirstOrDefault(m => m.ID == currentSelectionXZ[x, z]);
                                if (selMat != null)
                                {
                                    finalColor = BlendColors(finalColor, selMat.Color, 0.6f);
                                }
                            }

                            // Write pixel data directly
                            int offset = z * stride + x * 3;
                            ptr[offset] = finalColor.B;     // Blue
                            ptr[offset + 1] = finalColor.G; // Green
                            ptr[offset + 2] = finalColor.R; // Red
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[XZ Pixel Error] {ex.Message}");
                    }
                });
            }

            bmp.UnlockBits(bmpData);
            return bmp;
        }

        private Bitmap RenderYZProjection(int fixedX, bool showMask, bool renderMaterials, bool thresholdActive,
                                  ChunkedVolume localVolumeData, ChunkedLabelVolume localVolumeLabels,
                                  CancellationToken ct)
        {
            // Validate dimensions before creating resources
            if (depth <= 0 || height <= 0) return new Bitmap(1, 1);

            Bitmap bmp = new Bitmap(depth, height, PixelFormat.Format24bppRgb);
            Rectangle rect = new Rectangle(0, 0, depth, height);

            BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
            int stride = bmpData.Stride;

            unsafe
            {
                byte* ptr = (byte*)bmpData.Scan0;

                Parallel.For(0, height, y =>
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        for (int z = 0; z < depth; z++)
                        {
                            // Get grayscale value with null safety
                            byte gVal = (localVolumeData != null && fixedX < localVolumeData.Width && y < localVolumeData.Height && z < localVolumeData.Depth)
                                ? localVolumeData[fixedX, y, z]
                                : (byte)128;

                            Color finalColor = Color.FromArgb(gVal, gVal, gVal);

                            // Apply segmentation overlay
                            if (localVolumeLabels != null && showMask &&
                                fixedX < localVolumeLabels.Width && y < localVolumeLabels.Height && z < localVolumeLabels.Depth)
                            {
                                byte segID = localVolumeLabels[fixedX, y, z];
                                if (segID != 0 && Materials.Any(m => m.ID == segID))
                                {
                                    Material mat = Materials.First(m => m.ID == segID);
                                    finalColor = renderMaterials ? mat.Color :
                                               BlendColors(finalColor, mat.Color, 0.5f);
                                }
                            }

                            // Apply threshold overlay
                            if (thresholdActive &&
                                SelectedMaterialIndex >= 0 && SelectedMaterialIndex < Materials.Count &&
                                gVal >= PreviewMin && gVal <= PreviewMax)
                            {
                                Color selColor = GetComplementaryColor(Materials[SelectedMaterialIndex].Color);
                                finalColor = BlendColors(finalColor, selColor, 0.5f);
                            }

                            // Apply temporary selection overlay
                            if (currentSelectionYZ != null &&
                                z < currentSelectionYZ.GetLength(0) &&
                                y < currentSelectionYZ.GetLength(1) &&
                                currentSelectionYZ[z, y] != 0)
                            {
                                byte selID = currentSelectionYZ[z, y];
                                if (Materials.Any(m => m.ID == selID))
                                {
                                    Material selMat = Materials.First(m => m.ID == selID);
                                    finalColor = BlendColors(finalColor, selMat.Color, 0.6f);
                                }
                            }

                            // Calculate pointer offset (YZ view: horizontal axis = Z, vertical axis = Y)
                            int offset = y * stride + z * 3;

                            // Write RGB values directly to memory
                            ptr[offset] = finalColor.B;     // Blue
                            ptr[offset + 1] = finalColor.G; // Green
                            ptr[offset + 2] = finalColor.R; // Red
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[YZ Render Error] X:{fixedX} Y:{y} - {ex.Message}");
                    }
                });
            }

            bmp.UnlockBits(bmpData);
            return bmp;
        }

        private byte GetGrayscale(int x, int y, int z)
        {
            return volumeData?[x, y, z] ?? 0;
        }

        private async Task PrefetchOrthoNeighborsAsync()
        {
            await Task.Run(() =>
            {
                int[] neighborRows = { XzSliceY - 1, XzSliceY + 1 };
                foreach (int row in neighborRows)
                {
                    if (row < 0 || row >= height) continue;
                    string key = $"XZ_{row}";
                    lock (orthoViewCache)
                    {
                        if (orthoViewCache.ContainsKey(key)) continue;
                    }
                    Bitmap bmp = RenderOrthoViewXzForRow(row);
                    lock (orthoViewCache)
                    {
                        orthoViewCache[key] = bmp;
                    }
                }
                int[] neighborCols = { YzSliceX - 1, YzSliceX + 1 };
                foreach (int col in neighborCols)
                {
                    if (col < 0 || col >= width) continue;
                    string key = $"YZ_{col}";
                    lock (orthoViewCache)
                    {
                        if (orthoViewCache.ContainsKey(key)) continue;
                    }
                    Bitmap bmp = RenderOrthoViewYzForCol(col);
                    lock (orthoViewCache)
                    {
                        orthoViewCache[key] = bmp;
                    }
                }
            });
        }

        private Bitmap RenderOrthoViewXzForRow(int row)
        {
            Bitmap bmp = new Bitmap(width, depth, PixelFormat.Format32bppArgb);
            byte[][] slices = new byte[depth][];
            Parallel.For(0, depth, z => { slices[z] = GetSliceData(z); });
            Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                Parallel.For(0, depth, z =>
                {
                    byte[] slice = slices[z];
                    for (int x = 0; x < width; x++)
                    {
                        byte mIdx = slice[row * width + x];
                        if (mIdx >= Materials.Count)
                            mIdx = 0;
                        Material mat = Materials[mIdx];
                        Color c;
                        if (mat.IsExterior && volumeData != null)
                        {
                            byte g = volumeData[x, row, z];
                            c = Color.FromArgb(g, g, g);
                        }
                        else
                        {
                            c = mat.Color;
                        }
                        int offset = z * stride + x * 4;
                        ptr[offset] = c.B;
                        ptr[offset + 1] = c.G;
                        ptr[offset + 2] = c.R;
                        ptr[offset + 3] = c.A;
                    }
                });
            }
            bmp.UnlockBits(data);
            Logger.Log($"[Prefetch] XZ view for row {row} precomputed.");
            return bmp;
        }

        private Bitmap RenderOrthoViewYzForCol(int col)
        {
            Bitmap bmp = new Bitmap(depth, height, PixelFormat.Format32bppArgb);
            byte[][] slices = new byte[depth][];
            Parallel.For(0, depth, z => { slices[z] = GetSliceData(z); });
            Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                Parallel.For(0, height, y =>
                {
                    for (int z = 0; z < depth; z++)
                    {
                        byte[] slice = slices[z];
                        byte mIdx = slice[y * width + col];
                        if (mIdx >= Materials.Count)
                            mIdx = 0;
                        Material mat = Materials[mIdx];
                        Color c;
                        if (mat.IsExterior && volumeData != null)
                        {
                            byte g = volumeData[col, y, z];
                            c = Color.FromArgb(g, g, g);
                        }
                        else
                        {
                            c = mat.Color;
                        }
                        int offset = y * stride + z * 4;
                        ptr[offset] = c.B;
                        ptr[offset + 1] = c.G;
                        ptr[offset + 2] = c.R;
                        ptr[offset + 3] = c.A;
                    }
                });
            }
            bmp.UnlockBits(data);
            Logger.Log($"[Prefetch] YZ view for column {col} precomputed.");
            return bmp;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (currentBitmap == null)
                return;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            RectangleF destRect = new RectangleF(panOffset.X, panOffset.Y, currentBitmap.Width * zoomLevel, currentBitmap.Height * zoomLevel);
            e.Graphics.DrawImage(currentBitmap, destRect);
            DrawScaleBar(e.Graphics, mainView.ClientRectangle, zoomLevel);

            // Draw the brush/eraser overlay only if the current tool is Brush or Eraser.
            if (showBrushOverlay && (currentTool == SegmentationTool.Brush || currentTool == SegmentationTool.Eraser))
            {
                using (Pen pen = new Pen(Color.Red, 2))
                {
                    float radius = currentBrushSize / 2f;
                    e.Graphics.DrawEllipse(pen, brushOverlayCenter.X - radius, brushOverlayCenter.Y - radius, currentBrushSize, currentBrushSize);
                }
            }
        }
        // Helper method to format pixel size
        private string FormatPixelSize(double meters)
        {
            if (meters >= 1e-3) // 1mm or larger
                return $"{(meters * 1000):0.###} mm";

            if (meters >= 1e-6) // 1µm or larger
                return $"{(meters * 1e6):0.###} μm";

            return $"{(meters * 1e9):0} nm";
        }
        private void MainView_Paint(object sender, PaintEventArgs e)
        {
            if (_scaledBitmap == null || width == 0 || height == 0)
                return;
            _bitmapLock.EnterReadLock();
            try
            {
                e.Graphics.DrawImage(_scaledBitmap, panOffset);
                using (Font headerFont = new Font("Arial", 12, FontStyle.Bold))
                using (SolidBrush headerBrush = new SolidBrush(Color.Yellow))
                {
                    // Draw fixed "XY" label in top-left
                    e.Graphics.DrawString("XY", headerFont, headerBrush, new PointF(5, 5));
                }
                // Draw pixel size info in the top-right as before…
                string sizeText = $"Pixel size: {FormatPixelSize(pixelSize)}";
                using (Font infoFont = new Font("Arial", 10))
                using (SolidBrush infoBrush = new SolidBrush(Color.White))
                {
                    SizeF textSize = e.Graphics.MeasureString(sizeText, infoFont);
                    PointF pos = new PointF(mainView.ClientSize.Width - textSize.Width - 10, 5);
                    e.Graphics.DrawString(sizeText, infoFont, infoBrush, pos);
                }
                // *** New: Draw slice number in the bottom-right corner ***
                string sliceInfo = $"Slice: {CurrentSlice + 1}/{depth}";
                using (Font sliceFont = new Font("Arial", 10, FontStyle.Bold))
                using (SolidBrush sliceBrush = new SolidBrush(Color.LightGreen))
                {
                    SizeF sliceSize = e.Graphics.MeasureString(sliceInfo, sliceFont);
                    PointF slicePos = new PointF(mainView.ClientSize.Width - sliceSize.Width - 10,
                                                 mainView.ClientSize.Height - sliceSize.Height - 10);
                    e.Graphics.DrawString(sliceInfo, sliceFont, sliceBrush, slicePos);
                }
                // Draw overlays (scale bar, annotations, brush overlay) as before.
                DrawScaleBar(e.Graphics, mainView.ClientRectangle, globalZoom);
                DrawAnnotations(e.Graphics);
                DrawBrushOverlay(e.Graphics);
            }
            finally
            {
                _bitmapLock.ExitReadLock();
            }
        }
        private void DrawAnnotations(Graphics g)
        {
            foreach (var point in AnnotationMgr.GetPointsForSlice(CurrentSlice))
            {
                Color materialColor = Materials.FirstOrDefault(m => m.Name == point.Label)?.Color ?? Color.Red;

                if (point.Type == "Box")
                {
                    // Draw box annotation
                    float x1 = point.X * globalZoom + panOffset.X;
                    float y1 = point.Y * globalZoom + panOffset.Y;
                    float x2 = point.X2 * globalZoom + panOffset.X;
                    float y2 = point.Y2 * globalZoom + panOffset.Y;

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
                    float x = point.X * globalZoom + panOffset.X;
                    float y = point.Y * globalZoom + panOffset.Y;

                    using (var pen = new Pen(materialColor, 2))
                    using (var brush = new SolidBrush(materialColor))
                    {
                        g.DrawLine(pen, x - 5, y, x + 5, y);
                        g.DrawLine(pen, x, y - 5, x, y + 5);
                        g.DrawString(point.ID.ToString(), Font, Brushes.Yellow, x + 5, y + 5);
                    }
                }
            }

            // Draw box preview during dragging
            if (isBoxDrawingPossible && isActuallyDrawingBox)
            {
                int boxX = Math.Min(boxStartPoint.X, boxCurrentPoint.X);
                int boxY = Math.Min(boxStartPoint.Y, boxCurrentPoint.Y);
                int boxWidth = Math.Abs(boxCurrentPoint.X - boxStartPoint.X);
                int boxHeight = Math.Abs(boxCurrentPoint.Y - boxStartPoint.Y);

                using (var pen = new Pen(Color.Yellow, 2))
                {
                    g.DrawRectangle(pen, boxX, boxY, boxWidth, boxHeight);
                }
            }
        }
        private void UpdateSAMFormWithBox(AnnotationPoint box)
        {
            // If a SAMForm is open, update its DataGridView
            if (this.SamFormInstance != null && !this.SamFormInstance.IsDisposed)
            {
                DataGridView dgv = SamFormInstance.GetPointsDataGridView();
                if (dgv != null)
                {
                    // Add entry to the DataGridView
                    dgv.Rows.Add(box.ID, box.X, box.Y, box.Z, box.Type, box.Label);
                }
            }
        }
        private void DrawBrushOverlay(Graphics g)
        {
            if (!showBrushOverlay || currentTool == SegmentationTool.Pan) return;

            float radius = currentBrushSize * globalZoom / 2;
            using (var pen = new Pen(Color.Red, 2))
            {
                g.DrawEllipse(pen,
                    brushOverlayCenter.X - radius,
                    brushOverlayCenter.Y - radius,
                    radius * 2,
                    radius * 2);
            }
        }
        public unsafe delegate void PixelProcessorDelegate(byte* row, int x, int y, int bytesPerPixel);
        private readonly LRUCache<int, byte[]> _sliceCache = new LRUCache<int, byte[]>(5);
        private const int BYTES_PER_PIXEL = 3; // For 24bpp RGB
        private Bitmap CreateBitmapFromCache(byte[] pixelData)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            Rectangle rect = new Rectangle(0, 0, width, height);

            BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                Marshal.Copy(pixelData, 0, bmpData.Scan0, pixelData.Length);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
            return bmp;
        }
        private unsafe Bitmap RenderOrthoView(PixelProcessorDelegate pixelProcessor, int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, width, height);

            var bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
            int bytesPerPixel = 3;

            byte* ptr = (byte*)bmpData.Scan0;
            int stride = bmpData.Stride;

            Parallel.For(0, height, y =>
            {
                byte* row = ptr + (y * stride);
                for (int x = 0; x < width; x++)
                {
                    pixelProcessor(row, x, y, bytesPerPixel);
                }
            });

            bmp.UnlockBits(bmpData);
            return bmp;
        }
        private void UpdateScaledBitmap(Bitmap source)
        {
            _bitmapLock.EnterWriteLock();
            try
            {
                if (_scaledBitmap != null &&
                    Math.Abs(globalZoom - _lastZoom) < 0.01f &&
                    _scaledBitmap.Size == mainView.ClientSize)
                {
                    return;
                }

                _scaledBitmap?.Dispose();
                _scaledBitmap = CreateScaledBitmap(source, globalZoom);
                _lastZoom = globalZoom;

                mainView.Invalidate();
            }
            finally
            {
                _bitmapLock.ExitWriteLock();
            }
        }

        private async Task RenderViewsAsync(bool forceRedraw = false)
        {
            try
            {
                if (width == 0 || height == 0) return;

                // Check cache first
                byte[] sliceData;
                if (!forceRedraw && sliceCache.TryGetValue(CurrentSlice, out sliceData))
                {
                    using (var temp = CreateBitmapFromCache(sliceData))
                    {
                        UpdateScaledBitmap(temp);
                    }
                    return;
                }

                // Generate new bitmap
                var newBitmap = await Task.Run(() => CreateBitmapFromData(CurrentSlice,CancellationToken.None));

                // Update cache
                UpdateSliceCache(CurrentSlice, newBitmap);

                // Update display
                UpdateScaledBitmap(newBitmap);
            }
            catch (Exception ex)
            {
                Logger.Log($"[RenderViewsAsync] Error: {ex}");
            }
        }

        private void UpdateSliceCache(int sliceIndex, Bitmap bitmap)
        {
            Rectangle rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat);
            try
            {
                byte[] pixelData = new byte[bmpData.Stride * bmpData.Height];
                Marshal.Copy(bmpData.Scan0, pixelData, 0, pixelData.Length);
                _sliceCache.Add(sliceIndex, pixelData);
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }
        private Bitmap CreateScaledBitmap(Bitmap source, float zoom)
        {
            var scaledWidth = (int)(source.Width * zoom);
            var scaledHeight = (int)(source.Height * zoom);

            var scaled = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format24bppRgb);

            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, scaledWidth, scaledHeight);
            }

            return scaled;
        }

        private void MainView_MouseWheel(object sender, MouseEventArgs e)
        {
            // Determine zoom factor from the mouse wheel.
            float factor = (e.Delta > 0) ? 1.1f : 0.9f;
            globalZoom = Math.Max(0.1f, Math.Min(5f, globalZoom * factor));
            zoomLevel = globalZoom;

            // Update the scaled bitmap using the current bitmap.
            if (currentBitmap != null)
            {
                UpdateScaledBitmap(currentBitmap);
            }

            // Invalidate the main view to trigger a redraw.
            mainView.Invalidate();

            // Restart the render timer for a full update.
            renderTimer.Stop();
            renderTimer.Start();
        }

        private DateTime _lastPointCreationTime = DateTime.MinValue;
        public void OnThresholdRangeChanged(byte newMin, byte newMax)
        {
            if (SelectedMaterialIndex < 0 || Materials[SelectedMaterialIndex].IsExterior)
                return;

            PreviewMin = newMin;
            PreviewMax = newMax;

            if (currentTool == SegmentationTool.Thresholding)
            {
                UpdateThresholdPreview();
                renderTimer.Start(); // Schedule a render
            }
        }
        private void MainView_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // Existing left-click handling
                lastMousePosition = e.Location;
                mainView.Capture = true;
                renderTimer.Stop();
                renderTimer.Start();
            }
            else if (e.Button == MouseButtons.Right)
            {
                // For point tool, check if we might be drawing a box
                if (currentTool == SegmentationTool.Point)
                {
                    DateTime now = DateTime.Now;
                    if ((now - _lastPointCreationTime).TotalMilliseconds < 200)
                        return;

                    // Start tracking potential box drawing
                    isBoxDrawingPossible = true;
                    boxStartPoint = e.Location;
                    boxCurrentPoint = e.Location;
                    isActuallyDrawingBox = false;

                    // We'll defer point/box creation until mouse up
                    return;
                }

                // Existing right-click handling for other tools
                if (currentTool == SegmentationTool.Thresholding)
                {
                    UpdateThresholdPreview();
                }
                if (currentTool == SegmentationTool.Brush)
                {
                    PaintMaskAt(e.Location, currentBrushSize);
                }
                else if (currentTool == SegmentationTool.Eraser)
                {
                    EraseMaskAt(e.Location, currentBrushSize);
                }

                mainView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();

                if (RealTimeProcessing)
                    ProcessSegmentationPreview();
            }
        }


        public Bitmap GenerateXYBitmap(int sliceIndex)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            // Determine if thresholding is active (only used when no label is present)
            bool thresholdActive = (Materials.Count > 1) && EnableThresholdMask &&
                                   (SelectedMaterialIndex >= 0) && (PreviewMax > PreviewMin);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Use volumeData if available; otherwise use a neutral gray (128)
                    byte gVal = (volumeData != null) ? volumeData[x, y, sliceIndex] : (byte)128;
                    Color baseColor = Color.FromArgb(gVal, gVal, gVal);
                    Color finalColor = baseColor;

                    // Always try to overlay the segmentation label from volumeLabels.
                    byte segID = (volumeLabels != null) ? volumeLabels[x, y, sliceIndex] : (byte)0;
                    if (segID != 0)
                    {
                        Material mat = Materials.FirstOrDefault(m => m.ID == segID);
                        if (mat != null)
                        {
                            // If RenderMaterials is true or if neither flag is active, use full material color.
                            // If ShowMask is active and RenderMaterials is false, blend 50% with baseColor.
                            if (RenderMaterials || (!ShowMask && !RenderMaterials))
                            {
                                finalColor = mat.Color;
                            }
                            else if (ShowMask)
                            {
                                finalColor = BlendColors(baseColor, mat.Color, 0.5f);
                            }
                        }
                    }
                    // Optionally apply threshold overlay when no label is present.
                    else if (ShowMask && thresholdActive && gVal >= PreviewMin && gVal <= PreviewMax)
                    {
                        Color sel = GetComplementaryColor(Materials[SelectedMaterialIndex].Color);
                        finalColor = BlendColors(baseColor, sel, 0.5f);
                    }

                    // Overlay any temporary brush selection.
                    if (currentSelection != null)
                    {
                        byte sel = currentSelection[x, y];
                        if (sel != 0)
                        {
                            Material selMat = Materials.FirstOrDefault(m => m.ID == sel);
                            if (selMat != null)
                            {
                                finalColor = BlendColors(finalColor, selMat.Color, 0.5f);
                            }
                        }
                    }

                    bmp.SetPixel(x, y, finalColor);
                }
            }
            return bmp;
        }




        public Bitmap GenerateXZBitmap(int fixedY, int width, int depth)
        {
            Bitmap bmp = new Bitmap(width, depth, PixelFormat.Format24bppRgb);
            bool thresholdActive = (currentTool == SegmentationTool.Thresholding) &&
                                  (SelectedMaterialIndex >= 0) &&
                                  (PreviewMax > PreviewMin);

            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte gVal = volumeData?[x, fixedY, z] ?? (byte)128;
                    Color baseColor = Color.FromArgb(gVal, gVal, gVal);
                    Color finalColor = baseColor;

                    // Existing segmentation
                    byte segID = volumeLabels?[x, fixedY, z] ?? (byte)0;
                    if (segID != 0 && ShowMask)
                    {
                        Material mat = Materials.FirstOrDefault(m => m.ID == segID);
                        if (mat != null)
                        {
                            finalColor = RenderMaterials ? mat.Color :
                                       BlendColors(baseColor, mat.Color, 0.5f);
                        }
                    }

                    // Threshold preview overlay
                    if (thresholdActive && gVal >= PreviewMin && gVal <= PreviewMax)
                    {
                        Color sel = GetComplementaryColor(Materials[SelectedMaterialIndex].Color);
                        finalColor = BlendColors(baseColor, sel, 0.5f);
                    }

                    // Temporary XZ selection overlay
                    if (currentSelectionXZ != null && currentSelectionXZ[x, z] != 0)
                    {
                        Material selMat = Materials.FirstOrDefault(m => m.ID == currentSelectionXZ[x, z]);
                        if (selMat != null)
                        {
                            finalColor = BlendColors(finalColor, selMat.Color, 0.5f);
                        }
                    }

                    bmp.SetPixel(x, z, finalColor);
                }
            }
            return bmp;
        }


        public Bitmap GenerateYZBitmap(int fixedX, int height, int depth)
        {
            Bitmap bmp = new Bitmap(depth, height, PixelFormat.Format24bppRgb);
            bool thresholdActive = (currentTool == SegmentationTool.Thresholding) &&
                                  (SelectedMaterialIndex >= 0) &&
                                  (PreviewMax > PreviewMin);

            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    byte gVal = volumeData?[fixedX, y, z] ?? (byte)128;
                    Color baseColor = Color.FromArgb(gVal, gVal, gVal);
                    Color finalColor = baseColor;

                    // Existing segmentation
                    byte segID = volumeLabels?[fixedX, y, z] ?? (byte)0;
                    if (segID != 0 && ShowMask)
                    {
                        Material mat = Materials.FirstOrDefault(m => m.ID == segID);
                        if (mat != null)
                        {
                            finalColor = RenderMaterials ? mat.Color :
                                       BlendColors(baseColor, mat.Color, 0.5f);
                        }
                    }

                    // Threshold preview overlay
                    if (thresholdActive && gVal >= PreviewMin && gVal <= PreviewMax)
                    {
                        Color sel = GetComplementaryColor(Materials[SelectedMaterialIndex].Color);
                        finalColor = BlendColors(baseColor, sel, 0.5f);
                    }

                    // Temporary YZ selection overlay
                    if (currentSelectionYZ != null && currentSelectionYZ[z, y] != 0)
                    {
                        Material selMat = Materials.FirstOrDefault(m => m.ID == currentSelectionYZ[z, y]);
                        if (selMat != null)
                        {
                            finalColor = BlendColors(finalColor, selMat.Color, 0.5f);
                        }
                    }

                    bmp.SetPixel(z, y, finalColor);
                }
            }
            return bmp;
        }

        /// <summary>
        /// If real-time processing is enabled in the settings, this method processes the current slice 
        /// and the orthogonal views (XZ and YZ) to produce segmentation previews based on the current annotation points.
        /// </summary>
        public void ProcessSegmentationPreview()
        {
            try
            {
                SAMSettingsParams settings = SamFormInstance.CurrentSettings;
                string modelFolder = settings.ModelFolderPath;
                int imageSize = settings.ImageInputSize;

                // Check which model version to use
                bool usingSam2 = settings.UseSam2Models;

                string imageEncoderPath, promptEncoderPath, maskDecoderPath;
                string memoryEncoderPath, memoryAttentionPath, mlpPath;

                if (usingSam2)
                {
                    // SAM 2.1 paths
                    imageEncoderPath = Path.Combine(modelFolder, "sam2.1_large.encoder.onnx");
                    promptEncoderPath = ""; // Kept for compatibility
                    maskDecoderPath = Path.Combine(modelFolder, "sam2.1_large.decoder.onnx");
                    memoryEncoderPath = ""; // Not used in SAM 2.1
                    memoryAttentionPath = ""; // Not used in SAM 2.1
                    mlpPath = ""; // Not used in SAM 2.1

                    Logger.Log($"[ProcessSegmentationPreview] Using SAM 2.1 models from {modelFolder}");
                }
                else
                {
                    // Original SAM paths
                    imageEncoderPath = Path.Combine(modelFolder, "image_encoder_hiera_t.onnx");
                    promptEncoderPath = Path.Combine(modelFolder, "prompt_encoder_hiera_t.onnx");
                    maskDecoderPath = Path.Combine(modelFolder, "mask_decoder_hiera_t.onnx");
                    memoryEncoderPath = Path.Combine(modelFolder, "memory_encoder_hiera_t.onnx");
                    memoryAttentionPath = Path.Combine(modelFolder, "memory_attention_hiera_t.onnx");
                    mlpPath = Path.Combine(modelFolder, "mlp_hiera_t.onnx");
                }

                CTMemorySegmenter segmenter;
                if (RealTimeProcessing)
                {
                    if (_liveSegmenter == null)
                    {
                        _liveSegmenter = new CTMemorySegmenter(
                            imageEncoderPath,
                            promptEncoderPath,
                            maskDecoderPath,
                            memoryEncoderPath,
                            memoryAttentionPath,
                            mlpPath,
                            imageSize,
                            false,
                            settings.EnableMlp, settings.UseCpuExecutionProvider);

                        // Get threshold from SAMForm if available
                        if (SamFormInstance != null && !SamFormInstance.IsDisposed)
                        {
                            _liveSegmenter.MaskBinarizationThreshold = 0.5f;
                        }
                    }
                    segmenter = _liveSegmenter;
                }
                else
                {
                    // Use a new segmenter instance each time
                    segmenter = new CTMemorySegmenter(
                        imageEncoderPath,
                        promptEncoderPath,
                        maskDecoderPath,
                        memoryEncoderPath,
                        memoryAttentionPath,
                        mlpPath,
                        imageSize,
                        false,
                        settings.EnableMlp);

                    if (SamFormInstance != null && !SamFormInstance.IsDisposed)
                    {
                        _liveSegmenter.MaskBinarizationThreshold = 0.5f;
                    }
                }

                int currentZ = CurrentSlice;
                int w = GetWidth();
                int h = GetHeight();

                // Retrieve all annotation points for the current slice.
                List<AnnotationPoint> annPoints = AnnotationMgr.GetPointsForSlice(currentZ).ToList();
                if (annPoints.Count > 0)
                {
                    using (Bitmap baseXY = GenerateXYBitmap(currentZ, w, h))
                    {
                        byte[,] previewMask = new byte[w, h];

                        // Process one pass per material.
                        // For each material, use BuildMixedPrompts to mark positive for the target material
                        // and negative for all other points.
                        foreach (var group in annPoints.GroupBy(p => p.Label))
                        {
                            string materialName = group.Key;
                            List<AnnotationPoint> mergedPrompts = BuildMixedPrompts(annPoints, materialName);

                            using (Bitmap resultMask = segmenter.ProcessXYSlice(currentZ, baseXY, mergedPrompts, materialName))
                            {
                                Material mat = Materials.FirstOrDefault(m => m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));
                                byte matID = (mat != null) ? mat.ID : (byte)1;

                                // For each pixel, only assign if it is not already labeled.
                                for (int y = 0; y < h; y++)
                                {
                                    for (int x = 0; x < w; x++)
                                    {
                                        Color c = resultMask.GetPixel(x, y);
                                        if (c.R > 128 && previewMask[x, y] == 0)
                                        {
                                            previewMask[x, y] = matID;
                                        }
                                    }
                                }
                            }
                        }

                        currentSelection = previewMask;
                    }
                }

                // Redraw the view to display the overlay.
                ShowMask = true;
                ClearSliceCache();
                RenderViews();
            }
            catch (Exception ex)
            {
                Logger.Log("[ProcessSegmentationPreview] Exception: " + ex.Message);
            }
        }
        /// <summary>
        /// Builds a prompt list for one material pass.
        /// If there is only a single positive point and no negatives, it returns that single point.
        /// Otherwise, it returns all annotation points with the following rule:
        /// - Points belonging to the target material are marked as "Foreground" (positive).
        /// - All other points are marked as "Exterior" (negative).
        /// </summary>
        private List<AnnotationPoint> BuildMixedPrompts(
            IEnumerable<AnnotationPoint> slicePoints,
            string targetMaterialName)
        {
            // Separate positive and negative points.
            var positivePoints = slicePoints.Where(pt => pt.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase)).ToList();
            var negativePoints = slicePoints.Where(pt => !pt.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase)).ToList();

            // If there is exactly one positive point and no negatives, return it directly.
            if (positivePoints.Count == 1 && negativePoints.Count == 0)
            {
                return new List<AnnotationPoint>
        {
            new AnnotationPoint
            {
                ID = positivePoints[0].ID,
                X = positivePoints[0].X,
                Y = positivePoints[0].Y,
                Z = positivePoints[0].Z,
                Type = positivePoints[0].Type,
                Label = "Foreground" // positive prompt
            }
        };
            }

            // Otherwise, build a merged list.
            List<AnnotationPoint> finalPoints = new List<AnnotationPoint>();
            foreach (var pt in slicePoints)
            {
                finalPoints.Add(new AnnotationPoint
                {
                    ID = pt.ID,
                    X = pt.X,
                    Y = pt.Y,
                    Z = pt.Z,
                    Type = pt.Type,
                    Label = pt.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase) ? "Foreground" : "Exterior"
                });
            }
            return finalPoints;
        }



        // Helper method to generate the XY slice bitmap in MainForm if you prefer it here:
        public Bitmap GenerateXYBitmap(int sliceIndex, int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte gVal = volumeData[x, y, sliceIndex];
                    bmp.SetPixel(x, y, Color.FromArgb(gVal, gVal, gVal));
                }
            }
            return bmp;
        }

        



        // Applies the temporary XZ selection mask (if present and non-empty) to the fixed row slice.
        public void ApplyXZSelection()
        {
            int fixedY = XzSliceY;
            if (currentSelectionXZ == null)
                return;

            // For each pixel in the XZ mask, update the corresponding voxel at the fixed Y.
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte sel = currentSelectionXZ[x, z];
                    if (sel != 0)
                        volumeLabels[x, fixedY, z] = sel;
                }
            }
            // Clear the temporary mask for XZ.
            currentSelectionXZ = new byte[width, depth];
        }

        // Applies the temporary YZ selection mask (if present and non-empty) to the fixed column slice.
        public void ApplyYZSelection()
        {
            int fixedX = YzSliceX;
            if (currentSelectionYZ == null)
                return;

            // Note: currentSelectionYZ is organized as [depth, height] in this code.
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    byte sel = currentSelectionYZ[z, y];
                    if (sel != 0)
                        volumeLabels[fixedX, y, z] = sel;
                }
            }
            // Clear the temporary mask for YZ.
            currentSelectionYZ = new byte[depth, height];
        }




        private void UpdateSAMFormWithPoint(AnnotationPoint point)
        {
            // If a SAMForm is open, update its DataGridView.
            if (this.SamFormInstance != null && !this.SamFormInstance.IsDisposed)
            {
                DataGridView dgv = SamFormInstance.GetPointsDataGridView();
                if (dgv != null)
                {
                    dgv.Rows.Add(point.ID, point.X, point.Y, point.Z, point.Type, point.Label);
                }
            }
        }
        private void InsertPointToSAMForm(AnnotationPoint point)
        {
            foreach (Form frm in Application.OpenForms)
            {
                if (frm is SAMForm samForm)
                {
                    DataGridView dgv = samForm.Controls.Find("dataGridPoints", true)[0] as DataGridView;
                    dgv.Rows.Add(point.ID, point.X, point.Y, point.Z, point.Type, point.Label);
                    break;
                }
            }
        }
        private void PaintMaskAt(Point screenLocation, int brushSize)
        {
            interpolatedMask = null;
            // Convert screen (client) coordinates to image coordinates.
            int imageX = (int)((screenLocation.X - panOffset.X) / zoomLevel);
            int imageY = (int)((screenLocation.Y - panOffset.Y) / zoomLevel);
            int radius = brushSize / 2; // brushSize is defined in image space

            // Ensure the temporary selection mask is allocated.
            if (currentSelection == null || currentSelection.GetLength(0) != width || currentSelection.GetLength(1) != height)
            {
                currentSelection = new byte[width, height];
            }

            // Determine the label to set from the selected material.
            byte labelToSet = (byte)((SelectedMaterialIndex > 0) ? Materials[SelectedMaterialIndex].ID : 1);

            // Paint the circular area in the temporary mask.
            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = imageY + dy;
                if (y < 0 || y >= height)
                    continue;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = imageX + dx;
                    if (x < 0 || x >= width)
                        continue;
                    if (dx * dx + dy * dy <= radius * radius)
                    {
                        currentSelection[x, y] = labelToSet;
                    }
                }
            }
            RenderViews();
            _ = RenderOrthoViewsAsync();
        }
        /// <summary>
        /// Computes a full 3D interpolated mask from the sparse brush selections (for XY, XZ, and YZ views),
        /// then applies a 3D smoothing filter and immediately updates the entire label volume.
        /// </summary>
        /// <param name="materialID">The material label to assign.</param>
        public void InterpolateSelection(byte materialID)
        {
            // Create a new raw 3D mask covering the full volume.
            interpolatedMask = new bool[width, height, depth];

            // Interpolate along the three axes using the stored sparse selections.
            // For the XY view (sparseSelectionsZ), keys are Z-slices.
            bool[,,] maskZ = InterpolateAlongAxis(sparseSelectionsZ, width, height, depth, Axis.Z);
            // For the XZ view (sparseSelectionsY), keys are Y-slices.
            bool[,,] maskY = InterpolateAlongAxis(sparseSelectionsY, width, depth, height, Axis.Y);
            // For the YZ view (sparseSelectionsX), keys are X-slices.
            bool[,,] maskX = InterpolateAlongAxis(sparseSelectionsX, height, depth, width, Axis.X);

            // Combine the three raw masks (logical OR) into the interpolatedMask.
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        interpolatedMask[x, y, z] = maskZ[x, y, z] || maskY[x, y, z] || maskX[x, y, z];
                    }
                }
            }

            // Now smooth the raw interpolated mask.
            bool[,,] smoothMask = SmoothMask(interpolatedMask, kernelSize: 3, threshold: 0.5);

            // Immediately update the entire label volume using the smoothed mask.
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (smoothMask[x, y, z])
                        {
                            volumeLabels[x, y, z] = materialID;
                        }
                    }
                }
            }

            Logger.Log("[InterpolateSelection] 3D interpolated and smoothed mask applied across the entire volume.");
            RenderViews();
            _ = RenderOrthoViewsAsync();
        }
        /// <summary>
        /// Fills entirely empty slices in the mask by copying from the nearest non-empty slice.
        /// This helps ensure that a few missing slices (if any) do not break the overall continuity.
        /// </summary>
        /// <param name="mask">The raw 3D mask computed from interpolation.</param>
        /// <returns>The 3D mask with empty slices filled.</returns>
        private bool[,,] FillEmptySlices(bool[,,] mask)
        {
            int d = mask.GetLength(2);
            // For each slice in the z-dimension:
            for (int z = 0; z < d; z++)
            {
                if (IsSliceEmpty(mask, z))
                {
                    // Look for the nearest non-empty slice.
                    int nearest = -1;
                    for (int offset = 1; offset < d; offset++)
                    {
                        if (z - offset >= 0 && !IsSliceEmpty(mask, z - offset))
                        {
                            nearest = z - offset;
                            break;
                        }
                        if (z + offset < d && !IsSliceEmpty(mask, z + offset))
                        {
                            nearest = z + offset;
                            break;
                        }
                    }
                    if (nearest != -1)
                    {
                        // Copy the entire slice from the nearest non-empty slice.
                        for (int x = 0; x < mask.GetLength(0); x++)
                        {
                            for (int y = 0; y < mask.GetLength(1); y++)
                            {
                                mask[x, y, z] = mask[x, y, nearest];
                            }
                        }
                    }
                }
            }
            return mask;
        }
        /// <summary>
        /// Determines whether a given slice (at index z) is completely empty (all false).
        /// </summary>
        private bool IsSliceEmpty(bool[,,] mask, int z)
        {
            int w = mask.GetLength(0), h = mask.GetLength(1);
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    if (mask[x, y, z])
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Interpolates along one axis using sparse brush selections.
        /// For each in-plane coordinate, it gathers all slice indices where a brush stroke was recorded
        /// and fills all slices between the minimum and maximum indices.
        /// </summary>
        /// <param name="sparseDict">
        /// Dictionary whose keys are slice indices (for a given view) and values are the corresponding 2D brush masks.
        /// </param>
        /// <param name="dimA">
        /// For Axis.Z: volume width; for Axis.Y: volume width; for Axis.X: volume height.
        /// </param>
        /// <param name="dimB">
        /// For Axis.Z: volume height; for Axis.Y: volume depth; for Axis.X: volume depth.
        /// </param>
        /// <param name="fullDim">
        /// For Axis.Z: volume depth; for Axis.Y: volume height; for Axis.X: volume width.
        /// </param>
        /// <param name="axis">The axis along which to interpolate.</param>
        /// <returns>A 3D boolean mask of dimensions [width, height, depth].</returns>
        private bool[,,] InterpolateAlongAxis(Dictionary<int, byte[,]> sparseDict, int dimA, int dimB, int fullDim, Axis axis)
        {
            bool[,,] mask = new bool[width, height, depth];

            switch (axis)
            {
                case Axis.Z:
                    // For each (x,y) coordinate in the XY plane.
                    for (int x = 0; x < width; x++)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            List<int> zIndices = new List<int>();
                            foreach (var kvp in sparseDict)
                            {
                                int zKey = kvp.Key;
                                byte[,] plane = kvp.Value; // Expected size: [width, height]
                                if (x < plane.GetLength(0) && y < plane.GetLength(1) && plane[x, y] != 0)
                                {
                                    zIndices.Add(zKey);
                                }
                            }
                            if (zIndices.Count > 0)
                            {
                                int minZ = zIndices.Min();
                                int maxZ = zIndices.Max();
                                minZ = Math.Max(0, minZ);
                                maxZ = Math.Min(depth - 1, maxZ);
                                for (int z = minZ; z <= maxZ; z++)
                                {
                                    mask[x, y, z] = true;
                                }
                            }
                        }
                    }
                    break;
                case Axis.Y:
                    // For each (x,z) coordinate in the XZ plane.
                    for (int x = 0; x < width; x++)
                    {
                        for (int z = 0; z < depth; z++)
                        {
                            List<int> yIndices = new List<int>();
                            foreach (var kvp in sparseDict)
                            {
                                int yKey = kvp.Key;
                                byte[,] plane = kvp.Value; // Expected size: [width, depth]
                                if (x < plane.GetLength(0) && z < plane.GetLength(1) && plane[x, z] != 0)
                                {
                                    yIndices.Add(yKey);
                                }
                            }
                            if (yIndices.Count > 0)
                            {
                                int minY = yIndices.Min();
                                int maxY = yIndices.Max();
                                minY = Math.Max(0, minY);
                                maxY = Math.Min(height - 1, maxY);
                                for (int y = minY; y <= maxY; y++)
                                {
                                    mask[x, y, z] = true;
                                }
                            }
                        }
                    }
                    break;
                case Axis.X:
                    // For each (y,z) coordinate in the YZ plane.
                    for (int y = 0; y < height; y++)
                    {
                        for (int z = 0; z < depth; z++)
                        {
                            List<int> xIndices = new List<int>();
                            foreach (var kvp in sparseDict)
                            {
                                int xKey = kvp.Key;
                                byte[,] plane = kvp.Value; // Expected size: [height, depth]
                                if (y < plane.GetLength(0) && z < plane.GetLength(1) && plane[y, z] != 0)
                                {
                                    xIndices.Add(xKey);
                                }
                            }
                            if (xIndices.Count > 0)
                            {
                                int minX = xIndices.Min();
                                int maxX = xIndices.Max();
                                minX = Math.Max(0, minX);
                                maxX = Math.Min(width - 1, maxX);
                                for (int x = minX; x <= maxX; x++)
                                {
                                    mask[x, y, z] = true;
                                }
                            }
                        }
                    }
                    break;
            }
            return mask;
        }

        /// <summary>
        /// Applies a simple 3D mean filter to the binary mask to smooth abrupt transitions.
        /// The method averages over a cubic kernel of given size and then thresholds the result.
        /// </summary>
        /// <param name="mask">The original 3D binary mask.</param>
        /// <param name="kernelSize">Size of the cubic kernel (should be an odd number; default is 3).</param>
        /// <param name="threshold">Threshold (between 0 and 1) at which to binarize the result (default is 0.5).</param>
        /// <returns>A smoothed 3D binary mask.</returns>
        private bool[,,] SmoothMask(bool[,,] mask, int kernelSize = 3, double threshold = 0.5)
        {
            int w = mask.GetLength(0), h = mask.GetLength(1), d = mask.GetLength(2);
            double[,,] accum = new double[w, h, d];
            int offset = kernelSize / 2;

            // For each voxel, average the values in its neighborhood.
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int z = 0; z < d; z++)
                    {
                        double sum = 0;
                        int count = 0;
                        for (int i = -offset; i <= offset; i++)
                        {
                            for (int j = -offset; j <= offset; j++)
                            {
                                for (int k = -offset; k <= offset; k++)
                                {
                                    int nx = x + i, ny = y + j, nz = z + k;
                                    if (nx >= 0 && nx < w && ny >= 0 && ny < h && nz >= 0 && nz < d)
                                    {
                                        sum += mask[nx, ny, nz] ? 1.0 : 0.0;
                                        count++;
                                    }
                                }
                            }
                        }
                        accum[x, y, z] = sum / count;
                    }
                }
            }

            // Create the smoothed mask by thresholding the averaged values.
            bool[,,] smoothMask = new bool[w, h, d];
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int z = 0; z < d; z++)
                    {
                        smoothMask[x, y, z] = accum[x, y, z] >= threshold;
                    }
                }
            }
            return smoothMask;
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

            RenderViews();
        }

        // Helper method to interpolate along a single axis
        private void ProcessAxisInterpolation(Dictionary<int, byte[,]> sparseDict, Action<int, int, int> setMask)
        {
            foreach (var kvp in sparseDict)
            {
                int fixedAxisIndex = kvp.Key; // e.g., Z for XY slices
                byte[,] slice = kvp.Value;

                for (int a = 0; a < slice.GetLength(0); a++)
                {
                    for (int b = 0; b < slice.GetLength(1); b++)
                    {
                        if (slice[a, b] != 0)
                        {
                            // Determine voxel coordinates based on axis
                            int x = -1, y = -1, z = -1;
                            if (sparseDict == sparseSelectionsZ) // XY slice
                            {
                                x = a;
                                y = b;
                                z = fixedAxisIndex;
                            }
                            else if (sparseDict == sparseSelectionsY) // XZ slice
                            {
                                x = a;
                                z = b;
                                y = fixedAxisIndex;
                            }
                            else if (sparseDict == sparseSelectionsX) // YZ slice
                            {
                                y = a;
                                z = b;
                                x = fixedAxisIndex;
                            }

                            if (x >= 0 && y >= 0 && z >= 0)
                            {
                                setMask(x, y, z);
                            }
                        }
                    }
                }
            }
        }

        private void EraseMaskAt(Point screenLocation, int brushSize)
        {
            interpolatedMask = null;
            int imageX = (int)((screenLocation.X - panOffset.X) / zoomLevel);
            int imageY = (int)((screenLocation.Y - panOffset.Y) / zoomLevel);
            int radius = brushSize / 2;
            if (currentSelection == null || currentSelection.GetLength(0) != width || currentSelection.GetLength(1) != height)
            {
                currentSelection = new byte[width, height];
            }
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
                        currentSelection[x, y] = 0;
                    }
                }
            }
            RenderViews();
            _ = RenderOrthoViewsAsync();
        }
        private void MainView_MouseMove(object sender, MouseEventArgs e)
        {
            // Existing code for brush overlay and panning
            if (e.Button == MouseButtons.None &&
                (currentTool == SegmentationTool.Brush || currentTool == SegmentationTool.Eraser))
            {
                brushOverlayCenter = e.Location;
                showBrushOverlay = true;
                mainView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();
            }

            // Handle box drawing (add this)
            if (isBoxDrawingPossible && e.Button == MouseButtons.Right)
            {
                boxCurrentPoint = e.Location;

                // If moved significantly, we're definitely drawing a box
                if (Math.Abs(boxCurrentPoint.X - boxStartPoint.X) > 5 ||
                    Math.Abs(boxCurrentPoint.Y - boxStartPoint.Y) > 5)
                {
                    isActuallyDrawingBox = true;
                    mainView.Invalidate(); // Update UI to show box
                }
            }
            // Existing panning and painting code
            else if (e.Button == MouseButtons.Left && mainView.Capture)
            {
                int dx = e.Location.X - lastMousePosition.X;
                int dy = e.Location.Y - lastMousePosition.Y;
                panOffset = new PointF(panOffset.X + dx, panOffset.Y + dy);
                ClampPanOffset();
                lastMousePosition = e.Location;
                mainView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (currentTool == SegmentationTool.Brush)
                    PaintMaskAt(e.Location, currentBrushSize);
                else if (currentTool == SegmentationTool.Eraser)
                    EraseMaskAt(e.Location, currentBrushSize);
                mainView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();
                if (RealTimeProcessing)
                    ProcessSegmentationPreview();
            }
        }

        private void MainView_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                mainView.Capture = false;

            // Add box drawing finalization
            if (e.Button == MouseButtons.Right && isBoxDrawingPossible)
            {
                if (isActuallyDrawingBox)
                {
                    // Create box annotation
                    float x1 = (boxStartPoint.X - panOffset.X) / globalZoom;
                    float y1 = (boxStartPoint.Y - panOffset.Y) / globalZoom;
                    float x2 = (boxCurrentPoint.X - panOffset.X) / globalZoom;
                    float y2 = (boxCurrentPoint.Y - panOffset.Y) / globalZoom;

                    // Clamp to image boundaries
                    x1 = Math.Max(0, Math.Min(x1, width - 1));
                    y1 = Math.Max(0, Math.Min(y1, height - 1));
                    x2 = Math.Max(0, Math.Min(x2, width - 1));
                    y2 = Math.Max(0, Math.Min(y2, height - 1));

                    Material selectedMaterial = (SelectedMaterialIndex >= 0 && SelectedMaterialIndex < Materials.Count)
                        ? Materials[SelectedMaterialIndex]
                        : new Material("Default", Color.Red, 0, 0, 1);

                    AnnotationPoint box = AnnotationMgr.AddBox(x1, y1, x2, y2, CurrentSlice, selectedMaterial.Name);
                    UpdateSAMFormWithBox(box);
                }
                else
                {
                    // Create point annotation (using the existing code)
                    float sliceX = (boxStartPoint.X - panOffset.X) / globalZoom;
                    float sliceY = (boxStartPoint.Y - panOffset.Y) / globalZoom;
                    Material selectedMaterial = (SelectedMaterialIndex >= 0 && SelectedMaterialIndex < Materials.Count)
                        ? Materials[SelectedMaterialIndex]
                        : new Material("Default", Color.Red, 0, 0, 1);

                    if (sliceX >= 0 && sliceX < width && sliceY >= 0 && sliceY < height)
                    {
                        _lastPointCreationTime = DateTime.Now;
                        AnnotationPoint point = AnnotationMgr.AddPoint(sliceX, sliceY, CurrentSlice, selectedMaterial.Name);
                        UpdateSAMFormWithPoint(point);
                    }
                }

                isBoxDrawingPossible = false;
                isActuallyDrawingBox = false;
                mainView.Invalidate();

                renderTimer.Stop();
                renderTimer.Start();

                if (RealTimeProcessing)
                    ProcessSegmentationPreview();
            }
        }

        private void ClampXzPan()
        {
            if (xzProjection == null) return;
            float imageWidth = xzProjection.Width * xzZoom;
            float imageHeight = xzProjection.Height * xzZoom;
            float maxPanX = Math.Min(0, xzView.ClientSize.Width - imageWidth);
            float maxPanY = Math.Min(0, xzView.ClientSize.Height - imageHeight);
            xzPan.X = Math.Max(maxPanX, Math.Min(xzPan.X, 0));
            xzPan.Y = Math.Max(maxPanY, Math.Min(xzPan.Y, 0));
            if (imageWidth < xzView.ClientSize.Width)
                xzPan.X = (xzView.ClientSize.Width - imageWidth) / 2;
            if (imageHeight < xzView.ClientSize.Height)
                xzPan.Y = (xzView.ClientSize.Height - imageHeight) / 2;
        }
        private void ClampYzPan()
        {
            if (yzProjection == null) return;
            float imageWidth = yzProjection.Width * yzZoom;
            float imageHeight = yzProjection.Height * yzZoom;
            float maxPanX = Math.Min(0, yzView.ClientSize.Width - imageWidth);
            float maxPanY = Math.Min(0, yzView.ClientSize.Height - imageHeight);
            yzPan.X = Math.Max(maxPanX, Math.Min(yzPan.X, 0));
            yzPan.Y = Math.Max(maxPanY, Math.Min(yzPan.Y, 0));
            if (imageWidth < yzView.ClientSize.Width)
                yzPan.X = (yzView.ClientSize.Width - imageWidth) / 2;
            if (imageHeight < yzView.ClientSize.Height)
                yzPan.Y = (yzView.ClientSize.Height - imageHeight) / 2;
        }
        private void ClampPanOffset()
        {
            if (currentBitmap == null)
                return;
            float imageW = currentBitmap.Width * zoomLevel;
            float imageH = currentBitmap.Height * zoomLevel;
            if (imageW > mainView.ClientSize.Width)
            {
                float maxPanX = mainView.ClientSize.Width - imageW;
                panOffset.X = Math.Max(maxPanX, Math.Min(panOffset.X, 0));
            }
            else
            {
                panOffset.X = (mainView.ClientSize.Width - imageW) / 2;
            }
            if (imageH > mainView.ClientSize.Height)
            {
                float maxPanY = mainView.ClientSize.Height - imageH;
                panOffset.Y = Math.Max(maxPanY, Math.Min(panOffset.Y, 0));
            }
            else
            {
                panOffset.Y = (mainView.ClientSize.Height - imageH) / 2;
            }
        }
        public void ResetView()
        {
            globalZoom = 1.0f;
            zoomLevel = 1.0f;
            xzZoom = 1.0f;
            yzZoom = 1.0f;

            if (currentBitmap != null)
            {
                float imageW = currentBitmap.Width;
                float imageH = currentBitmap.Height;
                panOffset = new PointF((mainView.ClientSize.Width - imageW) / 2, (mainView.ClientSize.Height - imageH) / 2);
            }
            else
            {
                panOffset = PointF.Empty;
            }
            if (xzProjection != null)
                xzPan = new PointF((xzView.ClientSize.Width - xzProjection.Width) / 2, (xzView.ClientSize.Height - xzProjection.Height) / 2);
            else
                xzPan = PointF.Empty;
            if (yzProjection != null)
                yzPan = new PointF((yzView.ClientSize.Width - yzProjection.Width) / 2, (yzView.ClientSize.Height - yzProjection.Height) / 2);
            else
                yzPan = PointF.Empty;

            // Redraw the main view and orthoviews.
            RenderViews();
            _ = RenderOrthoViewsAsync();
            xzView.Invalidate();
            yzView.Invalidate();
        }


        private void DrawScaleBar(Graphics g, Rectangle clientRect, float theZoom)
        {
            const float baseScreenLength = 100f;
            double candidateLengthMeters = baseScreenLength / theZoom * pixelSize;
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
            float screenLength = (float)(labelInMeters / pixelSize * theZoom);
            using (SolidBrush brush = new SolidBrush(Color.White))
            using (Font font = new Font("Arial", 10))
            {
                float x = 20;
                float y = clientRect.Height - 40;
                g.FillRectangle(brush, x, y, screenLength, 3);
                g.DrawString(labelText, font, brush, x, y + 5);
            }
        }

        private void XzView_Paint(object sender, PaintEventArgs e)
        {
            // Keep existing drawing code
            e.Graphics.Clear(Color.Black);
            if (xzProjection == null)
                return;
            ClampXzPan();
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            RectangleF destRect = new RectangleF(xzPan.X, xzPan.Y, xzProjection.Width * xzZoom, xzProjection.Height * xzZoom);
            e.Graphics.DrawImage(xzProjection, destRect);
            DrawScaleBar(e.Graphics, xzView.ClientRectangle, xzZoom);

            // Draw header and slice text
            using (Font font = new Font("Arial", 12, FontStyle.Bold))
            using (SolidBrush headerBrush = new SolidBrush(Color.Red))
            {
                e.Graphics.DrawString("XZ", font, headerBrush, new PointF(5, 5));
            }

            string sliceText = $"Slice: {this.XzSliceY}";
            using (Font font = new Font("Arial", 10))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                SizeF textSize = e.Graphics.MeasureString(sliceText, font);
                e.Graphics.DrawString(sliceText, font, textBrush,
                    new PointF(xzView.ClientSize.Width - textSize.Width - 5, xzView.ClientSize.Height - textSize.Height - 5));
            }

            // Draw annotation points in XZ view
            float tolerance = 5.0f;
            foreach (var point in AnnotationMgr.Points)
            {
                if (Math.Abs(point.Y - XzSliceY) <= tolerance)
                {
                    Color pointColor = Materials.FirstOrDefault(m => m.Name == point.Label)?.Color ?? Color.Red;

                    if (point.Type == "Box")
                    {
                        // In XZ view, draw box using X and Z coordinates
                        float x1 = point.X * xzZoom + xzPan.X;
                        float z1 = point.Z * xzZoom + xzPan.Y;

                        // Use X2 and Z for the second corner if available
                        float x2 = point.X2 * xzZoom + xzPan.X;
                        float z2 = point.Y2 * xzZoom + xzPan.Y; // Y2 is actually storing Z2

                        float boxX = Math.Min(x1, x2);
                        float boxY = Math.Min(z1, z2);
                        float boxWidth = Math.Abs(x2 - x1);
                        float boxHeight = Math.Abs(z2 - z1);

                        using (Pen pen = new Pen(pointColor, 2))
                        {
                            e.Graphics.DrawRectangle(pen, boxX, boxY, boxWidth, boxHeight);
                        }
                        using (Font font = new Font("Arial", 8))
                        using (SolidBrush idBrush = new SolidBrush(Color.Yellow))
                        {
                            e.Graphics.DrawString(point.ID.ToString(), font, idBrush, boxX, boxY - 10);
                        }
                    }
                    else // Draw point
                    {
                        float drawX = point.X * xzZoom + xzPan.X;
                        float drawZ = point.Z * xzZoom + xzPan.Y;

                        using (Pen pen = new Pen(pointColor, 2))
                        {
                            e.Graphics.DrawLine(pen, drawX - 5, drawZ, drawX + 5, drawZ);
                            e.Graphics.DrawLine(pen, drawX, drawZ - 5, drawX, drawZ + 5);
                        }
                        using (Font font = new Font("Arial", 8))
                        using (SolidBrush idBrush = new SolidBrush(Color.Yellow))
                        {
                            e.Graphics.DrawString(point.ID.ToString(), font, idBrush, drawX + 5, drawZ + 5);
                        }
                    }
                }
            }

            // Draw box preview during dragging
            if (isXzBoxDrawingPossible && isXzActuallyDrawingBox)
            {
                int boxX = Math.Min(xzBoxStartPoint.X, xzBoxCurrentPoint.X);
                int boxY = Math.Min(xzBoxStartPoint.Y, xzBoxCurrentPoint.Y);
                int boxWidth = Math.Abs(xzBoxCurrentPoint.X - xzBoxStartPoint.X);
                int boxHeight = Math.Abs(xzBoxCurrentPoint.Y - xzBoxStartPoint.Y);

                using (var pen = new Pen(Color.Yellow, 2))
                {
                    e.Graphics.DrawRectangle(pen, boxX, boxY, boxWidth, boxHeight);
                }
            }
        }



        private void XzView_MouseWheel(object sender, MouseEventArgs e)
        {
            // Determine zoom factor for the XZ view.
            float factor = (e.Delta > 0) ? 1.1f : 0.9f;
            xzZoom = Math.Max(0.1f, Math.Min(5f, xzZoom * factor));

            // Invalidate the XZ view to update the projection.
            xzView.Invalidate();

            // Restart the render timer to schedule a full update.
            renderTimer.Stop();
            renderTimer.Start();
        }

        private void XzView_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (currentTool == SegmentationTool.Point)
                {
                    // Start tracking potential box drawing in XZ view
                    isXzBoxDrawingPossible = true;
                    xzBoxStartPoint = e.Location;
                    xzBoxCurrentPoint = e.Location;
                    isXzActuallyDrawingBox = false;
                    return;
                }

                if (currentTool == SegmentationTool.Brush)
                    PaintOrthoMaskAtXZ(e.Location, currentBrushSize);
                else if (currentTool == SegmentationTool.Eraser)
                    EraseOrthoMaskAtXZ(e.Location, currentBrushSize);
                xzView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();
                if (RealTimeProcessing)
                    ProcessSegmentationPreview();
            }
            else if (e.Button == MouseButtons.Left)
            {
                lastMousePosition = e.Location;
                renderTimer.Stop();
                renderTimer.Start();
            }
        }


        private void XzView_MouseMove(object sender, MouseEventArgs e)
        {
            // Handle XZ box drawing
            if (isXzBoxDrawingPossible && e.Button == MouseButtons.Right)
            {
                xzBoxCurrentPoint = e.Location;

                // If moved significantly, we're definitely drawing a box
                if (Math.Abs(xzBoxCurrentPoint.X - xzBoxStartPoint.X) > 5 ||
                    Math.Abs(xzBoxCurrentPoint.Y - xzBoxStartPoint.Y) > 5)
                {
                    isXzActuallyDrawingBox = true;
                    xzView.Invalidate(); // Update UI to show box
                }
            }
            // Existing code
            else if (e.Button == MouseButtons.None &&
                (currentTool == SegmentationTool.Brush || currentTool == SegmentationTool.Eraser))
            {
                xzOverlayCenter = e.Location;
                showBrushOverlay = true;
                xzView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (currentTool == SegmentationTool.Brush)
                    PaintOrthoMaskAtXZ(e.Location, currentBrushSize);
                else if (currentTool == SegmentationTool.Eraser)
                    EraseOrthoMaskAtXZ(e.Location, currentBrushSize);
                xzView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();
                if (RealTimeProcessing)
                    ProcessSegmentationPreview();
            }
            else if (e.Button == MouseButtons.Left)
            {
                int dx = e.Location.X - lastMousePosition.X;
                int dy = e.Location.Y - lastMousePosition.Y;
                xzPan = new PointF(xzPan.X + dx, xzPan.Y + dy);
                lastMousePosition = e.Location;
                xzView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();
            }
        }
        private void XzView_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && isXzBoxDrawingPossible)
            {
                if (isXzActuallyDrawingBox)
                {
                    // Create box annotation
                    float x1 = (xzBoxStartPoint.X - xzPan.X) / xzZoom;
                    float z1 = (xzBoxStartPoint.Y - xzPan.Y) / xzZoom;
                    float x2 = (xzBoxCurrentPoint.X - xzPan.X) / xzZoom;
                    float z2 = (xzBoxCurrentPoint.Y - xzPan.Y) / xzZoom;

                    // Clamp to image boundaries
                    x1 = Math.Max(0, Math.Min(x1, width - 1));
                    z1 = Math.Max(0, Math.Min(z1, depth - 1));
                    x2 = Math.Max(0, Math.Min(x2, width - 1));
                    z2 = Math.Max(0, Math.Min(z2, depth - 1));

                    Material selectedMaterial = (SelectedMaterialIndex >= 0 && SelectedMaterialIndex < Materials.Count)
                        ? Materials[SelectedMaterialIndex]
                        : new Material("Default", Color.Red, 0, 0, 1);

                    // For XZ view, the Y coordinate is fixed at XzSliceY
                    AnnotationPoint box = AnnotationMgr.AddBox(x1, XzSliceY, x2, XzSliceY, (int)((z1 + z2) / 2), selectedMaterial.Name);
                    // Store Z values in the box's X2,Y2 coordinates
                    box.X2 = x2;
                    box.Y2 = z2;
                    UpdateSAMFormWithBox(box);
                }
                else
                {
                    // Create point annotation
                    int x = (int)((xzBoxStartPoint.X - xzPan.X) / xzZoom);
                    int z = (int)((xzBoxStartPoint.Y - xzPan.Y) / xzZoom);

                    if (x >= 0 && x < width && z >= 0 && z < depth)
                    {
                        Material selectedMaterial = (SelectedMaterialIndex >= 0 && SelectedMaterialIndex < Materials.Count)
                            ? Materials[SelectedMaterialIndex]
                            : new Material("Default", Color.Red, 0, 0, 1);

                        AnnotationPoint point = AnnotationMgr.AddPoint(x, XzSliceY, z, selectedMaterial.Name);
                        UpdateSAMFormWithPoint(point);
                    }
                }

                isXzBoxDrawingPossible = false;
                isXzActuallyDrawingBox = false;
                xzView.Invalidate();

                renderTimer.Stop();
                renderTimer.Start();

                if (RealTimeProcessing)
                    ProcessSegmentationPreview();
            }
        }


        private void PaintOrthoMaskAtXZ(Point screenLocation, int brushSize)
        {
            // Ensure the temporary selection array is allocated.
            if (currentSelectionXZ == null || currentSelectionXZ.GetLength(0) != width || currentSelectionXZ.GetLength(1) != depth)
                currentSelectionXZ = new byte[width, depth];

            int imageX = (int)((screenLocation.X - xzPan.X) / xzZoom);
            int imageZ = (int)((screenLocation.Y - xzPan.Y) / xzZoom);
            int radius = brushSize / 2;
            byte labelToSet = (byte)((SelectedMaterialIndex > 0) ? Materials[SelectedMaterialIndex].ID : 1);

            for (int dz = -radius; dz <= radius; dz++)
            {
                int z = imageZ + dz;
                if (z < 0 || z >= depth)
                    continue;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = imageX + dx;
                    if (x < 0 || x >= width)
                        continue;
                    if (dx * dx + dz * dz <= radius * radius)
                    {
                        currentSelectionXZ[x, z] = labelToSet;
                    }
                }
            }
        }

        private void EraseOrthoMaskAtXZ(Point screenLocation, int brushSize)
        {
            if (currentSelectionXZ == null || currentSelectionXZ.GetLength(0) != width || currentSelectionXZ.GetLength(1) != depth)
                currentSelectionXZ = new byte[width, depth];

            int imageX = (int)((screenLocation.X - xzPan.X) / xzZoom);
            int imageZ = (int)((screenLocation.Y - xzPan.Y) / xzZoom);
            int radius = brushSize / 2;

            for (int dz = -radius; dz <= radius; dz++)
            {
                int z = imageZ + dz;
                if (z < 0 || z >= depth)
                    continue;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = imageX + dx;
                    if (x < 0 || x >= width)
                        continue;
                    if (dx * dx + dz * dz <= radius * radius)
                    {
                        currentSelectionXZ[x, z] = 0;
                    }
                }
            }
        }

        private void YzView_Paint(object sender, PaintEventArgs e)
        {
            // Keep existing drawing code
            e.Graphics.Clear(Color.Black);
            if (yzProjection == null)
                return;
            ClampYzPan();
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            RectangleF destRect = new RectangleF(yzPan.X, yzPan.Y, yzProjection.Width * yzZoom, yzProjection.Height * yzZoom);
            e.Graphics.DrawImage(yzProjection, destRect);
            DrawScaleBar(e.Graphics, yzView.ClientRectangle, yzZoom);

            using (Font font = new Font("Arial", 12, FontStyle.Bold))
            using (SolidBrush headerBrush = new SolidBrush(Color.Green))
            {
                e.Graphics.DrawString("YZ", font, headerBrush, new PointF(5, 5));
            }
            string sliceText = $"Slice: {this.YzSliceX}";
            using (Font font = new Font("Arial", 10))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                SizeF textSize = e.Graphics.MeasureString(sliceText, font);
                e.Graphics.DrawString(sliceText, font, textBrush,
                    new PointF(yzView.ClientSize.Width - textSize.Width - 5, yzView.ClientSize.Height - textSize.Height - 5));
            }

            // Draw annotation points in YZ view
            float tolerance = 5.0f;
            foreach (var point in AnnotationMgr.Points)
            {
                if (Math.Abs(point.X - YzSliceX) <= tolerance)
                {
                    Color pointColor = Materials.FirstOrDefault(m => m.Name == point.Label)?.Color ?? Color.Red;

                    if (point.Type == "Box")
                    {
                        // In YZ view, we draw box using Z and Y coordinates
                        float z1 = point.X2 * yzZoom + yzPan.X; // X2 is storing Z1 for YZ boxes
                        float y1 = point.Y * yzZoom + yzPan.Y;
                        float z2 = point.Y2 * yzZoom + yzPan.X; // Y2 is storing Z2 for YZ boxes
                        float y2 = point.Y2 * yzZoom + yzPan.Y;

                        float boxX = Math.Min(z1, z2);
                        float boxY = Math.Min(y1, y2);
                        float boxWidth = Math.Abs(z2 - z1);
                        float boxHeight = Math.Abs(y2 - y1);

                        using (Pen pen = new Pen(pointColor, 2))
                        {
                            e.Graphics.DrawRectangle(pen, boxX, boxY, boxWidth, boxHeight);
                        }
                        using (Font font = new Font("Arial", 8))
                        using (SolidBrush idBrush = new SolidBrush(Color.Yellow))
                        {
                            e.Graphics.DrawString(point.ID.ToString(), font, idBrush, boxX, boxY - 10);
                        }
                    }
                    else // Draw point
                    {
                        float drawZ = point.Z * yzZoom + yzPan.X;
                        float drawY = point.Y * yzZoom + yzPan.Y;

                        using (Pen pen = new Pen(pointColor, 2))
                        {
                            e.Graphics.DrawLine(pen, drawZ - 5, drawY, drawZ + 5, drawY);
                            e.Graphics.DrawLine(pen, drawZ, drawY - 5, drawZ, drawY + 5);
                        }
                        using (Font font = new Font("Arial", 8))
                        using (SolidBrush idBrush = new SolidBrush(Color.Yellow))
                        {
                            e.Graphics.DrawString(point.ID.ToString(), font, idBrush, drawZ + 5, drawY + 5);
                        }
                    }
                }
            }

            // Draw box preview during dragging
            if (isYzBoxDrawingPossible && isYzActuallyDrawingBox)
            {
                int boxX = Math.Min(yzBoxStartPoint.X, yzBoxCurrentPoint.X);
                int boxY = Math.Min(yzBoxStartPoint.Y, yzBoxCurrentPoint.Y);
                int boxWidth = Math.Abs(yzBoxCurrentPoint.X - yzBoxStartPoint.X);
                int boxHeight = Math.Abs(yzBoxCurrentPoint.Y - yzBoxStartPoint.Y);

                using (var pen = new Pen(Color.Yellow, 2))
                {
                    e.Graphics.DrawRectangle(pen, boxX, boxY, boxWidth, boxHeight);
                }
            }
        }


        private void YzView_MouseWheel(object sender, MouseEventArgs e)
        {
            // Determine zoom factor for the YZ view.
            float factor = (e.Delta > 0) ? 1.1f : 0.9f;
            yzZoom = Math.Max(0.1f, Math.Min(5f, yzZoom * factor));

            // Invalidate the YZ view to update the projection.
            yzView.Invalidate();

            // Restart the render timer to schedule a full update.
            renderTimer.Stop();
            renderTimer.Start();
        }

        private void YzView_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (currentTool == SegmentationTool.Point)
                {
                    // Start tracking potential box drawing in YZ view
                    isYzBoxDrawingPossible = true;
                    yzBoxStartPoint = e.Location;
                    yzBoxCurrentPoint = e.Location;
                    isYzActuallyDrawingBox = false;
                    return;
                }

                if (currentTool == SegmentationTool.Brush)
                    PaintOrthoMaskAtYZ(e.Location, currentBrushSize);
                else if (currentTool == SegmentationTool.Eraser)
                    EraseOrthoMaskAtYZ(e.Location, currentBrushSize);
                yzView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();
                if (RealTimeProcessing)
                    ProcessSegmentationPreview();
            }
            else if (e.Button == MouseButtons.Left)
            {
                lastMousePosition = e.Location;
                renderTimer.Stop();
                renderTimer.Start();
            }
        }



        private void YzView_MouseMove(object sender, MouseEventArgs e)
        {
            // Handle YZ box drawing
            if (isYzBoxDrawingPossible && e.Button == MouseButtons.Right)
            {
                yzBoxCurrentPoint = e.Location;

                // If moved significantly, we're definitely drawing a box
                if (Math.Abs(yzBoxCurrentPoint.X - yzBoxStartPoint.X) > 5 ||
                    Math.Abs(yzBoxCurrentPoint.Y - yzBoxStartPoint.Y) > 5)
                {
                    isYzActuallyDrawingBox = true;
                    yzView.Invalidate(); // Update UI to show box
                }
            }
            // Existing code
            else if (e.Button == MouseButtons.None &&
                (currentTool == SegmentationTool.Brush || currentTool == SegmentationTool.Eraser))
            {
                yzOverlayCenter = e.Location;
                showBrushOverlay = true;
                yzView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (currentTool == SegmentationTool.Brush)
                    PaintOrthoMaskAtYZ(e.Location, currentBrushSize);
                else if (currentTool == SegmentationTool.Eraser)
                    EraseOrthoMaskAtYZ(e.Location, currentBrushSize);
                yzView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();
                if (RealTimeProcessing)
                    ProcessSegmentationPreview();
            }
            else if (e.Button == MouseButtons.Left)
            {
                int dx = e.Location.X - lastMousePosition.X;
                int dy = e.Location.Y - lastMousePosition.Y;
                yzPan = new PointF(yzPan.X + dx, yzPan.Y + dy);
                lastMousePosition = e.Location;
                yzView.Invalidate();
                renderTimer.Stop();
                renderTimer.Start();
            }
        }
        private void YzView_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && isYzBoxDrawingPossible)
            {
                if (isYzActuallyDrawingBox)
                {
                    // Create box annotation
                    float z1 = (yzBoxStartPoint.X - yzPan.X) / yzZoom;
                    float y1 = (yzBoxStartPoint.Y - yzPan.Y) / yzZoom;
                    float z2 = (yzBoxCurrentPoint.X - yzPan.X) / yzZoom;
                    float y2 = (yzBoxCurrentPoint.Y - yzPan.Y) / yzZoom;

                    // Clamp to image boundaries
                    z1 = Math.Max(0, Math.Min(z1, depth - 1));
                    y1 = Math.Max(0, Math.Min(y1, height - 1));
                    z2 = Math.Max(0, Math.Min(z2, depth - 1));
                    y2 = Math.Max(0, Math.Min(y2, height - 1));

                    Material selectedMaterial = (SelectedMaterialIndex >= 0 && SelectedMaterialIndex < Materials.Count)
                        ? Materials[SelectedMaterialIndex]
                        : new Material("Default", Color.Red, 0, 0, 1);

                    // For YZ view, the X coordinate is fixed at YzSliceX
                    AnnotationPoint box = AnnotationMgr.AddBox(YzSliceX, y1, YzSliceX, y2, (int)((z1 + z2) / 2), selectedMaterial.Name);
                    // Store Z values in the box's X2,Y2 coordinates
                    box.X2 = z1; // Z1 in X2
                    box.Y2 = z2; // Z2 in Y2
                    UpdateSAMFormWithBox(box);
                }
                else
                {
                    // Create point annotation
                    int z = (int)((yzBoxStartPoint.X - yzPan.X) / yzZoom);
                    int y = (int)((yzBoxStartPoint.Y - yzPan.Y) / yzZoom);

                    if (z >= 0 && z < depth && y >= 0 && y < height)
                    {
                        Material selectedMaterial = (SelectedMaterialIndex >= 0 && SelectedMaterialIndex < Materials.Count)
                            ? Materials[SelectedMaterialIndex]
                            : new Material("Default", Color.Red, 0, 0, 1);

                        AnnotationPoint point = AnnotationMgr.AddPoint(YzSliceX, y, z, selectedMaterial.Name);
                        UpdateSAMFormWithPoint(point);
                    }
                }

                isYzBoxDrawingPossible = false;
                isYzActuallyDrawingBox = false;
                yzView.Invalidate();

                renderTimer.Stop();
                renderTimer.Start();

                if (RealTimeProcessing)
                    ProcessSegmentationPreview();
            }
        }


        private void PaintOrthoMaskAtYZ(Point screenLocation, int brushSize)
        {
            if (currentSelectionYZ == null || currentSelectionYZ.GetLength(0) != depth || currentSelectionYZ.GetLength(1) != height)
                currentSelectionYZ = new byte[depth, height];

            int imageZ = (int)((screenLocation.X - yzPan.X) / yzZoom);
            int imageY = (int)((screenLocation.Y - yzPan.Y) / yzZoom);
            int radius = brushSize / 2;
            byte labelToSet = (byte)((SelectedMaterialIndex > 0) ? Materials[SelectedMaterialIndex].ID : 1);

            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = imageY + dy;
                if (y < 0 || y >= height)
                    continue;
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int z = imageZ + dz;
                    if (z < 0 || z >= depth)
                        continue;
                    if (dy * dy + dz * dz <= radius * radius)
                    {
                        currentSelectionYZ[z, y] = labelToSet;
                    }
                }
            }
        }

        private void EraseOrthoMaskAtYZ(Point screenLocation, int brushSize)
        {
            if (currentSelectionYZ == null || currentSelectionYZ.GetLength(0) != depth || currentSelectionYZ.GetLength(1) != height)
                currentSelectionYZ = new byte[depth, height];

            int imageZ = (int)((screenLocation.X - yzPan.X) / yzZoom);
            int imageY = (int)((screenLocation.Y - yzPan.Y) / yzZoom);
            int radius = brushSize / 2;

            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = imageY + dy;
                if (y < 0 || y >= height)
                    continue;
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int z = imageZ + dz;
                    if (z < 0 || z >= depth)
                        continue;
                    if (dy * dy + dz * dz <= radius * radius)
                    {
                        currentSelectionYZ[z, y] = 0;
                    }
                }
            }
        }
        public void ApplyOrthoSelections()
        {
            // For XZ view: commit selection to the slice at XzSliceY.
            if (currentSelectionXZ != null)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        byte sel = currentSelectionXZ[x, z];
                        if (sel != 0)
                            volumeLabels[x, XzSliceY, z] = sel;
                    }
                }

                // Store in sparseSelectionsY
                byte[,] copy = new byte[width, depth];
                Array.Copy(currentSelectionXZ, copy, width * depth);
                sparseSelectionsY[XzSliceY] = copy;

                currentSelectionXZ = null;
            }

            // For YZ view: commit selection to the slice at YzSliceX.
            if (currentSelectionYZ != null)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        byte sel = currentSelectionYZ[z, y];
                        if (sel != 0)
                            volumeLabels[YzSliceX, y, z] = sel;
                    }
                }

                // Store in sparseSelectionsX
                byte[,] copy = new byte[depth, height];
                Array.Copy(currentSelectionYZ, copy, depth * height);
                sparseSelectionsX[YzSliceX] = copy;

                currentSelectionYZ = null;
            }

            RenderViews();
            _ = RenderOrthoViewsAsync();
        }
        public void SubtractOrthoSelections()
        {
            // For XZ view:
            if (currentSelectionXZ != null)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        byte sel = currentSelectionXZ[x, z];
                        if (sel != 0 && volumeLabels[x, XzSliceY, z] == sel)
                            volumeLabels[x, XzSliceY, z] = 0;
                    }
                }
                currentSelectionXZ = null;
            }

            // For YZ view:
            if (currentSelectionYZ != null)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        byte sel = currentSelectionYZ[z, y];
                        if (sel != 0 && volumeLabels[YzSliceX, y, z] == sel)
                            volumeLabels[YzSliceX, y, z] = 0;
                    }
                }
                currentSelectionYZ = null;
            }
            RenderViews();
            _ = RenderOrthoViewsAsync();
        }

        public void SaveScreenshot()
        {
            // Compute composite dimensions.
            // mainView is at top-left, yzView at top-right, and xzView at bottom-left.
            int compWidth = mainView.Width + yzView.Width;
            int compHeight = mainView.Height + xzView.Height;

            using (Bitmap composite = new Bitmap(compWidth, compHeight))
            {
                using (Graphics g = Graphics.FromImage(composite))
                {
                    // Fill the background.
                    g.Clear(Color.Black);

                    // Draw the main view (XY view) at top-left.
                    using (Bitmap bmpMain = new Bitmap(mainView.Width, mainView.Height))
                    {
                        mainView.DrawToBitmap(bmpMain, new Rectangle(0, 0, mainView.Width, mainView.Height));
                        g.DrawImage(bmpMain, 0, 0);
                    }

                    // Draw the YZ view in the top-right cell.
                    using (Bitmap bmpYZ = new Bitmap(yzView.Width, yzView.Height))
                    {
                        yzView.DrawToBitmap(bmpYZ, new Rectangle(0, 0, yzView.Width, yzView.Height));
                        g.DrawImage(bmpYZ, mainView.Width, 0);
                    }

                    // Draw the XZ view in the bottom-left cell.
                    using (Bitmap bmpXZ = new Bitmap(xzView.Width, xzView.Height))
                    {
                        xzView.DrawToBitmap(bmpXZ, new Rectangle(0, 0, xzView.Width, xzView.Height));
                        g.DrawImage(bmpXZ, 0, mainView.Height);
                    }

                    // Draw separating dark grey lines.
                    using (Pen linePen = new Pen(Color.DarkGray, 3))
                    {
                        // Vertical line between mainView and yzView.
                        g.DrawLine(linePen, mainView.Width, 0, mainView.Width, compHeight);
                        // Horizontal line between mainView and xzView.
                        g.DrawLine(linePen, 0, mainView.Height, compWidth, mainView.Height);
                    }
                }

                // Open a SaveFileDialog to let the user choose file type and location.
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "JPEG Image|*.jpg|TIFF Image|*.tif|PNG Image|*.png";
                    sfd.Title = "Save Screenshot";
                    if (sfd.ShowDialog() == DialogResult.OK)
                    {
                        // Determine the chosen image format.
                        ImageFormat format = ImageFormat.Png;
                        string ext = Path.GetExtension(sfd.FileName).ToLower();
                        if (ext == ".jpg" || ext == ".jpeg")
                            format = ImageFormat.Jpeg;
                        else if (ext == ".tif" || ext == ".tiff")
                            format = ImageFormat.Tiff;

                        composite.Save(sfd.FileName, format);
                        Logger.Log($"[SaveScreenshot] Screenshot saved to {sfd.FileName}");
                    }
                }
            }
        }


        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            xzView?.Image?.Dispose();
            yzView?.Image?.Dispose();
            xzProjection?.Dispose();
            yzProjection?.Dispose();
            volumeData?.Dispose();
            volumeLabels?.Dispose();
        }

        #region Optional Prompt Helper
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
                Icon=this.Icon
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

                        // Adjust the pixel size based on binning.
                        double adjustedPixelSize = val * binFactor;
                        // Store the binning factor for later processing.
                        // For example, MainForm might have a property: public int SelectedBinningFactor { get; set; }

                        SelectedBinningFactor = binFactor; 


                        return adjustedPixelSize;
                    }
                }
            }
            return null;
        }
        #endregion
    

    /// <summary>
/// Returns a 24-bit Bitmap of the XY slice at index z.
/// (width = X dimension, height = Y dimension).
/// </summary>
public Bitmap GetSliceBitmap(int z)
        {
            // Create a new 24-bit Bitmap
            Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // If volumeData is the grayscale volume, read the voxel:
                    byte intensity = volumeData[x, y, z];
                    // Paint the same intensity in R,G,B => grayscale
                    bmp.SetPixel(x, y, Color.FromArgb(intensity, intensity, intensity));
                }
            }

            return bmp;
        }

        /// <summary>
        /// Returns a 24-bit Bitmap of the XZ slice at Y=fixedY.
        /// (width = X dimension, height = Z dimension).
        /// Typically used for "vertical" slices or 3D orthographic views.
        /// </summary>
        public Bitmap GetXZSliceBitmap(int fixedY)
        {
            // Dimensions: X => width, Z => depth
            // So the resulting image is width-by-depth
            Bitmap bmp = new Bitmap(width, depth, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

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

        /// <summary>
        /// Returns a 24-bit Bitmap of the YZ slice at X=fixedX.
        /// (width = Y dimension, height = Z dimension).
        /// </summary>
        public Bitmap GetYZSliceBitmap(int fixedX)
        {
            // Dimensions: Y => height, Z => depth
            // So the resulting image is height-by-depth
            Bitmap bmp = new Bitmap(height, depth, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    byte intensity = volumeData[fixedX, y, z];
                    bmp.SetPixel(y, z, Color.FromArgb(intensity, intensity, intensity));
                }
            }

            return bmp;
        }






    }
    #endregion
}