//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CTS.NodeEditor
{
    // Base node class
    public abstract class BaseNode
    {
        private Dictionary<string, object> outputData = new Dictionary<string, object>();
        public virtual Dictionary<string, string> GetNodeParameters()
        {
            // Default implementation returns an empty dictionary
            return new Dictionary<string, string>();
        }
        public Point Position { get; set; }
        public Size Size { get; set; }
        public Color Color { get; set; }
        protected List<NodePin> inputs;
        protected List<NodePin> outputs;
        public object Tag { get; set; }
        public BaseNode(Point position)
        {
            Position = position;
            Size = new Size(150, 80);
            Color = Color.FromArgb(100, 100, 150);
            inputs = new List<NodePin>();
            outputs = new List<NodePin>();

            SetupPins();
        }

        public Rectangle Bounds => new Rectangle(Position, Size);

        protected abstract void SetupPins();

        public virtual Control CreatePropertyPanel()
        {
            return null; // Override in derived classes
        }

        public IEnumerable<NodePin> GetAllPins()
        {
            return inputs.Concat(outputs);
        }

        public virtual void Execute()
        {
            // To be implemented in derived classes
        }

        public void SetOutputData(string pinName, object data)
        {
            outputData[pinName] = data;
        }

        // Add a public GetOutputData method to BaseNode that can be used by all nodes
        public virtual object GetOutputData(string pinName)
        {
            if (outputData.ContainsKey(pinName))
                return outputData[pinName];
            return null;
        }

        public object GetInputData(string pinName)
        {
            // Find the input pin with the given name
            var pin = inputs.FirstOrDefault(p => p.Name == pinName);
            if (pin == null)
                return null;

            // Find connections to this input pin
            var incomingConnections = GetIncomingConnections(pin);
            if (!incomingConnections.Any())
                return null;

            // Get data from the first connected output pin
            // Note: Multiple connections to one input are possible, but we'll use the first one by default
            var connection = incomingConnections.First();

            // Get the output data from the source node
            var result = connection.From.Node.GetOutputData(connection.From.Name);

            // Log retrieval for debugging
            if (result != null)
            {
                Logger.Log($"[BaseNode] Got {result.GetType().Name} from {connection.From.Node.GetType().Name}.{connection.From.Name}");
            }
            else
            {
                Logger.Log($"[BaseNode] Warning: Null data from {connection.From.Node.GetType().Name}.{connection.From.Name}");
            }

            return result;
        }

        protected NodePin AddInputPin(string name, Color color)
        {
            var pin = new NodePin(this, false, name, color);
            pin.PositionRelative = new Point(0, 30 + inputs.Count * 20);
            inputs.Add(pin);
            return pin;
        }

        private IEnumerable<NodeConnection> GetIncomingConnections(NodePin pin)
        {
            // Use NodeEditorForm.Instance instead of NodeEditor.Instance
            if (NodeEditorForm.Instance != null)
                return NodeEditorForm.Instance.Connections.Where(c => c.To == pin);
            return Enumerable.Empty<NodeConnection>();
        }

        protected NodePin AddOutputPin(string name, Color color)
        {
            var pin = new NodePin(this, true, name, color);
            pin.PositionRelative = new Point(Size.Width - 10, 30 + outputs.Count * 20);
            outputs.Add(pin);
            return pin;
        }
    }

    // Node pin class
    public class NodePin
    {
        public BaseNode Node { get; set; }
        public bool IsOutput { get; set; }
        public string Name { get; set; }
        public Color Color { get; set; }
        public Point PositionRelative { get; set; }

        public NodePin(BaseNode node, bool isOutput, string name, Color color)
        {
            Node = node;
            IsOutput = isOutput;
            Name = name;
            Color = color;
        }

        public Point AbsolutePosition => new Point(
            Node.Position.X + PositionRelative.X,
            Node.Position.Y + PositionRelative.Y);

        public Rectangle Bounds => new Rectangle(
            AbsolutePosition.X - 5,
            AbsolutePosition.Y - 5,
            10, 10);
    }

    // Node connection class
    public class NodeConnection
    {
        public NodePin From { get; set; }
        public NodePin To { get; set; }

        public NodeConnection(NodePin from, NodePin to)
        {
            From = from;
            To = to;
        }
    }

    #region Dataset Nodes

    public class VolumeDataNode : BaseNode
    {
        public VolumeDataNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddOutputPin("Volume", Color.LightBlue);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Current Volume Dataset",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class LabelDataNode : BaseNode
    {
        public LabelDataNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Current Label Dataset",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class LoadDatasetNode : BaseNode
    {
        public string DatasetPath { get; set; }
        private Button browseButton;
        private TextBox pathTextBox;

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

            browseButton = new Button
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

            panel.Controls.Add(pathContainer);
            panel.Controls.Add(pathLabel);

            return panel;
        }

        private void BrowseForDataset()
        {
            // TODO: Implement dataset browsing
            // For now, just show a placeholder dialog
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
    }

    #endregion

    #region Tool Nodes

    public class ThresholdNode : BaseNode
    {
        public int MinThreshold { get; set; } = 0;
        public int MaxThreshold { get; set; } = 255;

        public ThresholdNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var minLabel = new Label
            {
                Text = "Min Threshold:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var minSlider = new TrackBar
            {
                Dock = DockStyle.Top,
                Minimum = 0,
                Maximum = 255,
                Value = MinThreshold,
                Height = 45
            };
            minSlider.ValueChanged += (s, e) => MinThreshold = minSlider.Value;

            var maxLabel = new Label
            {
                Text = "Max Threshold:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var maxSlider = new TrackBar
            {
                Dock = DockStyle.Top,
                Minimum = 0,
                Maximum = 255,
                Value = MaxThreshold,
                Height = 45
            };
            maxSlider.ValueChanged += (s, e) => MaxThreshold = maxSlider.Value;

            panel.Controls.Add(maxSlider);
            panel.Controls.Add(maxLabel);
            panel.Controls.Add(minSlider);
            panel.Controls.Add(minLabel);

            return panel;
        }
    }

    public class BrushNode : BaseNode
    {
        public int BrushSize { get; set; } = 50;

        public BrushNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Labels", Color.LightCoral);
            AddInputPin("Volume", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var sizeLabel = new Label
            {
                Text = "Brush Size:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var sizeSlider = new TrackBar
            {
                Dock = DockStyle.Top,
                Minimum = 1,
                Maximum = 200,
                Value = BrushSize,
                Height = 45
            };
            sizeSlider.ValueChanged += (s, e) => BrushSize = sizeSlider.Value;

            panel.Controls.Add(sizeSlider);
            panel.Controls.Add(sizeLabel);

            return panel;
        }
    }

    public class EraserNode : BaseNode
    {
        public int EraserSize { get; set; } = 50;

        public EraserNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var sizeLabel = new Label
            {
                Text = "Eraser Size:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var sizeSlider = new TrackBar
            {
                Dock = DockStyle.Top,
                Minimum = 1,
                Maximum = 200,
                Value = EraserSize,
                Height = 45
            };
            sizeSlider.ValueChanged += (s, e) => EraserSize = sizeSlider.Value;

            panel.Controls.Add(sizeSlider);
            panel.Controls.Add(sizeLabel);

            return panel;
        }
    }

    public class SegmentAnythingNode : BaseNode
    {
        public SegmentAnythingNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Segment Anything CT\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class MicroSAMNode : BaseNode
    {
        public MicroSAMNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "MicroSAM Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class GroundingDINONode : BaseNode
    {
        public GroundingDINONode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Grounding DINO\nText prompt:",
                Dock = DockStyle.Top,
                Height = 40,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var textBox = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };

            panel.Controls.Add(textBox);
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class InterpolateNode : BaseNode
    {
        public InterpolateNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Interpolation Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    #endregion

    #region Simulation Nodes

    public class PoreNetworkNode : BaseNode
    {
        public PoreNetworkNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Network", Color.LightGreen);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Pore Network Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class StressAnalysisNode : BaseNode
    {
        public StressAnalysisNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Results", Color.LightGreen);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Stress Analysis Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class AcousticSimulationNode : BaseNode
    {
        public AcousticSimulationNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Results", Color.LightGreen);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Acoustic Simulation Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class TriaxialSimulationNode : BaseNode
    {
        public TriaxialSimulationNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Results", Color.LightGreen);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Triaxial Simulation Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    #endregion

    #region Filter Nodes

    public class BandDetectionNode : BaseNode
    {
        public BandDetectionNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Band Detection Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class TransformDatasetNode : BaseNode
    {
        public TransformDatasetNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Volume", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Transform Dataset Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class CoreExtractionNode : BaseNode
    {
        public CoreExtractionNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Volume", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Core Extraction Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class FilterManagerNode : BaseNode
    {
        public FilterManagerNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddOutputPin("Volume", Color.LightBlue);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Filter Manager Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    #endregion

    #region Material Nodes

    public class AddMaterialNode : BaseNode
    {
        public string MaterialName { get; set; } = "New Material";
        public Color MaterialColor { get; set; } = Color.Blue;

        public AddMaterialNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var nameLabel = new Label
            {
                Text = "Material Name:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var nameBox = new TextBox
            {
                Text = MaterialName,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            nameBox.TextChanged += (s, e) => MaterialName = nameBox.Text;

            var colorButton = new Button
            {
                Text = "Choose Color",
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            colorButton.Click += (s, e) =>
            {
                using (var dialog = new ColorDialog())
                {
                    dialog.Color = MaterialColor;
                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        MaterialColor = dialog.Color;
                    }
                }
            };

            panel.Controls.Add(colorButton);
            panel.Controls.Add(nameBox);
            panel.Controls.Add(nameLabel);

            return panel;
        }
    }

    public class RemoveMaterialNode : BaseNode
    {
        public RemoveMaterialNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Remove Material Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class MergeMaterialsNode : BaseNode
    {
        public MergeMaterialsNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Merge Materials Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    public class ExtractMaterialNode : BaseNode
    {
        public ExtractMaterialNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Extract Material Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    #endregion

    #region Output Nodes

    public class ExportImagesNode : BaseNode
    {
        public string OutputPath { get; set; }

        public ExportImagesNode(Point position) : base(position) { }

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

            var pathLabel = new Label
            {
                Text = "Output Path:",
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

            var pathBox = new TextBox
            {
                Text = OutputPath ?? "",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };

            var browseButton = new Button
            {
                Text = "Browse",
                Dock = DockStyle.Right,
                Width = 80,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            browseButton.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        OutputPath = dialog.SelectedPath;
                        pathBox.Text = OutputPath;
                    }
                }
            };

            pathContainer.Controls.Add(pathBox);
            pathContainer.Controls.Add(browseButton);

            panel.Controls.Add(pathContainer);
            panel.Controls.Add(pathLabel);

            return panel;
        }
    }

    public class SaveDatasetNode : BaseNode
    {
        public string SavePath { get; set; }

        public SaveDatasetNode(Point position) : base(position) { }

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

            var pathBox = new TextBox
            {
                Text = SavePath ?? "",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };

            var browseButton = new Button
            {
                Text = "Browse",
                Dock = DockStyle.Right,
                Width = 80,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            browseButton.Click += (s, e) =>
            {
                using (var dialog = new SaveFileDialog())
                {
                    dialog.Filter = "Binary Volume|*.bin|All Files|*.*";
                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        SavePath = dialog.FileName;
                        pathBox.Text = SavePath;
                    }
                }
            };

            pathContainer.Controls.Add(pathBox);
            pathContainer.Controls.Add(browseButton);

            panel.Controls.Add(pathContainer);
            panel.Controls.Add(pathLabel);

            return panel;
        }
    }

    public class StatisticsNode : BaseNode
    {
        public StatisticsNode(Point position) : base(position) { }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Results", Color.LightGreen);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var label = new Label
            {
                Text = "Statistics Settings\nConfigure settings here",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };
            panel.Controls.Add(label);
            return panel;
        }
    }

    #endregion
}