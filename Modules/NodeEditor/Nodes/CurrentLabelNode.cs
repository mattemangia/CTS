using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class CurrentLabelNode : BaseNode
    {
        // Properties that will be accessed by other nodes
        public ILabelVolumeData LabelData { get; private set; }
        public List<Material> Materials { get; private set; }

        private ListBox materialsList;
        private Label dimensionsLabel;

        public CurrentLabelNode(Point position) : base(position)
        {
            Color = Color.FromArgb(120, 200, 120); // Green theme for input nodes
            UpdateDataFromMainForm();
        }

        protected override void SetupPins()
        {
            // This node only has outputs since it's a data source
            AddOutputPin("Labels", Color.LightCoral);
            AddOutputPin("Materials", Color.Orange);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48),
                AutoScroll = true
            };

            var titleLabel = new Label
            {
                Text = "Current Label Dataset",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            dimensionsLabel = new Label
            {
                Text = "Dimensions: Not Available",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var infoLabel = new Label
            {
                Text = "Accesses the label dataset currently loaded in the main application.",
                Dock = DockStyle.Top,
                Height = 40,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.LightGray,
                Font = new Font("Arial", 8)
            };

            var updateButton = new Button
            {
                Text = "Refresh Data",
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Margin = new Padding(5, 10, 5, 5)
            };
            updateButton.Click += (s, e) => {
                UpdateDataFromMainForm();
                RefreshDisplay();
            };

            // Materials list display
            var materialsLabel = new Label
            {
                Text = "Materials:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Margin = new Padding(0, 10, 0, 0)
            };

            materialsList = new ListBox
            {
                Dock = DockStyle.Top,
                Height = 150,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                DrawMode = DrawMode.OwnerDrawFixed
            };
            materialsList.DrawItem += MaterialsList_DrawItem;

            // Add controls to panel (in reverse order due to DockStyle.Top)
            panel.Controls.Add(materialsList);
            panel.Controls.Add(materialsLabel);
            panel.Controls.Add(updateButton);
            panel.Controls.Add(dimensionsLabel);
            panel.Controls.Add(infoLabel);
            panel.Controls.Add(titleLabel);

            // Update the display with current data
            RefreshDisplay();

            return panel;
        }

        private void MaterialsList_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || Materials == null || e.Index >= Materials.Count)
                return;

            e.DrawBackground();
            Material mat = Materials[e.Index];

            // Draw material color swatch
            using (SolidBrush b = new SolidBrush(mat.Color))
                e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y, 20, e.Bounds.Height);

            // Choose text color based on whether it's exterior
            Color textColor = mat.IsExterior ? Color.Red : Color.White;

            // Draw material info
            using (SolidBrush textBrush = new SolidBrush(textColor))
                e.Graphics.DrawString($"{mat.Name} [ID: {mat.ID}]", e.Font, textBrush,
                    e.Bounds.X + 25, e.Bounds.Y + 2);

            e.DrawFocusRectangle();
        }

        private void RefreshDisplay()
        {
            materialsList.Items.Clear();

            if (LabelData != null)
            {
                dimensionsLabel.Text = $"Dimensions: {LabelData.Width}×{LabelData.Height}×{LabelData.Depth}";
            }
            else
            {
                dimensionsLabel.Text = "Dimensions: Not Available";
            }

            if (Materials != null)
            {
                foreach (var material in Materials)
                {
                    materialsList.Items.Add(material);
                }
            }
        }

        public void UpdateDataFromMainForm()
        {
            var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
            if (mainForm != null)
            {
                // Get the label volume and materials from the main form
                LabelData = mainForm.volumeLabels;
                Materials = new List<Material>(mainForm.Materials);

                Logger.Log($"[CurrentLabelNode] Updated data from MainForm: " +
                          $"Label data {(LabelData != null ? "available" : "not available")}, " +
                          $"Materials: {Materials.Count}");
            }
            else
            {
                Logger.Log("[CurrentLabelNode] Warning: Could not find MainForm");
                LabelData = null;
                Materials = new List<Material>();
            }
        }

        public override void Execute()
        {
            // Refresh data from main form to ensure we have the latest
            UpdateDataFromMainForm();
            Logger.Log("[CurrentLabelNode] Execute called, data refreshed from MainForm");
        }
    }
}
