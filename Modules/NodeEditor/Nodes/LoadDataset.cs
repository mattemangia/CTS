using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class LoadDatasetNode : BaseNode
    {
        public string DatasetPath { get; set; }
        public bool LoadLabelsOnly { get; set; } = false;
        public double PixelSize { get; set; } = 1.0; // Default 1.0 μm
        private TextBox pathTextBox;
        private NumericUpDown pixelSizeInput;

        public LoadDatasetNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddOutputPin("Volume", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            // Dataset Path
            var pathLabel = new Label
            {
                Text = "Dataset Path:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var pathContainer = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            pathTextBox = new TextBox
            {
                Text = DatasetPath ?? "",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            pathTextBox.TextChanged += (s, e) => DatasetPath = pathTextBox.Text;

            var browseButton = new Button
            {
                Text = "Browse",
                Dock = DockStyle.Right,
                Width = 80,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            browseButton.Click += (s, e) => BrowseForDataset();

            pathContainer.Controls.Add(pathTextBox);
            pathContainer.Controls.Add(browseButton);

            // Pixel Size input
            var pixelSizeLabel = new Label
            {
                Text = "Pixel Size (μm):",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            pixelSizeInput = new NumericUpDown
            {
                Value = (decimal)PixelSize,
                Minimum = 0.001m,
                Maximum = 1000m,
                DecimalPlaces = 3,
                Increment = 0.1m,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            pixelSizeInput.ValueChanged += (s, e) => PixelSize = (double)pixelSizeInput.Value;

            // Load Labels Only checkbox
            var labelsOnlyCheckbox = new CheckBox
            {
                Text = "Load Labels Only",
                Checked = LoadLabelsOnly,
                Dock = DockStyle.Top,
                Height = 30,
                ForeColor = Color.White
            };
            labelsOnlyCheckbox.CheckedChanged += (s, e) => LoadLabelsOnly = labelsOnlyCheckbox.Checked;

            // Add controls to panel (in reverse order because of DockStyle.Top)
            panel.Controls.Add(labelsOnlyCheckbox);
            panel.Controls.Add(pixelSizeInput);
            panel.Controls.Add(pixelSizeLabel);
            panel.Controls.Add(pathContainer);
            panel.Controls.Add(pathLabel);

            return panel;
        }

        private void BrowseForDataset()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Binary Volume|*.bin|All Files|*.*";
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    DatasetPath = dialog.FileName;
                    if (pathTextBox != null)
                    {
                        pathTextBox.Text = DatasetPath;
                    }
                }
            }
        }

        public override void Execute()
        {
            if (string.IsNullOrEmpty(DatasetPath))
                return;

            try
            {
                // Convert pixel size from μm to meters
                double pixelSizeInMeters = PixelSize * 1e-6;

                var progressForm = new ProgressFormWithProgress("Loading dataset...");
                progressForm.Show();

                var task = Task.Run(async () => {
                    var result = await FileOperations.LoadDatasetAsync(
                        DatasetPath,
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
                        MessageBox.Show($"Error loading dataset: {t.Exception.InnerException?.Message}",
                            "Loading Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error executing node: {ex.Message}",
                    "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

}
