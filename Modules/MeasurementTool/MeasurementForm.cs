using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using Microsoft.VisualBasic;

namespace CTS
{
    public partial class MeasurementForm : Form
    {
        private MainForm mainForm;
        private MeasurementManager measurementManager;

        // UI Controls
        private ListView measurementListView;
        private Button btnExportCSV;
        private Button btnExportExcel;
        private Button btnImportCSV;
        private Button btnClearAll;
        private Button btnDeleteSelected;
        private Label lblInfo;
        private ContextMenuStrip contextMenu;

        public MeasurementForm(MainForm mainForm, MeasurementManager measurementManager)
        {
            this.mainForm = mainForm;
            this.measurementManager = measurementManager;

            InitializeComponent();
            SetupListView();
            SetupContextMenu();

            // Subscribe to measurement changes
            measurementManager.MeasurementsChanged += MeasurementManager_MeasurementsChanged;

            // Load existing measurements
            RefreshMeasurementList();
        }

        private void InitializeComponent()
        {
            this.Icon = Properties.Resources.favicon; // Set the form icon
            this.Text = "Measurements";
            this.Size = new Size(700, 500);
            this.StartPosition = FormStartPosition.CenterParent;

            // Create main layout
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Padding = new Padding(10)
            };

            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            // Info label
            lblInfo = new Label
            {
                Text = "Measurements List (double-click to jump to measurement)",
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            mainLayout.Controls.Add(lblInfo, 0, 0);

            // List view
            measurementListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                GridLines = true,
                FullRowSelect = true,
                MultiSelect = true
            };
            mainLayout.Controls.Add(measurementListView, 0, 1);

            // Button panel
            FlowLayoutPanel buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(5)
            };

            btnExportCSV = new Button { Text = "Export CSV", Width = 100, Height = 25 };
            btnExportExcel = new Button { Text = "Export Excel", Width = 100, Height = 25 };
            btnImportCSV = new Button { Text = "Import CSV", Width = 100, Height = 25 };
            btnDeleteSelected = new Button { Text = "Delete Selected", Width = 100, Height = 25 };
            btnClearAll = new Button { Text = "Clear All", Width = 100, Height = 25 };

            buttonPanel.Controls.Add(btnExportCSV);
            buttonPanel.Controls.Add(btnExportExcel);
            buttonPanel.Controls.Add(btnImportCSV);
            buttonPanel.Controls.Add(btnDeleteSelected);
            buttonPanel.Controls.Add(btnClearAll);

            mainLayout.Controls.Add(buttonPanel, 0, 2);

            this.Controls.Add(mainLayout);

