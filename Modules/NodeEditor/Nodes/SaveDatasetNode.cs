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
    public class SaveDatasetNode : BaseNode
    {
        public string SavePath { get; set; }
        private TextBox pathTextBox;

        public SaveDatasetNode(Point position) : base(position)
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
                Text = "Save Complete Dataset",
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
                Text = "Save Now",
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
                Text = "Saves both volume data and labels to a single dataset file.",
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
                dialog.Filter = "Binary Volume|*.bin|All Files|*.*";
                dialog.Title = "Save Dataset";
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
                if (mainForm == null || mainForm.volumeData == null || mainForm.volumeLabels == null)
                {
                    MessageBox.Show("No dataset is currently loaded to save.",
                        "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Show progress dialog
                using (var progress = new ProgressFormWithProgress("Saving dataset..."))
                {
                    progress.Show();

                    // Create task to save in background
                    var task = Task.Run(() => {
                        try
                        {
                            // Save the dataset using FileOperations
                            FileOperations.SaveBinary(
                                SavePath,
                                mainForm.volumeData,
                                mainForm.volumeLabels,
                                mainForm.Materials,
                                mainForm.GetWidth(),
                                mainForm.GetHeight(),
                                mainForm.GetDepth(),
                                mainForm.GetPixelSize());

                            return true;
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Error saving dataset: {ex.Message}", ex);
                        }
                    });

                    // Handle task completion
                    task.ContinueWith(t => {
                        progress.Close();

                        if (t.IsFaulted)
                        {
                            MessageBox.Show($"Failed to save dataset: {t.Exception.InnerException?.Message}",
                                "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            MessageBox.Show($"Dataset saved successfully to: {SavePath}",
                                "Save Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error preparing to save dataset: {ex.Message}",
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

}
