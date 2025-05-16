using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class ExportImageStackNode : BaseNode
    {
        public string OutputFolder { get; set; }
        public string FilePrefix { get; set; } = "slice_";
        public int StartingIndex { get; set; } = 0;
        public ImageFormat ExportFormat { get; set; } = ImageFormat.Png;

        private TextBox folderTextBox;
        private TextBox prefixTextBox;
        private NumericUpDown startIndexInput;
        private ComboBox formatComboBox;
        private CheckBox includeGrayscaleCheckbox;
        private CheckBox includeMaterialsCheckbox;

        public bool IncludeGrayscale { get; set; } = true;
        public bool IncludeMaterials { get; set; } = true;

        public ExportImageStackNode(Point position) : base(position)
        {
            Color = Color.FromArgb(255, 120, 120); // Red theme for output nodes
        }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var titleLabel = new Label
            {
                Text = "Export Image Stack",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            var folderLabel = new Label
            {
                Text = "Output Folder:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var folderContainer = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            folderTextBox = new TextBox
            {
                Text = OutputFolder ?? "",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            folderTextBox.TextChanged += (s, e) => OutputFolder = folderTextBox.Text;

            var browseButton = new Button
            {
                Text = "Browse",
                Dock = DockStyle.Right,
                Width = 80,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            browseButton.Click += (s, e) => BrowseForFolder();

            folderContainer.Controls.Add(folderTextBox);
            folderContainer.Controls.Add(browseButton);

            var prefixLabel = new Label
            {
                Text = "File Prefix:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            prefixTextBox = new TextBox
            {
                Text = FilePrefix,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            prefixTextBox.TextChanged += (s, e) => FilePrefix = prefixTextBox.Text;

            var startIndexLabel = new Label
            {
                Text = "Starting Index:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            startIndexInput = new NumericUpDown
            {
                Value = StartingIndex,
                Minimum = 0,
                Maximum = 100000,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            startIndexInput.ValueChanged += (s, e) => StartingIndex = (int)startIndexInput.Value;

            var formatLabel = new Label
            {
                Text = "Image Format:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            formatComboBox = new ComboBox
            {
                Dock = DockStyle.Top,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            formatComboBox.Items.AddRange(new object[] { "PNG", "JPEG", "BMP", "TIFF" });
            formatComboBox.SelectedIndex = 0; // Default to PNG
            formatComboBox.SelectedIndexChanged += (s, e) => {
                switch (formatComboBox.SelectedIndex)
                {
                    case 0: ExportFormat = ImageFormat.Png; break;
                    case 1: ExportFormat = ImageFormat.Jpeg; break;
                    case 2: ExportFormat = ImageFormat.Bmp; break;
                    case 3: ExportFormat = ImageFormat.Tiff; break;
                    default: ExportFormat = ImageFormat.Png; break;
                }
            };

            includeGrayscaleCheckbox = new CheckBox
            {
                Text = "Include Grayscale Data",
                Checked = IncludeGrayscale,
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = Color.White
            };
            includeGrayscaleCheckbox.CheckedChanged += (s, e) => IncludeGrayscale = includeGrayscaleCheckbox.Checked;

            includeMaterialsCheckbox = new CheckBox
            {
                Text = "Include Material Colors",
                Checked = IncludeMaterials,
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = Color.White
            };
            includeMaterialsCheckbox.CheckedChanged += (s, e) => IncludeMaterials = includeMaterialsCheckbox.Checked;

            var exportButton = new Button
            {
                Text = "Export Images",
                Dock = DockStyle.Top,
                Height = 35,
                BackColor = Color.FromArgb(100, 180, 100), // Green for action
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            exportButton.Click += (s, e) => Execute();

            // Add controls to panel (in reverse order because of DockStyle.Top)
            panel.Controls.Add(exportButton);
            panel.Controls.Add(includeMaterialsCheckbox);
            panel.Controls.Add(includeGrayscaleCheckbox);
            panel.Controls.Add(formatComboBox);
            panel.Controls.Add(formatLabel);
            panel.Controls.Add(startIndexInput);
            panel.Controls.Add(startIndexLabel);
            panel.Controls.Add(prefixTextBox);
            panel.Controls.Add(prefixLabel);
            panel.Controls.Add(folderContainer);
            panel.Controls.Add(folderLabel);
            panel.Controls.Add(titleLabel);

            return panel;
        }

        private void BrowseForFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    OutputFolder = dialog.SelectedPath;
                    if (folderTextBox != null)
                    {
                        folderTextBox.Text = OutputFolder;
                    }
                }
            }
        }

        public override void Execute()
        {
            if (string.IsNullOrEmpty(OutputFolder))
            {
                MessageBox.Show("Please specify an output folder first.",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Get MainForm reference to access the dataset
                var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();

                bool hasVolume = mainForm?.volumeData != null && IncludeGrayscale;
                bool hasLabels = mainForm?.volumeLabels != null && IncludeMaterials;

                if (!hasVolume && !hasLabels)
                {
                    MessageBox.Show("No data is available to export. Please load a dataset first or enable at least one data type.",
                        "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Create output directory if it doesn't exist
                if (!Directory.Exists(OutputFolder))
                {
                    Directory.CreateDirectory(OutputFolder);
                }

                // Calculate number of digits for padding
                int depth = mainForm.GetDepth();
                int digits = depth.ToString().Length;
                string formatString = D(digits);

                // Show progress dialog
                using (var progress = new ProgressFormWithProgress($"Exporting {depth} images..."))
                {
                    progress.Show();

                    // Create task to export in background
                    var task = Task.Run(() => {
                        try
                        {
                            // Export each slice
                            for (int z = 0; z < depth; z++)
                            {
                                // Update progress
                                int percentage = (int)((z + 1) * 100.0 / depth);
                                progress.Report(percentage);

                                // Create the output filename with proper padding
                                string fileName = $"{FilePrefix}{(z + StartingIndex).ToString(formatString)}.{GetFileExtension()}";
                                string outputPath = Path.Combine(OutputFolder, fileName);

                                // Create the slice image
                                using (Bitmap slice = CreateSliceImage(mainForm, z, hasVolume, hasLabels))
                                {
                                    slice.Save(outputPath, ExportFormat);
                                }
                            }

                            return true;
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Error exporting images: {ex.Message}", ex);
                        }
                    });

                    // Handle task completion
                    task.ContinueWith(t => {
                        progress.Close();

                        if (t.IsFaulted)
                        {
                            MessageBox.Show($"Failed to export images: {t.Exception.InnerException?.Message}",
                                "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            MessageBox.Show($"Images exported successfully to: {OutputFolder}",
                                "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error preparing to export images: {ex.Message}",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        private string D(int count)
        {
            return "D" + count;
        }

        private string GetFileExtension()
        {
            if (ExportFormat == ImageFormat.Png) return "png";
            if (ExportFormat == ImageFormat.Jpeg) return "jpg";
            if (ExportFormat == ImageFormat.Bmp) return "bmp";
            if (ExportFormat == ImageFormat.Tiff) return "tif";
            return "png";
        }

        private Bitmap CreateSliceImage(MainForm mainForm, int z, bool includeGrayscale, bool includeMaterials)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();

            // Create a new bitmap
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            // Lock the bitmap for faster processing
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            try
            {
                unsafe
                {
                    byte* ptr = (byte*)bmpData.Scan0;

                    // Process each pixel
                    for (int y = 0; y < height; y++)
                    {
                        byte* row = ptr + (y * bmpData.Stride);

                        for (int x = 0; x < width; x++)
                        {
                            int offset = x * 3;

                            // Default color is black
                            byte r = 0, g = 0, b = 0;

                            // Get grayscale value
                            if (includeGrayscale && mainForm.volumeData != null)
                            {
                                byte gVal = mainForm.volumeData[x, y, z];
                                r = g = b = gVal;
                            }

                            // Apply label color if present
                            if (includeMaterials && mainForm.volumeLabels != null)
                            {
                                byte label = mainForm.volumeLabels[x, y, z];
                                if (label != 0)
                                {
                                    var material = mainForm.Materials.FirstOrDefault(m => m.ID == label);
                                    if (material != null)
                                    {
                                        if (includeGrayscale)
                                        {
                                            // Blend with grayscale
                                            r = (byte)((r + material.Color.R) / 2);
                                            g = (byte)((g + material.Color.G) / 2);
                                            b = (byte)((b + material.Color.B) / 2);
                                        }
                                        else
                                        {
                                            // Just use material color
                                            r = material.Color.R;
                                            g = material.Color.G;
                                            b = material.Color.B;
                                        }
                                    }
                                }
                            }

                            // Write pixel values (BGR order for bitmap)
                            row[offset] = b;
                            row[offset + 1] = g;
                            row[offset + 2] = r;
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return bmp;
        }
    }

}
