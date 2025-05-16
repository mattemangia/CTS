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
    public class SaveLabelsNode : BaseNode
    {
        public string SavePath { get; set; }
        private TextBox pathTextBox;

        public SaveLabelsNode(Point position) : base(position)
        {
            Color = Color.FromArgb(255, 120, 120); // Red theme for output nodes
        }

        protected override void SetupPins()
        {
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
                Text = "Save Labels Only",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            var pathLabel = new Label
            {
                Text = "Save Path:",
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
                Text = SavePath ?? "",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            pathTextBox.TextChanged += (s, e) => SavePath = pathTextBox.Text;

            var browseButton = new Button
            {
                Text = "Browse",
                Dock = DockStyle.Right,
                Width = 80,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            browseButton.Click += (s, e) => BrowseForSavePath();

            var saveButton = new Button
            {
                Text = "Save Labels Now",
                Dock = DockStyle.Top,
                Height = 35,
                BackColor = Color.FromArgb(100, 180, 100), // Green for save
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            saveButton.Click += (s, e) => Execute();

            pathContainer.Controls.Add(pathTextBox);
            pathContainer.Controls.Add(browseButton);

            var descriptionLabel = new Label
            {
                Text = "Saves only the label data (segmentation) to a file.",
                Dock = DockStyle.Top,
                Height = 40,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.LightGray
            };

            // Add controls to panel (in reverse order because of DockStyle.Top)
            panel.Controls.Add(saveButton);
            panel.Controls.Add(pathContainer);
            panel.Controls.Add(pathLabel);
            panel.Controls.Add(descriptionLabel);
            panel.Controls.Add(titleLabel);

            return panel;
        }

        private void BrowseForSavePath()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "Binary Labels|*.bin|All Files|*.*";
                dialog.Title = "Save Labels";
                dialog.DefaultExt = "bin";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    SavePath = dialog.FileName;
                    if (pathTextBox != null)
                    {
                        pathTextBox.Text = SavePath;
                    }
                }
            }
        }

        public override void Execute()
        {
            if (string.IsNullOrEmpty(SavePath))
            {
                MessageBox.Show("Please specify a save path first.",
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Get MainForm reference to access the dataset
                var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
                if (mainForm == null || mainForm.volumeLabels == null)
                {
                    MessageBox.Show("No label data is currently loaded to save.",
                        "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Show progress dialog
                using (var progress = new ProgressFormWithProgress("Saving labels..."))
                {
                    progress.Show();

                    // Create task to save in background
                    var task = Task.Run(() => {
                        try
                        {
                            // Save the labels using direct file operations
                            string directory = Path.GetDirectoryName(SavePath);
                            if (!Directory.Exists(directory))
                            {
                                Directory.CreateDirectory(directory);
                            }

                            // Create or update the labels.chk file in the same directory
                            FileOperations.CreateLabelsChk(directory, mainForm.Materials);

                            // Save labels.bin
                            using (FileStream fs = new FileStream(SavePath, FileMode.Create, FileAccess.Write))
                            using (BinaryWriter writer = new BinaryWriter(fs))
                            {
                                mainForm.volumeLabels.WriteChunks(writer);
                            }

                            return true;
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Error saving labels: {ex.Message}", ex);
                        }
                    });

                    // Handle task completion
                    task.ContinueWith(t => {
                        progress.Close();

                        if (t.IsFaulted)
                        {
                            MessageBox.Show($"Failed to save labels: {t.Exception.InnerException?.Message}",
                                "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            MessageBox.Show($"Labels saved successfully to: {SavePath}",
                                "Save Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error preparing to save labels: {ex.Message}",
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

}
