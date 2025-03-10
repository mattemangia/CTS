using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
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
        public SAMForm(MainForm mainForm, AnnotationManager annotationManager, List<Material> materials)
        {
            Logger.Log("[SAM] Constructor start");
            this.mainForm = mainForm;
            this.annotationManager = annotationManager;
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
            
            InitializeComponent();
            var labelColumn = dataGridPoints.Columns["Label"] as DataGridViewComboBoxColumn;
            labelColumn.Items.Clear();
            foreach (var material in materials)
            {
                labelColumn.Items.Add(material.Name);
            }
            this.dataGridPoints.KeyDown += DataGridPoints_KeyDown;

        
            labelColumn.Items.Clear();
            foreach (var material in materials)
            {
                labelColumn.Items.Add(material.Name);
            }
            // After initializing dataGridPoints (e.g., in the constructor or InitializeComponent)
            this.dataGridPoints.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (dataGridPoints.IsCurrentCellDirty)
                    dataGridPoints.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            Logger.Log("[SAM] Constructor end");
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
        private void DataGridPoints_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0)
                return;

            // Check if the changed column is "Label"
            if (dataGridPoints.Columns[e.ColumnIndex].Name == "Label")
            {
                // Get the point ID from the first column (or however your grid is organized)
                int pointID = Convert.ToInt32(dataGridPoints.Rows[e.RowIndex].Cells[0].Value);
                // Retrieve the new label from the cell's value
                string newLabel = dataGridPoints.Rows[e.RowIndex].Cells["Label"].Value?.ToString();
                if (!string.IsNullOrEmpty(newLabel))
                {
                    // Find the corresponding annotation point (assuming AnnotationPoint has a Label property)
                    var point = annotationManager.Points.FirstOrDefault(p => p.ID == pointID);
                    if (point != null)
                    {
                        point.Label = newLabel;
                        // Update the main form's rendering so the new label appears in the slices.
                        mainForm.RenderViews();
                        _ = mainForm.RenderOrthoViewsAsync();
                    }
                }
            }
        }
        private void DataGridPoints_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                DeleteSelectedRowsAndUpdateIDs();
                e.Handled = true;
            }
        }

        // New helper method to delete selected rows and update IDs.
        private void DeleteSelectedRowsAndUpdateIDs()
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

            // Reassign IDs sequentially starting at 1.
            int newID = 1;
            foreach (var point in annotationManager.Points.OrderBy(p => p.ID).ToList())
            {
                point.ID = newID;
                newID++;
            }

            // Update the DataGridView rows with the new IDs.
            foreach (DataGridViewRow row in dataGridPoints.Rows)
            {
                if (!row.IsNewRow)
                {
                    // Assuming the first column holds the ID.
                    row.Cells[0].Value = row.Index + 1;
                }
            }

            // Optionally, re-render the main views.
            mainForm.RenderViews();
            _ = mainForm.RenderOrthoViewsAsync();
        }


        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            try
            {
                string modelFolder = CurrentSettings.ModelFolderPath;
                int imageSize = CurrentSettings.ImageInputSize;
                string imageEncoderPath = Path.Combine(modelFolder, "image_encoder_hiera_t.onnx");
                string promptEncoderPath = Path.Combine(modelFolder, "prompt_encoder_hiera_t.onnx");
                string maskDecoderPath = Path.Combine(modelFolder, "mask_decoder_hiera_t.onnx");
                string memoryAttentionPath = Path.Combine(modelFolder, "memory_attention_hiera_t.onnx");
                string memoryEncoderPath = Path.Combine(modelFolder, "memory_encoder_hiera_t.onnx");
                string mlpPath = Path.Combine(modelFolder, "mlp_hiera_t.onnx");

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
                    int width = mainForm.GetWidth();
                    int height = mainForm.GetHeight();
                    int depth = mainForm.GetDepth();

                    // Process XY view if selected.
                    if ((SelectedDirection & SegmentationDirection.XY) != 0)
                    {
                        using (Bitmap baseXY = GenerateXYBitmap(mainForm.CurrentSlice, width, height)) 
                        {
                            List<Point> promptPointsXY = annotationManager.GetPointsForSlice(mainForm.CurrentSlice)
                                .Select(p => new Point((int)p.X, (int)p.Y)).ToList();
                            Bitmap maskXY = segmenter.ProcessXYSlice(mainForm.CurrentSlice, baseXY, promptPointsXY, null, null);
                            if (maskXY != null)
                            {
                                byte[,] maskXYArray = new byte[width, height];
                                for (int y = 0; y < height; y++)
                                {
                                    for (int x = 0; x < width; x++)
                                    {
                                        Color c = maskXY.GetPixel(x, y);
                                        if (c.R > 128)
                                        {
                                            maskXYArray[x, y] = mainForm.Materials.FirstOrDefault(m => m.Name ==
                                                (promptPointsXY.Count > 0 ? promptPointsXY[0].ToString() : "Default"))?.ID ?? 1;
                                        }
                                    }
                                }
                                mainForm.currentSelection = maskXYArray;
                                maskXY.Dispose();
                            }
                        }
                    }

                    // Process XZ view if selected.
                    if ((SelectedDirection & SegmentationDirection.XZ) != 0)
                    {
                        using (Bitmap baseXZ = GenerateXZBitmap(mainForm.XzSliceY, width, depth))
                        {
                            List<Point> promptPointsXZ = annotationManager.GetPointsForSlice(mainForm.XzSliceY)
                                .Select(p => new Point((int)p.X, (int)p.Z)).ToList();
                            Bitmap maskXZ = segmenter.ProcessXZSlice(mainForm.XzSliceY, baseXZ, promptPointsXZ, null, null);
                            if (maskXZ != null)
                            {
                                byte[,] maskXZArray = new byte[width, depth];
                                for (int z = 0; z < depth; z++)
                                {
                                    for (int x = 0; x < width; x++)
                                    {
                                        Color c = maskXZ.GetPixel(x, z);
                                        if (c.R > 128)
                                        {
                                            maskXZArray[x, z] = mainForm.Materials.FirstOrDefault(m => m.Name ==
                                                (promptPointsXZ.Count > 0 ? promptPointsXZ[0].ToString() : "Default"))?.ID ?? 1;
                                        }
                                    }
                                }
                                mainForm.currentSelectionXZ = maskXZArray;
                                maskXZ.Dispose();
                            }
                        }
                    }

                    // Process YZ view if selected.
                    if ((SelectedDirection & SegmentationDirection.YZ) != 0)
                    {
                        using (Bitmap baseYZ = GenerateYZBitmap(mainForm.YzSliceX, height, depth))
                        {
                            List<Point> promptPointsYZ = annotationManager.GetPointsForSlice(mainForm.YzSliceX)
                                .Select(p => new Point((int)p.Z, (int)p.Y)).ToList();
                            Bitmap maskYZ = segmenter.ProcessYZSlice(mainForm.YzSliceX, baseYZ, promptPointsYZ, null, null);
                            if (maskYZ != null)
                            {
                                byte[,] maskYZArray = new byte[depth, height];
                                for (int y = 0; y < height; y++)
                                {
                                    for (int z = 0; z < depth; z++)
                                    {
                                        Color c = maskYZ.GetPixel(z, y);
                                        if (c.R > 128)
                                        {
                                            maskYZArray[z, y] = mainForm.Materials.FirstOrDefault(m => m.Name ==
                                                (promptPointsYZ.Count > 0 ? promptPointsYZ[0].ToString() : "Default"))?.ID ?? 1;
                                        }
                                    }
                                }
                                mainForm.currentSelectionYZ = maskYZArray;
                                maskYZ.Dispose();
                            }
                        }
                    }

                    mainForm.RenderViews();
                    _ = mainForm.RenderOrthoViewsAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SAMForm.toolStripButton1_Click] Exception: " + ex.Message);
                MessageBox.Show("An error occurred while processing segmentation: " + ex.Message);
            }
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
        Bitmap GenerateXYBitmap(int sliceIndex, int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Access volume data (assumed public in MainForm)
                    byte val = mainForm.volumeData[x, y, sliceIndex];
                    bmp.SetPixel(x, y, Color.FromArgb(val, val, val));
                }
            }
            return bmp;
        }

        // Generates a grayscale XZ projection (at fixed Y) from MainForm.volumeData.
        Bitmap GenerateXZBitmap(int fixedY, int width, int depth)
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

        

        // Generates a grayscale YZ projection (at fixed X) from MainForm.volumeData.
        Bitmap GenerateYZBitmap(int fixedX, int height, int depth)
        {
            // Note: The resulting bitmap will have width=depth and height=height.
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

        private void btnApply_Click(object sender, EventArgs e)
        {
            try
            {
                // Commit the XY (current slice) temporary selection mask.
                mainForm.ApplyCurrentSelection();

                // Commit the orthogonal view selections (XZ and YZ).
                mainForm.ApplyOrthoSelections();

                // Optionally refresh the views in MainForm.
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
    }
}
