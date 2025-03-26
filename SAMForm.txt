using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTSegmenter
{
    public partial class SAMForm : Form
    {
        // --------------------------------------------------------------------------------
        // Internal classes and fields
        // --------------------------------------------------------------------------------

        // Per-material result container
        private class MaterialMaskResult
        {
            public string MaterialName;
            public bool IsMulti;
            public List<Bitmap> CandidateMasks; // multi
            public Bitmap SingleMask;           // single
            public int SelectedIndex;           // chosen candidate in multi mode
        }

        // We previously derived a threshold from thresholdingTrackbar. That is now removed.

        // We'll keep them in a list that toolStripButton1_Click populates
        private List<MaterialMaskResult> allMaterialsResults = new List<MaterialMaskResult>();

        private MainForm mainForm;
        private AnnotationManager annotationManager;

        private List<Bitmap> currentCandidates = null;
        private Bitmap singleCandidate = null;
        private int selectedCandidateIndex = 0;

        // Multi-direction results: direction -> material -> list of candidate masks
        private Dictionary<string, Dictionary<string, List<Bitmap>>> multiDirResults =
            new Dictionary<string, Dictionary<string, List<Bitmap>>>();

        // Direction -> material -> chosen candidate index
        private Dictionary<string, Dictionary<string, int>> candidateSelections = null;

        // Default settings (the slider was removed; threshold is handled in the model at 0.5)
        public SAMSettingsParams CurrentSettings { get; set; } = new SAMSettingsParams
        {
            FusionAlgorithm = "Majority Voting Fusion",
            ImageInputSize = 1024,
            ModelFolderPath = Application.StartupPath + "/ONNX/",
            EnableMultiMask = false
        };

        // --------------------------------------------------------------------------------
        // Constructor
        // --------------------------------------------------------------------------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!Logger.ShuttingDown)
            {
                // Clear all annotations
                annotationManager.Clear();
                // Set main form’s tool back to Pan
                if (mainForm != null)
                {
                    mainForm.SetSegmentationTool(SegmentationTool.Pan);
                    mainForm.RenderViews();
                    _ = mainForm.RenderOrthoViewsAsync();
                }
                e.Cancel = true;
                this.Dispose();
            }
            else
            {
                base.OnFormClosing(e);
            }
        }

        public SAMForm(MainForm mainForm, List<Material> materials)
        {
            Logger.Log("[SAM] Constructor start");

            InitializeComponent();

            this.mainForm = mainForm;
            this.annotationManager = mainForm.AnnotationMgr;

            dataGridPoints.KeyDown += DataGridPoints_KeyDown;

            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "favicon.ico");
                if (File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SAMForm] Error setting icon: " + ex.Message);
            }

            // Removed the trackbar and threshold label code here.

            // Populate the DataGridView’s combo box for "Label" with materials
            var labelColumn = dataGridPoints.Columns["Label"] as DataGridViewComboBoxColumn;
            if (labelColumn != null)
            {
                labelColumn.Items.Clear();
                foreach (var material in materials)
                {
                    labelColumn.Items.Add(material.Name);
                }
            }

            // Handle data errors so we don’t throw an exception if user types something invalid
            dataGridPoints.DataError += DataGridPoints_DataError;

            // Commit combo edits on change
            dataGridPoints.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (dataGridPoints.IsCurrentCellDirty)
                    dataGridPoints.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            Logger.Log("[SAM] Constructor end");
        }

        // --------------------------------------------------------------------------------
        // DataGridView / Annotations
        // --------------------------------------------------------------------------------

        private void DataGridPoints_DataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            Logger.Log($"[DataGridPoints_DataError] Column: {dataGridPoints.Columns[e.ColumnIndex].Name}, Error: {e.Exception.Message}");
            e.ThrowException = false;
        }

        public void UpdateSettings(SAMSettingsParams settings)
        {
            if (settings != null)
            {
                CurrentSettings = settings;
                Logger.Log("[SAMForm] Settings updated: " +
                           $"FusionAlgorithm={settings.FusionAlgorithm}, " +
                           $"ImageInputSize={settings.ImageInputSize}, " +
                           $"ModelFolderPath={settings.ModelFolderPath}");
                // If you need to re-init anything, do it here.
            }
        }

        public DataGridView GetPointsDataGridView()
        {
            return dataGridPoints;
        }

        private void dataGridPoints_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            // Typically no special code here unless you want to handle button columns, etc.
        }

        private void DataGridPoints_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;

            if (dataGridPoints.Columns[e.ColumnIndex].Name == "Label")
            {
                // Retrieve the changed row’s ID
                if (!int.TryParse(dataGridPoints.Rows[e.RowIndex].Cells[0].Value.ToString(), out int pointID))
                    return;

                // New label from the cell
                string newLabel = dataGridPoints.Rows[e.RowIndex].Cells["Label"].Value?.ToString();
                if (!string.IsNullOrEmpty(newLabel))
                {
                    // Possibly add new label to the combo box if missing
                    var labelColumn = dataGridPoints.Columns["Label"] as DataGridViewComboBoxColumn;
                    if (labelColumn != null && !labelColumn.Items.Contains(newLabel))
                    {
                        labelColumn.Items.Add(newLabel);
                        Logger.Log($"[DataGridPoints_CellValueChanged] Added new label '{newLabel}' to ComboBox items.");
                    }

                    // Find the corresponding annotation point
                    var point = annotationManager.Points.FirstOrDefault(p => p.ID == pointID);
                    if (point != null)
                    {
                        point.Label = newLabel;
                        // Re-render main view with new label
                        mainForm.RenderViews();
                        _ = mainForm.RenderOrthoViewsAsync();
                    }
                }
            }
        }

        public void UpdateMaterialComboBox(List<Material> materials)
        {
            var labelColumn = dataGridPoints.Columns["Label"] as DataGridViewComboBoxColumn;
            if (labelColumn != null)
            {
                labelColumn.Items.Clear();
                foreach (var mat in materials)
                {
                    labelColumn.Items.Add(mat.Name);
                }
            }
        }

        private void DataGridPoints_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelectedPoints();
                e.Handled = true;
            }
        }

        private void RemoveSelectedPoints()
        {
            var rowsToDelete = dataGridPoints.SelectedRows
                .Cast<DataGridViewRow>()
                .Where(r => !r.IsNewRow)
                .OrderByDescending(r => r.Index)
                .ToList();

            foreach (var row in rowsToDelete)
            {
                if (row.Cells[0].Value is int pointID)
                {
                    annotationManager.RemovePoint(pointID);
                }
                dataGridPoints.Rows.Remove(row);
            }

            RebindDataGrid();
            mainForm.RenderViews();
            _ = mainForm.RenderOrthoViewsAsync();
        }

        private void RebindDataGrid()
        {
            dataGridPoints.Rows.Clear();

            // Re-populate from annotationManager
            foreach (var p in annotationManager.Points)
            {
                dataGridPoints.Rows.Add(
                    p.ID,
                    p.X,
                    p.Y,
                    p.Z,
                    p.Type,
                    p.Label
                );
            }
        }

        // --------------------------------------------------------------------------------
        // Existing Buttons / Methods
        // --------------------------------------------------------------------------------

        /// <summary>
        /// The main “start segmentation” button. Called toolStripButton1_Click in your code.
        /// We keep the entire method exactly, but references to trackbar are removed.
        /// </summary>
        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            try
            {
                // 1) Ensure model folder path is valid
                if (string.IsNullOrEmpty(CurrentSettings.ModelFolderPath))
                {
                    Logger.Log("[SAMForm] ERROR: Model folder path is null or empty");
                    string defaultPath = Path.Combine(Application.StartupPath, "ONNX");
                    if (Directory.Exists(defaultPath))
                    {
                        Logger.Log($"[SAMForm] Using default model folder path: {defaultPath}");
                        CurrentSettings.ModelFolderPath = defaultPath;
                    }
                    else
                    {
                        MessageBox.Show("Model folder path is not set. Please configure it.",
                                       "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        if (!VerifyModelPaths())
                        {
                            return; // cannot proceed
                        }
                    }
                }

                // 2) Check for SAM 2.1 model files (or older SAM if needed)
                string encoderPath = Path.Combine(CurrentSettings.ModelFolderPath, "sam2.1_large.encoder.onnx");
                string decoderPath = Path.Combine(CurrentSettings.ModelFolderPath, "sam2.1_large.decoder.onnx");

                if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
                {
                    MessageBox.Show(
                        $"Required model files not found in:\n{CurrentSettings.ModelFolderPath}\n\nPlease check your model folder path in SAM Settings.",
                        "Missing Model Files",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                Logger.Log("[SAMForm] Starting segmentation process for all directions");
                Cursor = Cursors.WaitCursor;

                // 3) Check multi-candidate vs single-candidate
                bool multiCandidate = CurrentSettings.EnableMultiMask;
                Logger.Log($"[SAMForm] Multi-candidate mode: {multiCandidate}");

                // Clear old data
                multiDirResults.Clear();
                multiDirResults["XY"] = new Dictionary<string, List<Bitmap>>();
                multiDirResults["XZ"] = new Dictionary<string, List<Bitmap>>();
                multiDirResults["YZ"] = new Dictionary<string, List<Bitmap>>();
                allMaterialsResults.Clear();

                // 4) Distinguish older SAM from SAM 2.1 (still possible if your code allows)
                string modelFolder = CurrentSettings.ModelFolderPath;
                int imageSize = CurrentSettings.ImageInputSize;
                bool usingSam2 = CurrentSettings.UseSam2Models; // if your code has that option

                string imageEncoderPath, promptEncoderPath, maskDecoderPath;
                string memoryEncoderPath, memoryAttentionPath, mlpPath;

                if (usingSam2)
                {
                    // 2.1
                    imageEncoderPath = Path.Combine(modelFolder, "sam2.1_large.encoder.onnx");
                    promptEncoderPath = "";
                    maskDecoderPath = Path.Combine(modelFolder, "sam2.1_large.decoder.onnx");
                    memoryEncoderPath = "";
                    memoryAttentionPath = "";
                    mlpPath = "";

                    if (!File.Exists(imageEncoderPath) || !File.Exists(maskDecoderPath))
                    {
                        string mm = "";
                        if (!File.Exists(imageEncoderPath)) mm += $"- SAM 2.1 encoder: {imageEncoderPath}\n";
                        if (!File.Exists(maskDecoderPath)) mm += $"- SAM 2.1 decoder: {maskDecoderPath}\n";

                        MessageBox.Show($"SAM 2.1 models not found:\n{mm}", "Models Missing",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Cursor = Cursors.Default;
                        return;
                    }
                }
                else
                {
                    // Original SAM
                    imageEncoderPath = Path.Combine(modelFolder, "image_encoder_hiera_t.onnx");
                    promptEncoderPath = Path.Combine(modelFolder, "prompt_encoder_hiera_t.onnx");
                    maskDecoderPath = Path.Combine(modelFolder, "mask_decoder_hiera_t.onnx");
                    memoryEncoderPath = Path.Combine(modelFolder, "memory_encoder_hiera_t.onnx");
                    memoryAttentionPath = Path.Combine(modelFolder, "memory_attention_hiera_t.onnx");
                    mlpPath = Path.Combine(modelFolder, "mlp_hiera_t.onnx");

                    if (!File.Exists(imageEncoderPath) || !File.Exists(promptEncoderPath) || !File.Exists(maskDecoderPath))
                    {
                        string mm = "";
                        if (!File.Exists(imageEncoderPath)) mm += $"- Image encoder: {imageEncoderPath}\n";
                        if (!File.Exists(promptEncoderPath)) mm += $"- Prompt encoder: {promptEncoderPath}\n";
                        if (!File.Exists(maskDecoderPath)) mm += $"- Mask decoder: {maskDecoderPath}\n";

                        MessageBox.Show($"Original SAM models not found:\n{mm}", "Models Missing",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Cursor = Cursors.Default;
                        return;
                    }
                }

                // 5) Create folder for saving masks if desired
                string saveFolder = Path.Combine(Application.StartupPath, "SavedMasks");
                Directory.CreateDirectory(saveFolder);

                // 6) Identify which directions are toggled
                bool processXY = XYButton.Checked;
                bool processXZ = XZButton.Checked;
                bool processYZ = YZButton.Checked;

                // 7) Retrieve volume info from mainForm
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();
                int currentZ = mainForm.CurrentSlice;
                int fixedY = mainForm.XzSliceY;
                int fixedX = mainForm.YzSliceX;

                Logger.Log($"[SAMForm] Volume: {width}x{height}x{depth}");

                // 8) Construct the segmenter
                using (var segmenter = new CTMemorySegmenter(
                    imageEncoderPath,
                    promptEncoderPath,
                    maskDecoderPath,
                    memoryEncoderPath,
                    memoryAttentionPath,
                    mlpPath,
                    imageSize,
                    false,
                    CurrentSettings.EnableMlp,
                    CurrentSettings.UseCpuExecutionProvider))
                {
                    // SAM2.1 uses ~0.5 threshold internally, so no trackbar-based threshold
                    segmenter.StorePreviousEmbeddings = true;
                    segmenter.UseSelectiveHoleFilling = CurrentSettings.UseSelectiveHoleFilling;

                    // --------------------------
                    // *** XY direction ***
                    // --------------------------
                    if (processXY)
                    {
                        Logger.Log($"[SAMForm] Processing XY slice at Z={currentZ}");

                        using (Bitmap baseXY = GenerateXYBitmap(currentZ, width, height))
                        using (Bitmap accumMask = new Bitmap(width, height))
                        {
                            // accumMask for negative prompts
                            using (Graphics gAcc = Graphics.FromImage(accumMask))
                                gAcc.Clear(Color.Black);

                            // Gather annotation points in this XY slice
                            var slicePoints = annotationManager.GetPointsForSlice(currentZ);
                            var uniqueMats = slicePoints
                                .Select(p => p.Label)
                                .Where(lbl => !lbl.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                                .Distinct()
                                .ToList();

                            Logger.Log($"[SAMForm] XY: Found {uniqueMats.Count} materials to process");

                            foreach (string matName in uniqueMats)
                            {
                                Logger.Log($"[SAMForm] XY: Processing material '{matName}'");
                                List<AnnotationPoint> prompts = BuildMixedPrompts(slicePoints, matName);

                                // Negative prompts from accumMask
                                var negatives = SampleNegativePointsFromMask(accumMask, 40);
                                foreach (var (nx, ny) in negatives)
                                {
                                    prompts.Add(new AnnotationPoint { X = nx, Y = ny, Z = currentZ, Label = "Exterior" });
                                }

                                if (multiCandidate)
                                {
                                    // Multi-candidate approach
                                    List<Bitmap> candidates = segmenter
                                        .ProcessXYSlice_GetAllMasks(currentZ, baseXY, prompts, matName);
                                    multiDirResults["XY"][matName] = candidates;

                                    // Merge first candidate into accumMask
                                    if (candidates.Count > 0)
                                    {
                                        Bitmap c0 = candidates[0];
                                        for (int yy = 0; yy < height; yy++)
                                        {
                                            for (int xx = 0; xx < width; xx++)
                                            {
                                                if (c0.GetPixel(xx, yy).R > 128)
                                                    accumMask.SetPixel(xx, yy, Color.White);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    // Single-candidate approach
                                    Bitmap singleMask = segmenter
                                        .ProcessXYSlice(currentZ, baseXY, prompts, matName);
                                    MaterialMaskResult res = new MaterialMaskResult
                                    {
                                        MaterialName = matName,
                                        IsMulti = false,
                                        SingleMask = singleMask,
                                        SelectedIndex = 0
                                    };
                                    allMaterialsResults.Add(res);
                                    multiDirResults["XY"][matName] = new List<Bitmap> { singleMask };

                                    // Merge single mask
                                    for (int yy = 0; yy < height; yy++)
                                    {
                                        for (int xx = 0; xx < width; xx++)
                                        {
                                            if (singleMask.GetPixel(xx, yy).R > 128)
                                                accumMask.SetPixel(xx, yy, Color.White);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // --------------------------
                    // *** XZ direction ***
                    // --------------------------
                    if (processXZ)
                    {
                        Logger.Log($"[SAMForm] Processing XZ slice at Y={fixedY}");

                        using (Bitmap baseXZ = GenerateXZBitmap(fixedY, width, depth))
                        using (Bitmap accumMask = new Bitmap(width, depth))
                        {
                            using (Graphics gAcc = Graphics.FromImage(accumMask))
                                gAcc.Clear(Color.Black);

                            // Gather annotation points in this XZ slice
                            var slicePoints = annotationManager.Points
                                .Where(p => Math.Abs(p.Y - fixedY) < 1.0f).ToList();
                            var uniqueMats = slicePoints
                                .Select(p => p.Label)
                                .Where(lbl => !lbl.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                                .Distinct()
                                .ToList();

                            Logger.Log($"[SAMForm] XZ: Found {uniqueMats.Count} materials to process");

                            foreach (string matName in uniqueMats)
                            {
                                Logger.Log($"[SAMForm] XZ: Processing material '{matName}'");
                                List<AnnotationPoint> prompts = BuildMixedPrompts(slicePoints, matName);

                                var negatives = SampleNegativePointsFromMask(accumMask, 40);
                                foreach (var (nx, nz) in negatives)
                                {
                                    prompts.Add(new AnnotationPoint { X = nx, Y = fixedY, Z = nz, Label = "Exterior" });
                                }

                                if (multiCandidate)
                                {
                                    List<Bitmap> candidates = segmenter
                                        .ProcessXZSlice_GetAllMasks(fixedY, baseXZ, prompts, matName);
                                    multiDirResults["XZ"][matName] = candidates;

                                    // Merge first candidate
                                    if (candidates.Count > 0)
                                    {
                                        Bitmap c0 = candidates[0];
                                        for (int zz = 0; zz < depth && zz < c0.Height; zz++)
                                        {
                                            for (int xx = 0; xx < width && xx < c0.Width; xx++)
                                            {
                                                if (c0.GetPixel(xx, zz).R > 128)
                                                    accumMask.SetPixel(xx, zz, Color.White);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    Bitmap singleMask = segmenter
                                        .ProcessXZSlice(fixedY, baseXZ, prompts, matName);
                                    MaterialMaskResult res = new MaterialMaskResult
                                    {
                                        MaterialName = matName,
                                        IsMulti = false,
                                        SingleMask = singleMask,
                                        SelectedIndex = 0
                                    };
                                    allMaterialsResults.Add(res);
                                    multiDirResults["XZ"][matName] = new List<Bitmap> { singleMask };

                                    // Merge single mask
                                    for (int zz = 0; zz < depth && zz < singleMask.Height; zz++)
                                    {
                                        for (int xx = 0; xx < width && xx < singleMask.Width; xx++)
                                        {
                                            if (singleMask.GetPixel(xx, zz).R > 128)
                                                accumMask.SetPixel(xx, zz, Color.White);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // --------------------------
                    // *** YZ direction ***
                    // --------------------------
                    if (processYZ)
                    {
                        Logger.Log($"[SAMForm] Processing YZ slice at X={fixedX}");

                        using (Bitmap baseYZ = GenerateYZBitmap(fixedX, height, depth))
                        using (Bitmap accumMask = new Bitmap(depth, height))
                        {
                            using (Graphics gAcc = Graphics.FromImage(accumMask))
                                gAcc.Clear(Color.Black);

                            // Gather annotation points in this YZ slice
                            var slicePoints = annotationManager.Points
                                .Where(p => Math.Abs(p.X - fixedX) < 1.0f).ToList();
                            var uniqueMats = slicePoints
                                .Select(p => p.Label)
                                .Where(lbl => !lbl.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                                .Distinct()
                                .ToList();

                            Logger.Log($"[SAMForm] YZ: Found {uniqueMats.Count} materials to process");

                            foreach (string matName in uniqueMats)
                            {
                                Logger.Log($"[SAMForm] YZ: Processing material '{matName}'");
                                List<AnnotationPoint> prompts = BuildMixedPrompts(slicePoints, matName);

                                var negatives = SampleNegativePointsFromMask(accumMask, 40);
                                foreach (var (nz, ny) in negatives)
                                {
                                    prompts.Add(new AnnotationPoint { X = fixedX, Y = ny, Z = nz, Label = "Exterior" });
                                }

                                if (multiCandidate)
                                {
                                    List<Bitmap> candidates = segmenter
                                        .ProcessYZSlice_GetAllMasks(fixedX, baseYZ, prompts, matName);
                                    multiDirResults["YZ"][matName] = candidates;

                                    // Merge first candidate
                                    if (candidates.Count > 0)
                                    {
                                        Bitmap c0 = candidates[0];
                                        for (int yy = 0; yy < height && yy < c0.Height; yy++)
                                        {
                                            for (int zz = 0; zz < depth && zz < c0.Width; zz++)
                                            {
                                                if (c0.GetPixel(zz, yy).R > 128)
                                                    accumMask.SetPixel(zz, yy, Color.White);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    Bitmap singleMask = segmenter
                                        .ProcessYZSlice(fixedX, baseYZ, prompts, matName);
                                    MaterialMaskResult res = new MaterialMaskResult
                                    {
                                        MaterialName = matName,
                                        IsMulti = false,
                                        SingleMask = singleMask,
                                        SelectedIndex = 0
                                    };
                                    allMaterialsResults.Add(res);
                                    multiDirResults["YZ"][matName] = new List<Bitmap> { singleMask };

                                    // Merge single mask
                                    for (int yy = 0; yy < height && yy < singleMask.Height; yy++)
                                    {
                                        for (int zz = 0; zz < depth && zz < singleMask.Width; zz++)
                                        {
                                            if (singleMask.GetPixel(zz, yy).R > 128)
                                                accumMask.SetPixel(zz, yy, Color.White);
                                        }
                                    }
                                }
                            }
                        }
                    }
                } // end using CTMemorySegmenter

                // 9) If multi-candidate, open candidate selector
                if (multiCandidate)
                {
                    bool hasMasks = multiDirResults.Values.Any(dir => dir.Count > 0);
                    if (!hasMasks)
                    {
                        MessageBox.Show("No segmentation masks were generated. Ensure you have annotation points for at least one material.",
                            "No Masks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Cursor = Cursors.Default;
                        return;
                    }

                    CandidateSelectorForm csf = new CandidateSelectorForm(multiDirResults, this);
                    if (csf.ShowDialog() == DialogResult.OK)
                    {
                        candidateSelections = csf.Selections;
                        // Build MaterialMaskResult for each chosen mask
                        foreach (string direction in csf.Selections.Keys)
                        {
                            foreach (string material in csf.Selections[direction].Keys)
                            {
                                if (multiDirResults.ContainsKey(direction) &&
                                    multiDirResults[direction].ContainsKey(material))
                                {
                                    var cands = multiDirResults[direction][material];
                                    int idxSel = csf.Selections[direction][material];
                                    if (idxSel < cands.Count)
                                    {
                                        MaterialMaskResult mmr = new MaterialMaskResult
                                        {
                                            MaterialName = material,
                                            IsMulti = true,
                                            CandidateMasks = cands,
                                            SelectedIndex = idxSel
                                        };
                                        allMaterialsResults.Add(mmr);
                                    }
                                }
                            }
                        }

                        // Possibly preview the chosen masks for the active direction
                        string activeDir = GetActiveDirection();
                        if (activeDir != null && csf.SelectedMasks.ContainsKey(activeDir))
                        {
                            var masksToShow = csf.SelectedMasks[activeDir];
                            if (masksToShow.Count > 0)
                            {
                                PreviewSelectedMasks(activeDir, masksToShow);
                                Logger.Log($"[SAMForm] Previewing {masksToShow.Count} selected masks in {activeDir} view");
                            }
                        }
                    }
                }
                else
                {
                    // Single-candidate
                    if (allMaterialsResults.Count == 0)
                    {
                        MessageBox.Show("No segmentation masks were generated. Ensure you have annotation points.",
                            "No Masks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Cursor = Cursors.Default;
                        return;
                    }

                    // Directly preview in the active direction
                    string activeDir = GetActiveDirection();
                    if (activeDir != null)
                    {
                        Dictionary<string, Bitmap> toShow = new Dictionary<string, Bitmap>();
                        foreach (var r in allMaterialsResults)
                        {
                            if (!r.IsMulti && r.SingleMask != null)
                            {
                                toShow[r.MaterialName] = r.SingleMask;
                            }
                        }
                        if (toShow.Count > 0)
                        {
                            PreviewSelectedMasks(activeDir, toShow);
                            Logger.Log($"[SAMForm] Single-candidate mode: Previewing {toShow.Count} masks in {activeDir} view");
                        }
                    }
                }

                Logger.Log("[SAMForm] All directions done. Now handling multiCandidate UI...");
                Cursor = Cursors.Default;
                MessageBox.Show("Segmentation completed. Review masks in the main view and click Apply to confirm.",
                    "Segmentation", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                Logger.Log("[SAMForm] Error in segmentation process: " + ex.Message);
                Logger.Log("[SAMForm] Stack trace: " + ex.StackTrace);
                MessageBox.Show("Error applying segmentation: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        private bool VerifyModelPaths()
        {
            if (string.IsNullOrEmpty(CurrentSettings.ModelFolderPath) ||
                !Directory.Exists(CurrentSettings.ModelFolderPath))
            {
                DialogResult result = MessageBox.Show(
                    "Model folder path is not set or invalid. Would you like to locate the ONNX folder now?",
                    "Configuration Required",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    using (var fbd = new FolderBrowserDialog())
                    {
                        fbd.Description = "Select the folder containing ONNX model files";
                        fbd.ShowNewFolderButton = false;
                        if (fbd.ShowDialog() == DialogResult.OK)
                        {
                            string selectedPath = fbd.SelectedPath;
                            bool hasSAM2 = File.Exists(Path.Combine(selectedPath, "sam2.1_large.encoder.onnx")) &&
                                           File.Exists(Path.Combine(selectedPath, "sam2.1_large.decoder.onnx"));

                            if (hasSAM2)
                            {
                                CurrentSettings.ModelFolderPath = selectedPath;
                                return true;
                            }
                            else
                            {
                                MessageBox.Show(
                                    "The selected folder does not contain the required SAM 2.1 model files.",
                                    "Invalid Folder",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Error);
                                return false;
                            }
                        }
                    }
                }
                return false;
            }
            return true;
        }

        // Button that triggers the 3D segmentation propagation (toolStripButton2 in your code)
        private void toolStripButton2_Click(object sender, EventArgs e)
        {
            try
            {
                Logger.Log("[SAMForm] Starting 3D segmentation propagation");

                if (SelectedDirection == SegmentationDirection.None)
                {
                    MessageBox.Show("Please select at least one direction (XY, XZ, or YZ) for propagation.",
                                    "No Direction Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                bool usingSam2 = CurrentSettings.UseSam2Models;
                string modelFolder = CurrentSettings.ModelFolderPath;

                if (usingSam2)
                {
                    string ep = Path.Combine(modelFolder, "sam2.1_large.encoder.onnx");
                    string dp = Path.Combine(modelFolder, "sam2.1_large.decoder.onnx");
                    if (!File.Exists(ep) || !File.Exists(dp))
                    {
                        string mm = "";
                        if (!File.Exists(ep)) mm += $"- SAM 2.1 encoder: {ep}\n";
                        if (!File.Exists(dp)) mm += $"- SAM 2.1 decoder: {dp}\n";
                        MessageBox.Show($"SAM 2.1 models missing:\n{mm}", "Models Missing",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                else
                {
                    string iE = Path.Combine(modelFolder, "image_encoder_hiera_t.onnx");
                    string pE = Path.Combine(modelFolder, "prompt_encoder_hiera_t.onnx");
                    string mD = Path.Combine(modelFolder, "mask_decoder_hiera_t.onnx");
                    if (!File.Exists(iE) || !File.Exists(pE) || !File.Exists(mD))
                    {
                        string mm = "";
                        if (!File.Exists(iE)) mm += $"- Image encoder: {iE}\n";
                        if (!File.Exists(pE)) mm += $"- Prompt encoder: {pE}\n";
                        if (!File.Exists(mD)) mm += $"- Mask decoder: {mD}\n";
                        MessageBox.Show($"Original SAM models missing:\n{mm}", "Models Missing",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                // The actual 3D propagation
                byte[,,] result = SegmentationPropagator.Propagate(
            mainForm,
            CurrentSettings,
            128, // dummy threshold
            SelectedDirection
        );

                if (result != null)
                {
                    CopyResultsToVolumeLabels(result);

                    mainForm.ClearSliceCache();
                    mainForm.RenderViews();
                    _ = mainForm.RenderOrthoViewsAsync();

                    Logger.Log("[SAMForm] 3D propagation completed successfully");
                    MessageBox.Show("3D propagation completed successfully",
                                    "Propagation",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("No segmentation could be propagated. Ensure at least one slice is segmented.",
                                    "Propagation Failed",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SAMForm] Error during 3D propagation: " + ex.Message);
                Logger.Log("[SAMForm] Stack trace: " + ex.StackTrace);
                MessageBox.Show("Error during 3D propagation: " + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // The “Apply” button for applying 2D masks to volume data
        private void btnApply_Click(object sender, EventArgs e)
        {
            try
            {
                Logger.Log("[btnApply_Click] Starting to apply selected masks");

                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();

                int currentZ = mainForm.CurrentSlice;
                int fixedY = mainForm.XzSliceY;
                int fixedX = mainForm.YzSliceX;

                int totalPixelsModified = 0;
                int materialsApplied = 0;

                // If user has chosen from the CandidateSelectorForm
                if (candidateSelections != null)
                {
                    // XY
                    if (XYButton.Checked && multiDirResults.ContainsKey("XY")
                        && candidateSelections.ContainsKey("XY"))
                    {
                        foreach (var matName in candidateSelections["XY"].Keys)
                        {
                            var mat = mainForm.Materials
                                .FirstOrDefault(m => m.Name.Equals(matName, StringComparison.OrdinalIgnoreCase));
                            if (mat == null)
                            {
                                Logger.Log($"[btnApply] Warning: Material '{matName}' not found; skipping");
                                continue;
                            }
                            int idxSel = candidateSelections["XY"][matName];
                            if (multiDirResults["XY"].ContainsKey(matName) &&
                                multiDirResults["XY"][matName].Count > idxSel)
                            {
                                Bitmap mask = multiDirResults["XY"][matName][idxSel];
                                int changed = 0;
                                for (int y = 0; y < height && y < mask.Height; y++)
                                {
                                    for (int x = 0; x < width && x < mask.Width; x++)
                                    {
                                        if (mask.GetPixel(x, y).R > 128 && mainForm.volumeLabels[x, y, currentZ] == 0)
                                        {
                                            mainForm.volumeLabels[x, y, currentZ] = mat.ID;
                                            changed++;
                                        }
                                    }
                                }
                                totalPixelsModified += changed;
                                materialsApplied++;
                                Logger.Log($"[btnApply] Applied XY mask for {matName}, changed {changed} px");
                            }
                        }
                    }

                    // XZ
                    if (XZButton.Checked && multiDirResults.ContainsKey("XZ")
                        && candidateSelections.ContainsKey("XZ"))
                    {
                        foreach (var matName in candidateSelections["XZ"].Keys)
                        {
                            var mat = mainForm.Materials
                                .FirstOrDefault(m => m.Name.Equals(matName, StringComparison.OrdinalIgnoreCase));
                            if (mat == null)
                            {
                                Logger.Log($"[btnApply] Material '{matName}' not found; skipping");
                                continue;
                            }
                            int idxSel = candidateSelections["XZ"][matName];
                            if (multiDirResults["XZ"].ContainsKey(matName) &&
                                multiDirResults["XZ"][matName].Count > idxSel)
                            {
                                Bitmap mask = multiDirResults["XZ"][matName][idxSel];
                                int changed = 0;
                                for (int z = 0; z < depth && z < mask.Height; z++)
                                {
                                    for (int x = 0; x < width && x < mask.Width; x++)
                                    {
                                        if (mask.GetPixel(x, z).R > 128 && mainForm.volumeLabels[x, fixedY, z] == 0)
                                        {
                                            mainForm.volumeLabels[x, fixedY, z] = mat.ID;
                                            changed++;
                                        }
                                    }
                                }
                                totalPixelsModified += changed;
                                materialsApplied++;
                                Logger.Log($"[btnApply] Applied XZ mask for {matName}, changed {changed} px");
                            }
                        }
                    }

                    // YZ
                    if (YZButton.Checked && multiDirResults.ContainsKey("YZ")
                        && candidateSelections.ContainsKey("YZ"))
                    {
                        foreach (var matName in candidateSelections["YZ"].Keys)
                        {
                            var mat = mainForm.Materials
                                .FirstOrDefault(m => m.Name.Equals(matName, StringComparison.OrdinalIgnoreCase));
                            if (mat == null)
                            {
                                Logger.Log($"[btnApply] Material '{matName}' not found; skipping");
                                continue;
                            }
                            int idxSel = candidateSelections["YZ"][matName];
                            if (multiDirResults["YZ"].ContainsKey(matName) &&
                                multiDirResults["YZ"][matName].Count > idxSel)
                            {
                                Bitmap mask = multiDirResults["YZ"][matName][idxSel];
                                int changed = 0;
                                for (int y = 0; y < height && y < mask.Height; y++)
                                {
                                    for (int z = 0; z < depth && z < mask.Width; z++)
                                    {
                                        if (mask.GetPixel(z, y).R > 128 && mainForm.volumeLabels[fixedX, y, z] == 0)
                                        {
                                            mainForm.volumeLabels[fixedX, y, z] = mat.ID;
                                            changed++;
                                        }
                                    }
                                }
                                totalPixelsModified += changed;
                                materialsApplied++;
                                Logger.Log($"[btnApply] Applied YZ mask for {matName}, changed {changed} px");
                            }
                        }
                    }
                }

                mainForm.ClearSliceCache();
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();

                MessageBox.Show($"Applied {materialsApplied} materials, modified {totalPixelsModified} pixels",
                    "Segmentation Applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Log("[SAMForm] Error applying segmentation: " + ex.Message);
                Logger.Log("[SAMForm] Stack trace: " + ex.StackTrace);
                MessageBox.Show("Error applying segmentation: " + ex.Message,
                                "Error",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
            }
        }

        // The “Open Settings” button (toolStripButton3 in your code)
        private void toolStripButton3_Click(object sender, EventArgs e)
        {
            using (SAMSettings settingsForm = new SAMSettings(this))
            {
                if (settingsForm.ShowDialog() == DialogResult.OK)
                {
                    this.CurrentSettings = settingsForm.SettingsResult;
                }
            }
        }

        // Direction toggles: XYButton, XZButton, YZButton
        [Flags]
        public enum SegmentationDirection
        {
            None = 0,
            XY = 1,
            XZ = 2,
            YZ = 4
        }
        public SegmentationDirection SelectedDirection { get; set; } = SegmentationDirection.XY;

        private void XYButton_Click(object sender, EventArgs e)
        {
            CheckBox cb = sender as CheckBox;
            if (cb == null) return;
            if (cb.Checked) SelectedDirection |= SegmentationDirection.XY;
            else SelectedDirection &= ~SegmentationDirection.XY;
            if (SelectedDirection == SegmentationDirection.None)
                SelectedDirection = SegmentationDirection.XY;
        }
        /// <summary>
        /// Called by CandidateSelectorForm when the user clicks "Preview in Main View."
        /// We pick the currently active direction (XY, XZ, or YZ) and preview those masks.
        /// </summary>
        public void PreviewCandidatesInMainView(Dictionary<string, Dictionary<string, Bitmap>> selectedMasks)
        {
            // Use your existing GetActiveDirection() method
            string activeDir = GetActiveDirection();
            if (string.IsNullOrEmpty(activeDir))
            {
                Logger.Log("[PreviewCandidatesInMainView] No active direction selected; cannot preview.");
                return;
            }

            if (!selectedMasks.ContainsKey(activeDir) || selectedMasks[activeDir].Count == 0)
            {
                Logger.Log($"[PreviewCandidatesInMainView] No masks found for direction '{activeDir}'.");
                return;
            }

            // "selectedMasks[activeDir]" is the dictionary: materialName -> chosen mask
            var masksToShow = selectedMasks[activeDir];

            Logger.Log($"[PreviewCandidatesInMainView] Previewing {masksToShow.Count} chosen masks in direction={activeDir}");
            PreviewSelectedMasks(activeDir, masksToShow);
        }

        private void XZButton_Click(object sender, EventArgs e)
        {
            CheckBox cb = sender as CheckBox;
            if (cb == null) return;
            if (cb.Checked) SelectedDirection |= SegmentationDirection.XZ;
            else SelectedDirection &= ~SegmentationDirection.XZ;
            if (SelectedDirection == SegmentationDirection.None)
                SelectedDirection = SegmentationDirection.XZ;
        }

        private void YZButton_Click(object sender, EventArgs e)
        {
            CheckBox cb = sender as CheckBox;
            if (cb == null) return;
            if (cb.Checked) SelectedDirection |= SegmentationDirection.YZ;
            else SelectedDirection &= ~SegmentationDirection.YZ;
            if (SelectedDirection == SegmentationDirection.None)
                SelectedDirection = SegmentationDirection.YZ;
        }

        // --------------------------------------------------------------------------------
        // Preview Helpers
        // --------------------------------------------------------------------------------

        private string GetActiveDirection()
        {
            if (XYButton.Checked) return "XY";
            if (XZButton.Checked) return "XZ";
            if (YZButton.Checked) return "YZ";
            return null;
        }

        public void PreviewSelectedMasks(string direction, Dictionary<string, Bitmap> selectedMasks)
        {
            try
            {
                Logger.Log($"[SAMForm] Previewing masks for {direction} direction");

                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();
                int currentZ = mainForm.CurrentSlice;
                int fixedY = mainForm.XzSliceY;
                int fixedX = mainForm.YzSliceX;

                if (direction == "XY")
                {
                    if (mainForm.currentSelection == null ||
                        mainForm.currentSelection.GetLength(0) != width ||
                        mainForm.currentSelection.GetLength(1) != height)
                    {
                        mainForm.currentSelection = new byte[width, height];
                    }
                    else
                    {
                        Array.Clear(mainForm.currentSelection, 0, width * height);
                    }
                }
                else if (direction == "XZ")
                {
                    if (mainForm.currentSelectionXZ == null ||
                        mainForm.currentSelectionXZ.GetLength(0) != width ||
                        mainForm.currentSelectionXZ.GetLength(1) != depth)
                    {
                        mainForm.currentSelectionXZ = new byte[width, depth];
                    }
                    else
                    {
                        Array.Clear(mainForm.currentSelectionXZ, 0, width * depth);
                    }
                }
                else if (direction == "YZ")
                {
                    if (mainForm.currentSelectionYZ == null ||
                        mainForm.currentSelectionYZ.GetLength(0) != depth ||
                        mainForm.currentSelectionYZ.GetLength(1) != height)
                    {
                        mainForm.currentSelectionYZ = new byte[depth, height];
                    }
                    else
                    {
                        Array.Clear(mainForm.currentSelectionYZ, 0, depth * height);
                    }
                }

                foreach (var kv in selectedMasks)
                {
                    string matName = kv.Key;
                    Bitmap mask = kv.Value;

                    Material mat = mainForm.Materials
                        .FirstOrDefault(m => m.Name.Equals(matName, StringComparison.OrdinalIgnoreCase));
                    if (mat == null)
                    {
                        Logger.Log($"[PreviewSelectedMasks] Material '{matName}' not found; skipping");
                        continue;
                    }

                    byte matID = mat.ID;

                    if (direction == "XY")
                    {
                        for (int y = 0; y < Math.Min(height, mask.Height); y++)
                        {
                            for (int x = 0; x < Math.Min(width, mask.Width); x++)
                            {
                                if (mask.GetPixel(x, y).R > 128)
                                    mainForm.currentSelection[x, y] = matID;
                            }
                        }
                    }
                    else if (direction == "XZ")
                    {
                        for (int z = 0; z < Math.Min(depth, mask.Height); z++)
                        {
                            for (int x = 0; x < Math.Min(width, mask.Width); x++)
                            {
                                if (mask.GetPixel(x, z).R > 128)
                                    mainForm.currentSelectionXZ[x, z] = matID;
                            }
                        }
                    }
                    else if (direction == "YZ")
                    {
                        for (int y = 0; y < Math.Min(height, mask.Height); y++)
                        {
                            for (int z = 0; z < Math.Min(depth, mask.Width); z++)
                            {
                                if (mask.GetPixel(z, y).R > 128)
                                    mainForm.currentSelectionYZ[z, y] = matID;
                            }
                        }
                    }
                }

                mainForm.ClearSliceCache();
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();

                Logger.Log("[PreviewSelectedMasks] Preview masks applied to temporary overlay");
            }
            catch (Exception ex)
            {
                Logger.Log("[SAMForm] Error in preview masks: " + ex.Message);
                Logger.Log("[SAMForm] Stack trace: " + ex.StackTrace);
                MessageBox.Show("Error displaying preview: " + ex.Message,
                    "Preview Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // --------------------------------------------------------------------------------
        // Building prompts, negative sampling, chunk copying
        // --------------------------------------------------------------------------------

        private List<AnnotationPoint> BuildMixedPrompts(IEnumerable<AnnotationPoint> slicePoints, string targetMat)
        {
            if (slicePoints == null) return new List<AnnotationPoint>();
            var pts = slicePoints.ToList();

            Logger.Log($"[BuildMixedPrompts] Building prompts for '{targetMat}' from {pts.Count} points");

            List<AnnotationPoint> result = new List<AnnotationPoint>();
            foreach (var p in pts)
            {
                if (string.IsNullOrEmpty(p.Label)) continue;

                AnnotationPoint newPt = new AnnotationPoint
                {
                    ID = p.ID,
                    X = p.X,
                    Y = p.Y,
                    Z = p.Z,
                    Type = p.Type
                };

                if (p.Label.Equals(targetMat, StringComparison.OrdinalIgnoreCase))
                {
                    newPt.Label = "Foreground";
                }
                else
                {
                    newPt.Label = "Exterior";
                }

                result.Add(newPt);
            }

            int posCount = result.Count(pp => pp.Label == "Foreground");
            int negCount = result.Count(pp => pp.Label == "Exterior");
            Logger.Log($"[BuildMixedPrompts] -> {result.Count} total, {posCount} pos, {negCount} neg");
            return result;
        }

        private List<(int X, int Y)> SampleNegativePointsFromMask(Bitmap mask, int count)
        {
            List<(int X, int Y)> wPixels = new List<(int X, int Y)>();
            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    if (mask.GetPixel(x, y).R > 128)
                        wPixels.Add((x, y));
                }
            }
            if (wPixels.Count == 0) return new List<(int, int)>();

            Random rnd = new Random();
            if (wPixels.Count <= count) return wPixels;

            // shuffle
            for (int i = wPixels.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                var temp = wPixels[i];
                wPixels[i] = wPixels[j];
                wPixels[j] = temp;
            }
            return wPixels.Take(count).ToList();
        }

        private void CopyResultsToVolumeLabels(byte[,,] results)
        {
            int w = mainForm.GetWidth();
            int h = mainForm.GetHeight();
            int d = mainForm.GetDepth();

            Logger.Log("[SAMForm] Copying 3D propagation results to volumeLabels...");

            for (int z = 0; z < d; z++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        mainForm.volumeLabels[x, y, z] = results[x, y, z];
                    }
                }
            }
            Logger.Log("[SAMForm] Done copying 3D results");
        }

        // --------------------------------------------------------------------------------
        // Generating base slices for XY/XZ/YZ
        // --------------------------------------------------------------------------------

        private Bitmap GenerateXYBitmap(int sliceIndex, int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte val = mainForm.volumeData[x, y, sliceIndex];
                    bmp.SetPixel(x, y, Color.FromArgb(val, val, val));
                }
            }
            return bmp;
        }

        private Bitmap GenerateXZBitmap(int fixedY, int width, int depth)
        {
            Bitmap bmp = new Bitmap(width, depth, PixelFormat.Format24bppRgb);
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte val = mainForm.volumeData[x, fixedY, z];
                    bmp.SetPixel(x, z, Color.FromArgb(val, val, val));
                }
            }
            return bmp;
        }

        private Bitmap GenerateYZBitmap(int fixedX, int height, int depth)
        {
            Bitmap bmp = new Bitmap(depth, height, PixelFormat.Format24bppRgb);
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    byte val = mainForm.volumeData[fixedX, y, z];
                    bmp.SetPixel(z, y, Color.FromArgb(val, val, val));
                }
            }
            return bmp;
        }

       
    }
}
