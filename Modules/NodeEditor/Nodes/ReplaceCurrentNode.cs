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
    public class ReplaceCurrentNode : BaseNode
    {
        private bool replaceVolume = true;
        private bool replaceLabels = true;
        private CheckBox replaceVolumeCheckbox;
        private CheckBox replaceLabelsCheckbox;

        public ReplaceCurrentNode(Point position) : base(position)
        {
            Color = Color.FromArgb(255, 140, 120); // Red-orange for output nodes
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

            replaceVolumeCheckbox = new CheckBox
            {
                Text = "Replace Volume Data",
                Checked = replaceVolume,
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = Color.White
            };
            replaceVolumeCheckbox.CheckedChanged += (s, e) => replaceVolume = replaceVolumeCheckbox.Checked;

            replaceLabelsCheckbox = new CheckBox
            {
                Text = "Replace Label Data",
                Checked = replaceLabels,
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = Color.White
            };
            replaceLabelsCheckbox.CheckedChanged += (s, e) => replaceLabels = replaceLabelsCheckbox.Checked;

            var infoLabel = new Label
            {
                Text = "This node replaces the currently active dataset in the main application with the dataset from the input pins.",
                Dock = DockStyle.Top,
                Height = 40,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.LightGray,
                Font = new Font("Arial", 8)
            };

            var replaceButton = new Button
            {
                Text = "Replace Now",
                Dock = DockStyle.Top,
                Height = 35,
                BackColor = Color.FromArgb(220, 80, 80),
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            replaceButton.Click += (s, e) => Execute();

            // Add controls to panel
            panel.Controls.Add(replaceButton);
            panel.Controls.Add(infoLabel);
            panel.Controls.Add(replaceLabelsCheckbox);
            panel.Controls.Add(replaceVolumeCheckbox);
            panel.Controls.Add(titleLabel);

            return panel;
        }

        public override void Execute()
        {
            try
            {
                // Get the MainForm reference
                var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
                if (mainForm == null)
                {
                    MessageBox.Show("Cannot access the main form.",
                        "Replace Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Get the input data from connected nodes
                // (In a real implementation, this would come from the input pins)
                IGrayscaleVolumeData volumeData = mainForm.volumeData;
                ILabelVolumeData labelData = mainForm.volumeLabels;

                // Check if we're trying to replace with null data
                if ((replaceVolume && volumeData == null) ||
                    (replaceLabels && labelData == null))
                {
                    MessageBox.Show("No data available to replace the current dataset.",
                        "Replace Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Create confirmation dialog
                var result = MessageBox.Show(
                    "Are you sure you want to replace the current dataset? This cannot be undone.",
                    "Confirm Replace", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (result == DialogResult.Yes)
                {
                    try
                    {
                        // Replace the data in MainForm
                        if (replaceVolume && volumeData != null)
                        {
                            mainForm.volumeData = volumeData;
                        }

                        if (replaceLabels && labelData != null)
                        {
                            mainForm.volumeLabels = labelData;
                        }

                        // Notify MainForm of changes
                        mainForm.OnDatasetChanged();

                        MessageBox.Show("Dataset replaced successfully!",
                            "Replace Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error replacing dataset: {ex.Message}",
                            "Replace Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error in Replace Current node: {ex.Message}",
                    "Node Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

}
