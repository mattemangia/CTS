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
    public class CurrentDatasetNode : BaseNode
    {
        private MainForm mainForm;
        private Label dimensionsLabel;
        private Label pixelSizeLabel;

        // Properties to expose data
        public IGrayscaleVolumeData VolumeData => mainForm?.volumeData;
        public ILabelVolumeData LabelData => mainForm?.volumeLabels;
        public double PixelSize => mainForm?.pixelSize ?? 1e-6;
        public int Width => mainForm?.GetWidth() ?? 0;
        public int Height => mainForm?.GetHeight() ?? 0;
        public int Depth => mainForm?.GetDepth() ?? 0;

        public CurrentDatasetNode(Point position) : base(position)
        {
            mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        }

        protected override void SetupPins()
        {
            // Output pins for the volume and label data
            AddOutputPin("Volume", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
            AddOutputPin("PixelSize", Color.LightGreen);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var titleLabel = new Label
            {
                Text = "Current Dataset Reference",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            dimensionsLabel = new Label
            {
                Text = GetDimensionsText(),
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            pixelSizeLabel = new Label
            {
                Text = GetPixelSizeText(),
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var statusLabel = new Label
            {
                Text = IsDatasetLoaded() ? "Status: Dataset Loaded" : "Status: No Dataset Loaded",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = IsDatasetLoaded() ? Color.LightGreen : Color.Red
            };

            var refreshButton = new Button
            {
                Text = "Refresh",
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            refreshButton.Click += (s, e) => {
                UpdateDatasetInfo();
            };

            // Add controls to panel (in reverse order because of DockStyle.Top)
            panel.Controls.Add(refreshButton);
            panel.Controls.Add(statusLabel);
            panel.Controls.Add(pixelSizeLabel);
            panel.Controls.Add(dimensionsLabel);
            panel.Controls.Add(titleLabel);

            return panel;
        }

        // Override the GetOutputData method with this implementation
        public override object GetOutputData(string pinName)
        {
            switch (pinName)
            {
                case "Volume":
                    return VolumeData;
                case "Labels":
                    return LabelData;
                case "PixelSize":
                    return PixelSize;
                default:
                    return base.GetOutputData(pinName);
            }
        }

        public override Dictionary<string, string> GetNodeParameters()
        {
            var parameters = new Dictionary<string, string>();

            // Add dimensions if dataset is available
            if (IsDatasetLoaded())
            {
                parameters["Width"] = Width.ToString();
                parameters["Height"] = Height.ToString();
                parameters["Depth"] = Depth.ToString();
                parameters["PixelSize"] = PixelSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return parameters;
        }

        private bool IsDatasetLoaded()
        {
            return mainForm != null && mainForm.volumeData != null;
        }

        private string GetDimensionsText()
        {
            if (IsDatasetLoaded())
            {
                return $"Dimensions: {Width} × {Height} × {Depth}";
            }
            return "Dimensions: Not available";
        }

        private string GetPixelSizeText()
        {
            if (IsDatasetLoaded())
            {
                double micrometers = PixelSize * 1e6;
                return $"Pixel Size: {micrometers:F3} µm";
            }
            return "Pixel Size: Not available";
        }

        private void UpdateDatasetInfo()
        {
            if (dimensionsLabel != null)
            {
                dimensionsLabel.Text = GetDimensionsText();
            }

            if (pixelSizeLabel != null)
            {
                pixelSizeLabel.Text = GetPixelSizeText();
            }
        }

        public override void Execute()
        {
            // Check if the node is connected to MainForm and has data
            if (!IsDatasetLoaded())
            {
                Logger.Log("[CurrentDatasetNode] No dataset is currently loaded in the main application.");
                return;
            }

            // Store data in the output data dictionary so other nodes can access it

            SetOutputData("Volume", VolumeData);
            SetOutputData("Labels", LabelData);
            SetOutputData("PixelSize", PixelSize);

            UpdateDatasetInfo();

            Logger.Log("[CurrentDatasetNode] Successfully provided dataset reference");
        }
    }
}