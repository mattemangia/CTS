using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class LoadMultipleDatasetNode : BaseNode
    {
        // Class to hold dataset info
        public class DatasetInfo
        {
            public string Path { get; set; }
            public double PixelSize { get; set; } = 1.0; // Default to 1.0 μm
        }

        public List<DatasetInfo> Datasets { get; set; } = new List<DatasetInfo>();
        public bool LoadLabelsOnly { get; set; } = false;
        private ListView datasetListView;

        public LoadMultipleDatasetNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddOutputPin("Volumes", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var titleLabel = new Label
            {
                Text = "Multiple Datasets:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            // Create a ListView to show datasets and their pixel sizes
            datasetListView = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Dock = DockStyle.Top,
                Height = 150,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };

            // Add columns for path and pixel size
            datasetListView.Columns.Add("Dataset Path", 250);
            datasetListView.Columns.Add("Pixel Size (μm)", 100);

            // Populate list with current datasets
            foreach (var dataset in Datasets)
            {
                var item = new ListViewItem(dataset.Path);
                item.SubItems.Add(dataset.PixelSize.ToString("F3"));
                item.Tag = dataset; // Store reference to dataset
                datasetListView.Items.Add(item);
            }

            // Allow editing pixel size on double click
            datasetListView.DoubleClick += (s, e) => {
                if (datasetListView.SelectedItems.Count > 0)
                {
                    ListViewItem item = datasetListView.SelectedItems[0];
                    DatasetInfo dataset = item.Tag as DatasetInfo;

                    // Show dialog to edit pixel size
                    using (var form = new Form())
                    {
                        form.Text = "Edit Pixel Size";
                        form.Size = new Size(300, 150);
                        form.FormBorderStyle = FormBorderStyle.FixedDialog;
                        form.StartPosition = FormStartPosition.CenterParent;
                        form.MaximizeBox = false;
                        form.MinimizeBox = false;

                        var label = new Label
                        {
                            Text = "Pixel Size (μm):",
                            Location = new Point(20, 20),
                            Size = new Size(100, 20),
                            ForeColor = Color.Black
                        };

                        var numericInput = new NumericUpDown
                        {
                            Value = (decimal)dataset.PixelSize,
                            Minimum = 0.001m,
                            Maximum = 1000m,
                            DecimalPlaces = 3,
                            Increment = 0.1m,
                            Location = new Point(130, 20),
                            Size = new Size(120, 20)
                        };

                        var okButton = new Button
                        {
                            Text = "OK",
                            DialogResult = DialogResult.OK,
                            Location = new Point(100, 60),
                            Size = new Size(75, 30)
                        };

                        form.Controls.Add(label);
                        form.Controls.Add(numericInput);
                        form.Controls.Add(okButton);
                        form.AcceptButton = okButton;

                        if (form.ShowDialog() == DialogResult.OK)
                        {
                            dataset.PixelSize = (double)numericInput.Value;
                            item.SubItems[1].Text = dataset.PixelSize.ToString("F3");
                        }
                    }
                }
            };

            var buttonsPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var addButton = new Button
            {
                Text = "Add",
                Width = 80,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            addButton.Click += (s, e) => AddDataset();

            var removeButton = new Button
            {
                Text = "Remove",
                Left = 90,
                Width = 80,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            removeButton.Click += (s, e) => RemoveDataset();

            var labelsOnlyCheckbox = new CheckBox
            {
                Text = "Load Labels Only",
                Checked = LoadLabelsOnly,
                Dock = DockStyle.Top,
                Height = 30,
                ForeColor = Color.White
            };
            labelsOnlyCheckbox.CheckedChanged += (s, e) => LoadLabelsOnly = labelsOnlyCheckbox.Checked;

            buttonsPanel.Controls.Add(addButton);
            buttonsPanel.Controls.Add(removeButton);

            // Add controls to panel
            panel.Controls.Add(labelsOnlyCheckbox);
            panel.Controls.Add(buttonsPanel);
            panel.Controls.Add(datasetListView);
            panel.Controls.Add(titleLabel);

            return panel;
        }

        private void AddDataset()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Binary Volume|*.bin|All Files|*.*";
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string path = dialog.FileName;

                    // Add with panel for pixel size
                    using (var form = new Form())
                    {
                        form.Text = "Set Pixel Size";
                        form.Size = new Size(300, 150);
                        form.FormBorderStyle = FormBorderStyle.FixedDialog;
                        form.StartPosition = FormStartPosition.CenterParent;
                        form.MaximizeBox = false;
                        form.MinimizeBox = false;

                        var label = new Label
                        {
                            Text = "Pixel Size (μm):",
                            Location = new Point(20, 20),
                            Size = new Size(100, 20),
                            ForeColor = Color.Black
                        };

                        var numericInput = new NumericUpDown
                        {
                            Value = 1.0m, // Default value
                            Minimum = 0.001m,
                            Maximum = 1000m,
                            DecimalPlaces = 3,
                            Increment = 0.1m,
                            Location = new Point(130, 20),
                            Size = new Size(120, 20)
                        };

                        var okButton = new Button
                        {
                            Text = "OK",
                            DialogResult = DialogResult.OK,
                            Location = new Point(100, 60),
                            Size = new Size(75, 30)
                        };

                        form.Controls.Add(label);
                        form.Controls.Add(numericInput);
                        form.Controls.Add(okButton);
                        form.AcceptButton = okButton;

                        if (form.ShowDialog() == DialogResult.OK)
                        {
                            // Create new dataset info
                            var dataset = new DatasetInfo
                            {
                                Path = path,
                                PixelSize = (double)numericInput.Value
                            };

                            Datasets.Add(dataset);

                            // Add to ListView
                            var item = new ListViewItem(path);
                            item.SubItems.Add(dataset.PixelSize.ToString("F3"));
                            item.Tag = dataset;
                            datasetListView.Items.Add(item);
                        }
                    }
                }
            }
        }

        private void RemoveDataset()
        {
            if (datasetListView.SelectedItems.Count > 0)
            {
                ListViewItem item = datasetListView.SelectedItems[0];
                DatasetInfo dataset = item.Tag as DatasetInfo;

                Datasets.Remove(dataset);
                datasetListView.Items.Remove(item);
            }
        }

        public override void Execute()
        {
            if (Datasets.Count == 0)
                return;

            try
            {
                foreach (var dataset in Datasets)
                {
                    string path = dataset.Path;
                    double pixelSizeInMeters = dataset.PixelSize * 1e-6; // Convert from μm to m

                    var progressForm = new ProgressFormWithProgress($"Loading dataset: {Path.GetFileName(path)}");
                    progressForm.Show();

                    var task = Task.Run(async () => {
                        var result = await FileOperations.LoadDatasetAsync(
                            path,
                            true, // Use memory mapping
                            pixelSizeInMeters,
                            1,    // No binning
                            progressForm);

                        return result;
                    });

                    task.ContinueWith(t => {
                        progressForm.Close();
                        if (t.IsFaulted)
                        {
                            MessageBox.Show($"Error loading dataset {Path.GetFileName(path)}: {t.Exception.InnerException?.Message}",
                                "Loading Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error executing node: {ex.Message}",
                    "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

}
