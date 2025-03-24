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
        // Store results per material
        private class MaterialMaskResult
        {
            public string MaterialName;
            public bool IsMulti;
            public List<Bitmap> CandidateMasks; // multi
            public Bitmap SingleMask;           // single
            public int SelectedIndex;           // which candidate user picks in multi mode
        }
        public byte MaskThreshold => (byte)thresholdingTrackbar.Value;
        // We'll keep them in a list that toolStripButton1_Click populates
        private List<MaterialMaskResult> allMaterialsResults = new List<MaterialMaskResult>();
        private MainForm mainForm;
        private AnnotationManager annotationManager;
        private List<Bitmap> currentCandidates = null; // store multiple masks if multi is on
        private Bitmap singleCandidate = null;         // store single mask if multi is off
        private int selectedCandidateIndex = 0;        // user picks among multi
        private Dictionary<string, Dictionary<string, List<Bitmap>>> multiDirResults = new Dictionary<string, Dictionary<string, List<Bitmap>>>();
        private Dictionary<string, Dictionary<string, int>> candidateSelections = null;

        public SAMSettingsParams CurrentSettings { get; set; } = new SAMSettingsParams
        {
            FusionAlgorithm = "Majority Voting Fusion",
            ImageInputSize = 1024,
            ModelFolderPath = Application.StartupPath+"/ONNX/",
            EnableMultiMask=false
        };
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!Logger.ShuttingDown)
            {
                // Clear all annotations
                annotationManager.Clear();
                // Set the main form’s tool back to Pan
                if (mainForm != null)
                {
                    
                    mainForm.SetSegmentationTool(SegmentationTool.Pan);
                    mainForm.RenderViews(); // refresh to clear point overlays
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
                else
                {
                    // Optionally log that the icon file wasn't found.
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SAMForm] Error: Cannot find 'favicon.ico'. "+ex.Message);
            }
            
           
            // Initialize the threshold trackbar
            thresholdingTrackbar.Minimum = 0;
            thresholdingTrackbar.Maximum = 255;
            thresholdingTrackbar.Value = 128; // Default threshold value
            lblThr.Text = $"Threshold: {thresholdingTrackbar.Value}";
            // Set up the DataGridView ComboBox for material labels.
            var labelColumn = dataGridPoints.Columns["Label"] as DataGridViewComboBoxColumn;
            if (labelColumn != null)
            {
                // Clear and repopulate items from the current materials list.
                labelColumn.Items.Clear();
                foreach (var material in materials)
                {
                    labelColumn.Items.Add(material.Name);
                }
            }
            // Subscribe to the DataError event to prevent the cell error from showing.
            dataGridPoints.DataError += DataGridPoints_DataError;

            // Ensure that when the cell value changes, we commit the edit.
            dataGridPoints.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (dataGridPoints.IsCurrentCellDirty)
                    dataGridPoints.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            Logger.Log("[SAM] Constructor end");
        }
        /// <summary>
        /// Handles DataGridView errors (such as invalid ComboBox cell values) to prevent runtime exceptions.
        /// </summary>
        private void DataGridPoints_DataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            // Log the error if needed:
            Logger.Log($"[DataGridPoints_DataError] Column: {dataGridPoints.Columns[e.ColumnIndex].Name}, Error: {e.Exception.Message}");
            // Prevent the exception from being thrown.
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
                // Optionally, refresh or reinitialize any components that depend on these settings.
            }
        }


        public DataGridView GetPointsDataGridView()
        {
            return this.dataGridPoints;
        }

        private void dataGridPoints_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {

        }
        /// <summary>
        /// Handles cell value changes for the DataGridView. When a cell in the "Label" column is updated,
        /// the corresponding AnnotationPoint’s Label property is updated, and the main view is re-rendered.
        /// </summary>
        private void DataGridPoints_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0)
                return;

            // Check if the changed column is "Label"
            if (dataGridPoints.Columns[e.ColumnIndex].Name == "Label")
            {
                // Retrieve the point ID from the first column.
                int pointID;
                if (!int.TryParse(dataGridPoints.Rows[e.RowIndex].Cells[0].Value.ToString(), out pointID))
                    return;

                // Retrieve the new label from the cell's value.
                string newLabel = dataGridPoints.Rows[e.RowIndex].Cells["Label"].Value?.ToString();
                if (!string.IsNullOrEmpty(newLabel))
                {
                    // Ensure the new label exists in the ComboBox's items.
                    var labelColumn = dataGridPoints.Columns["Label"] as DataGridViewComboBoxColumn;
                    if (labelColumn != null && !labelColumn.Items.Contains(newLabel))
                    {
                        // Option 1: add the new label to the ComboBox items.
                        labelColumn.Items.Add(newLabel);
                        Logger.Log($"[DataGridPoints_CellValueChanged] Added new label '{newLabel}' to ComboBox items.");
                    }

                    // Find the corresponding annotation point and update its Label.
                    var point = annotationManager.Points.FirstOrDefault(p => p.ID == pointID);
                    if (point != null)
                    {
                        point.Label = newLabel;
                        // Refresh the main view so the new label appears.
                        mainForm.RenderViews();
                        _ = mainForm.RenderOrthoViewsAsync();
                    }
                }
            }
        }
        /// <summary>
        /// Updates the DataGridViewComboBoxColumn for material labels with the provided list.
        /// This method should be called whenever a new material is added or removed.
        /// </summary>
        public void UpdateMaterialComboBox(List<Material> materials)
        {
            var labelColumn = dataGridPoints.Columns["Label"] as DataGridViewComboBoxColumn;
            if (labelColumn != null)
            {
                labelColumn.Items.Clear();
                foreach (var material in materials)
                {
                    labelColumn.Items.Add(material.Name);
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
        /// <summary>
        /// Removes all the currently selected rows from the DataGridView
        /// and the AnnotationManager, updates IDs, and rebinds the grid.
        /// </summary>
        private void RemoveSelectedPoints()
        {
            // Collect selected rows that are not the "new row".
            var rowsToDelete = dataGridPoints.SelectedRows
                .Cast<DataGridViewRow>()
                .Where(r => !r.IsNewRow)
                .OrderByDescending(r => r.Index)
                .ToList();

            // Remove each selected annotation from the AnnotationManager.
            // AnnotationManager.RemovePoint() internally calls UpdatePointIDs().
            foreach (var row in rowsToDelete)
            {
                if (row.Cells[0].Value is int pointID)
                {
                    annotationManager.RemovePoint(pointID);
                }
                // Also remove the row from the grid's UI.
                dataGridPoints.Rows.Remove(row);
            }

            // Now rebuild the DataGridView so the IDs and rows match the updated list.
            RebindDataGrid();

            // Force a redraw in MainForm to ensure the annotations disappear.
            mainForm.RenderViews();
            _ = mainForm.RenderOrthoViewsAsync();
        }

        /// <summary>
        /// Clears all rows in the DataGridView and repopulates from annotationManager.Points.
        /// </summary>
        private void RebindDataGrid()
        {
            dataGridPoints.Rows.Clear();

            // After RemovePoint(...), the manager has already updated IDs.
            // Just fill the rows with the latest data.
            foreach (var point in annotationManager.Points)
            {
                dataGridPoints.Rows.Add(
                    point.ID,
                    point.X,
                    point.Y,
                    point.Z,
                    point.Type,
                    point.Label
                );
            }
        }
        // New helper method to delete selected rows and update IDs.
        /*private void DeleteSelectedRowsAndUpdateIDs()
        {
            // Collect selected rows that are not new rows.
            List<DataGridViewRow> rowsToDelete = dataGridPoints.SelectedRows
                .Cast<DataGridViewRow>()
                .Where(r => !r.IsNewRow)
                .OrderByDescending(r => r.Index)
                .ToList();

            // Remove the selected rows from the annotation manager and DataGridView.
            foreach (var row in rowsToDelete)
            {
                int currentID = Convert.ToInt32(row.Cells[0].Value);
                var point = annotationManager.Points.FirstOrDefault(p => p.ID == currentID);
                if (point != null)
                {
                    annotationManager.Points.Remove(point);
                }
                dataGridPoints.Rows.Remove(row);
            }

            // Clear the DataGridView completely to rebuild it with correct IDs
            dataGridPoints.Rows.Clear();

            // Reassign IDs sequentially starting at 1.
            int newID = 1;
            var sortedPoints = annotationManager.Points.OrderBy(p => p.ID).ToList();

            // First update all points with new IDs
            foreach (var point in sortedPoints)
            {
                point.ID = newID;
                newID++;
            }

            // Now rebuild the DataGridView with the updated points
            foreach (var point in sortedPoints)
            {
                dataGridPoints.Rows.Add(point.ID, point.X, point.Y, point.Z, point.Type, point.Label);
            }

            // Force a redraw of the main views to update the annotations
            mainForm.RenderViews();
            _ = mainForm.RenderOrthoViewsAsync();
        }*/

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
                    using (var folderDialog = new FolderBrowserDialog())
                    {
                        folderDialog.Description = "Select the folder containing ONNX model files";
                        folderDialog.ShowNewFolderButton = false;

                        if (folderDialog.ShowDialog() == DialogResult.OK)
                        {
                            string selectedPath = folderDialog.SelectedPath;

                            // Check if the selected folder contains the model files
                            bool hasSAM2Models = File.Exists(Path.Combine(selectedPath, "sam2.1_large.encoder.onnx")) &&
                                                 File.Exists(Path.Combine(selectedPath, "sam2.1_large.decoder.onnx"));

                            if (hasSAM2Models)
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

        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            
            try
            {

                if (string.IsNullOrEmpty(CurrentSettings.ModelFolderPath))
                {
                    Logger.Log("[SAMForm] ERROR: Model folder path is null or empty");

                    // Try to find a default path
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
                            // Exit the method if model paths couldn't be set
                            return;
                        }
                        
                    }
                }

                // Now check that the model files exist
                string encoderPath = Path.Combine(CurrentSettings.ModelFolderPath, "sam2.1_large.encoder.onnx");
                string decoderPath = Path.Combine(CurrentSettings.ModelFolderPath, "sam2.1_large.decoder.onnx");

                if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
                {
                    MessageBox.Show($"Required model files not found in:\n{CurrentSettings.ModelFolderPath}\n\nPlease check the model folder path in SAM Settings.",
                                   "Missing Model Files", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Logger.Log("[SAMForm] Starting segmentation process for all directions");
                Cursor = Cursors.WaitCursor;

                // Check if multi-candidate mode is enabled
                bool multiCandidate = CurrentSettings.EnableMultiMask;
                Logger.Log($"[SAMForm] Multi-candidate mode: {multiCandidate}");

                // Clear previous results
                multiDirResults.Clear();
                multiDirResults["XY"] = new Dictionary<string, List<Bitmap>>();
                multiDirResults["XZ"] = new Dictionary<string, List<Bitmap>>();
                multiDirResults["YZ"] = new Dictionary<string, List<Bitmap>>();

                // Clear previous material results
                allMaterialsResults.Clear();

                // Set up model paths based on which SAM version is being used
                string modelFolder = CurrentSettings.ModelFolderPath;
                int imageSize = CurrentSettings.ImageInputSize;
                bool usingSam2 = CurrentSettings.UseSam2Models;
                Logger.Log($"[SAMForm] Using SAM 2.1 models: {usingSam2}");

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

                    // Validate SAM 2.1 models
                    if (!File.Exists(imageEncoderPath) || !File.Exists(maskDecoderPath))
                    {
                        string missingModels = "";
                        if (!File.Exists(imageEncoderPath)) missingModels += $"- SAM 2.1 encoder: {imageEncoderPath}\n";
                        if (!File.Exists(maskDecoderPath)) missingModels += $"- SAM 2.1 decoder: {maskDecoderPath}\n";

                        MessageBox.Show($"SAM 2.1 models not found:\n{missingModels}\nPlease check your model folder settings.",
                            "Models Missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Cursor = Cursors.Default;
                        return;
                    }
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

                    // Validate original SAM models
                    if (!File.Exists(imageEncoderPath) || !File.Exists(promptEncoderPath) || !File.Exists(maskDecoderPath))
                    {
                        string missingModels = "";
                        if (!File.Exists(imageEncoderPath)) missingModels += $"- Image encoder: {imageEncoderPath}\n";
                        if (!File.Exists(promptEncoderPath)) missingModels += $"- Prompt encoder: {promptEncoderPath}\n";
                        if (!File.Exists(maskDecoderPath)) missingModels += $"- Mask decoder: {maskDecoderPath}\n";

                        MessageBox.Show($"Original SAM models not found:\n{missingModels}\nPlease check your model folder settings.",
                            "Models Missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Cursor = Cursors.Default;
                        return;
                    }
                }

                string saveFolder = Path.Combine(Application.StartupPath, "SavedMasks");
                Directory.CreateDirectory(saveFolder);

                // Determine which directions are active
                bool processXY = XYButton.Checked;
                bool processXZ = XZButton.Checked;
                bool processYZ = YZButton.Checked;

                // Get volume dimensions
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();
                int currentZ = mainForm.CurrentSlice;
                int fixedY = mainForm.XzSliceY;
                int fixedX = mainForm.YzSliceX;
                Logger.Log($"[SAMForm] Volume: {width}x{height}x{depth}");

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
                    segmenter.UseSelectiveHoleFilling = CurrentSettings.UseSelectiveHoleFilling;
                    segmenter.MaskThreshold = thresholdingTrackbar.Value;
                    segmenter.StorePreviousEmbeddings = true; // Enable embedding storage for propagation

                    // ----- Process XY Direction -----
                    if (processXY)
                    {
                        Logger.Log($"[SAMForm] Processing XY slice at Z={currentZ}");
                        using (Bitmap baseXY = GenerateXYBitmap(currentZ, width, height))
                        using (Bitmap accumMask = new Bitmap(width, height))
                        {
                            using (Graphics gAcc = Graphics.FromImage(accumMask))
                                gAcc.Clear(Color.Black);

                            var slicePoints = annotationManager.GetPointsForSlice(currentZ);
                            var uniqueMats = slicePoints
                                .Select(p => p.Label)
                                .Where(lbl => !lbl.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                                .Distinct().ToList();

                            Logger.Log($"[SAMForm] XY: Found {uniqueMats.Count} materials to process");

                            foreach (string matName in uniqueMats)
                            {
                                Logger.Log($"[SAMForm] XY: Processing material '{matName}'");

                                // Build prompts from points in this slice
                                List<AnnotationPoint> prompts = BuildMixedPrompts(slicePoints, matName);

                                // Add negative points sampled from accumMask so that already segmented areas are excluded
                                var negatives = SampleNegativePointsFromMask(accumMask, 40);
                                foreach (var (nx, ny) in negatives)
                                {
                                    prompts.Add(new AnnotationPoint { X = nx, Y = ny, Z = currentZ, Label = "Exterior" });
                                }

                                if (multiCandidate)
                                {
                                    // Multi-candidate mode
                                    List<Bitmap> candidates = segmenter.ProcessXYSlice_GetAllMasks(currentZ, baseXY, prompts, null, null);
                                    multiDirResults["XY"][matName] = candidates;

                                    // Merge first candidate (index 0) into accumMask for exclusion in subsequent passes
                                    if (candidates.Count > 0)
                                    {
                                        Bitmap msk = candidates[0];
                                        for (int y1 = 0; y1 < height; y1++)
                                        {
                                            for (int x1 = 0; x1 < width; x1++)
                                            {
                                                if (msk.GetPixel(x1, y1).R > 128)
                                                    accumMask.SetPixel(x1, y1, Color.White);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    // Single mask mode: call the single-candidate method and wrap it in a list.
                                    Bitmap singleMask = segmenter.ProcessXYSlice(currentZ, baseXY, prompts, null, null);

                                    // Create a material result for this single mask
                                    MaterialMaskResult result = new MaterialMaskResult
                                    {
                                        MaterialName = matName,
                                        IsMulti = false,
                                        SingleMask = singleMask,
                                        SelectedIndex = 0
                                    };

                                    allMaterialsResults.Add(result);

                                    // Also store in multiDirResults for consistency
                                    multiDirResults["XY"][matName] = new List<Bitmap> { singleMask };

                                    // Merge the single mask into accumMask for exclusion in subsequent passes
                                    for (int y1 = 0; y1 < height; y1++)
                                    {
                                        for (int x1 = 0; x1 < width; x1++)
                                        {
                                            if (singleMask.GetPixel(x1, y1).R > 128)
                                                accumMask.SetPixel(x1, y1, Color.White);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // ----- Process XZ Direction -----
                    if (processXZ)
                    {
                        Logger.Log($"[SAMForm] Processing XZ slice at Y={fixedY}");
                        using (Bitmap baseXZ = GenerateXZBitmap(fixedY, width, depth))
                        using (Bitmap accumMask = new Bitmap(width, depth))
                        {
                            using (Graphics gAcc = Graphics.FromImage(accumMask))
                                gAcc.Clear(Color.Black);

                            var slicePoints = annotationManager.Points.Where(p => Math.Abs(p.Y - fixedY) < 1.0f).ToList();
                            var uniqueMats = slicePoints
                                .Select(p => p.Label)
                                .Where(lbl => !lbl.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                                .Distinct().ToList();

                            Logger.Log($"[SAMForm] XZ: Found {uniqueMats.Count} materials to process");

                            foreach (string matName in uniqueMats)
                            {
                                Logger.Log($"[SAMForm] XZ: Processing material '{matName}'");

                                // Build prompts from points in this slice
                                List<AnnotationPoint> prompts = BuildMixedPrompts(slicePoints, matName);

                                // Add negative points from accumMask
                                var negatives = SampleNegativePointsFromMask(accumMask, 40);
                                foreach (var (nx, nz) in negatives)
                                {
                                    prompts.Add(new AnnotationPoint { X = nx, Y = fixedY, Z = nz, Label = "Exterior" });
                                }

                                if (multiCandidate)
                                {
                                    // For XZ we'll generate candidates even though the original code didn't
                                    List<Bitmap> candidates = new List<Bitmap>();

                                    // Generate multiple masks with different thresholds
                                    byte[] thresholds = new byte[] {
                                (byte)(MaskThreshold * 0.7), MaskThreshold,
                                (byte)(MaskThreshold * 1.3), (byte)(MaskThreshold * 1.5)
                            };

                                    foreach (byte threshold in thresholds)
                                    {
                                        // Save original threshold
                                        byte originalThreshold = (byte)segmenter.MaskThreshold;

                                        // Set temporary threshold
                                        segmenter.MaskThreshold = threshold;

                                        // Generate mask with this threshold
                                        Bitmap singleMask = segmenter.ProcessXZSlice(fixedY, baseXZ, prompts, null, null);
                                        candidates.Add(singleMask);

                                        // Restore original threshold
                                        segmenter.MaskThreshold = originalThreshold;
                                    }

                                    multiDirResults["XZ"][matName] = candidates;

                                    // Merge first candidate into accumMask
                                    if (candidates.Count > 0)
                                    {
                                        Bitmap msk = candidates[0];
                                        for (int z1 = 0; z1 < depth; z1++)
                                        {
                                            for (int x1 = 0; x1 < width; x1++)
                                            {
                                                if (x1 < msk.Width && z1 < msk.Height &&
                                                    msk.GetPixel(x1, z1).R > 128)
                                                    accumMask.SetPixel(x1, z1, Color.White);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    // Single mask mode
                                    Bitmap singleMask = segmenter.ProcessXZSlice(fixedY, baseXZ, prompts, null, null);

                                    // Create a material result for this single mask
                                    MaterialMaskResult result = new MaterialMaskResult
                                    {
                                        MaterialName = matName,
                                        IsMulti = false,
                                        SingleMask = singleMask,
                                        SelectedIndex = 0
                                    };

                                    allMaterialsResults.Add(result);

                                    // Also store in multiDirResults for consistency
                                    multiDirResults["XZ"][matName] = new List<Bitmap> { singleMask };

                                    // Merge the single mask into accumMask for exclusion in subsequent passes
                                    for (int z1 = 0; z1 < depth; z1++)
                                    {
                                        for (int x1 = 0; x1 < width; x1++)
                                        {
                                            if (x1 < singleMask.Width && z1 < singleMask.Height &&
                                                singleMask.GetPixel(x1, z1).R > 128)
                                                accumMask.SetPixel(x1, z1, Color.White);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // ----- Process YZ Direction -----
                    if (processYZ)
                    {
                        Logger.Log($"[SAMForm] Processing YZ slice at X={fixedX}");
                        using (Bitmap baseYZ = GenerateYZBitmap(fixedX, height, depth))
                        using (Bitmap accumMask = new Bitmap(depth, height))
                        {
                            using (Graphics gAcc = Graphics.FromImage(accumMask))
                                gAcc.Clear(Color.Black);

                            var slicePoints = annotationManager.Points.Where(p => Math.Abs(p.X - fixedX) < 1.0f).ToList();
                            var uniqueMats = slicePoints
                                .Select(p => p.Label)
                                .Where(lbl => !lbl.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                                .Distinct().ToList();

                            Logger.Log($"[SAMForm] YZ: Found {uniqueMats.Count} materials to process");

                            foreach (string matName in uniqueMats)
                            {
                                Logger.Log($"[SAMForm] YZ: Processing material '{matName}'");

                                // Build prompts from points in this slice
                                List<AnnotationPoint> prompts = BuildMixedPrompts(slicePoints, matName);

                                // Add negative points from accumMask
                                var negatives = SampleNegativePointsFromMask(accumMask, 40);
                                foreach (var (nz, ny) in negatives)
                                {
                                    prompts.Add(new AnnotationPoint { X = fixedX, Y = ny, Z = nz, Label = "Exterior" });
                                }

                                if (multiCandidate)
                                {
                                    // For YZ we'll also generate candidates
                                    List<Bitmap> candidates = new List<Bitmap>();

                                    // Generate multiple masks with different thresholds
                                    byte[] thresholds = new byte[] {
                                (byte)(MaskThreshold * 0.7), MaskThreshold,
                                (byte)(MaskThreshold * 1.3), (byte)(MaskThreshold * 1.5)
                            };

                                    foreach (byte threshold in thresholds)
                                    {
                                        // Save original threshold
                                        byte originalThreshold = (byte)segmenter.MaskThreshold;

                                        // Set temporary threshold
                                        segmenter.MaskThreshold = threshold;

                                        // Generate mask with this threshold
                                        Bitmap singleMask = segmenter.ProcessYZSlice(fixedX, baseYZ, prompts, null, null);
                                        candidates.Add(singleMask);

                                        // Restore original threshold
                                        segmenter.MaskThreshold = originalThreshold;
                                    }

                                    multiDirResults["YZ"][matName] = candidates;

                                    // Merge first candidate into accumMask
                                    if (candidates.Count > 0)
                                    {
                                        Bitmap msk = candidates[0];
                                        for (int y1 = 0; y1 < height; y1++)
                                        {
                                            for (int z1 = 0; z1 < depth; z1++)
                                            {
                                                if (z1 < msk.Width && y1 < msk.Height &&
                                                    msk.GetPixel(z1, y1).R > 128)
                                                    accumMask.SetPixel(z1, y1, Color.White);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    // Single mask mode
                                    Bitmap singleMask = segmenter.ProcessYZSlice(fixedX, baseYZ, prompts, null, null);

                                    // Create a material result for this single mask
                                    MaterialMaskResult result = new MaterialMaskResult
                                    {
                                        MaterialName = matName,
                                        IsMulti = false,
                                        SingleMask = singleMask,
                                        SelectedIndex = 0
                                    };

                                    allMaterialsResults.Add(result);

                                    // Also store in multiDirResults for consistency
                                    multiDirResults["YZ"][matName] = new List<Bitmap> { singleMask };

                                    // Merge the single mask into accumMask for exclusion in subsequent passes
                                    for (int y1 = 0; y1 < height; y1++)
                                    {
                                        for (int z1 = 0; z1 < depth; z1++)
                                        {
                                            if (z1 < singleMask.Width && y1 < singleMask.Height &&
                                                singleMask.GetPixel(z1, y1).R > 128)
                                                accumMask.SetPixel(z1, y1, Color.White);
                                        }
                                    }
                                }
                            }
                        }
                    }

                } // end using segmenter

                // If multi-candidate mode is enabled, open the candidate selector form
                if (multiCandidate)
                {
                    // Check if we have any masks to show
                    bool hasMasks = multiDirResults.Values.Any(dir => dir.Count > 0);

                    if (!hasMasks)
                    {
                        MessageBox.Show("No segmentation masks were generated. Please ensure you have placed annotation points for at least one material.",
                            "No Masks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Cursor = Cursors.Default;
                        return;
                    }

                    CandidateSelectorForm csf = new CandidateSelectorForm(multiDirResults, this);
                    if (csf.ShowDialog() == DialogResult.OK)
                    {
                        candidateSelections = csf.Selections;

                        // Process selected masks for each direction and material
                        foreach (string direction in csf.Selections.Keys)
                        {
                            foreach (string material in csf.Selections[direction].Keys)
                            {
                                if (multiDirResults.ContainsKey(direction) &&
                                    multiDirResults[direction].ContainsKey(material))
                                {
                                    var candidates = multiDirResults[direction][material];
                                    int selectedIdx = csf.Selections[direction][material];

                                    if (selectedIdx < candidates.Count)
                                    {
                                        // Create a material result object
                                        MaterialMaskResult result = new MaterialMaskResult
                                        {
                                            MaterialName = material,
                                            IsMulti = true,
                                            CandidateMasks = candidates,
                                            SelectedIndex = selectedIdx
                                        };

                                        allMaterialsResults.Add(result);
                                    }
                                }
                            }
                        }

                        // Preview the selected masks
                        string activeDirection = GetActiveDirection();
                        if (activeDirection != null && csf.SelectedMasks.ContainsKey(activeDirection))
                        {
                            Dictionary<string, Bitmap> masksToShow = csf.SelectedMasks[activeDirection];
                            if (masksToShow.Count > 0)
                            {
                                PreviewSelectedMasks(activeDirection, masksToShow);
                                Logger.Log($"[SAMForm] Previewing {masksToShow.Count} selected masks in {activeDirection} view");
                            }
                        }
                    }
                }
                else
                {
                    // In single-candidate mode, preview is already set up during processing

                    // Check if we got any results
                    if (allMaterialsResults.Count == 0)
                    {
                        MessageBox.Show("No segmentation masks were generated. Please ensure you have placed annotation points for at least one material.",
                            "No Masks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Cursor = Cursors.Default;
                        return;
                    }

                    // Preview the single masks in the active direction
                    string activeDirection = GetActiveDirection();
                    if (activeDirection != null)
                    {
                        Dictionary<string, Bitmap> masksToShow = new Dictionary<string, Bitmap>();

                        foreach (var result in allMaterialsResults)
                        {
                            if (!result.IsMulti && result.SingleMask != null)
                            {
                                masksToShow[result.MaterialName] = result.SingleMask;
                            }
                        }

                        if (masksToShow.Count > 0)
                        {
                            PreviewSelectedMasks(activeDirection, masksToShow);
                            Logger.Log($"[SAMForm] Single-candidate mode: Previewing {masksToShow.Count} masks in {activeDirection} view");
                        }
                    }
                }

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


        // Helper method to get active direction
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

                // Initialize temporary selections based on direction
                if (direction == "XY")
                {
                    // Create or clear the temporary selection for XY direction
                    if (mainForm.currentSelection == null ||
                        mainForm.currentSelection.GetLength(0) != width ||
                        mainForm.currentSelection.GetLength(1) != height)
                    {
                        mainForm.currentSelection = new byte[width, height];
                    }
                    else
                    {
                        // Clear existing selection
                        Array.Clear(mainForm.currentSelection, 0, width * height);
                    }
                }
                else if (direction == "XZ")
                {
                    // Create or clear the temporary selection for XZ direction
                    if (mainForm.currentSelectionXZ == null ||
                        mainForm.currentSelectionXZ.GetLength(0) != width ||
                        mainForm.currentSelectionXZ.GetLength(1) != depth)
                    {
                        mainForm.currentSelectionXZ = new byte[width, depth];
                    }
                    else
                    {
                        // Clear existing selection
                        Array.Clear(mainForm.currentSelectionXZ, 0, width * depth);
                    }
                }
                else if (direction == "YZ")
                {
                    // Create or clear the temporary selection for YZ direction
                    if (mainForm.currentSelectionYZ == null ||
                        mainForm.currentSelectionYZ.GetLength(0) != depth ||
                        mainForm.currentSelectionYZ.GetLength(1) != height)
                    {
                        mainForm.currentSelectionYZ = new byte[depth, height];
                    }
                    else
                    {
                        // Clear existing selection
                        Array.Clear(mainForm.currentSelectionYZ, 0, depth * height);
                    }
                }

                // Process masks for the appropriate direction
                foreach (var entry in selectedMasks)
                {
                    string materialName = entry.Key;
                    Bitmap mask = entry.Value;

                    // Find material ID
                    Material mat = mainForm.Materials.FirstOrDefault(m =>
                        m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));

                    if (mat == null)
                    {
                        Logger.Log($"[PreviewSelectedMasks] Material '{materialName}' not found; skipping");
                        continue;
                    }

                    byte materialID = mat.ID;

                    // Apply mask to the appropriate preview overlay based on direction
                    if (direction == "XY")
                    {
                        for (int y = 0; y < Math.Min(height, mask.Height); y++)
                        {
                            for (int x = 0; x < Math.Min(width, mask.Width); x++)
                            {
                                if (mask.GetPixel(x, y).R > 128)
                                {
                                    mainForm.currentSelection[x, y] = materialID;
                                }
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
                                {
                                    mainForm.currentSelectionXZ[x, z] = materialID;
                                }
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
                                {
                                    mainForm.currentSelectionYZ[z, y] = materialID;
                                }
                            }
                        }
                    }
                }

                // Update views to show the preview
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




        /// <summary>
        /// Randomly picks up to 'count' white pixels from 'mask' to serve as negative prompts
        /// so that future materials don't re-label the same region.
        /// 'mask' is a black/white Bitmap, white = already segmented region.
        /// </summary>
        private List<(int X, int Y)> SampleNegativePointsFromMask(Bitmap mask, int count)
        {
            List<(int X, int Y)> whitePixels = new List<(int X, int Y)>();
            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    if (mask.GetPixel(x, y).R > 128)
                    {
                        whitePixels.Add((x, y));
                    }
                }
            }
            if (whitePixels.Count == 0) return new List<(int, int)>();

            Random rnd = new Random();
            // If we have fewer white pixels than 'count', just return them all
            if (whitePixels.Count <= count)
                return whitePixels;

            // Shuffle and take the first 'count'
            for (int i = whitePixels.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (whitePixels[i], whitePixels[j]) = (whitePixels[j], whitePixels[i]);
            }
            return whitePixels.Take(count).ToList();
        }



        /// <summary>
        /// Builds a prompt list for one material pass.
        /// If there is only a single positive point and no negatives, it returns that single point.
        /// Otherwise, it returns all annotation points with the following rule:
        /// - Points belonging to the target material are marked as "Foreground" (positive).
        /// - All other points are marked as "Exterior" (negative).
        /// </summary>
        /// <summary>
        /// Builds a prompt list for segmentation, making sure all labels are properly assigned
        /// </summary>
        private List<AnnotationPoint> BuildMixedPrompts(
            IEnumerable<AnnotationPoint> slicePoints,
            string targetMaterialName)
        {
            if (slicePoints == null)
                return new List<AnnotationPoint>();

            var points = slicePoints.ToList();

            Logger.Log($"[BuildMixedPrompts] Building prompts for material '{targetMaterialName}' from {points.Count} points");

            List<AnnotationPoint> result = new List<AnnotationPoint>();

            foreach (var point in points)
            {
                if (string.IsNullOrEmpty(point.Label))
                    continue;

                AnnotationPoint newPoint = new AnnotationPoint
                {
                    ID = point.ID,
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z,
                    Type = point.Type
                };

                if (point.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase))
                {
                    newPoint.Label = "Foreground";
                    Logger.Log($"  Point {point.ID} at ({point.X},{point.Y}) - POSITIVE (original: {point.Label})");
                }
                else
                {
                    newPoint.Label = "Exterior";
                    Logger.Log($"  Point {point.ID} at ({point.X},{point.Y}) - NEGATIVE (original: {point.Label})");
                }

                result.Add(newPoint);
            }

            int positiveCount = result.Count(p => p.Label == "Foreground");
            int negativeCount = result.Count(p => p.Label == "Exterior");

            Logger.Log($"[BuildMixedPrompts] Created {result.Count} prompt points: {positiveCount} positive, {negativeCount} negative");

            return result;
        }


        public void SetRealTimeProcessing(bool enable)
        {
            // mainForm is already stored in SAMForm (set during construction).
            mainForm.RealTimeProcessing = enable;
            // If live preview is disabled, clear any cached segmenter.
            if (!enable)
            {
                mainForm.ClearLiveSegmenter();
            }
        }
        // --- Helper methods to generate base bitmaps for each view ---
        // Generates a grayscale XY slice image from MainForm.volumeData.
        private Bitmap GenerateXYBitmap(int sliceIndex, int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
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

        // Generates a grayscale XZ projection (at fixed Y) from MainForm.volumeData.
        private Bitmap GenerateXZBitmap(int fixedY, int width, int depth)
        {
            Bitmap bmp = new Bitmap(width, depth, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
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

        // Generate a grayscale YZ bitmap at a fixed X index (like mainForm.YzSliceX).
        private Bitmap GenerateYZBitmap(int fixedX, int height, int depth)
        {
            // For YZ, the resulting image has width=depth, height=height.
            Bitmap bmp = new Bitmap(depth, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
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
        private void thresholdingTrackbar_Scroll(object sender, EventArgs e)
        {
            // Update the threshold label
            int threshold = thresholdingTrackbar.Value;
            lblThr.Text = $"Threshold: {threshold}";

            // If a segmenter exists in MainForm, update its threshold
            UpdateSegmenterThreshold(threshold);

            // If real-time processing is enabled, this will trigger an update
            if (mainForm.RealTimeProcessing)
            {
                mainForm.ProcessSegmentationPreview();
            }
        }
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

                // Track stats for reporting
                int totalPixelsModified = 0;
                int materialsApplied = 0;

                // Process each direction's masks based on user selection
                if (candidateSelections != null)
                {
                    // Apply XY masks
                    if (XYButton.Checked && multiDirResults.ContainsKey("XY") && candidateSelections.ContainsKey("XY"))
                    {
                        foreach (var matName in candidateSelections["XY"].Keys)
                        {
                            // Find material ID
                            Material mat = mainForm.Materials.FirstOrDefault(m =>
                                m.Name.Equals(matName, StringComparison.OrdinalIgnoreCase));

                            if (mat == null)
                            {
                                Logger.Log($"[btnApply] Warning: Material '{matName}' not found; skipping");
                                continue;
                            }

                            // Get the selected candidate index and the mask
                            int candidateIdx = candidateSelections["XY"][matName];

                            if (multiDirResults["XY"].ContainsKey(matName) &&
                                multiDirResults["XY"][matName].Count > candidateIdx)
                            {
                                Bitmap mask = multiDirResults["XY"][matName][candidateIdx];

                                // Apply the mask
                                int pixelsChanged = 0;
                                for (int y = 0; y < height && y < mask.Height; y++)
                                {
                                    for (int x = 0; x < width && x < mask.Width; x++)
                                    {
                                        if (mask.GetPixel(x, y).R > 128 && mainForm.volumeLabels[x, y, currentZ] == 0)
                                        {
                                            mainForm.volumeLabels[x, y, currentZ] = mat.ID;
                                            pixelsChanged++;
                                        }
                                    }
                                }

                                totalPixelsModified += pixelsChanged;
                                materialsApplied++;
                                Logger.Log($"[btnApply] Applied XY mask for {matName}, modified {pixelsChanged} pixels");
                            }
                        }
                    }

                    // Apply XZ masks
                    if (XZButton.Checked && multiDirResults.ContainsKey("XZ") && candidateSelections.ContainsKey("XZ"))
                    {
                        foreach (var matName in candidateSelections["XZ"].Keys)
                        {
                            Material mat = mainForm.Materials.FirstOrDefault(m =>
                                m.Name.Equals(matName, StringComparison.OrdinalIgnoreCase));

                            if (mat == null)
                            {
                                Logger.Log($"[btnApply] Warning: Material '{matName}' not found; skipping");
                                continue;
                            }

                            int candidateIdx = candidateSelections["XZ"][matName];

                            if (multiDirResults["XZ"].ContainsKey(matName) &&
                                multiDirResults["XZ"][matName].Count > candidateIdx)
                            {
                                Bitmap mask = multiDirResults["XZ"][matName][candidateIdx];

                                // Apply the mask (XZ coordinates)
                                int pixelsChanged = 0;
                                for (int z = 0; z < depth && z < mask.Height; z++)
                                {
                                    for (int x = 0; x < width && x < mask.Width; x++)
                                    {
                                        if (mask.GetPixel(x, z).R > 128 && mainForm.volumeLabels[x, fixedY, z] == 0)
                                        {
                                            mainForm.volumeLabels[x, fixedY, z] = mat.ID;
                                            pixelsChanged++;
                                        }
                                    }
                                }

                                totalPixelsModified += pixelsChanged;
                                materialsApplied++;
                                Logger.Log($"[btnApply] Applied XZ mask for {matName}, modified {pixelsChanged} pixels");
                            }
                        }
                    }

                    // Apply YZ masks
                    if (YZButton.Checked && multiDirResults.ContainsKey("YZ") && candidateSelections.ContainsKey("YZ"))
                    {
                        foreach (var matName in candidateSelections["YZ"].Keys)
                        {
                            Material mat = mainForm.Materials.FirstOrDefault(m =>
                                m.Name.Equals(matName, StringComparison.OrdinalIgnoreCase));

                            if (mat == null)
                            {
                                Logger.Log($"[btnApply] Warning: Material '{matName}' not found; skipping");
                                continue;
                            }

                            int candidateIdx = candidateSelections["YZ"][matName];

                            if (multiDirResults["YZ"].ContainsKey(matName) &&
                                multiDirResults["YZ"][matName].Count > candidateIdx)
                            {
                                Bitmap mask = multiDirResults["YZ"][matName][candidateIdx];

                                // Apply the mask (YZ coordinates)
                                int pixelsChanged = 0;
                                for (int y = 0; y < height && y < mask.Height; y++)
                                {
                                    for (int z = 0; z < depth && z < mask.Width; z++)
                                    {
                                        if (mask.GetPixel(z, y).R > 128 && mainForm.volumeLabels[fixedX, y, z] == 0)
                                        {
                                            mainForm.volumeLabels[fixedX, y, z] = mat.ID;
                                            pixelsChanged++;
                                        }
                                    }
                                }

                                totalPixelsModified += pixelsChanged;
                                materialsApplied++;
                                Logger.Log($"[btnApply] Applied YZ mask for {matName}, modified {pixelsChanged} pixels");
                            }
                        }
                    }
                }

                // Refresh the views
                mainForm.ClearSliceCache();
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();

                // Update user with results
                MessageBox.Show($"Applied {materialsApplied} materials, modified {totalPixelsModified} pixels",
                    "Segmentation Applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Log("[SAMForm] Error applying segmentation: " + ex.Message);
                Logger.Log("[SAMForm] Stack trace: " + ex.StackTrace);
                MessageBox.Show("Error applying segmentation: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        // Helper method: returns true if the mask has at least one non-zero pixel.
        private bool ContainsNonZero(byte[,] mask)
        {
            for (int i = 0; i < mask.GetLength(0); i++)
            {
                for (int j = 0; j < mask.GetLength(1); j++)
                {
                    if (mask[i, j] != 0)
                        return true;
                }
            }
            return false;
        }


        private void toolStripButton3_Click(object sender, EventArgs e)
        {
            using (SAMSettings settingsForm = new SAMSettings(this))
            {
                if (settingsForm.ShowDialog() == DialogResult.OK)
                {
                    // The SAMSettings form calls UpdateSettings on this SAMForm,
                    // so any changed settings are automatically applied.
                }
            }
        }
        [Flags]
        public enum SegmentationDirection
        {
            None = 0,
            XY = 1,
            XZ = 2,
            YZ = 4
        }

        // Expose the selected direction as a property.
        public SegmentationDirection SelectedDirection { get; set; } = SegmentationDirection.XY;

        private void XYButton_Click(object sender, EventArgs e)
        {
            // Assuming XYButton is a CheckBox or ToggleButton
            CheckBox btn = sender as CheckBox;
            if (btn == null)
                return;

            if (btn.Checked)
            {
                // Add XY flag
                SelectedDirection |= SegmentationDirection.XY;
            }
            else
            {
                // Remove XY flag
                SelectedDirection &= ~SegmentationDirection.XY;
            }
            // Ensure at least one axis is always selected (default to XY if none)
            if (SelectedDirection == SegmentationDirection.None)
                SelectedDirection = SegmentationDirection.XY;
        }

        private void XZButton_Click(object sender, EventArgs e)
        {
            CheckBox btn = sender as CheckBox;
            if (btn == null)
                return;

            if (btn.Checked)
            {
                SelectedDirection |= SegmentationDirection.XZ;
            }
            else
            {
                SelectedDirection &= ~SegmentationDirection.XZ;
            }
            if (SelectedDirection == SegmentationDirection.None)
                SelectedDirection = SegmentationDirection.XZ;  // defaulting to XZ if none selected
        }

        private void YZButton_Click(object sender, EventArgs e)
        {
            CheckBox btn = sender as CheckBox;
            if (btn == null)
                return;

            if (btn.Checked)
            {
                SelectedDirection |= SegmentationDirection.YZ;
            }
            else
            {
                SelectedDirection &= ~SegmentationDirection.YZ;
            }
            if (SelectedDirection == SegmentationDirection.None)
                SelectedDirection = SegmentationDirection.YZ;  // defaulting to YZ if none selected
        }
        // Helper method to update the segmenter threshold
        private void UpdateSegmenterThreshold(int threshold)
        {
            // Update the threshold on the live segmenter if it exists
            if (mainForm.LiveSegmenter != null)
            {
                mainForm.LiveSegmenter.MaskThreshold = threshold;
            }
        }

        private void ApplyMaskToVolume(Bitmap mask, byte matID, int sliceIndex)
        {
            int w = mainForm.GetWidth();
            int h = mainForm.GetHeight();
            int assigned = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (mask.GetPixel(x, y).R > 128 && mainForm.volumeLabels[x, y, sliceIndex] == 0)
                    {
                        mainForm.volumeLabels[x, y, sliceIndex] = matID;
                        assigned++;
                    }
                }
            }
            Logger.Log($"Applied mask, assigned {assigned} px to matID={matID}");
        }
        public int GetThresholdValue()
        {
            return thresholdingTrackbar.Value;
        }

        private void toolStripButton2_Click(object sender, EventArgs e)
        {
            try
            {
                Logger.Log("[SAMForm] Starting 3D segmentation propagation");

                // Check if any direction is selected
                if (SelectedDirection == SegmentationDirection.None)
                {
                    MessageBox.Show("Please select at least one direction (XY, XZ, or YZ) for propagation.",
                                   "No Direction Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Validate models first
                bool usingSam2 = CurrentSettings.UseSam2Models;
                string modelFolder = CurrentSettings.ModelFolderPath;

                if (usingSam2)
                {
                    string encoderPath = Path.Combine(modelFolder, "sam2.1_large.encoder.onnx");
                    string decoderPath = Path.Combine(modelFolder, "sam2.1_large.decoder.onnx");

                    if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
                    {
                        string missingModels = "";
                        if (!File.Exists(encoderPath)) missingModels += $"- SAM 2.1 encoder: {encoderPath}\n";
                        if (!File.Exists(decoderPath)) missingModels += $"- SAM 2.1 decoder: {decoderPath}\n";

                        MessageBox.Show($"SAM 2.1 models missing:\n{missingModels}\nPlease check model paths in settings.",
                            "Models Missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                else
                {
                    string imageEncoderPath = Path.Combine(modelFolder, "image_encoder_hiera_t.onnx");
                    string promptEncoderPath = Path.Combine(modelFolder, "prompt_encoder_hiera_t.onnx");
                    string maskDecoderPath = Path.Combine(modelFolder, "mask_decoder_hiera_t.onnx");

                    if (!File.Exists(imageEncoderPath) || !File.Exists(promptEncoderPath) || !File.Exists(maskDecoderPath))
                    {
                        string missingModels = "";
                        if (!File.Exists(imageEncoderPath)) missingModels += $"- Image encoder: {imageEncoderPath}\n";
                        if (!File.Exists(promptEncoderPath)) missingModels += $"- Prompt encoder: {promptEncoderPath}\n";
                        if (!File.Exists(maskDecoderPath)) missingModels += $"- Mask decoder: {maskDecoderPath}\n";

                        MessageBox.Show($"Original SAM models missing:\n{missingModels}\nPlease check model paths in settings.",
                            "Models Missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                // Call the propagator with appropriate settings
                byte[,,] result = SegmentationPropagator.Propagate(
                    mainForm,
                    CurrentSettings,
                    thresholdingTrackbar.Value,
                    SelectedDirection);

                if (result != null)
                {
                    // Copy results to volume labels
                    CopyResultsToVolumeLabels(result);

                    // Update display after propagation
                    mainForm.ClearSliceCache();
                    mainForm.RenderViews();
                    _ = mainForm.RenderOrthoViewsAsync();

                    Logger.Log("[SAMForm] 3D propagation completed successfully");
                    MessageBox.Show("3D propagation completed successfully", "Propagation", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("No segmentation could be propagated. Please ensure at least one slice is segmented.",
                                   "Propagation Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SAMForm] Error during 3D propagation: " + ex.Message);
                Logger.Log("[SAMForm] Stack trace: " + ex.StackTrace);
                MessageBox.Show($"Error during 3D propagation: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        // Helper method to copy results from a byte[,,] array to the ChunkedLabelVolume structure
        private void CopyResultsToVolumeLabels(byte[,,] results)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            Logger.Log("[SAMForm] Copying propagation results to ChunkedLabelVolume...");

            // Copy the results to the ChunkedLabelVolume structure
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        mainForm.volumeLabels[x, y, z] = results[x, y, z];
                    }
                }
            }

            Logger.Log("[SAMForm] Results successfully transferred to volume labels");
        }
    }
}
