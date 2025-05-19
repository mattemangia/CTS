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
    public class BrightnessContrastNode : BaseNode
    {
        // Adjustment parameters
        public int Brightness { get; set; } = 0;
        public int Contrast { get; set; } = 100;
        public byte BlackPoint { get; set; } = 0;
        public byte WhitePoint { get; set; } = 255;

        // UI controls
        private NumericUpDown brightnessNumeric;
        private NumericUpDown contrastNumeric;
        private NumericUpDown blackPointNumeric;
        private NumericUpDown whitePointNumeric;

        public BrightnessContrastNode(Point position) : base(position)
        {
            Color = Color.FromArgb(100, 150, 255); // Blue theme for processing nodes
        }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddOutputPin("Volume", Color.LightBlue);
        }

        public override Dictionary<string, string> GetNodeParameters()
        {
            var parameters = new Dictionary<string, string>
            {
                ["Brightness"] = Brightness.ToString(),
                ["Contrast"] = Contrast.ToString(),
                ["BlackPoint"] = BlackPoint.ToString(),
                ["WhitePoint"] = WhitePoint.ToString()
            };
            return parameters;
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            // Title
            var titleLabel = new Label
            {
                Text = "Brightness & Contrast",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            // Brightness controls
            var brightnessLabel = new Label
            {
                Text = "Brightness:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            brightnessNumeric = new NumericUpDown
            {
                Minimum = -128,
                Maximum = 128,
                Value = Brightness,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            brightnessNumeric.ValueChanged += (s, e) => Brightness = (int)brightnessNumeric.Value;

            // Contrast controls
            var contrastLabel = new Label
            {
                Text = "Contrast:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            contrastNumeric = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 200,
                Value = Contrast,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            contrastNumeric.ValueChanged += (s, e) => Contrast = (int)contrastNumeric.Value;

            // Black point controls
            var blackPointLabel = new Label
            {
                Text = "Black Point:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            blackPointNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 254,
                Value = BlackPoint,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            blackPointNumeric.ValueChanged += (s, e) => {
                BlackPoint = (byte)blackPointNumeric.Value;
                // Ensure blackPoint < whitePoint
                if (BlackPoint >= WhitePoint)
                {
                    WhitePoint = (byte)Math.Min(255, BlackPoint + 1);
                    whitePointNumeric.Value = WhitePoint;
                }
            };

            // White point controls
            var whitePointLabel = new Label
            {
                Text = "White Point:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            whitePointNumeric = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 255,
                Value = WhitePoint,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            whitePointNumeric.ValueChanged += (s, e) => {
                WhitePoint = (byte)whitePointNumeric.Value;
                // Ensure blackPoint < whitePoint
                if (BlackPoint >= WhitePoint)
                {
                    BlackPoint = (byte)Math.Max(0, WhitePoint - 1);
                    blackPointNumeric.Value = BlackPoint;
                }
            };

            // Process button for manual testing
            var processButton = new Button
            {
                Text = "Process Dataset",
                Dock = DockStyle.Top,
                Height = 35,
                Margin = new Padding(5, 10, 5, 0),
                BackColor = Color.FromArgb(100, 180, 100), // Green for process
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            processButton.Click += (s, e) => Execute();

            // Add controls to panel (in reverse order because of DockStyle.Top)
            panel.Controls.Add(processButton);
            panel.Controls.Add(whitePointNumeric);
            panel.Controls.Add(whitePointLabel);
            panel.Controls.Add(blackPointNumeric);
            panel.Controls.Add(blackPointLabel);
            panel.Controls.Add(contrastNumeric);
            panel.Controls.Add(contrastLabel);
            panel.Controls.Add(brightnessNumeric);
            panel.Controls.Add(brightnessLabel);
            panel.Controls.Add(titleLabel);

            return panel;
        }

        public override void Execute()
        {
            try
            {
                // Get the input volume data using the standard BaseNode.GetInputData method
                var inputVolumeData = GetInputData("Volume") as IGrayscaleVolumeData;

                if (inputVolumeData == null)
                {
                    Logger.Log("[BrightnessContrastNode] No volume data available to process.");
                    return;
                }

                // Process the dataset with current adjustment settings
                var outputVolumeData = ProcessVolume(
                    inputVolumeData,
                    Brightness,
                    Contrast,
                    BlackPoint,
                    WhitePoint);

                // Set the output data using the proper BaseNode.SetOutputData method
                SetOutputData("Volume", outputVolumeData);

                Logger.Log("[BrightnessContrastNode] Dataset processed successfully.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BrightnessContrastNode] Error processing dataset: {ex.Message}");
            }
        }

        // Process a volume with brightness/contrast adjustments
        private ChunkedVolume ProcessVolume(
            IGrayscaleVolumeData inputVolume,
            int brightness,
            int contrast,
            byte blackPoint,
            byte whitePoint)
        {
            // Create a new volume to hold the adjusted data
            int width = inputVolume.Width;
            int height = inputVolume.Height;
            int depth = inputVolume.Depth;

            // Use 64 as the default chunk dimension if we can't determine it from the input
            int chunkDim = 64;

            // Try to get the chunk dimension from the input if it's a ChunkedVolume
            if (inputVolume is ChunkedVolume chunkedInput)
            {
                chunkDim = chunkedInput.ChunkDim;
            }

            ChunkedVolume newVolume = new ChunkedVolume(width, height, depth, chunkDim);

            // Process all slices
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte origValue = inputVolume[x, y, z];

                        // Apply adjustment using the same algorithm from BrightnessContrastForm
                        int adjustedValue = ApplyAdjustment(origValue, blackPoint, whitePoint, brightness, contrast);
                        newVolume[x, y, z] = (byte)Math.Max(0, Math.Min(255, adjustedValue));
                    }
                }
            }

            Logger.Log($"[BrightnessContrastNode] Processed volume with brightness={brightness}, contrast={contrast}, blackPoint={blackPoint}, whitePoint={whitePoint}");
            return newVolume;
        }

        // Implement the same adjustment algorithm used in BrightnessContrastForm
        private int ApplyAdjustment(byte value, byte bPoint, byte wPoint, int bright, int cont)
        {
            // Map the value from [blackPoint, whitePoint] to [0, 255]
            double normalized = 0;
            if (wPoint > bPoint)
            {
                normalized = (value - bPoint) / (double)(wPoint - bPoint);
            }
            normalized = Math.Max(0, Math.Min(1, normalized));

            // Apply contrast (percentage)
            double contrasted = (normalized - 0.5) * (cont / 100.0) + 0.5;
            contrasted = Math.Max(0, Math.Min(1, contrasted));

            // Apply brightness (offset)
            int result = (int)(contrasted * 255) + bright;
            return result;
        }
    }
}