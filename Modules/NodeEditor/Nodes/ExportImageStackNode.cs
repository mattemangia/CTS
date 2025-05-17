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

        // Data model classes to store volume data from input pins
        private class VolumeData
        {
            public byte[,,] Data { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Depth { get; set; }
        }

        private class MaterialData
        {
            public int ID { get; set; }
            public Color Color { get; set; }
        }

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
                // Get data from input pins
                var volumeData = GetInputData("Volume") as VolumeData;
                var labelsData = GetInputData("Labels") as VolumeData;
                var materials = GetMaterialsFromLabels(labelsData);

                bool hasVolume = volumeData != null && IncludeGrayscale;
                bool hasLabels = labelsData != null && IncludeMaterials;

                if (!hasVolume && !hasLabels)
                {
                    MessageBox.Show("No data is available to export. Please connect valid data sources.",
                        "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Create output directory if it doesn't exist
                if (!Directory.Exists(OutputFolder))
                {
                    Directory.CreateDirectory(OutputFolder);
                }

                // Get dimensions from the data
                int width, height, depth;
                if (hasVolume)
                {
                    width = volumeData.Width;
                    height = volumeData.Height;
                    depth = volumeData.Depth;
                }
                else
                {
                    width = labelsData.Width;
                    height = labelsData.Height;
                    depth = labelsData.Depth;
                }

                // Calculate number of digits for padding
                int digits = depth.ToString().Length;
                string formatString = $"D{digits}";

                // Show progress dialog
                using (var progress = new ProgressDialog($"Exporting {depth} images..."))
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
                                progress.ReportProgress(percentage);

                                // Create the output filename with proper padding
                                string fileName = $"{FilePrefix}{(z + StartingIndex).ToString(formatString)}.{GetFileExtension()}";
                                string outputPath = Path.Combine(OutputFolder, fileName);

                                // Create the slice image
                                using (Bitmap slice = CreateSliceImage(volumeData, labelsData, materials, z, width, height, hasVolume, hasLabels))
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

        private string GetFileExtension()
        {
            if (ExportFormat == ImageFormat.Png) return "png";
            if (ExportFormat == ImageFormat.Jpeg) return "jpg";
            if (ExportFormat == ImageFormat.Bmp) return "bmp";
            if (ExportFormat == ImageFormat.Tiff) return "tif";
            return "png";
        }

        private Dictionary<byte, MaterialData> GetMaterialsFromLabels(VolumeData labelsData)
        {
            var result = new Dictionary<byte, MaterialData>();

            if (labelsData == null)
                return result;

            // Try to get materials from the connected label node
            var labelNodeData = GetInputData("Labels") as Dictionary<string, object>;

            if (labelNodeData != null && labelNodeData.ContainsKey("Materials"))
            {
                var materials = labelNodeData["Materials"] as List<Material>;
                if (materials != null && materials.Count > 0)
                {
                    // Convert the Material objects to our internal MaterialData representation
                    foreach (var material in materials)
                    {
                        if (material.ID > 0 || material.IsExterior) // Include exterior (ID 0) and all other materials
                        {
                            result[material.ID] = new MaterialData
                            {
                                ID = material.ID,
                                Color = material.Color
                            };
                        }
                    }
                    return result;
                }
            }

            // Fallback: If no materials were found from the input node,
            // extract unique material IDs from the label data and create dummy materials
            var uniqueLabels = new HashSet<byte>();

            // Sample the volume to find unique labels
            for (int z = 0; z < labelsData.Depth; z += Math.Max(1, labelsData.Depth / 10))
            {
                for (int y = 0; y < labelsData.Height; y += Math.Max(1, labelsData.Height / 10))
                {
                    for (int x = 0; x < labelsData.Width; x += Math.Max(1, labelsData.Width / 10))
                    {
                        byte label = labelsData.Data[x, y, z];
                        if (label > 0)
                            uniqueLabels.Add(label);
                    }
                }
            }

            // Create a random but consistent color for each unique label
            var random = new Random(42); // Fixed seed for consistent colors

            foreach (byte label in uniqueLabels)
            {
                // Create a deterministic color based on the label ID
                int hue = (label * 137) % 360; // Use prime number to spread out the hues
                Color color = ColorFromHSV(hue, 0.8, 0.9);

                result[label] = new MaterialData
                {
                    ID = label,
                    Color = color
                };
            }

            // Always include exterior material (ID 0) with transparent color
            if (!result.ContainsKey(0))
            {
                result[0] = new MaterialData
                {
                    ID = 0,
                    Color = Color.Transparent
                };
            }

            return result;
        }

        // Helper method to create a color from HSV values
        private Color ColorFromHSV(double hue, double saturation, double value)
        {
            int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
            double f = hue / 60 - Math.Floor(hue / 60);

            value = value * 255;
            int v = Convert.ToInt32(value);
            int p = Convert.ToInt32(value * (1 - saturation));
            int q = Convert.ToInt32(value * (1 - f * saturation));
            int t = Convert.ToInt32(value * (1 - (1 - f) * saturation));

            if (hi == 0)
                return Color.FromArgb(255, v, t, p);
            else if (hi == 1)
                return Color.FromArgb(255, q, v, p);
            else if (hi == 2)
                return Color.FromArgb(255, p, v, t);
            else if (hi == 3)
                return Color.FromArgb(255, p, q, v);
            else if (hi == 4)
                return Color.FromArgb(255, t, p, v);
            else
                return Color.FromArgb(255, v, p, q);
        }

        private Bitmap CreateSliceImage(VolumeData volumeData, VolumeData labelsData,
                                      Dictionary<byte, MaterialData> materials,
                                      int z, int width, int height,
                                      bool includeGrayscale, bool includeMaterials)
        {
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
                            if (includeGrayscale && volumeData != null)
                            {
                                byte gVal = volumeData.Data[x, y, z];
                                r = g = b = gVal;
                            }

                            // Apply label color if present
                            if (includeMaterials && labelsData != null)
                            {
                                byte label = labelsData.Data[x, y, z];
                                if (label != 0 && materials.TryGetValue(label, out var material))
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

        // Basic progress dialog implementation
        private class ProgressDialog : Form
        {
            private ProgressBar progressBar;
            private Label statusLabel;

            public ProgressDialog(string title)
            {
                this.Text = title;
                this.Size = new Size(400, 120);
                this.FormBorderStyle = FormBorderStyle.FixedDialog;
                this.StartPosition = FormStartPosition.CenterScreen;
                this.ControlBox = false;

                statusLabel = new Label
                {
                    Text = "Preparing...",
                    Dock = DockStyle.Top,
                    Height = 30,
                    TextAlign = ContentAlignment.MiddleCenter
                };

                progressBar = new ProgressBar
                {
                    Dock = DockStyle.Fill,
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0,
                    Style = ProgressBarStyle.Continuous
                };

                this.Controls.Add(progressBar);
                this.Controls.Add(statusLabel);
            }

            public void ReportProgress(int percentage)
            {
                if (this.InvokeRequired)
                {
                    this.Invoke(new Action<int>(ReportProgress), percentage);
                }
                else
                {
                    progressBar.Value = Math.Min(100, Math.Max(0, percentage));
                    statusLabel.Text = $"Exporting: {percentage}%";
                }
            }
        }
    }
}