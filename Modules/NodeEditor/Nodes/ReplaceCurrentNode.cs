using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class ReplaceCurrentNode : BaseNode
    {
        private MainForm mainForm;

        public ReplaceCurrentNode(Point position) : base(position)
        {
            Color = Color.FromArgb(255, 140, 120); // Red theme for output nodes
            mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
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
                Text = "Replace Current Dataset",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            var descriptionLabel = new Label
            {
                Text = "Replaces the current dataset in the main viewer with the connected input dataset.",
                Dock = DockStyle.Top,
                Height = 40,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                AutoSize = false
            };

            var executeButton = new Button
            {
                Text = "Replace Now",
                Dock = DockStyle.Top,
                Height = 35,
                Margin = new Padding(5, 10, 5, 0),
                BackColor = Color.FromArgb(200, 80, 80),
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            executeButton.Click += (s, e) => Execute();

            panel.Controls.Add(executeButton);
            panel.Controls.Add(descriptionLabel);
            panel.Controls.Add(titleLabel);

            return panel;
        }

        public override void Execute()
        {
            if (mainForm == null)
            {
                Logger.Log("[ReplaceCurrentNode] ERROR: No MainForm reference");
                return;
            }

            try
            {
                // Get the volume data from the connected node
                var volumeData = GetInputData("Volume");
                if (volumeData == null)
                {
                    Logger.Log("[ReplaceCurrentNode] Warning: No volume data provided");
                    return;
                }

                // Make sure we have the right type
                if (volumeData is IGrayscaleVolumeData grayscaleData)
                {
                    // Check if it's the specific ChunkedVolume type we need
                    if (grayscaleData is ChunkedVolume chunkedVolume)
                    {
                        // Update the MainForm with the new volume data
                        Logger.Log($"[ReplaceCurrentNode] Replacing current volume with {chunkedVolume.Width}x{chunkedVolume.Height}x{chunkedVolume.Depth} volume");
                        mainForm.UpdateVolumeData(chunkedVolume);

                        // Also get label data if available
                        var labelData = GetInputData("Labels") as ILabelVolumeData;
                        if (labelData != null)
                        {
                            // Update the label volume in MainForm
                            if (mainForm.volumeLabels != null)
                            {
                                // Release the old labels file lock if it exists
                                mainForm.volumeLabels.ReleaseFileLock();
                                mainForm.volumeLabels.Dispose();
                            }

                            // Set the new label data
                            mainForm.volumeLabels = labelData;

                            // Make sure the UI is updated to reflect all changes
                            mainForm.OnDatasetChanged();

                            Logger.Log("[ReplaceCurrentNode] Updated label data successfully");
                        }
                    }
                    else
                    {
                        Logger.Log($"[ReplaceCurrentNode] ERROR: Volume data is {grayscaleData.GetType().Name}, but ChunkedVolume expected");
                    }
                }
                else
                {
                    Logger.Log($"[ReplaceCurrentNode] ERROR: Invalid volume data type: {volumeData.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ReplaceCurrentNode] ERROR: Exception replacing dataset: {ex.Message}");
            }
        }
    }
}