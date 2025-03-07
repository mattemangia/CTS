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
            Logger.Log("[SAM] Constructor start");
            InitializeComponent();
            var labelColumn = dataGridPoints.Columns["Label"] as DataGridViewComboBoxColumn;
            labelColumn.Items.Clear();
            foreach (var material in materials)
            {
                labelColumn.Items.Add(material.Name);
            }
            Logger.Log("[SAM] Constructor end");
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
            // Assuming the first column holds the annotation ID and the "Label" column is named "Label"
            if (dataGridPoints.Columns[e.ColumnIndex].Name == "Label")
            {
                int pointID = Convert.ToInt32(dataGridPoints.Rows[e.RowIndex].Cells[0].Value);
                string newLabel = dataGridPoints.Rows[e.RowIndex].Cells["Label"].Value?.ToString();
                if (!string.IsNullOrEmpty(newLabel))
                {
                    // Update the annotation point in the manager.
                    var point = annotationManager.Points.FirstOrDefault(p => p.ID == pointID);
                    if (point != null)
                    {
                        point.Label = newLabel;
                        // Optionally, force MainForm to re-render the points so their colors update.
                        mainForm.RenderViews();
                    }
                }
            }
        }

        private void toolStripButton1_Click(object sender, EventArgs e)
        {

        }
    }
}
