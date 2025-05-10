using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Krypton.Toolkit;
using Krypton.Navigator;

namespace CTS.NodeEditor
{
    public class NodeEditorForm : KryptonPanel
    {
        private MainForm mainForm;
        private Panel canvasPanel;
        private Panel toolboxPanel;
        private Panel propertiesPanel;
        private List<BaseNode> nodes;
        private List<NodeConnection> connections;
        private BaseNode selectedNode;
        private List<BaseNode> selectedNodes; // For multi-selection
        private Point dragOffset;
        private bool isDragging;
        private bool isConnecting;
        private NodePin connectingPin;
        private bool isMultiSelecting;
        private Point selectionStart;
        private Rectangle selectionRectangle;

        // For copy/paste
        private List<BaseNode> copiedNodes;
        private List<NodeConnection> copiedConnections;

        public NodeEditorForm(MainForm mainForm)
        {
            this.mainForm = mainForm;
            nodes = new List<BaseNode>();
            connections = new List<NodeConnection>();
            selectedNodes = new List<BaseNode>();
            copiedNodes = new List<BaseNode>();
            copiedConnections = new List<NodeConnection>();

            InitializeComponent();
            InitializeMenuBar();
            SetupNodeTypes();
            SetupKeyboardHandling();
        }

        private void InitializeComponent()
        {
            this.Text = "Node Editor";
            this.Size = new Size(1200, 800);

            // Create the main layout
            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1
            };

