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
        
        private MainForm mainForm;
        private AnnotationManager annotationManager;
        public SAMSettingsParams CurrentSettings { get; set; } = new SAMSettingsParams
        {
            FusionAlgorithm = "Majority Voting Fusion",
            ImageInputSize = 1024,
            ModelFolderPath = Application.StartupPath+"/ONNX/"
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
                // Optionally log or handle the exception.
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


        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            try
            {
                Logger.Log("[SAMForm] Starting segmentation process");

                // 1) Load models and initialize segmenter
                string modelFolder = CurrentSettings.ModelFolderPath;
                int imageSize = CurrentSettings.ImageInputSize;
                string imageEncoderPath = Path.Combine(modelFolder, "image_encoder_hiera_t.onnx");
                string promptEncoderPath = Path.Combine(modelFolder, "prompt_encoder_hiera_t.onnx");
                string maskDecoderPath = Path.Combine(modelFolder, "mask_decoder_hiera_t.onnx");
                string memoryAttentionPath = Path.Combine(modelFolder, "memory_attention_hiera_t.onnx");
                string memoryEncoderPath = Path.Combine(modelFolder, "memory_encoder_hiera_t.onnx");
                string mlpPath = Path.Combine(modelFolder, "mlp_hiera_t.onnx");

                string saveFolder = Path.Combine(Application.StartupPath, "SavedMasks");
                Directory.CreateDirectory(saveFolder);

                // Gather all non-Exterior materials from MainForm
                var allMaterials = mainForm.Materials.Where(m =>
                    !m.Name.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();

                Logger.Log($"[SAMForm] Found {allMaterials.Count} valid materials for segmentation");
                foreach (var mat in allMaterials)
                {
                    Logger.Log($"  - Material: {mat.Name} (ID: {mat.ID})");
                }

                using (var segmenter = new CTMemorySegmenter(
                    imageEncoderPath,
                    promptEncoderPath,
                    maskDecoderPath,
                    memoryEncoderPath,
                    memoryAttentionPath,
                    mlpPath,
                    imageSize,
                    false,
                    CurrentSettings.EnableMlp))
                {
                    segmenter.UseSelectiveHoleFilling = CurrentSettings.UseSelectiveHoleFilling;
                    // Set the threshold from the trackbar
                    segmenter.MaskThreshold = thresholdingTrackbar.Value;

                    int width = mainForm.GetWidth();
                    int height = mainForm.GetHeight();
                    int depth = mainForm.GetDepth();
                    Logger.Log($"[SAMForm] Volume: {width}x{height}x{depth}");

                    // For each direction (XY, XZ, YZ) that’s checked, we do a multi-material pass.
                    // ---------------------------------------------------------------------
                    if (XYButton.Checked)
                    {
                        int currentZ = mainForm.CurrentSlice;
                        Logger.Log($"[SAMForm] Multi-material pass on XY slice Z={currentZ}");

                        // Grab all points from annotation manager for this slice
                        var slicePoints = annotationManager.GetPointsForSlice(currentZ);

                        // We'll identify all material names (except 'Exterior')
                        var uniqueMatNames = slicePoints
                            .Select(pt => pt.Label)
                            .Where(lbl => !lbl.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                            .Distinct()
                            .ToList();

                        if (uniqueMatNames.Count == 0)
                        {
                            Logger.Log("[SAMForm] No material-labeled points found in XY slice");
                        }
                        else
                        {
                            using (Bitmap baseXY = GenerateXYBitmap(currentZ, width, height))
                            using (Bitmap accumMask = new Bitmap(width, height))
                            {
                                using (Graphics ga = Graphics.FromImage(accumMask))
                                {
                                    // Start accumMask black => no region excluded yet
                                    ga.Clear(Color.Black);
                                }

                                // We'll do one pass per material in uniqueMatNames, in order of appearance
                                foreach (var materialName in uniqueMatNames)
                                {
                                    Logger.Log($"[SAMForm] Processing material '{materialName}' in XY slice={currentZ}");

                                    // Build standard "Foreground" for targetMaterial, "Exterior" for other-labeled points
                                    // from BuildMixedPrompts
                                    List<AnnotationPoint> basePrompts = BuildMixedPrompts(slicePoints, materialName);

                                    // We'll now add negative points inside accumMask so we exclude previously segmented areas
                                    var negativePoints = SampleNegativePointsFromMask(accumMask, 40); // up to 40 negative
                                    foreach (var (nx, ny) in negativePoints)
                                    {
                                        basePrompts.Add(new AnnotationPoint
                                        {
                                            X = nx,
                                            Y = ny,
                                            Z = currentZ,
                                            Label = "Exterior"
                                        });
                                    }

                                    // Now run the segmentation
                                    using (Bitmap mask = segmenter.ProcessXYSlice(
                                        currentZ, baseXY, basePrompts, null, null))
                                    {
                                        // Save debug image
                                        string fileName = Path.Combine(
                                            saveFolder, $"XY_{materialName}_{currentZ}.jpg");
                                        mask.Save(fileName, ImageFormat.Jpeg);

                                        // Find the actual material
                                        Material mat = mainForm.Materials.FirstOrDefault(m =>
                                            m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));
                                        if (mat == null)
                                        {
                                            Logger.Log($"[SAMForm] Material '{materialName}' not found, skipping");
                                            continue;
                                        }
                                        byte matID = mat.ID;

                                        // (a) Apply mask to volume
                                        int segmentedPixels = 0;
                                        for (int y = 0; y < height; y++)
                                        {
                                            for (int x = 0; x < width; x++)
                                            {
                                                if (mask.GetPixel(x, y).R > 128 &&
                                                    mainForm.volumeLabels[x, y, currentZ] == 0)
                                                {
                                                    mainForm.volumeLabels[x, y, currentZ] = matID;
                                                    segmentedPixels++;
                                                }
                                            }
                                        }
                                        Logger.Log($"[SAMForm] Assigned {segmentedPixels} pixels to '{materialName}' (ID={matID}) in XY slice");

                                        // (b) Merge that mask into accumMask so next pass excludes it
                                        for (int y = 0; y < height; y++)
                                        {
                                            for (int x = 0; x < width; x++)
                                            {
                                                if (mask.GetPixel(x, y).R > 128)
                                                {
                                                    accumMask.SetPixel(x, y, Color.White);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    } // XY end

                    // ---------------------------------------------------------------------
                    // XZ direction
                    if (XZButton.Checked)
                    {
                        int fixedY = mainForm.XzSliceY;
                        Logger.Log($"[SAMForm] Multi-material pass on XZ slice Y={fixedY}");

                        // Points near Y=fixedY
                        var slicePoints = annotationManager.Points
                            .Where(p => Math.Abs(p.Y - fixedY) < 1.0f)
                            .ToList();

                        var uniqueMatNames = slicePoints
                            .Select(pt => pt.Label)
                            .Where(lbl => !lbl.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                            .Distinct()
                            .ToList();

                        if (uniqueMatNames.Count == 0)
                        {
                            Logger.Log("[SAMForm] No material-labeled points found in XZ slice");
                        }
                        else
                        {
                            using (Bitmap baseXZ = GenerateXZBitmap(fixedY, width, depth))
                            using (Bitmap accumMask = new Bitmap(width, depth))
                            {
                                using (Graphics ga = Graphics.FromImage(accumMask))
                                    ga.Clear(Color.Black);

                                foreach (var materialName in uniqueMatNames)
                                {
                                    Logger.Log($"[SAMForm] Material '{materialName}' in XZ slice Y={fixedY}");
                                    List<AnnotationPoint> basePrompts = BuildMixedPrompts(slicePoints, materialName);

                                    // negative from accumMask
                                    var negativePoints = SampleNegativePointsFromMask(accumMask, 40);
                                    foreach (var (nx, nz) in negativePoints)
                                    {
                                        basePrompts.Add(new AnnotationPoint
                                        {
                                            X = nx,
                                            Y = fixedY,
                                            Z = nz,
                                            Label = "Exterior"
                                        });
                                    }

                                    using (Bitmap mask = segmenter.ProcessXZSlice(fixedY, baseXZ, basePrompts, null, null))
                                    {
                                        string fname = Path.Combine(saveFolder, $"XZ_{materialName}_{fixedY}.jpg");
                                        mask.Save(fname, ImageFormat.Jpeg);

                                        Material mat = mainForm.Materials.FirstOrDefault(m =>
                                            m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));
                                        if (mat == null) continue;
                                        byte matID = mat.ID;

                                        int segmentedPixels = 0;
                                        for (int z = 0; z < depth; z++)
                                        {
                                            for (int x = 0; x < width; x++)
                                            {
                                                if (x < mask.Width && z < mask.Height &&
                                                    mask.GetPixel(x, z).R > 128 &&
                                                    mainForm.volumeLabels[x, fixedY, z] == 0)
                                                {
                                                    mainForm.volumeLabels[x, fixedY, z] = matID;
                                                    segmentedPixels++;
                                                }
                                            }
                                        }
                                        Logger.Log($"[SAMForm] Assigned {segmentedPixels} px to '{materialName}' in XZ slice Y={fixedY}");

                                        // accumulate
                                        for (int z = 0; z < depth; z++)
                                        {
                                            for (int x = 0; x < width; x++)
                                            {
                                                if (x < mask.Width && z < mask.Height &&
                                                    mask.GetPixel(x, z).R > 128)
                                                {
                                                    accumMask.SetPixel(x, z, Color.White);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // ---------------------------------------------------------------------
                    // YZ direction
                    if (YZButton.Checked)
                    {
                        int fixedX = mainForm.YzSliceX;
                        Logger.Log($"[SAMForm] Multi-material pass on YZ slice X={fixedX}");

                        // Points near X=fixedX
                        var slicePoints = annotationManager.Points
                            .Where(p => Math.Abs(p.X - fixedX) < 1.0f)
                            .ToList();

                        var uniqueMatNames = slicePoints
                            .Select(pt => pt.Label)
                            .Where(lbl => !lbl.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                            .Distinct()
                            .ToList();

                        if (uniqueMatNames.Count == 0)
                        {
                            Logger.Log("[SAMForm] No material-labeled points in YZ slice");
                        }
                        else
                        {
                            using (Bitmap baseYZ = GenerateYZBitmap(fixedX, height, depth))
                            using (Bitmap accumMask = new Bitmap(depth, height))
                            {
                                using (Graphics ga = Graphics.FromImage(accumMask))
                                    ga.Clear(Color.Black);

                                foreach (var materialName in uniqueMatNames)
                                {
                                    Logger.Log($"[SAMForm] Material '{materialName}' in YZ slice X={fixedX}");
                                    List<AnnotationPoint> basePrompts = BuildMixedPrompts(slicePoints, materialName);

                                    // negative from accumMask
                                    var negativePoints = SampleNegativePointsFromMask(accumMask, 40);
                                    foreach (var (nz, ny) in negativePoints)
                                    {
                                        basePrompts.Add(new AnnotationPoint
                                        {
                                            X = fixedX,
                                            Y = ny,
                                            Z = nz,
                                            Label = "Exterior"
                                        });
                                    }

                                    using (Bitmap mask = segmenter.ProcessYZSlice(fixedX, baseYZ, basePrompts, null, null))
                                    {
                                        string fname = Path.Combine(saveFolder, $"YZ_{materialName}_{fixedX}.jpg");
                                        mask.Save(fname, ImageFormat.Jpeg);

                                        Material mat = mainForm.Materials.FirstOrDefault(m =>
                                            m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));
                                        if (mat == null) continue;
                                        byte matID = mat.ID;

                                        int segmentedPixels = 0;
                                        // YZ => mask width=depth, mask height=height
                                        for (int z = 0; z < depth; z++)
                                        {
                                            for (int y = 0; y < height; y++)
                                            {
                                                if (z < mask.Width && y < mask.Height &&
                                                    mask.GetPixel(z, y).R > 128 &&
                                                    mainForm.volumeLabels[fixedX, y, z] == 0)
                                                {
                                                    mainForm.volumeLabels[fixedX, y, z] = matID;
                                                    segmentedPixels++;
                                                }
                                            }
                                        }
                                        Logger.Log($"[SAMForm] Assigned {segmentedPixels} px to '{materialName}' in YZ slice X={fixedX}");

                                        // accumulate
                                        for (int y = 0; y < height; y++)
                                        {
                                            for (int z = 0; z < depth; z++)
                                            {
                                                if (z < mask.Width && y < mask.Height &&
                                                    mask.GetPixel(z, y).R > 128)
                                                {
                                                    accumMask.SetPixel(z, y, Color.White);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Done. Refresh everything
                    mainForm.ClearSliceCache();
                    mainForm.RenderViews();
                    _ = mainForm.RenderOrthoViewsAsync();

                    Logger.Log("[SAMForm] Segmentation multi-pass process completed successfully");
                    MessageBox.Show("Segmentation completed for all materials on selected slices.", "Segmentation",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SAMForm] Error applying segmentation: " + ex.Message);
                Logger.Log("[SAMForm] Stack trace: " + ex.StackTrace);
                MessageBox.Show($"Error applying segmentation: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        private List<AnnotationPoint> BuildMixedPrompts(
    IEnumerable<AnnotationPoint> slicePoints,
    string targetMaterialName)
        {
            // Print some debugging information
            Logger.Log($"Building prompts for material: {targetMaterialName}");
            var allLabels = slicePoints.Select(p => p.Label).Distinct().ToList();
            Logger.Log($"All labels in slice: {string.Join(", ", allLabels)}");

            var targetPoints = slicePoints.Where(pt => pt.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase)).ToList();
            var otherMaterialPoints = slicePoints.Where(pt => !pt.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase)).ToList();

            Logger.Log($"Found {targetPoints.Count} positive points and {otherMaterialPoints.Count} points for other materials/exterior");

            List<AnnotationPoint> finalList = new List<AnnotationPoint>();

            // Add target points as Foreground (positive)
            finalList.AddRange(targetPoints.Select(pt => new AnnotationPoint
            {
                ID = pt.ID,
                X = pt.X,
                Y = pt.Y,
                Z = pt.Z,
                Type = pt.Type,
                Label = "Foreground"
            }));

            // Add other material points as Exterior (negative)
            finalList.AddRange(otherMaterialPoints.Select(pt => new AnnotationPoint
            {
                ID = pt.ID,
                X = pt.X,
                Y = pt.Y,
                Z = pt.Z,
                Type = pt.Type,
                Label = "Exterior"
            }));

            // Log the final prompts
            Logger.Log($"Generated {finalList.Count} total prompts: {finalList.Count(p => p.Label == "Foreground")} positive, {finalList.Count(p => p.Label == "Exterior")} negative");

            return finalList;
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
                // Apply XY selection if available.
                if (mainForm.currentSelection != null && ContainsNonZero(mainForm.currentSelection))
                    mainForm.ApplyCurrentSelection();

                // Apply XZ selection if available.
                if (mainForm.currentSelectionXZ != null && ContainsNonZero(mainForm.currentSelectionXZ))
                    mainForm.ApplyXZSelection();

                // Apply YZ selection if available.
                if (mainForm.currentSelectionYZ != null && ContainsNonZero(mainForm.currentSelectionYZ))
                    mainForm.ApplyYZSelection();

                // Force the main viewer to show the mask.
                mainForm.ShowMask = true;
                // Clear the cached slice so that the new segmentation is rendered.
                mainForm.ClearSliceCache();

                // Refresh views.
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();

                MessageBox.Show("Segmentation masks have been applied to the volume.", "Apply", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Log("[SAMForm.btnApply_Click] Exception: " + ex.Message);
                MessageBox.Show("An error occurred while applying segmentation: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

                // Call the static propagator class to perform the segmentation
                byte[,,] result = SegmentationPropagator.Propagate(
                    mainForm,
                    CurrentSettings,
                    thresholdingTrackbar.Value,
                    SelectedDirection);

                if (result != null)
                {
                    // Copy results to ChunkedLabelVolume instead of direct assignment
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
