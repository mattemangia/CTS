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
                this.Hide();
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
            if (dataGridPoints.Columns[e.ColumnIndex].Name == "Label")
            {
                int pointID = Convert.ToInt32(dataGridPoints.Rows[e.RowIndex].Cells[0].Value);
                string newLabel = dataGridPoints.Rows[e.RowIndex].Cells["Label"].Value?.ToString();
                if (!string.IsNullOrEmpty(newLabel))
                {
                    var point = annotationManager.Points.FirstOrDefault(p => p.ID == pointID);
                    if (point != null)
                    {
                        point.Label = newLabel;
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
                // --- 1. Create a CTMemorySegmenter instance using current settings ---
                string modelFolder = CurrentSettings.ModelFolderPath;
                int imageSize = CurrentSettings.ImageInputSize;
                // Derive model paths from the settings (see SAMSettings.cs)
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
                    imageSize))
                {
                    // Parameters for segmentation preview
                    int threshold = 128; // pixel intensity threshold to decide segmentation
                    int tolerance = 5;   // tolerance (in pixels) for matching fixed coordinate in orthoviews

                    // Get volume dimensions from MainForm (assumed accessible)
                    int width = mainForm.GetWidth();
                    int height = mainForm.GetHeight();
                    // Assume MainForm has a GetDepth() method (or use a stored depth property)
                    int depth = mainForm.GetDepth();

                    // --- Process XY view (current slice) ---
                    // Visible points in XY view have their Z exactly equal to the current slice.
                    var pointsXY = annotationManager.Points.Where(p => p.Z == mainForm.CurrentSlice).ToList();
                    if (pointsXY.Any())
                    {
                        // Generate base XY image from the volume (grayscale)
                        using (Bitmap baseXY = GenerateXYBitmap(mainForm.CurrentSlice, width, height))
                        {
                            // Create an empty temporary mask (same dimensions as XY view)
                            byte[,] maskXY = new byte[width, height];

                            // Group points by material label
                            foreach (var group in pointsXY.GroupBy(p => p.Label))
                            {
                                // Convert each annotation point to an integer prompt (using its X and Y)
                                List<Point> promptPoints = group.Select(p => new Point((int)p.X, (int)p.Y)).ToList();
                                // Process the slice with the given prompt; no boxes, brush mask or text prompt provided here.
                                using (Bitmap maskResult = segmenter.ProcessSingleSlice(baseXY, promptPoints, null, null, null))
                                {
                                    // For each pixel, if the mask indicates segmentation, set the material id.
                                    for (int yPix = 0; yPix < height; yPix++)
                                    {
                                        for (int xPix = 0; xPix < width; xPix++)
                                        {
                                            Color c = maskResult.GetPixel(xPix, yPix);
                                            if (c.R > threshold)
                                            {
                                                var mat = mainForm.Materials.FirstOrDefault(m => m.Name == group.Key);
                                                if (mat != null)
                                                {
                                                    maskXY[xPix, yPix] = mat.ID;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            // Pass the preview mask to the MainForm for the XY view.
                            mainForm.currentSelection = maskXY;
                        }
                    }

                    // --- Process XZ view ---
                    // Visible points in XZ view: those with Y coordinate close to mainForm.XzSliceY.
                    var pointsXZ = annotationManager.Points.Where(p => Math.Abs(p.Y - mainForm.XzSliceY) <= tolerance).ToList();
                    if (pointsXZ.Any())
                    {
                        using (Bitmap baseXZ = GenerateXZBitmap(mainForm.XzSliceY, width, depth))
                        {
                            byte[,] maskXZ = new byte[width, depth]; // dimensions: [width, depth]
                            foreach (var group in pointsXZ.GroupBy(p => p.Label))
                            {
                                // For XZ view, use (X, Z) coordinates.
                                List<Point> promptPoints = group.Select(p => new Point((int)p.X, (int)p.Z)).ToList();
                                using (Bitmap maskResult = segmenter.ProcessSingleSlice(baseXZ, promptPoints, null, null, null))
                                {
                                    for (int zPix = 0; zPix < depth; zPix++)
                                    {
                                        for (int xPix = 0; xPix < width; xPix++)
                                        {
                                            Color c = maskResult.GetPixel(xPix, zPix);
                                            if (c.R > threshold)
                                            {
                                                var mat = mainForm.Materials.FirstOrDefault(m => m.Name == group.Key);
                                                if (mat != null)
                                                {
                                                    maskXZ[xPix, zPix] = mat.ID;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            mainForm.currentSelectionXZ = maskXZ;
                        }
                    }

                    // --- Process YZ view ---
                    // Visible points in YZ view: those with X coordinate close to mainForm.YzSliceX.
                    var pointsYZ = annotationManager.Points.Where(p => Math.Abs(p.X - mainForm.YzSliceX) <= tolerance).ToList();
                    if (pointsYZ.Any())
                    {
                        using (Bitmap baseYZ = GenerateYZBitmap(mainForm.YzSliceX, height, depth))
                        {
                            // Note: For YZ view, the bitmap dimensions are [depth, height]
                            byte[,] maskYZ = new byte[depth, height];
                            foreach (var group in pointsYZ.GroupBy(p => p.Label))
                            {
                                // For YZ view, use (Z, Y) coordinates.
                                List<Point> promptPoints = group.Select(p => new Point((int)p.Z, (int)p.Y)).ToList();
                                using (Bitmap maskResult = segmenter.ProcessSingleSlice(baseYZ, promptPoints, null, null, null))
                                {
                                    for (int yPix = 0; yPix < height; yPix++)
                                    {
                                        for (int zPix = 0; zPix < depth; zPix++)
                                        {
                                            // In baseYZ, x coordinate corresponds to zPix.
                                            Color c = maskResult.GetPixel(zPix, yPix);
                                            if (c.R > threshold)
                                            {
                                                var mat = mainForm.Materials.FirstOrDefault(m => m.Name == group.Key);
                                                if (mat != null)
                                                {
                                                    maskYZ[zPix, yPix] = mat.ID;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            mainForm.currentSelectionYZ = maskYZ;
                        }
                    }

                    // --- Update MainForm views ---
                    mainForm.RenderViews();
                    _ = mainForm.RenderOrthoViewsAsync();
                }
            }
            catch (Exception ex)
            {
                // Log and optionally display an error.
                Logger.Log("[SAMForm.toolStripButton1_Click] Exception: " + ex.Message);
                MessageBox.Show("An error occurred while processing segmentation: " + ex.Message);
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
    }
}