            // Add column styles
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));

            // Create toolbox panel
            toolboxPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                BorderStyle = BorderStyle.Fixed3D
            };

            var toolboxLabel = new Label
            {
                Text = "Node Toolbox",
                Dock = DockStyle.Top,
                Height = 30,
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(35, 35, 38)
            };

            toolboxPanel.Controls.Add(toolboxLabel);

            // Create canvas panel
            canvasPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                BorderStyle = BorderStyle.Fixed3D
            };

            canvasPanel.MouseDown += Canvas_MouseDown;
            canvasPanel.MouseMove += Canvas_MouseMove;
            canvasPanel.MouseUp += Canvas_MouseUp;
            canvasPanel.Paint += Canvas_Paint;

            // Create properties panel
            propertiesPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                BorderStyle = BorderStyle.Fixed3D
            };

            var propertiesLabel = new Label
            {
                Text = "Properties",
                Dock = DockStyle.Top,
                Height = 30,
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(35, 35, 38)
            };

            propertiesPanel.Controls.Add(propertiesLabel);

            // Add panels to main layout
            mainLayout.Controls.Add(toolboxPanel, 0, 0);
            mainLayout.Controls.Add(canvasPanel, 1, 0);
            mainLayout.Controls.Add(propertiesPanel, 2, 0);

            this.Controls.Add(mainLayout);
        }

        private void SetupKeyboardHandling()
        {
            // Remove KeyPreview as it's not available on Panel
            this.KeyDown += NodeEditorForm_KeyDown;

            // Ensure canvas can receive focus for keyboard input
            canvasPanel.TabStop = true;
            canvasPanel.KeyDown += Canvas_KeyDown;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            HandleKeyDown(new KeyEventArgs(keyData));
            return true;  // Indicate that we handled the key
        }
        private void NodeEditorForm_KeyDown(object sender, KeyEventArgs e)
        {
            HandleKeyDown(e);
        }

        private void Canvas_KeyDown(object sender, KeyEventArgs e)
        {
            HandleKeyDown(e);
        }

        private void HandleKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Delete:
                case Keys.Back:
                    DeleteSelectedNodes();
                    e.Handled = true;
                    break;

                case Keys.A:
                    if (e.Control)
                    {
                        SelectAllNodes();
                        e.Handled = true;
                    }
                    break;

                case Keys.C:
                    if (e.Control)
                    {
                        CopySelectedNodes();
                        e.Handled = true;
                    }
                    break;

                case Keys.V:
                    if (e.Control)
                    {
                        PasteNodes();
                        e.Handled = true;
                    }
                    break;

                case Keys.X:
                    if (e.Control)
                    {
                        CutSelectedNodes();
                        e.Handled = true;
                    }
                    break;
            }
        }

        private void InitializeMenuBar()
        {
            var menuStrip = CreateMenuBar();
            menuStrip.Dock = DockStyle.Top;
            this.Controls.Add(menuStrip);
        }

        private void SetupNodeTypes()
        {
            var nodeTypes = new List<NodeTypeInfo>
            {
                // Input nodes (Green theme)
                new NodeTypeInfo("Input", "Volume Data", typeof(VolumeDataNode), Color.FromArgb(120, 200, 120)),
                new NodeTypeInfo("Input", "Label Data", typeof(LabelDataNode), Color.FromArgb(120, 200, 120)),
                new NodeTypeInfo("Input", "Load Dataset", typeof(LoadDatasetNode), Color.FromArgb(100, 180, 100)),
                
                // Processing nodes (Blue theme)
                new NodeTypeInfo("Tools", "Threshold", typeof(ThresholdNode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "Brush", typeof(BrushNode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "Eraser", typeof(EraserNode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "Segment Anything", typeof(SegmentAnythingNode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "MicroSAM", typeof(MicroSAMNode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "Grounding DINO", typeof(GroundingDINONode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "Interpolate", typeof(InterpolateNode), Color.FromArgb(100, 150, 255)),
                
                // Simulation nodes (Purple theme)
                new NodeTypeInfo("Simulation", "Pore Network", typeof(PoreNetworkNode), Color.FromArgb(180, 100, 255)),
                new NodeTypeInfo("Simulation", "Stress Analysis", typeof(StressAnalysisNode), Color.FromArgb(180, 100, 255)),
                new NodeTypeInfo("Simulation", "Acoustic", typeof(AcousticSimulationNode), Color.FromArgb(180, 100, 255)),
                new NodeTypeInfo("Simulation", "Triaxial", typeof(TriaxialSimulationNode), Color.FromArgb(180, 100, 255)),
                
                // Filtering nodes (Teal theme)
                new NodeTypeInfo("Filters", "Band Detection", typeof(BandDetectionNode), Color.FromArgb(100, 200, 200)),
                new NodeTypeInfo("Filters", "Transform", typeof(TransformDatasetNode), Color.FromArgb(100, 200, 200)),
                new NodeTypeInfo("Filters", "Core Extraction", typeof(CoreExtractionNode), Color.FromArgb(100, 200, 200)),
                new NodeTypeInfo("Filters", "Filter Manager", typeof(FilterManagerNode), Color.FromArgb(100, 200, 200)),
                
                // Material nodes (Orange theme)
                new NodeTypeInfo("Materials", "Add Material", typeof(AddMaterialNode), Color.FromArgb(255, 180, 100)),
                new NodeTypeInfo("Materials", "Remove Material", typeof(RemoveMaterialNode), Color.FromArgb(255, 180, 100)),
                new NodeTypeInfo("Materials", "Merge Materials", typeof(MergeMaterialsNode), Color.FromArgb(255, 180, 100)),
                new NodeTypeInfo("Materials", "Extract Material", typeof(ExtractMaterialNode), Color.FromArgb(255, 180, 100)),
                
                // Output nodes (Red theme)
                new NodeTypeInfo("Output", "Export Images", typeof(ExportImagesNode), Color.FromArgb(255, 120, 120)),
                new NodeTypeInfo("Output", "Save Dataset", typeof(SaveDatasetNode), Color.FromArgb(255, 120, 120)),
                new NodeTypeInfo("Output", "Statistics", typeof(StatisticsNode), Color.FromArgb(255, 120, 120))
            };

            // Create toolbox list
            var toolboxList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.List,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                GridLines = false,
                FullRowSelect = true,
                ShowGroups = true
            };

            // Group nodes by category
            var groups = nodeTypes.GroupBy(t => t.Category).ToList();
            foreach (var group in groups)
            {
                var listGroup = new ListViewGroup(group.Key)
                {
                    HeaderAlignment = HorizontalAlignment.Left
                };
                toolboxList.Groups.Add(listGroup);

                foreach (var nodeType in group)
                {
                    var item = new ListViewItem(nodeType.Name)
                    {
                        Group = listGroup,
                        Tag = nodeType
                    };
                    toolboxList.Items.Add(item);
                }
            }

            toolboxList.ItemActivate += ToolboxList_ItemActivate;
            toolboxPanel.Controls.Add(toolboxList);
        }

        private void ToolboxList_ItemActivate(object sender, EventArgs e)
        {
            var listView = sender as ListView;
            if (listView.SelectedItems.Count > 0)
            {
                var nodeTypeInfo = listView.SelectedItems[0].Tag as NodeTypeInfo;
                if (nodeTypeInfo != null)
                {
                    CreateNode(nodeTypeInfo);
                }
            }
        }

        private void CreateNode(NodeTypeInfo nodeTypeInfo)
        {
            // Create node at center of canvas
            var centerPoint = new Point(
                canvasPanel.Width / 2,
                canvasPanel.Height / 2);

            var node = (BaseNode)Activator.CreateInstance(nodeTypeInfo.NodeType, centerPoint);
            node.Color = nodeTypeInfo.Color;
            nodes.Add(node);
            canvasPanel.Invalidate();
        }

        #region Canvas Event Handlers

        private void Canvas_MouseDown(object sender, MouseEventArgs e)
        {
            // Check if clicking on a node or pin
            foreach (var node in nodes.Reverse<BaseNode>())
            {
                if (node.Bounds.Contains(e.Location))
                {
                    // Check if clicking on a pin
                    foreach (var pin in node.GetAllPins())
                    {
                        if (pin.Bounds.Contains(e.Location))
                        {
                            // Start connecting
                            isConnecting = true;
                            connectingPin = pin;
                            return;
                        }
                    }

                    // Handle multi-selection with Ctrl key
                    if (Control.ModifierKeys == Keys.Control)
                    {
                        if (selectedNodes.Contains(node))
                        {
                            selectedNodes.Remove(node);
                        }
                        else
                        {
                            selectedNodes.Add(node);
                        }
                    }
                    else
                    {
                        // Single selection
                        selectedNodes.Clear();
                        selectedNodes.Add(node);
                    }

                    // Start dragging
                    selectedNode = node;
                    isDragging = true;
                    dragOffset = new Point(
                        e.X - node.Position.X,
                        e.Y - node.Position.Y);

                    // Move selected nodes to front
                    foreach (var selected in selectedNodes)
                    {
                        nodes.Remove(selected);
                        nodes.Add(selected);
                    }

                    // Update properties panel
                    UpdatePropertiesPanel(node);
                    canvasPanel.Invalidate();
                    return;
                }
            }

            // Start rectangle selection if no node clicked
            if (Control.ModifierKeys != Keys.Control)
            {
                selectedNodes.Clear();
            }

            isMultiSelecting = true;
            selectionStart = e.Location;
            selectionRectangle = new Rectangle(e.X, e.Y, 0, 0);

            // Clear selection
            selectedNode = null;
            UpdatePropertiesPanel(null);
            canvasPanel.Invalidate();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDragging && selectedNodes.Count > 0)
            {
                // Calculate movement delta
                int dx = e.X - (selectedNode.Position.X + dragOffset.X);
                int dy = e.Y - (selectedNode.Position.Y + dragOffset.Y);

                // Move all selected nodes
                foreach (var node in selectedNodes)
                {
                    node.Position = new Point(
                        node.Position.X + dx,
                        node.Position.Y + dy);
                }
                canvasPanel.Invalidate();
            }
            else if (isConnecting && connectingPin != null)
            {
                canvasPanel.Invalidate();
            }
            else if (isMultiSelecting)
            {
                // Update selection rectangle
                selectionRectangle = new Rectangle(
                    Math.Min(selectionStart.X, e.X),
                    Math.Min(selectionStart.Y, e.Y),
                    Math.Abs(e.X - selectionStart.X),
                    Math.Abs(e.Y - selectionStart.Y));

                // Select nodes within rectangle
                foreach (var node in nodes)
                {
                    if (selectionRectangle.IntersectsWith(node.Bounds))
                    {
                        if (!selectedNodes.Contains(node))
                        {
                            selectedNodes.Add(node);
                        }
                    }
                    else if (Control.ModifierKeys != Keys.Control)
                    {
                        selectedNodes.Remove(node);
                    }
                }

                canvasPanel.Invalidate();
            }
        }

        private void Canvas_MouseUp(object sender, MouseEventArgs e)
        {
            if (isConnecting && connectingPin != null)
            {
                // Try to create connection
                foreach (var node in nodes)
                {
                    foreach (var pin in node.GetAllPins())
                    {
                        if (pin.Bounds.Contains(e.Location) && pin != connectingPin)
                        {
                            // Check if connection is valid
                            if (CanConnect(connectingPin, pin))
                            {
                                var connection = new NodeConnection(connectingPin, pin);
                                connections.Add(connection);
                                canvasPanel.Invalidate();
                            }
                            break;
                        }
                    }
                }

                isConnecting = false;
                connectingPin = null;
            }

            isDragging = false;
            isMultiSelecting = false;
        }

        private bool CanConnect(NodePin from, NodePin to)
        {
            // Can't connect to self
            if (from.Node == to.Node) return false;

            // Can't connect output to output or input to input
            if (from.IsOutput == to.IsOutput) return false;

            // Check if connection already exists
            if (connections.Any(c => c.From == from && c.To == to))
                return false;

            // TODO: Add type checking

            return true;
        }

        #endregion

        #region Canvas Rendering

        private void Canvas_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Draw grid
            DrawGrid(g);

            // Draw connections
            foreach (var connection in connections)
            {
                DrawConnection(g, connection);
            }

            // Draw temporary connection while connecting
            if (isConnecting && connectingPin != null)
            {
                var mousePos = canvasPanel.PointToClient(MousePosition);
                DrawTempConnection(g, connectingPin, mousePos);
            }

            // Draw nodes
            foreach (var node in nodes)
            {
                DrawNode(g, node);
            }

            // Draw selection rectangle
            if (isMultiSelecting)
            {
                using (var pen = new Pen(Color.Yellow, 1))
                using (var brush = new SolidBrush(Color.FromArgb(50, Color.Yellow)))
                {
                    g.FillRectangle(brush, selectionRectangle);
                    g.DrawRectangle(pen, selectionRectangle);
                }
            }
        }

        private void DrawGrid(Graphics g)
        {
            using (var pen = new Pen(Color.FromArgb(40, 40, 40), 1))
            {
                int spacing = 20;
                for (int x = 0; x < canvasPanel.Width; x += spacing)
                {
                    g.DrawLine(pen, x, 0, x, canvasPanel.Height);
                }
                for (int y = 0; y < canvasPanel.Height; y += spacing)
                {
                    g.DrawLine(pen, 0, y, canvasPanel.Width, y);
                }
            }
        }

        private void DrawNode(Graphics g, BaseNode node)
        {
            // Draw node background
            using (var brush = new SolidBrush(node.Color))
            using (var borderPen = new Pen(selectedNodes.Contains(node) ? Color.Yellow : Color.White, 2))
            {
                g.FillRectangle(brush, node.Bounds);
                g.DrawRectangle(borderPen, node.Bounds);
            }

            // Draw node title
            using (var font = new Font("Arial", 10, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.White))
            {
                var titleBounds = new Rectangle(
                    node.Bounds.X + 5,
                    node.Bounds.Y + 5,
                    node.Bounds.Width - 10,
                    20);

                var nodeName = node.GetType().Name.Replace("Node", "");
                g.DrawString(nodeName, font, brush, titleBounds);
            }

            // Draw pins
            foreach (var pin in node.GetAllPins())
            {
                DrawPin(g, pin);
            }
        }

        private void DrawPin(Graphics g, NodePin pin)
        {
            using (var brush = new SolidBrush(pin.Color))
            using (var pen = new Pen(Color.White, 1))
            {
                g.FillEllipse(brush, pin.Bounds);
                g.DrawEllipse(pen, pin.Bounds);
            }

            // Draw pin label
            using (var font = new Font("Arial", 8))
            using (var brush = new SolidBrush(Color.White))
            {
                var textSize = g.MeasureString(pin.Name, font);
                var textPos = new PointF(
                    pin.IsOutput ? pin.Bounds.X - textSize.Width - 5 : pin.Bounds.X + pin.Bounds.Width + 5,
                    pin.Bounds.Y + (pin.Bounds.Height - textSize.Height) / 2);
                g.DrawString(pin.Name, font, brush, textPos);
            }
        }

        private void DrawConnection(Graphics g, NodeConnection connection)
        {
            using (var pen = new Pen(Color.White, 2))
            {
                var start = new Point(
                    connection.From.Bounds.X + connection.From.Bounds.Width / 2,
                    connection.From.Bounds.Y + connection.From.Bounds.Height / 2);
                var end = new Point(
                    connection.To.Bounds.X + connection.To.Bounds.Width / 2,
                    connection.To.Bounds.Y + connection.To.Bounds.Height / 2);

                // Draw Bezier curve for better aesthetics
                var cp1 = new Point(start.X + 50, start.Y);
                var cp2 = new Point(end.X - 50, end.Y);

                g.DrawBezier(pen, start, cp1, cp2, end);
            }
        }

        private void DrawTempConnection(Graphics g, NodePin from, Point to)
        {
            using (var pen = new Pen(Color.Yellow, 2))
            {
                var start = new Point(
                    from.Bounds.X + from.Bounds.Width / 2,
                    from.Bounds.Y + from.Bounds.Height / 2);

                var cp1 = new Point(start.X + 50, start.Y);
                var cp2 = new Point(to.X - 50, to.Y);

                g.DrawBezier(pen, start, cp1, cp2, to);
            }
        }

        #endregion

        #region Edit Operations

        private void DeleteSelectedNodes()
        {
            if (selectedNodes.Count == 0) return;

            // Remove connections related to selected nodes
            connections.RemoveAll(c =>
                selectedNodes.Contains(c.From.Node) ||
                selectedNodes.Contains(c.To.Node));

            // Remove selected nodes
            foreach (var node in selectedNodes)
            {
                nodes.Remove(node);
            }

            selectedNodes.Clear();
            selectedNode = null;
            UpdatePropertiesPanel(null);
            canvasPanel.Invalidate();
        }

        private void CopySelectedNodes()
        {
            if (selectedNodes.Count == 0) return;

            copiedNodes = new List<BaseNode>();
            copiedConnections = new List<NodeConnection>();

            // Copy selected nodes
            foreach (var node in selectedNodes)
            {
                copiedNodes.Add(CloneNode(node));
            }

            // Copy connections between copied nodes
            foreach (var connection in connections)
            {
                int fromIndex = selectedNodes.IndexOf(connection.From.Node);
                int toIndex = selectedNodes.IndexOf(connection.To.Node);

                if (fromIndex >= 0 && toIndex >= 0)
                {
                    var fromNode = copiedNodes[fromIndex];
                    var toNode = copiedNodes[toIndex];
                    var fromPin = fromNode.GetAllPins().ElementAt(connection.From.Node.GetAllPins().ToList().IndexOf(connection.From));
                    var toPin = toNode.GetAllPins().ElementAt(connection.To.Node.GetAllPins().ToList().IndexOf(connection.To));

                    copiedConnections.Add(new NodeConnection(fromPin, toPin));
                }
            }
        }

        private void PasteNodes()
        {
            if (copiedNodes.Count == 0) return;

            var offsetX = 50;
            var offsetY = 50;

            selectedNodes.Clear();

            // Create new nodes from copied ones
            var pastedNodes = new List<BaseNode>();
            foreach (var copiedNode in copiedNodes)
            {
                var newNode = CloneNode(copiedNode);
                newNode.Position = new Point(
                    copiedNode.Position.X + offsetX,
                    copiedNode.Position.Y + offsetY);
                nodes.Add(newNode);
                selectedNodes.Add(newNode);
                pastedNodes.Add(newNode);
            }

            // Recreate connections
            foreach (var copiedConnection in copiedConnections)
            {
                int fromIndex = copiedNodes.IndexOf(copiedConnection.From.Node);
                int toIndex = copiedNodes.IndexOf(copiedConnection.To.Node);

                if (fromIndex >= 0 && toIndex >= 0)
                {
                    var fromNode = pastedNodes[fromIndex];
                    var toNode = pastedNodes[toIndex];
                    var fromPin = fromNode.GetAllPins().ElementAt(copiedConnection.From.Node.GetAllPins().ToList().IndexOf(copiedConnection.From));
                    var toPin = toNode.GetAllPins().ElementAt(copiedConnection.To.Node.GetAllPins().ToList().IndexOf(copiedConnection.To));

                    connections.Add(new NodeConnection(fromPin, toPin));
                }
            }

            canvasPanel.Invalidate();
        }

        private void CutSelectedNodes()
        {
            CopySelectedNodes();
            DeleteSelectedNodes();
        }

        private void SelectAllNodes()
        {
            selectedNodes.Clear();
            selectedNodes.AddRange(nodes);
            canvasPanel.Invalidate();
        }

        private void ClearConnections()
        {
            connections.Clear();
            canvasPanel.Invalidate();
        }

        private BaseNode CloneNode(BaseNode original)
        {
            var clone = (BaseNode)Activator.CreateInstance(original.GetType(), original.Position);
            clone.Color = original.Color;

            // Copy properties if needed
            // TODO: Implement property copying for each node type

            return clone;
        }

        #endregion

        private void UpdatePropertiesPanel(BaseNode node)
        {
            // Clear existing controls except label
            var controlsToRemove = propertiesPanel.Controls.Cast<Control>()
                .Where(c => !(c is Label && c.Text == "Properties"))
                .ToList();

            foreach (var control in controlsToRemove)
            {
                propertiesPanel.Controls.Remove(control);
            }

            if (node == null) return;

            // Add properties for the selected node
            var propertyPanel = node.CreatePropertyPanel();
            if (propertyPanel != null)
            {
                propertyPanel.Dock = DockStyle.Fill;
                propertiesPanel.Controls.Add(propertyPanel);
            }
        }

        private MenuStrip CreateMenuBar()
        {
            var menuStrip = new MenuStrip();

            // File menu
            var fileMenu = new ToolStripMenuItem("File");
            var newMenuItem = new ToolStripMenuItem("New Graph");
            newMenuItem.Click += (s, e) => NewGraph();

            var saveMenuItem = new ToolStripMenuItem("Save Graph");
            saveMenuItem.Click += (s, e) => SaveGraph();

            var loadMenuItem = new ToolStripMenuItem("Load Graph");
            loadMenuItem.Click += (s, e) => LoadGraph();

            fileMenu.DropDownItems.Add(newMenuItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(saveMenuItem);
            fileMenu.DropDownItems.Add(loadMenuItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());

            var exitMenuItem = new ToolStripMenuItem("Exit");
            exitMenuItem.Click += (s, e) =>
            {
                // Find the parent KryptonPage and close it
                Control parent = this;
                while (parent != null && !(parent is KryptonPage))
                {
                    parent = parent.Parent;
                }

                if (parent is KryptonPage page)
                {
                    // Ask the docking manager to hide this page
                    page.Hide();
                    this.Dispose();
                }
                else
                {
                    // Fallback: if we can't find the KryptonPage, just hide this panel
                    this.Visible = false;
                }
            };
            fileMenu.DropDownItems.Add(exitMenuItem);

            // Edit menu
            var editMenu = new ToolStripMenuItem("Edit");

            var deleteMenuItem = new ToolStripMenuItem("Delete", null, (s, e) => DeleteSelectedNodes())
            {
                ShortcutKeys = Keys.Delete
            };

            var copyMenuItem = new ToolStripMenuItem("Copy", null, (s, e) => CopySelectedNodes())
            {
                ShortcutKeys = Keys.Control | Keys.C
            };

            var pasteMenuItem = new ToolStripMenuItem("Paste", null, (s, e) => PasteNodes())
            {
                ShortcutKeys = Keys.Control | Keys.V
            };

            var cutMenuItem = new ToolStripMenuItem("Cut", null, (s, e) => CutSelectedNodes())
            {
                ShortcutKeys = Keys.Control | Keys.X
            };

            var selectAllMenuItem = new ToolStripMenuItem("Select All", null, (s, e) => SelectAllNodes())
            {
                ShortcutKeys = Keys.Control | Keys.A
            };

            var clearConnectionsMenuItem = new ToolStripMenuItem("Clear Connections");
            clearConnectionsMenuItem.Click += (s, e) => ClearConnections();

            var clearAllMenuItem = new ToolStripMenuItem("Clear All");
            clearAllMenuItem.Click += (s, e) => ClearAllNodes();

            editMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                cutMenuItem, copyMenuItem, pasteMenuItem,
                new ToolStripSeparator(),
                selectAllMenuItem, deleteMenuItem,
                new ToolStripSeparator(),
                clearConnectionsMenuItem, clearAllMenuItem
            });

            // Execute menu
            var executeMenu = new ToolStripMenuItem("Execute");
            var executeAllMenuItem = new ToolStripMenuItem("Execute All");
            executeAllMenuItem.Click += (s, e) => ExecuteGraph();

            var validateMenuItem = new ToolStripMenuItem("Validate Graph");
            validateMenuItem.Click += (s, e) => ValidateGraph();

            executeMenu.DropDownItems.AddRange(new ToolStripItem[] { executeAllMenuItem, validateMenuItem });

            menuStrip.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, executeMenu });

            return menuStrip;
        }

        private void NewGraph()
        {
            nodes.Clear();
            connections.Clear();
            selectedNode = null;
            selectedNodes.Clear();
            canvasPanel.Invalidate();
        }

        private void SaveGraph()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "Node Graph|*.nodegraph|JSON|*.json|All Files|*.*";
                dialog.DefaultExt = "nodegraph";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        var graphData = new NodeGraphData
                        {
                            Nodes = nodes.Select(n => new NodeData
                            {
                                Id = Guid.NewGuid().ToString(),
                                Type = n.GetType().Name,
                                Position = n.Position,
                                Color = n.Color
                            }).ToList(),
                            Connections = new List<ConnectionData>()
                        };

                        // Create connections data
                        foreach (var conn in connections)
                        {
                            var fromNode = graphData.Nodes.First(nd => nodes[graphData.Nodes.IndexOf(nd)] == conn.From.Node);
                            var toNode = graphData.Nodes.First(nd => nodes[graphData.Nodes.IndexOf(nd)] == conn.To.Node);

                            graphData.Connections.Add(new ConnectionData
                            {
                                FromNodeId = fromNode.Id,
                                FromPinName = conn.From.Name,
                                ToNodeId = toNode.Id,
                                ToPinName = conn.To.Name
                            });
                        }

                        string json = JsonSerializer.Serialize(graphData, new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Converters = { new PointConverter(), new ColorConverter() }
                        });
                        File.WriteAllText(dialog.FileName, json);
                        Logger.Log($"[NodeEditor] Saved graph to {dialog.FileName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[NodeEditor] Error saving graph: {ex.Message}");
                        MessageBox.Show($"Error saving graph: {ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void LoadGraph()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Node Graph|*.nodegraph|JSON|*.json|All Files|*.*";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string json = File.ReadAllText(dialog.FileName);
                        var graphData = JsonSerializer.Deserialize<NodeGraphData>(json, new JsonSerializerOptions
                        {
                            Converters = { new PointConverter(), new ColorConverter() }
                        });

                        // Clear existing graph
                        NewGraph();

                        // Create nodes
                        var nodeDict = new Dictionary<string, BaseNode>();
                        foreach (var nodeData in graphData.Nodes)
                        {
                            // Find the node type
                            var nodeType = Type.GetType($"CTS.NodeEditor.{nodeData.Type}");
                            if (nodeType != null)
                            {
                                var node = (BaseNode)Activator.CreateInstance(nodeType, nodeData.Position);
                                node.Color = nodeData.Color;
                                nodes.Add(node);
                                nodeDict[nodeData.Id] = node;
                            }
                        }

                        // Create connections
                        foreach (var connData in graphData.Connections)
                        {
                            if (nodeDict.TryGetValue(connData.FromNodeId, out var fromNode) &&
                                nodeDict.TryGetValue(connData.ToNodeId, out var toNode))
                            {
                                var fromPin = fromNode.GetAllPins().FirstOrDefault(p => p.Name == connData.FromPinName);
                                var toPin = toNode.GetAllPins().FirstOrDefault(p => p.Name == connData.ToPinName);

                                if (fromPin != null && toPin != null)
                                {
                                    connections.Add(new NodeConnection(fromPin, toPin));
                                }
                            }
                        }

                        canvasPanel.Invalidate();
                        Logger.Log($"[NodeEditor] Loaded graph from {dialog.FileName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[NodeEditor] Error loading graph: {ex.Message}");
                        MessageBox.Show($"Error loading graph: {ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ExecuteGraph()
        {
            Logger.Log("[NodeEditor] Starting graph execution");

            try
            {
                // Validate graph first
                if (!ValidateGraph())
                {
                    MessageBox.Show("Graph validation failed. Please check for errors.",
                                   "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get execution order (topological sort)
                var executionOrder = GetExecutionOrder();

                if (executionOrder == null)
                {
                    MessageBox.Show("Graph contains cycles. Cannot execute.",
                                   "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Execute nodes in order
                foreach (var node in executionOrder)
                {
                    try
                    {
                        Logger.Log($"[NodeEditor] Executing {node.GetType().Name}");
                        node.Execute();

                        // Highlight executing node
                        HighlightExecutingNode(node);
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(100); // Small delay to show execution
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[NodeEditor] Error executing {node.GetType().Name}: {ex.Message}");
                        MessageBox.Show($"Error executing {node.GetType().Name}: {ex.Message}",
                                       "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                // Clear highlighting
                selectedNodes.Clear();
                canvasPanel.Invalidate();

                Logger.Log("[NodeEditor] Graph execution completed successfully");
                MessageBox.Show("Graph execution completed successfully!",
                               "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Log($"[NodeEditor] Error during graph execution: {ex.Message}");
                MessageBox.Show($"Error during graph execution: {ex.Message}",
                               "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool ValidateGraph()
        {
            Logger.Log("[NodeEditor] Validating graph");

            var errors = new List<string>();

            // Check for unconnected input pins
            foreach (var node in nodes)
            {
                foreach (var pin in node.GetAllPins().Where(p => !p.IsOutput))
                {
                    if (!connections.Any(c => c.To == pin))
                    {
                        // Some input pins might be optional, but for now, we'll warn about all
                        Logger.Log($"Warning: {node.GetType().Name}.{pin.Name} is not connected");
                    }
                }
            }

            // Check for cycles
            if (HasCycles())
            {
                errors.Add("Graph contains cycles");
            }

            // Check for isolated nodes (except output nodes)
            foreach (var node in nodes)
            {
                bool hasConnectedInput = false;
                bool hasConnectedOutput = false;

                foreach (var pin in node.GetAllPins())
                {
                    if (pin.IsOutput && connections.Any(c => c.From == pin))
                        hasConnectedOutput = true;
                    else if (!pin.IsOutput && connections.Any(c => c.To == pin))
                        hasConnectedInput = true;
                }

                if (!hasConnectedInput && !hasConnectedOutput)
                {
                    Logger.Log($"Warning: {node.GetType().Name} is isolated");
                }
            }

            if (errors.Count > 0)
            {
                MessageBox.Show(string.Join("\n", errors), "Validation Errors",
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            Logger.Log("[NodeEditor] Graph validation completed successfully");
            return true;
        }

        private List<BaseNode> GetExecutionOrder()
        {
            // Topological sort
            var inDegree = new Dictionary<BaseNode, int>();
            var graph = new Dictionary<BaseNode, List<BaseNode>>();

            // Initialize
            foreach (var node in nodes)
            {
                inDegree[node] = 0;
                graph[node] = new List<BaseNode>();
            }

            // Build graph and calculate in-degrees
            foreach (var connection in connections)
            {
                var fromNode = connection.From.Node;
                var toNode = connection.To.Node;

                graph[fromNode].Add(toNode);
                inDegree[toNode]++;
            }

            // Queue for nodes with no incoming edges
            var queue = new Queue<BaseNode>();
            foreach (var kvp in inDegree)
            {
                if (kvp.Value == 0)
                    queue.Enqueue(kvp.Key);
            }

            var result = new List<BaseNode>();

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                result.Add(node);

                foreach (var neighbor in graph[node])
                {
                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0)
                        queue.Enqueue(neighbor);
                }
            }

            // Check for cycles
            if (result.Count != nodes.Count)
                return null; // Graph has cycles

            return result;
        }

        private bool HasCycles()
        {
            var visited = new HashSet<BaseNode>();
            var recStack = new HashSet<BaseNode>();

            foreach (var node in nodes)
            {
                if (!visited.Contains(node))
                {
                    if (HasCyclesDFS(node, visited, recStack))
                        return true;
                }
            }

            return false;
        }

        private bool HasCyclesDFS(BaseNode node, HashSet<BaseNode> visited, HashSet<BaseNode> recStack)
        {
            visited.Add(node);
            recStack.Add(node);

            // Get all connected nodes from this node's output pins
            var connectedNodes = connections
                .Where(c => c.From.Node == node)
                .Select(c => c.To.Node)
                .Distinct();

            foreach (var neighbor in connectedNodes)
            {
                if (!visited.Contains(neighbor))
                {
                    if (HasCyclesDFS(neighbor, visited, recStack))
                        return true;
                }
                else if (recStack.Contains(neighbor))
                {
                    return true; // Back edge found, cycle detected
                }
            }

            recStack.Remove(node);
            return false;
        }

        private void HighlightExecutingNode(BaseNode node)
        {
            // Temporarily highlight the executing node
            selectedNodes.Clear();
            selectedNodes.Add(node);
            canvasPanel.Invalidate();
        }

        private void ClearAllNodes()
        {
            var result = MessageBox.Show("Are you sure you want to clear all nodes?",
                                        "Confirm Clear", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                NewGraph();
            }
        }
    }

    public class NodeTypeInfo
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public Type NodeType { get; set; }
        public Color Color { get; set; }

        public NodeTypeInfo(string category, string name, Type nodeType, Color color)
        {
            Category = category;
            Name = name;
            NodeType = nodeType;
            Color = color;
        }
    }

    // Data structures for serialization
    [Serializable]
    public class NodeGraphData
    {
        public List<NodeData> Nodes { get; set; }
        public List<ConnectionData> Connections { get; set; }
    }

    [Serializable]
    public class NodeData
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public Point Position { get; set; }
        public Color Color { get; set; }
    }

    [Serializable]
    public class ConnectionData
    {
        public string FromNodeId { get; set; }
        public string FromPinName { get; set; }
        public string ToNodeId { get; set; }
        public string ToPinName { get; set; }
    }

    // Custom converters for JSON serialization
    public class PointConverter : JsonConverter<Point>
    {
        public override Point Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException();

            int x = 0, y = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return new Point(x, y);

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    string propertyName = reader.GetString();
                    reader.Read();

                    switch (propertyName)
                    {
                        case "X":
                            x = reader.GetInt32();
                            break;
                        case "Y":
                            y = reader.GetInt32();
                            break;
                    }
                }
            }
            throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, Point value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("X", value.X);
            writer.WriteNumber("Y", value.Y);
            writer.WriteEndObject();
        }
    }

    public class ColorConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException();

            int a = 255, r = 0, g = 0, b = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return Color.FromArgb(a, r, g, b);

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    string propertyName = reader.GetString();
                    reader.Read();

                    switch (propertyName)
                    {
                        case "A":
                            a = reader.GetInt32();
                            break;
                        case "R":
                            r = reader.GetInt32();
                            break;
                        case "G":
                            g = reader.GetInt32();
                            break;
                        case "B":
                            b = reader.GetInt32();
                            break;
                    }
                }
            }
            throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("A", value.A);
            writer.WriteNumber("R", value.R);
            writer.WriteNumber("G", value.G);
            writer.WriteNumber("B", value.B);
            writer.WriteEndObject();
        }
    }
}