            // Setup event handlers
            btnExportCSV.Click += BtnExportCSV_Click;
            btnExportExcel.Click += BtnExportExcel_Click;
            btnImportCSV.Click += BtnImportCSV_Click;
            btnDeleteSelected.Click += BtnDeleteSelected_Click;
            btnClearAll.Click += BtnClearAll_Click;
            measurementListView.DoubleClick += MeasurementListView_DoubleClick;
        }

        private void SetupListView()
        {
            // Add columns
            measurementListView.Columns.Add("ID", 40);
            measurementListView.Columns.Add("Name", 120);
            measurementListView.Columns.Add("View", 60);
            measurementListView.Columns.Add("Slice", 50);
            measurementListView.Columns.Add("Start", 80);
            measurementListView.Columns.Add("End", 80);
            measurementListView.Columns.Add("Distance", 100);
            measurementListView.Columns.Add("Created", 130);
        }

        private void SetupContextMenu()
        {
            contextMenu = new ContextMenuStrip();

            var deleteItem = new ToolStripMenuItem("Delete");
            deleteItem.Click += (s, e) => BtnDeleteSelected_Click(s, e);

            var renameItem = new ToolStripMenuItem("Rename");
            renameItem.Click += (s, e) => RenameSelectedMeasurement();

            var jumpToItem = new ToolStripMenuItem("Jump to Location");
            jumpToItem.Click += (s, e) => JumpToSelectedMeasurement();

            contextMenu.Items.Add(deleteItem);
            contextMenu.Items.Add(renameItem);
            contextMenu.Items.Add(jumpToItem);

            measurementListView.ContextMenuStrip = contextMenu;
        }

        private void RefreshMeasurementList()
        {
            measurementListView.Items.Clear();

            foreach (var measurement in measurementManager.Measurements)
            {
                var item = new ListViewItem(measurement.ID.ToString());
                item.SubItems.Add(measurement.Name);
                item.SubItems.Add(measurement.ViewType.ToString());
                item.SubItems.Add(measurement.SliceIndex.ToString());
                item.SubItems.Add($"({measurement.StartPoint.X}, {measurement.StartPoint.Y})");
                item.SubItems.Add($"({measurement.EndPoint.X}, {measurement.EndPoint.Y})");
                item.SubItems.Add(measurement.DistanceDisplayText);
                item.SubItems.Add(measurement.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                item.Tag = measurement;

                // Set color indicator
                item.BackColor = Color.FromArgb(30, measurement.LineColor.R, measurement.LineColor.G, measurement.LineColor.B);

                measurementListView.Items.Add(item);
            }
        }

        private void MeasurementManager_MeasurementsChanged(object sender, EventArgs e)
        {
            RefreshMeasurementList();
        }

        private void BtnExportCSV_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
                dialog.DefaultExt = "csv";
                dialog.Title = "Export Measurements to CSV";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        measurementManager.ExportToCSV(dialog.FileName);
                        MessageBox.Show("Measurements exported successfully.", "Export Complete",
                                       MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting measurements: {ex.Message}", "Export Error",
                                       MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void BtnExportExcel_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "Excel files (*.xlsx)|*.xlsx|All files (*.*)|*.*";
                dialog.DefaultExt = "xlsx";
                dialog.Title = "Export Measurements to Excel";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        measurementManager.ExportToExcel(dialog.FileName);
                        MessageBox.Show("Measurements exported successfully.", "Export Complete",
                                       MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting measurements: {ex.Message}", "Export Error",
                                       MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void BtnImportCSV_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
                dialog.Title = "Import Measurements from CSV";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        measurementManager.ImportFromCSV(dialog.FileName);
                        mainForm.RenderViews(); // Refresh views to show imported measurements
                        MessageBox.Show("Measurements imported successfully.", "Import Complete",
                                       MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error importing measurements: {ex.Message}", "Import Error",
                                       MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void BtnDeleteSelected_Click(object sender, EventArgs e)
        {
            if (measurementListView.SelectedItems.Count == 0)
            {
                MessageBox.Show("Please select measurements to delete.", "No Selection",
                               MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var result = MessageBox.Show($"Are you sure you want to delete {measurementListView.SelectedItems.Count} measurement(s)?",
                                        "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                foreach (ListViewItem item in measurementListView.SelectedItems)
                {
                    var measurement = item.Tag as Measurement;
                    if (measurement != null)
                    {
                        measurementManager.RemoveMeasurement(measurement.ID);
                    }
                }

                mainForm.RenderViews(); // Refresh views to remove deleted measurements
            }
        }

        private void BtnClearAll_Click(object sender, EventArgs e)
        {
            if (measurementManager.Measurements.Count == 0)
            {
                MessageBox.Show("No measurements to clear.", "Nothing to Clear",
                               MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var result = MessageBox.Show("Are you sure you want to clear all measurements?",
                                        "Confirm Clear All", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                measurementManager.ClearAllMeasurements();
                mainForm.RenderViews(); // Refresh views to remove all measurements
            }
        }

        private void MeasurementListView_DoubleClick(object sender, EventArgs e)
        {
            JumpToSelectedMeasurement();
        }

        private void JumpToSelectedMeasurement()
        {
            if (measurementListView.SelectedItems.Count == 1)
            {
                var measurement = measurementListView.SelectedItems[0].Tag as Measurement;
                if (measurement != null)
                {
                    // Set the view and slice to show the measurement
                    switch (measurement.ViewType)
                    {
                        case ViewType.XY:
                            mainForm.CurrentSlice = measurement.SliceIndex;
                            break;
                        case ViewType.XZ:
                            mainForm.XzSliceY = measurement.SliceIndex;
                            break;
                        case ViewType.YZ:
                            mainForm.YzSliceX = measurement.SliceIndex;
                            break;
                    }

                    // Calculate center point for zooming
                    Point center = measurement.GetLabelPoint();

                    // Optionally highlight the measurement
                    mainForm.RenderViews();
                }
            }
        }

        private void RenameSelectedMeasurement()
        {
            if (measurementListView.SelectedItems.Count == 1)
            {
                var measurement = measurementListView.SelectedItems[0].Tag as Measurement;
                if (measurement != null)
                {
                    string newName = Interaction.InputBox(
                        "Enter new name for the measurement:",
                        "Rename Measurement",
                        measurement.Name);

                    if (!string.IsNullOrEmpty(newName) && newName != measurement.Name)
                    {
                        measurement.Name = newName;
                        RefreshMeasurementList();
                    }
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Unsubscribe from events
            if (measurementManager != null)
            {
                measurementManager.MeasurementsChanged -= MeasurementManager_MeasurementsChanged;
            }

            base.OnFormClosing(e);
        }
    }
}