using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Krypton.Toolkit;
using Krypton.Navigator;
using CTS.Modules.NodeEditor.Nodes;
using System.IO.Compression;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace CTS.NodeEditor
{
    // Node Type Info class definition
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
    public class PointJsonConverter : JsonConverter<Point>
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

    public class ColorJsonConverter : JsonConverter<Color>
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

    public class NodeEditorForm : KryptonPanel
    {
        // Custom double-buffered panel class for the canvas
        private class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                this.SetStyle(ControlStyles.AllPaintingInWmPaint, true);
                this.SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
                this.SetStyle(ControlStyles.UserPaint, true);
                this.SetStyle(ControlStyles.ResizeRedraw, true);
                this.UpdateStyles();
            }
        }

        // Dialog for cluster options
        private class ClusterOptionsDialog : KryptonForm
        {
            private KryptonCheckBox chkUseCluster;

            public bool UseCluster { get; private set; }

            public ClusterOptionsDialog(bool currentValue)
            {
                UseCluster = currentValue;

                this.Text = "Compute Cluster Options";
                this.Size = new Size(400, 200);
                this.FormBorderStyle = FormBorderStyle.FixedDialog;
                this.MaximizeBox = false;
                this.MinimizeBox = false;
                this.StartPosition = FormStartPosition.CenterParent;

                var panel = new Panel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(20)
                };

                chkUseCluster = new KryptonCheckBox
                {
                    Text = "Use Compute Cluster for Node Execution",
                    Checked = UseCluster,
                    Location = new Point(20, 20),
                    AutoSize = true
                };

                var label = new Label
                {
                    Text = "When enabled, node processing tasks will be distributed across available compute endpoints in the cluster.",
                    Location = new Point(40, 50),
                    Size = new Size(300, 40),
                    AutoSize = false
                };

                var btnOK = new KryptonButton
                {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Location = new Point(120, 110),
                    Width = 80
                };

                var btnCancel = new KryptonButton
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(210, 110),
                    Width = 80
                };

                btnOK.Click += (s, e) =>
                {
                    UseCluster = chkUseCluster.Checked;
                };

                panel.Controls.Add(chkUseCluster);
                panel.Controls.Add(label);
                panel.Controls.Add(btnOK);
                panel.Controls.Add(btnCancel);

                this.Controls.Add(panel);
                this.AcceptButton = btnOK;
                this.CancelButton = btnCancel;
            }
        }

        // Progress form for cluster execution
        private class ClusterExecutionProgressForm : KryptonForm
        {
            private ProgressBar progressBar;
            private Label lblStatus;
            private int totalNodes;

            public ClusterExecutionProgressForm(int totalNodeCount)
            {
                this.totalNodes = totalNodeCount;

                this.Text = "Cluster Execution Progress";
                this.Size = new Size(400, 150);
                this.FormBorderStyle = FormBorderStyle.FixedDialog;
                this.MaximizeBox = false;
                this.MinimizeBox = false;
                this.StartPosition = FormStartPosition.CenterParent;

                lblStatus = new Label
                {
                    Text = "Executing graph on compute cluster...",
                    Dock = DockStyle.Top,
                    Height = 30,
                    TextAlign = ContentAlignment.MiddleCenter
                };

                progressBar = new ProgressBar
                {
                    Dock = DockStyle.Top,
                    Height = 25,
                    Maximum = totalNodes,
                    Value = 0,
                    Margin = new Padding(20, 10, 20, 10)
                };

                var panel = new Panel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(20)
                };

                panel.Controls.Add(progressBar);
                panel.Controls.Add(lblStatus);

                this.Controls.Add(panel);
            }

            public void UpdateProgress(int completedNodes)
            {
                if (InvokeRequired)
                {
                    Invoke(new Action<int>(UpdateProgress), completedNodes);
                    return;
                }

                progressBar.Value = Math.Min(completedNodes, totalNodes);
                lblStatus.Text = $"Executing graph on compute cluster... ({completedNodes}/{totalNodes})";
                Application.DoEvents();
            }
        }

        // Helper class for cluster endpoint items in combobox
        private class ClusterEndpointItem
        {
            public ComputeEndpoint Endpoint { get; }
            public string DisplayText { get; }

            public ClusterEndpointItem(ComputeEndpoint endpoint, string displayText)
            {
                Endpoint = endpoint;
                DisplayText = displayText;
            }

            public override string ToString()
            {
                return DisplayText;
            }
        }

        public MainForm mainForm;
        private DoubleBufferedPanel canvasPanel;
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
        private List<NodeConnection> selectedConnections = new List<NodeConnection>();
        // For copy/paste
        private List<BaseNode> copiedNodes;
        private List<NodeConnection> copiedConnections;

        // Cluster support
        private CheckBox chkUseCluster;
        private ComboBox cmbEndpoints;
        private Label lblClusterStatus;
        private bool useCluster = false;

        // Flickering fix
        private BufferedGraphicsContext context;
        private BufferedGraphics grafx;

        private List<string> availableServerNodeTypes = new List<string>();
        public static NodeEditorForm Instance { get; private set; }

        public IEnumerable<NodeConnection> Connections => connections.AsReadOnly();

        public bool UseCluster
        {
            get { return useCluster; }
            set
            {
                useCluster = value;
                if (chkUseCluster != null)
                    chkUseCluster.Checked = value;
                UpdateClusterStatus();
            }
        }

        public NodeEditorForm(MainForm mainForm)
        {
            this.mainForm = mainForm;
            nodes = new List<BaseNode>();
            connections = new List<NodeConnection>();
            selectedNodes = new List<BaseNode>();
            copiedNodes = new List<BaseNode>();
            copiedConnections = new List<NodeConnection>();
            selectedConnections = new List<NodeConnection>();

            // Set the static instance
            NodeEditorForm.Instance = this;

            // Initialize buffered graphics context safely after components are created
            context = BufferedGraphicsManager.Current;

            InitializeComponent();
            SetupNodeTypes();
            SetupKeyboardHandling();

            // Initialize buffer only after components are fully created and sized
            this.HandleCreated += (s, e) => {
                try
                {
                    if (canvasPanel != null && canvasPanel.Width > 0 && canvasPanel.Height > 0 && context != null)
                    {
                        context.MaximumBuffer = new Size(Math.Max(1, canvasPanel.Width + 1),
                                                         Math.Max(1, canvasPanel.Height + 1));
                        grafx = context.Allocate(canvasPanel.CreateGraphics(),
                            new Rectangle(0, 0, canvasPanel.Width, canvasPanel.Height));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[NodeEditor] Buffer initialization error: {ex.Message}");
                }
            };

            // Handle resize event to recreate the buffer graphics
            this.SizeChanged += (s, e) => {
                try
                {
                    if (grafx != null)
                    {
                        grafx.Dispose();
                        grafx = null;
                    }

                    if (canvasPanel != null && canvasPanel.Width > 0 && canvasPanel.Height > 0 && context != null)
                    {
                        context.MaximumBuffer = new Size(Math.Max(1, canvasPanel.Width + 1),
                                                         Math.Max(1, canvasPanel.Height + 1));
                        grafx = context.Allocate(canvasPanel.CreateGraphics(),
                            new Rectangle(0, 0, canvasPanel.Width, canvasPanel.Height));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[NodeEditor] Buffer resize error: {ex.Message}");
                }
            };
        }

        private bool isUpdatingUI = false;

        private void InitializeComponent()
        {
            this.Text = "Node Editor";
            this.Size = new Size(1200, 800);

            // Create a very simple layout structure
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                BackColor = Color.FromArgb(35, 35, 38)
            };

            // Define three rows: Menu, Cluster Controls, Main Content
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // Menu bar - fixed height
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); // Cluster controls - fixed height
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Main content - fills remaining space

            // 1. Create the menu bar
            var menuStrip = CreateMenuBar();
            menuStrip.Dock = DockStyle.Fill;

            // Add menu to first row
            mainLayout.Controls.Add(menuStrip, 0, 0);

            // 2. Create cluster controls panel
            var clusterPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                Padding = new Padding(5)
            };

            // Create a flow layout for the cluster controls
            var clusterFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true
            };

            // Create regular Windows Forms checkbox
            chkUseCluster = new CheckBox
            {
                Text = "Use Compute Cluster",
                Margin = new Padding(10, 5, 10, 0),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(45, 45, 48),
                Checked = useCluster,
                FlatStyle = FlatStyle.Flat
            };

            // Set up event handler with safety mechanism
            chkUseCluster.CheckedChanged += SafeCheckedChangedHandler;

            // Create a standard ComboBox
            cmbEndpoints = new ComboBox
            {
                Width = 250,
                DropDownWidth = 300,
                Margin = new Padding(5, 5, 10, 0),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(60, 60, 60),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };

            // Create status label
            lblClusterStatus = new Label
            {
                Text = "Cluster disabled",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.Silver,
                Margin = new Padding(5, 8, 5, 0)
            };

            // Add controls to flow layout
            clusterFlow.Controls.Add(chkUseCluster);
            clusterFlow.Controls.Add(cmbEndpoints);
            clusterFlow.Controls.Add(lblClusterStatus);

            // Add flow layout to cluster panel
            clusterPanel.Controls.Add(clusterFlow);

            // Add cluster panel to second row
            mainLayout.Controls.Add(clusterPanel, 0, 1);

            // 3. Create content panel (contains toolbox, canvas, properties)
            var contentPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.FromArgb(35, 35, 38)
            };

            // Define column proportions for the content area - adjust to make toolbox narrower
            contentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); // Fixed width toolbox (narrower)
            contentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // Canvas (fills remaining space)
            contentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250)); // Properties panel

            // Create toolbox panel with AutoScroll enabled
            toolboxPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                Margin = new Padding(1, 1, 0, 1),
                AutoScroll = true // Enable scrolling for toolbox
            };

            var toolboxLabel = new Label
            {
                Text = "Node Toolbox",
                Dock = DockStyle.Top,
                Height = 15,
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(35, 35, 38)
            };

            toolboxPanel.Controls.Add(toolboxLabel);

            // Create canvas panel with double-buffering to prevent flickering
            canvasPanel = new DoubleBufferedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                Margin = new Padding(1)
            };

            canvasPanel.MouseDown += Canvas_MouseDown;
            canvasPanel.MouseMove += Canvas_MouseMove;
            canvasPanel.MouseUp += Canvas_MouseUp;
            canvasPanel.Paint += Canvas_Paint;
            canvasPanel.Resize += Canvas_Resize;

            // Create properties panel
            propertiesPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                Margin = new Padding(0, 1, 1, 1),
                AutoScroll = true // Enable scrolling for properties panel
            };

            /*var propertiesLabel = new Label
            {
                Text = "Properties",
                Dock = DockStyle.Top,
                Height = 15,
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(35, 35, 38)
            };

            propertiesPanel.Controls.Add(propertiesLabel);*/

            // Add panels to content layout
            contentPanel.Controls.Add(toolboxPanel, 0, 0);
            contentPanel.Controls.Add(canvasPanel, 1, 0);
            contentPanel.Controls.Add(propertiesPanel, 2, 0);

            // Add content panel to third row
            mainLayout.Controls.Add(contentPanel, 0, 2);

            // Add main layout to form
            this.Controls.Add(mainLayout);

            // Initialize endpoints list
            RefreshClusterEndpoints();
        }

        private void Canvas_Resize(object sender, EventArgs e)
        {
            try
            {
                // Recreate the buffer when the canvas is resized
                if (grafx != null)
                {
                    grafx.Dispose();
                    grafx = null;
                }

                if (canvasPanel != null && canvasPanel.Width > 0 && canvasPanel.Height > 0 && context != null)
                {
                    context.MaximumBuffer = new Size(Math.Max(1, canvasPanel.Width + 1),
                                                    Math.Max(1, canvasPanel.Height + 1));
                    grafx = context.Allocate(canvasPanel.CreateGraphics(),
                        new Rectangle(0, 0, Math.Max(1, canvasPanel.Width), Math.Max(1, canvasPanel.Height)));
                }
            }
            catch (Exception ex)
            {
                // Log error but prevent crash
                Logger.Log($"[NodeEditor] Error in Canvas_Resize: {ex.Message}");
            }
        }

        private void AddClusterControls(FlowLayoutPanel buttonPanel)
        {
            // Create a panel to hold cluster controls with increased margins for spacing
            var clusterPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(10, 5, 5, 5),  // Increased left margin
                Padding = new Padding(10, 5, 10, 5), // Increased padding
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(40, 40, 45)
            };

            // For KryptonCheckBox we need to set the StateCommon properties
            chkUseCluster = new CheckBox
            {
                Text = "Use Compute Cluster",
                Checked = useCluster,
                Margin = new Padding(5),
            };

            // Set the text color explicitly using the Krypton styling system


            chkUseCluster.CheckedChanged += (s, e) =>
            {
                useCluster = chkUseCluster.Checked;
                UpdateClusterStatus();
            };

            cmbEndpoints = new ComboBox
            {
                Width = 200,
                DropDownWidth = 250,
                Margin = new Padding(10, 5, 5, 5),  // Increased left margin for spacing
                ForeColor = Color.White,
            };

            // Set text colors for the ComboBox


            lblClusterStatus = new Label
            {
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.Silver,
                Margin = new Padding(10, 5, 5, 5)  // Increased left margin for spacing
            };

            clusterPanel.Controls.Add(chkUseCluster);
            clusterPanel.Controls.Add(cmbEndpoints);
            clusterPanel.Controls.Add(lblClusterStatus);

            // Add to button panel
            buttonPanel.Controls.Add(clusterPanel);

            // Initialize endpoints list
            RefreshClusterEndpoints();
        }
        private void SafeCheckedChangedHandler(object sender, EventArgs e)
        {
            try
            {
                // Prevent recursive UI updates
                if (isUpdatingUI)
                    return;

                isUpdatingUI = true;

                // Update the useCluster flag only if the checkbox still exists and isn't disposed
                if (chkUseCluster != null && !chkUseCluster.IsDisposed && chkUseCluster.IsHandleCreated)
                {
                    useCluster = chkUseCluster.Checked;

                    // Update UI based on the new state
                    UpdateClusterStatus();
                }
            }
            catch (Exception ex)
            {
                // Log the error but prevent application crash
                Logger.Log($"[NodeEditor] Error in checkbox event: {ex.Message}");
            }
            finally
            {
                isUpdatingUI = false;
            }
        }
        private bool isEventHandlerActive = false;

        private void RefreshClusterEndpoints()
        {
            try
            {
                // Prevent recursive UI updates
                if (isUpdatingUI)
                    return;

                isUpdatingUI = true;

                if (cmbEndpoints == null || cmbEndpoints.IsDisposed || !cmbEndpoints.IsHandleCreated)
                    return;

                // Ensure we're on the UI thread
                if (cmbEndpoints.InvokeRequired)
                {
                    try
                    {
                        cmbEndpoints.BeginInvoke(new Action(() => RefreshClusterEndpoints()));
                    }
                    catch (Exception)
                    {
                        // Handle invocation errors silently
                    }
                    return;
                }

                cmbEndpoints.Items.Clear();

                if (mainForm != null && mainForm.ComputeEndpoints != null && mainForm.ComputeEndpoints.Count > 0)
                {
                    foreach (var endpoint in mainForm.ComputeEndpoints)
                    {
                        string gpuInfo = endpoint.HasGPU ? " (GPU)" : "";
                        string displayText = $"{endpoint.Name} - {endpoint.IP}:{endpoint.Port}{gpuInfo}";
                        cmbEndpoints.Items.Add(displayText);
                    }

                    if (cmbEndpoints.Items.Count > 0)
                        cmbEndpoints.SelectedIndex = 0;

                    if (chkUseCluster != null && !chkUseCluster.IsDisposed && chkUseCluster.IsHandleCreated)
                        chkUseCluster.Enabled = true;
                }
                else
                {
                    cmbEndpoints.Items.Add("No endpoints available");
                    cmbEndpoints.SelectedIndex = 0;

                    // Even with no endpoints, allow the checkbox to be clicked
                    if (chkUseCluster != null && !chkUseCluster.IsDisposed && chkUseCluster.IsHandleCreated)
                        chkUseCluster.Enabled = true;

                    // Only update the checked state if we're not in an event handler already
                    if (!isEventHandlerActive && chkUseCluster != null && !chkUseCluster.IsDisposed && chkUseCluster.IsHandleCreated)
                        chkUseCluster.Checked = false;

                    useCluster = chkUseCluster != null && !chkUseCluster.IsDisposed ? chkUseCluster.Checked : false;
                }

                UpdateClusterStatus();
            }
            catch (Exception ex)
            {
                // Log the error but prevent application crash
                Logger.Log($"[NodeEditor] Error refreshing endpoints: {ex.Message}");
            }
            finally
            {
                isUpdatingUI = false;
            }
        }
        private void UpdateClusterStatus()
        {
            try
            {
                // Check for null or disposed controls
                if (lblClusterStatus == null || lblClusterStatus.IsDisposed || !lblClusterStatus.IsHandleCreated)
                    return;

                // Safe access to endpoints list
                var endpoints = mainForm?.ComputeEndpoints;
                int endpointCount = endpoints?.Count ?? 0;
                int availableEndpoints = 0;

                if (endpoints != null)
                {
                    // Only count endpoints that are actually connected
                    foreach (var endpoint in endpoints)
                    {
                        if (endpoint != null && endpoint.IsConnected && !endpoint.IsBusy)
                            availableEndpoints++;
                    }
                }

                if (useCluster && endpoints != null && endpointCount > 0)
                {
                    // Update safely with null checks
                    if (lblClusterStatus != null && !lblClusterStatus.IsDisposed && lblClusterStatus.IsHandleCreated)
                    {
                        try
                        {
                            if (lblClusterStatus.InvokeRequired)
                            {
                                lblClusterStatus.BeginInvoke(new Action(() => {
                                    lblClusterStatus.Text = $"Cluster: {availableEndpoints}/{endpointCount} endpoints";
                                    lblClusterStatus.ForeColor = Color.LightGreen;
                                }));
                            }
                            else
                            {
                                lblClusterStatus.Text = $"Cluster: {availableEndpoints}/{endpointCount} endpoints";
                                lblClusterStatus.ForeColor = Color.LightGreen;
                            }
                        }
                        catch (Exception)
                        {
                            // Silently handle UI update errors
                        }
                    }

                    if (cmbEndpoints != null && !cmbEndpoints.IsDisposed && cmbEndpoints.IsHandleCreated)
                    {
                        try
                        {
                            if (cmbEndpoints.InvokeRequired)
                            {
                                cmbEndpoints.BeginInvoke(new Action(() => cmbEndpoints.Enabled = true));
                            }
                            else
                            {
                                cmbEndpoints.Enabled = true;
                            }
                        }
                        catch (Exception)
                        {
                            // Silently handle UI update errors
                        }
                    }
                }
                else if (useCluster)
                {
                    if (lblClusterStatus != null && !lblClusterStatus.IsDisposed && lblClusterStatus.IsHandleCreated)
                    {
                        try
                        {
                            if (lblClusterStatus.InvokeRequired)
                            {
                                lblClusterStatus.BeginInvoke(new Action(() => {
                                    lblClusterStatus.Text = "No endpoints available";
                                    lblClusterStatus.ForeColor = Color.Orange;
                                }));
                            }
                            else
                            {
                                lblClusterStatus.Text = "No endpoints available";
                                lblClusterStatus.ForeColor = Color.Orange;
                            }
                        }
                        catch (Exception)
                        {
                            // Silently handle UI update errors
                        }
                    }

                    if (cmbEndpoints != null && !cmbEndpoints.IsDisposed && cmbEndpoints.IsHandleCreated)
                    {
                        try
                        {
                            if (cmbEndpoints.InvokeRequired)
                            {
                                cmbEndpoints.BeginInvoke(new Action(() => cmbEndpoints.Enabled = false));
                            }
                            else
                            {
                                cmbEndpoints.Enabled = false;
                            }
                        }
                        catch (Exception)
                        {
                            // Silently handle UI update errors
                        }
                    }
                }
                else
                {
                    if (lblClusterStatus != null && !lblClusterStatus.IsDisposed && lblClusterStatus.IsHandleCreated)
                    {
                        try
                        {
                            if (lblClusterStatus.InvokeRequired)
                            {
                                lblClusterStatus.BeginInvoke(new Action(() => {
                                    lblClusterStatus.Text = "Cluster disabled";
                                    lblClusterStatus.ForeColor = Color.Silver;
                                }));
                            }
                            else
                            {
                                lblClusterStatus.Text = "Cluster disabled";
                                lblClusterStatus.ForeColor = Color.Silver;
                            }
                        }
                        catch (Exception)
                        {
                            // Silently handle UI update errors
                        }
                    }

                    if (cmbEndpoints != null && !cmbEndpoints.IsDisposed && cmbEndpoints.IsHandleCreated)
                    {
                        try
                        {
                            if (cmbEndpoints.InvokeRequired)
                            {
                                cmbEndpoints.BeginInvoke(new Action(() => cmbEndpoints.Enabled = false));
                            }
                            else
                            {
                                cmbEndpoints.Enabled = false;
                            }
                        }
                        catch (Exception)
                        {
                            // Silently handle UI update errors
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the error but prevent application crash
                Logger.Log($"[NodeEditor] Error updating cluster status: {ex.Message}");
            }
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
            return base.ProcessCmdKey(ref msg, keyData);
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
                    if (selectedConnections.Count > 0)
                    {
                        DeleteSelectedConnections();
                    }
                    else
                    {
                        DeleteSelectedNodes();
                    }
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
                new NodeTypeInfo("Input", "Current Dataset", typeof(CurrentDatasetNode), Color.FromArgb(120, 200, 120)),
                new NodeTypeInfo("Input", "Volume Data", typeof(VolumeDataNode), Color.FromArgb(120, 200, 120)),
                new NodeTypeInfo("Input", "Label Data", typeof(LabelDataNode), Color.FromArgb(120, 200, 120)),
                new NodeTypeInfo("Input", "Label", typeof(LabelNode), Color.FromArgb(255, 180, 100)),
                new NodeTypeInfo("Input", "Current Label", typeof(CurrentLabelNode), Color.FromArgb(120, 200, 120)),
                new NodeTypeInfo("Input", "Load Dataset", typeof(LoadDatasetNode), Color.FromArgb(100, 180, 100)),
                new NodeTypeInfo("Input", "Load Multiple Datasets", typeof(LoadMultipleDatasetNode), Color.FromArgb(100, 180, 100)),
                new NodeTypeInfo("Input", "Dataset Decompression", typeof(DatasetDecompressionNode), Color.FromArgb(120, 200, 120)),
                
                // Processing nodes (Blue theme)
                new NodeTypeInfo("Tools", "Brightness & Contrast", typeof(BrightnessContrastNode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "Resample Volume", typeof(ResampleVolumeNode), Color.FromArgb(100, 180, 255)),
                new NodeTypeInfo("Tools", "Threshold", typeof(ThresholdNode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "Segment Anything", typeof(SegmentAnythingNode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "MicroSAM", typeof(MicroSAMNode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "Grounding DINO", typeof(GroundingDINONode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "Interpolate", typeof(InterpolateNode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "Manual Thresholding", typeof(ManualThresholdingNode), Color.FromArgb(120, 180, 255)),
                new NodeTypeInfo("Tools", "Binarize", typeof(BinarizeNode), Color.FromArgb(100, 150, 255)),
                new NodeTypeInfo("Tools", "Remove Small Islands", typeof(RemoveSmallIslandsNode), Color.FromArgb(120, 180, 255)),
                
                // Simulation nodes (Purple theme)
                new NodeTypeInfo("Simulation", "Pore Network", typeof(PoreNetworkNode), Color.FromArgb(180, 100, 255)),
                new NodeTypeInfo("Simulation", "Acoustic", typeof(AcousticSimulationNode), Color.FromArgb(180, 100, 255)),
                new NodeTypeInfo("Simulation", "Triaxial", typeof(TriaxialSimulationNode), Color.FromArgb(180, 100, 255)),
                new NodeTypeInfo("Simulation", "NMR Simulation", typeof(TriaxialSimulationNode), Color.FromArgb(180, 100, 255)),
                // Filtering nodes (Teal theme)
                new NodeTypeInfo("Filters", "Band Detection", typeof(BandDetectionNode), Color.FromArgb(100, 200, 200)),
                new NodeTypeInfo("Filters", "Transform", typeof(TransformDatasetNode), Color.FromArgb(100, 200, 200)),
                new NodeTypeInfo("Filters", "Core Extraction", typeof(CoreExtractionNode), Color.FromArgb(100, 200, 200)),
                new NodeTypeInfo("Filters", "Image Filter", typeof(FilterNode), Color.FromArgb(100, 200, 200)),
                
                // Material nodes (Orange theme)
                new NodeTypeInfo("Materials", "Add Material", typeof(AddMaterialNode), Color.FromArgb(255, 180, 100)),
                new NodeTypeInfo("Materials", "Remove Material", typeof(RemoveMaterialNode), Color.FromArgb(255, 180, 100)),
                new NodeTypeInfo("Materials", "Merge Materials", typeof(MergeMaterialsNode), Color.FromArgb(255, 180, 100)),
                new NodeTypeInfo("Materials", "Extract Material", typeof(ExtractMaterialsNode), Color.FromArgb(180, 100, 255)),
                new NodeTypeInfo("Materials", "Material Density", typeof(DensityNode), Color.FromArgb(255, 180, 100)),
                
                // Output nodes (Red theme)
                new NodeTypeInfo("Output", "Export Image Stack", typeof(ExportImageStackNode), Color.FromArgb(255, 120, 120)),
                new NodeTypeInfo("Output", "Save Dataset", typeof(SaveDatasetNode), Color.FromArgb(255, 120, 120)),
                new NodeTypeInfo("Output", "Save Labels", typeof(SaveLabelsNode), Color.FromArgb(255, 120, 120)),
                new NodeTypeInfo("Output", "Replace Current", typeof(ReplaceCurrentNode), Color.FromArgb(255, 140, 120)),
                new NodeTypeInfo("Output", "Statistics", typeof(StatisticsNode), Color.FromArgb(255, 120, 120)),
                new NodeTypeInfo("Output", "Dataset Compression", typeof(DatasetCompressionNode), Color.FromArgb(255, 120, 120)),

                //Analysis Nodes (Violet theme)
                new NodeTypeInfo("Analysis", "Material Statistics", typeof(MaterialStatisticsNode), Color.FromArgb(160, 120, 200)),
            };

            // Create a TreeView for nodes with collapsible categories
            var toolboxTree = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                ShowLines = false,
                FullRowSelect = true,
                ShowPlusMinus = true, // This enables collapsing/expanding
                HideSelection = false,
                ItemHeight = 22
            };

            // Group nodes by category
            var groups = nodeTypes.GroupBy(t => t.Category).ToList();
            foreach (var group in groups)
            {
                var categoryNode = new TreeNode(group.Key);
                toolboxTree.Nodes.Add(categoryNode);

                foreach (var nodeType in group)
                {
                    var nodeTreeNode = new TreeNode(nodeType.Name)
                    {
                        Tag = nodeType
                    };
                    categoryNode.Nodes.Add(nodeTreeNode);
                }
            }

            // Expand all categories initially
            toolboxTree.ExpandAll();

            // Handle node selection
            toolboxTree.NodeMouseDoubleClick += (sender, e) =>
            {
                if (e.Node?.Tag is NodeTypeInfo nodeTypeInfo)
                {
                    CreateNode(nodeTypeInfo);
                }
            };
            var emptyLabel = new Label();
            emptyLabel.Text = " ";
            toolboxPanel.Controls.Add(emptyLabel);
            toolboxPanel.Controls.Add(toolboxTree);
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

        private Point lastMousePosition; // To track mouse movements

        private void Canvas_MouseDown(object sender, MouseEventArgs e)
        {
            lastMousePosition = e.Location; // Store initial position

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
                    if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
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

                    // Clear connection selection when selecting nodes
                    selectedConnections.Clear();

                    // Update properties panel
                    UpdatePropertiesPanel(node);
                    canvasPanel.Invalidate();
                    return;
                }
            }

            // Check if clicking on a connection
            foreach (var connection in connections)
            {
                if (IsMouseOverConnection(e.Location, connection))
                {
                    if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                    {
                        // Toggle selection with Ctrl
                        if (selectedConnections.Contains(connection))
                            selectedConnections.Remove(connection);
                        else
                            selectedConnections.Add(connection);
                    }
                    else
                    {
                        // Single selection
                        selectedConnections.Clear();
                        selectedConnections.Add(connection);
                    }

                    // Clear node selection when selecting connections
                    if ((Control.ModifierKeys & Keys.Control) != Keys.Control)
                    {
                        selectedNodes.Clear();
                        selectedNode = null;
                        UpdatePropertiesPanel(null);
                    }

                    canvasPanel.Invalidate();
                    return;
                }
            }

            // If we got here, we didn't click on a node or connection
            // Clear connection selection
            selectedConnections.Clear();

            // Start rectangle selection if no node clicked
            if ((Control.ModifierKeys & Keys.Control) != Keys.Control)
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
            // Avoid excessive processing for small mouse movements to reduce flickering
            if (Math.Abs(e.X - lastMousePosition.X) < 1 &&
                Math.Abs(e.Y - lastMousePosition.Y) < 1)
            {
                return;
            }

            if (isDragging && selectedNodes.Count > 0)
            {
                // Calculate movement delta from last position
                int dx = e.X - lastMousePosition.X;
                int dy = e.Y - lastMousePosition.Y;

                if (dx != 0 || dy != 0) // Only process if there's actual movement
                {
                    // Move all selected nodes by the delta
                    foreach (var node in selectedNodes)
                    {
                        node.Position = new Point(
                            node.Position.X + dx,
                            node.Position.Y + dy);
                    }

                    // Update last position
                    lastMousePosition = e.Location;

                    // Redraw with double buffering
                    canvasPanel.Invalidate();
                }
            }
            else if (isConnecting && connectingPin != null)
            {
                // We only need to redraw if the mouse has moved significantly
                lastMousePosition = e.Location;
                canvasPanel.Invalidate();
            }
            else if (isMultiSelecting)
            {
                // Update selection rectangle based on mouse movement
                int left = Math.Min(selectionStart.X, e.X);
                int top = Math.Min(selectionStart.Y, e.Y);
                int width = Math.Abs(e.X - selectionStart.X);
                int height = Math.Abs(e.Y - selectionStart.Y);

                Rectangle newSelectionRect = new Rectangle(left, top, width, height);

                // Only update if the selection rectangle has changed significantly
                if (Math.Abs(newSelectionRect.Width - selectionRectangle.Width) > 1 ||
                    Math.Abs(newSelectionRect.Height - selectionRectangle.Height) > 1 ||
                    Math.Abs(newSelectionRect.X - selectionRectangle.X) > 1 ||
                    Math.Abs(newSelectionRect.Y - selectionRectangle.Y) > 1)
                {
                    selectionRectangle = newSelectionRect;

                    // Update node selection based on the new rectangle
                    // Track if selection actually changed to avoid unnecessary redraws
                    bool selectionChanged = false;

                    foreach (var node in nodes)
                    {
                        bool wasSelected = selectedNodes.Contains(node);
                        bool shouldBeSelected = selectionRectangle.IntersectsWith(node.Bounds);

                        if ((Control.ModifierKeys & Keys.Control) != Keys.Control)
                        {
                            // Standard selection behavior - replace selection
                            if (shouldBeSelected && !wasSelected)
                            {
                                selectedNodes.Add(node);
                                selectionChanged = true;
                            }
                            else if (!shouldBeSelected && wasSelected)
                            {
                                selectedNodes.Remove(node);
                                selectionChanged = true;
                            }
                        }
                        else
                        {
                            // Ctrl key behavior - add to selection
                            if (shouldBeSelected && !wasSelected)
                            {
                                selectedNodes.Add(node);
                                selectionChanged = true;
                            }
                        }
                    }

                    // Only redraw if selection changed or rectangle size changed significantly
                    lastMousePosition = e.Location;
                    canvasPanel.Invalidate();
                }
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
            canvasPanel.Invalidate();
        }

        private bool CanConnect(NodePin from, NodePin to)
        {
            // Can't connect to self
            if (from.Node == to.Node) return false;

            // Can't connect output to output or input to input
            if (from.IsOutput == to.IsOutput) return false;

            // Check if this exact connection already exists
            // Note: One output pin CAN connect to multiple input pins on different nodes
            // And one input pin CAN receive from multiple output pins (if needed)
            if (connections.Any(c => c.From == from && c.To == to))
                return false;

            // TODO: Add type checking

            return true;
        }

        #endregion

        #region Canvas Rendering

        private void Canvas_Paint(object sender, PaintEventArgs e)
        {
            try
            {
                // Use direct painting if buffered graphics not ready yet
                if (grafx == null)
                {
                    // Fallback direct drawing
                    var g = e.Graphics;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(canvasPanel.BackColor);
                    DrawGrid(g);

                    foreach (var connection in connections)
                    {
                        DrawConnection(g, connection);
                    }

                    if (isConnecting && connectingPin != null)
                    {
                        var mousePos = canvasPanel.PointToClient(MousePosition);
                        DrawTempConnection(g, connectingPin, mousePos);
                    }

                    foreach (var node in nodes)
                    {
                        DrawNode(g, node);
                    }

                    if (isMultiSelecting)
                    {
                        using (var pen = new Pen(Color.Yellow, 1))
                        using (var brush = new SolidBrush(Color.FromArgb(50, Color.Yellow)))
                        {
                            g.FillRectangle(brush, selectionRectangle);
                            g.DrawRectangle(pen, selectionRectangle);
                        }
                    }

                    return;
                }

                // Recreate buffer if size changed
                if (e.ClipRectangle.Width > 0 && e.ClipRectangle.Height > 0 &&
                    (e.ClipRectangle.Width != canvasPanel.Width || e.ClipRectangle.Height != canvasPanel.Height))
                {
                    if (grafx != null)
                    {
                        grafx.Dispose();
                        grafx = null;
                    }

                    if (context != null)
                    {
                        context.MaximumBuffer = new Size(Math.Max(1, canvasPanel.Width + 1),
                                                        Math.Max(1, canvasPanel.Height + 1));
                        grafx = context.Allocate(canvasPanel.CreateGraphics(),
                            new Rectangle(0, 0, Math.Max(1, canvasPanel.Width), Math.Max(1, canvasPanel.Height)));
                    }
                }

                // Get graphics object from the buffer
                if (grafx != null)
                {
                    Graphics g = grafx.Graphics;

                    // Clear the background
                    g.Clear(canvasPanel.BackColor);

                    // Set high quality rendering
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

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

                    // Render the buffered graphics to the screen
                    grafx.Render(e.Graphics);
                }
            }
            catch (Exception ex)
            {
                // Log error but prevent crash
                Logger.Log($"[NodeEditor] Error in Canvas_Paint: {ex.Message}");

                // Try direct rendering as fallback
                try
                {
                    var g = e.Graphics;
                    g.Clear(canvasPanel.BackColor);
                    g.DrawString("Rendering error - please resize window",
                                 new Font("Arial", 10), Brushes.White, new PointF(10, 10));
                }
                catch
                {
                    // If even direct rendering fails, just ignore to prevent crash
                }
            }
        }

        private void DrawGrid(Graphics g)
        {
            // Draw grid more efficiently
            int gridSpacing = 20;
            using (var pen = new Pen(Color.FromArgb(40, 40, 40), 1))
            {
                // Only draw grid lines in the visible area
                for (int x = 0; x < canvasPanel.Width; x += gridSpacing)
                {
                    g.DrawLine(pen, x, 0, x, canvasPanel.Height);
                }
                for (int y = 0; y < canvasPanel.Height; y += gridSpacing)
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
            using (var pen = new Pen(selectedConnections.Contains(connection) ? Color.Yellow : Color.White,
                                   selectedConnections.Contains(connection) ? 3 : 2))
            {
                var start = new Point(
                    connection.From.AbsolutePosition.X,
                    connection.From.AbsolutePosition.Y);
                var end = new Point(
                    connection.To.AbsolutePosition.X,
                    connection.To.AbsolutePosition.Y);

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
                    from.AbsolutePosition.X,
                    from.AbsolutePosition.Y);

                var cp1 = new Point(start.X + 50, start.Y);
                var cp2 = new Point(to.X - 50, to.Y);

                g.DrawBezier(pen, start, cp1, cp2, to);
            }
        }

        #endregion

        #region Edit Operations
        private bool IsMouseOverConnection(Point mousePos, NodeConnection connection)
        {
            var start = new Point(
                connection.From.AbsolutePosition.X,
                connection.From.AbsolutePosition.Y);
            var end = new Point(
                connection.To.AbsolutePosition.X,
                connection.To.AbsolutePosition.Y);


            return DistanceToLine(mousePos, start, end) < 5; // 5 pixels tolerance
        }

        private float DistanceToLine(Point point, Point lineStart, Point lineEnd)
        {
            float lineLength = (float)Math.Sqrt(
                Math.Pow(lineEnd.X - lineStart.X, 2) +
                Math.Pow(lineEnd.Y - lineStart.Y, 2));

            if (lineLength == 0)
                return float.MaxValue;

            // Calculate perpendicular distance from point to line using vector cross product
            float distance = Math.Abs(
                (point.Y - lineStart.Y) * (lineEnd.X - lineStart.X) -
                (point.X - lineStart.X) * (lineEnd.Y - lineStart.Y)) / lineLength;

            // Also check if the point is near the line segment, not just the infinite line
            float dot1 = (point.X - lineStart.X) * (lineEnd.X - lineStart.X) +
                        (point.Y - lineStart.Y) * (lineEnd.Y - lineStart.Y);
            float dot2 = (point.X - lineEnd.X) * (lineStart.X - lineEnd.X) +
                        (point.Y - lineEnd.Y) * (lineStart.Y - lineEnd.Y);

            // If either dot product is negative, point is outside the line segment
            if (dot1 < 0 || dot2 < 0)
            {
                // Return distance to nearest endpoint instead
                float distToStart = (float)Math.Sqrt(
                    Math.Pow(point.X - lineStart.X, 2) +
                    Math.Pow(point.Y - lineStart.Y, 2));
                float distToEnd = (float)Math.Sqrt(
                    Math.Pow(point.X - lineEnd.X, 2) +
                    Math.Pow(point.Y - lineEnd.Y, 2));
                return Math.Min(distToStart, distToEnd);
            }

            return distance;
        }

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
                    if (grafx != null)
                    {
                        grafx.Dispose();
                        grafx = null;
                    }
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

            var deleteMenuItem = new ToolStripMenuItem("Delete Selected", null, (s, e) =>
            {
                if (selectedConnections.Count > 0)
                    DeleteSelectedConnections();
                else
                    DeleteSelectedNodes();
            })
            {
                ShortcutKeys = Keys.Delete
            };

            var deleteConnectionMenuItem = new ToolStripMenuItem("Delete Connection", null, (s, e) => DeleteSelectedConnections());

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

            var clearConnectionsMenuItem = new ToolStripMenuItem("Clear All Connections");
            clearConnectionsMenuItem.Click += (s, e) => ClearConnections();

            var clearAllMenuItem = new ToolStripMenuItem("Clear All Nodes");
            clearAllMenuItem.Click += (s, e) => ClearAllNodes();

            editMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                cutMenuItem, copyMenuItem, pasteMenuItem,
                new ToolStripSeparator(),
                selectAllMenuItem, deleteMenuItem, deleteConnectionMenuItem,
                new ToolStripSeparator(),
                clearConnectionsMenuItem, clearAllMenuItem
            });

            // Execute menu
            var executeMenu = new ToolStripMenuItem("Execute");
            var executeAllMenuItem = new ToolStripMenuItem("Execute All");
            executeAllMenuItem.Click += (s, e) => ExecuteGraph();

            var validateMenuItem = new ToolStripMenuItem("Validate Graph");
            validateMenuItem.Click += (s, e) => ValidateGraph();

            // Add compute cluster specific menu items
            var configureClusterMenuItem = new ToolStripMenuItem("Configure Cluster Options");
            configureClusterMenuItem.Click += (s, e) => ShowClusterOptions();

            var refreshClusterMenuItem = new ToolStripMenuItem("Refresh Cluster Endpoints");
            refreshClusterMenuItem.Click += (s, e) => RefreshClusterEndpoints();

            executeMenu.DropDownItems.AddRange(new ToolStripItem[] {
                executeAllMenuItem,
                validateMenuItem,
                new ToolStripSeparator(),
                configureClusterMenuItem,
                refreshClusterMenuItem
            });

            menuStrip.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, executeMenu });

            return menuStrip;
        }

        private void ShowClusterOptions()
        {
            using (var dialog = new ClusterOptionsDialog(useCluster))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    useCluster = dialog.UseCluster;
                    chkUseCluster.Checked = useCluster;
                    UpdateClusterStatus();
                }
            }
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
                            Converters = { new PointJsonConverter(), new ColorJsonConverter() }
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
                            Converters = { new PointJsonConverter(), new ColorJsonConverter() }
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

        private void DeleteSelectedConnections()
        {
            if (selectedConnections.Count == 0) return;

            // Use ToList() to avoid modifying the collection during enumeration
            foreach (var connection in selectedConnections.ToList())
            {
                connections.Remove(connection);
            }

            selectedConnections.Clear();
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

                // Clear previous execution data
                foreach (var node in nodes)
                {
                    ClearNodeOutputs(node);
                }

                // Get execution order (topological sort)
                var executionOrder = GetExecutionOrder();

                if (executionOrder == null)
                {
                    MessageBox.Show("Graph contains cycles. Cannot execute.",
                                   "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                bool useClusterForExecution = useCluster && mainForm.ComputeEndpoints != null &&
                                           mainForm.ComputeEndpoints.Any(e => e.IsConnected && !e.IsBusy);

                if (useClusterForExecution)
                {
                    ExecuteGraphOnCluster(executionOrder);
                }
                else
                {
                    // Execute nodes locally in order
                    foreach (var node in executionOrder)
                    {
                        try
                        {
                            Logger.Log($"[NodeEditor] Executing {node.GetType().Name}");

                            // Highlight executing node
                            HighlightExecutingNode(node);
                            Application.DoEvents();

                            // Execute node
                            node.Execute();

                            // Small delay to show execution
                            System.Threading.Thread.Sleep(100);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[NodeEditor] Error executing {node.GetType().Name}: {ex.Message}");
                            MessageBox.Show($"Error executing {node.GetType().Name}: {ex.Message}",
                                         "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
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
        private async Task<bool> QueryServerForAvailableNodes(ComputeEndpoint endpoint)
        {
            try
            {
                Logger.Log($"[NodeEditor] Querying server {endpoint.Name} for available node types...");

                // Create command to query server
                var command = new
                {
                    Command = "GET_AVAILABLE_NODES"
                };

                string commandJson = JsonSerializer.Serialize(command);
                string resultJson = await endpoint.SendCommandAsync(commandJson);

                // Parse the result
                var result = JsonSerializer.Deserialize<JsonElement>(resultJson);

                if (result.TryGetProperty("Status", out JsonElement statusElement) &&
                    statusElement.GetString() == "OK")
                {
                    availableServerNodeTypes.Clear();

                    if (result.TryGetProperty("AvailableNodes", out JsonElement nodesElement))
                    {
                        foreach (var node in nodesElement.EnumerateArray())
                        {
                            availableServerNodeTypes.Add(node.GetString());
                        }

                        Logger.Log($"[NodeEditor] Server has {availableServerNodeTypes.Count} available node types");
                        return true;
                    }
                }

                Logger.Log($"[NodeEditor] Error querying server: {resultJson}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"[NodeEditor] Error querying server for available nodes: {ex.Message}");
                return false;
            }
        }
        private List<BaseNode> PlanGraphExecution(List<BaseNode> executionOrder)
        {
            if (!useCluster || availableServerNodeTypes.Count == 0)
            {
                // If cluster is disabled or no available nodes, execute everything locally
                return executionOrder;
            }

            // Mark nodes for local or remote execution
            foreach (var node in executionOrder)
            {
                string nodeTypeName = node.GetType().Name;
                bool isServerAvailable = availableServerNodeTypes.Contains(nodeTypeName);

                // Set a tag on the node to indicate execution target
                node.Tag = isServerAvailable ? "Remote" : "Local";

                // Only force local execution for export and save nodes
                // Allow data source nodes to be processed remotely if available on server
                if (nodeTypeName.Contains("Export") || nodeTypeName.Contains("Save"))
                {
                    node.Tag = "Local";
                }
            }

            return executionOrder;
        }

        // Cluster execution method
        private async Task ExecuteGraphOnCluster(List<BaseNode> executionOrder)
        {
            Logger.Log("[NodeEditor] Executing graph on compute cluster");

            // First, query the server for available nodes
            var endpoint = FindAvailableEndpoint();
            if (endpoint == null)
            {
                MessageBox.Show("No available compute endpoints found. Execution aborted.",
                              "Cluster Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Query for available node types
            bool querySuccess = await QueryServerForAvailableNodes(endpoint);
            if (!querySuccess)
            {
                MessageBox.Show("Failed to query server for available node types. Execution aborted.",
                              "Cluster Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Plan execution based on available node types
            executionOrder = PlanGraphExecution(executionOrder);

            // Show progress form
            using (var progressForm = new ClusterExecutionProgressForm(executionOrder.Count))
            {
                progressForm.Show(this);

                // Process each node sequentially
                int completedNodes = 0;

                foreach (var node in executionOrder)
                {
                    try
                    {
                        // Highlight executing node
                        HighlightExecutingNode(node);
                        Application.DoEvents();

                        string executionTarget = node.Tag as string ?? "Local";

                        if (executionTarget == "Remote")
                        {
                            // Execute node on the server
                            Logger.Log($"[NodeEditor] Executing {node.GetType().Name} remotely on {endpoint.Name}");

                            // Mark endpoint as busy
                            endpoint.SetBusy(true);

                            bool success = await ExecuteNodeRemotely(node, endpoint);

                            // Mark endpoint as free
                            endpoint.SetBusy(false);

                            if (!success)
                            {
                                // Fallback to local execution if remote execution fails
                                Logger.Log($"[NodeEditor] Remote execution failed, falling back to local execution for {node.GetType().Name}");
                                node.Execute();
                            }
                        }
                        else
                        {
                            // Execute the node locally
                            Logger.Log($"[NodeEditor] Executing {node.GetType().Name} locally");
                            node.Execute();
                        }

                        // Update progress
                        completedNodes++;
                        progressForm.UpdateProgress(completedNodes);

                        // Small delay to show execution
                        System.Threading.Thread.Sleep(100);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[NodeEditor] Error executing {node.GetType().Name}: {ex.Message}");
                        MessageBox.Show($"Error executing {node.GetType().Name}: {ex.Message}",
                                     "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
            }
        }
        private async Task<bool> ExecuteNodeRemotely(BaseNode node, ComputeEndpoint endpoint)
        {
            try
            {
                Logger.Log($"[NodeEditor] Executing {node.GetType().Name} remotely on {endpoint.Name}...");

                // Collect input data for the node - use a serializable dictionary structure
                Dictionary<string, string> inputData = new Dictionary<string, string>();
                Dictionary<string, byte[]> binaryData = new Dictionary<string, byte[]>();

                foreach (var pin in node.GetAllPins().Where(p => !p.IsOutput))
                {
                    var data = node.GetInputData(pin.Name);
                    if (data != null)
                    {
                        if (data is byte[] binaryValue)
                        {
                            // For binary data, store a reference and add to binary collection
                            string binaryKey = $"binary_{pin.Name}";
                            binaryData[binaryKey] = binaryValue;
                            inputData[pin.Name] = $"binary_ref:{binaryKey}";
                        }
                        else if (data is IGrayscaleVolumeData volumeData)
                        {
                            // Handle volume data by extracting raw bytes
                            string binaryKey = $"binary_{pin.Name}";
                            int width = volumeData.Width;
                            int height = volumeData.Height;
                            int depth = volumeData.Depth;
                            byte[] volumeBytes = new byte[width * height * depth];

                            // Add dimensions to metadata
                            inputData["Width"] = width.ToString();
                            inputData["Height"] = height.ToString();
                            inputData["Depth"] = depth.ToString();

                            // Extract data from volume
                            for (int z = 0; z < depth; z++)
                            {
                                for (int y = 0; y < height; y++)
                                {
                                    for (int x = 0; x < width; x++)
                                    {
                                        int index = (z * height + y) * width + x;
                                        volumeBytes[index] = volumeData[x, y, z];
                                    }
                                }
                            }

                            binaryData[binaryKey] = volumeBytes;
                            inputData[pin.Name] = $"volume_ref:{binaryKey}";
                        }
                        else if (data is ILabelVolumeData labelData)
                        {
                            // Handle label data similarly to volume data
                            string binaryKey = $"binary_labels_{pin.Name}";
                            int width = labelData.Width;
                            int height = labelData.Height;
                            int depth = labelData.Depth;
                            byte[] labelBytes = new byte[width * height * depth];

                            // Extract data from label volume
                            for (int z = 0; z < depth; z++)
                            {
                                for (int y = 0; y < height; y++)
                                {
                                    for (int x = 0; x < width; x++)
                                    {
                                        int index = (z * height + y) * width + x;
                                        labelBytes[index] = labelData[x, y, z];
                                    }
                                }
                            }

                            binaryData[binaryKey] = labelBytes;
                            inputData[pin.Name] = $"labels_ref:{binaryKey}";
                        }
                        else
                        {
                            // For other types, serialize as JSON or toString
                            try
                            {
                                inputData[pin.Name] = System.Text.Json.JsonSerializer.Serialize(data);
                            }
                            catch
                            {
                                inputData[pin.Name] = data.ToString();
                            }
                        }
                    }
                }

                // Get node-specific parameters through the standardized method
                var nodeParams = node.GetNodeParameters();
                foreach (var param in nodeParams)
                {
                    inputData[param.Key] = param.Value;
                }
                // Compress everything using memory stream
                byte[] compressedData;
                using (var memoryStream = new MemoryStream())
                using (var gzipStream = new GZipStream(memoryStream, CompressionMode.Compress))
                {
                    // Write metadata length and metadata
                    byte[] metadataBytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(inputData));
                    gzipStream.Write(BitConverter.GetBytes(metadataBytes.Length), 0, 4);
                    gzipStream.Write(metadataBytes, 0, metadataBytes.Length);

                    // Write binary keys length and keys
                    var binaryKeys = binaryData.Keys.ToList();
                    byte[] binaryKeysBytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(binaryKeys));
                    gzipStream.Write(BitConverter.GetBytes(binaryKeysBytes.Length), 0, 4);
                    gzipStream.Write(binaryKeysBytes, 0, binaryKeysBytes.Length);

                    // Write each binary data block
                    foreach (var entry in binaryData)
                    {
                        // Write key
                        byte[] keyBytes = Encoding.UTF8.GetBytes(entry.Key);
                        gzipStream.Write(BitConverter.GetBytes(keyBytes.Length), 0, 4);
                        gzipStream.Write(keyBytes, 0, keyBytes.Length);

                        // Write value
                        gzipStream.Write(BitConverter.GetBytes(entry.Value.Length), 0, 4);
                        gzipStream.Write(entry.Value, 0, entry.Value.Length);
                    }

                    gzipStream.Flush();
                    compressedData = memoryStream.ToArray();
                }

                // Create command to send to server
                var command = new Dictionary<string, string>
                {
                    ["Command"] = "EXECUTE_NODE",
                    ["NodeType"] = node.GetType().Name,
                    ["InputData"] = Convert.ToBase64String(compressedData)
                };

                // Send to server
                string commandJson = System.Text.Json.JsonSerializer.Serialize(command);
                string resultJson = await endpoint.SendCommandAsync(commandJson);

                // Parse the result
                var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(resultJson);

                if (result.TryGetProperty("Status", out var statusElement) &&
                    statusElement.GetString() == "OK" &&
                    result.TryGetProperty("OutputData", out var outputDataElement))
                {
                    // Decompress the output data
                    byte[] resultCompressed = Convert.FromBase64String(outputDataElement.GetString());

                    Dictionary<string, string> outputData = new Dictionary<string, string>();
                    Dictionary<string, byte[]> outputBinary = new Dictionary<string, byte[]>();

                    using (var memStream = new MemoryStream(resultCompressed))
                    using (var gzStream = new GZipStream(memStream, CompressionMode.Decompress))
                    {
                        // Read metadata length
                        byte[] lengthBuffer = new byte[4];
                        gzStream.Read(lengthBuffer, 0, 4);
                        int metadataLength = BitConverter.ToInt32(lengthBuffer, 0);

                        // Read metadata
                        byte[] metadataBuffer = new byte[metadataLength];
                        gzStream.Read(metadataBuffer, 0, metadataLength);
                        string metadataJson = Encoding.UTF8.GetString(metadataBuffer);
                        outputData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);

                        // Read binary keys
                        gzStream.Read(lengthBuffer, 0, 4);
                        int binaryKeysLength = BitConverter.ToInt32(lengthBuffer, 0);
                        byte[] keysBuffer = new byte[binaryKeysLength];
                        gzStream.Read(keysBuffer, 0, binaryKeysLength);
                        string keysJson = Encoding.UTF8.GetString(keysBuffer);
                        List<string> binaryKeys = System.Text.Json.JsonSerializer.Deserialize<List<string>>(keysJson);

                        // Read binary data
                        foreach (string key in binaryKeys)
                        {
                            // Read key name
                            gzStream.Read(lengthBuffer, 0, 4);
                            int keyLength = BitConverter.ToInt32(lengthBuffer, 0);
                            byte[] keyBytes = new byte[keyLength];
                            gzStream.Read(keyBytes, 0, keyLength);
                            string binaryKey = Encoding.UTF8.GetString(keyBytes);

                            // Read binary value
                            gzStream.Read(lengthBuffer, 0, 4);
                            int valueLength = BitConverter.ToInt32(lengthBuffer, 0);
                            byte[] valueBytes = new byte[valueLength];
                            gzStream.Read(valueBytes, 0, valueLength);

                            outputBinary[binaryKey] = valueBytes;
                        }
                    }

                    // Apply the results to the node's outputs
                    foreach (var pin in node.GetAllPins().Where(p => p.IsOutput))
                    {
                        if (outputData.TryGetValue(pin.Name, out string value))
                        {
                            if (value.StartsWith("volume_ref:") || value.StartsWith("binary_ref:"))
                            {
                                string binaryKey = value.Substring(value.IndexOf(':') + 1);
                                if (outputBinary.TryGetValue(binaryKey, out byte[] binaryValue))
                                {
                                    if (pin.Name == "Volume" && outputData.TryGetValue("Width", out string widthStr) &&
                                        outputData.TryGetValue("Height", out string heightStr) &&
                                        outputData.TryGetValue("Depth", out string depthStr))
                                    {
                                        try
                                        {
                                            int width = int.Parse(widthStr);
                                            int height = int.Parse(heightStr);
                                            int depth = int.Parse(depthStr);

                                            var newVolume = new ChunkedVolume(width, height, depth, 64);

                                            // Copy data to the new volume
                                            for (int z = 0; z < depth; z++)
                                            {
                                                for (int y = 0; y < height; y++)
                                                {
                                                    for (int x = 0; x < width; x++)
                                                    {
                                                        int index = (z * height + y) * width + x;
                                                        if (index < binaryValue.Length)
                                                            newVolume[x, y, z] = binaryValue[index];
                                                    }
                                                }
                                            }

                                            // Set output data
                                            node.SetOutputData(pin.Name, newVolume);
                                            Logger.Log($"[NodeEditor] Successfully reconstructed volume data ({width}x{height}x{depth})");
                                        }
                                        catch (Exception ex)
                                        {
                                            Logger.Log($"[NodeEditor] Error reconstructing volume data: {ex.Message}");
                                        }
                                    }
                                    else
                                    {
                                        // For other binary data
                                        node.SetOutputData(pin.Name, binaryValue);
                                    }
                                }
                            }
                            else if (value.StartsWith("labels_ref:"))
                            {
                                string binaryKey = value.Substring(value.IndexOf(':') + 1);
                                if (outputBinary.TryGetValue(binaryKey, out byte[] binaryValue))
                                {
                                    // Create label volume if dimensions available
                                    if (outputData.TryGetValue("Width", out string widthStr) &&
                                        outputData.TryGetValue("Height", out string heightStr) &&
                                        outputData.TryGetValue("Depth", out string depthStr))
                                    {
                                        try
                                        {
                                            int width = int.Parse(widthStr);
                                            int height = int.Parse(heightStr);
                                            int depth = int.Parse(depthStr);

                                            // Create with correct parameters including useMemoryMapping flag
                                            var newLabels = new ChunkedLabelVolume(width, height, depth, 64, false);

                                            // Copy data to the new labels
                                            for (int z = 0; z < depth; z++)
                                            {
                                                for (int y = 0; y < height; y++)
                                                {
                                                    for (int x = 0; x < width; x++)
                                                    {
                                                        int index = (z * height + y) * width + x;
                                                        if (index < binaryValue.Length)
                                                            newLabels[x, y, z] = binaryValue[index];
                                                    }
                                                }
                                            }

                                            node.SetOutputData(pin.Name, newLabels);
                                            Logger.Log($"[NodeEditor] Successfully reconstructed label data ({width}x{height}x{depth})");
                                        }
                                        catch (Exception ex)
                                        {
                                            Logger.Log($"[NodeEditor] Error reconstructing label data: {ex.Message}");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Handle regular values
                                try
                                {
                                    var deserializedValue = System.Text.Json.JsonSerializer.Deserialize<object>(value);
                                    node.SetOutputData(pin.Name, deserializedValue);
                                }
                                catch
                                {
                                    // If deserialization fails, use the string as is
                                    node.SetOutputData(pin.Name, value);
                                }
                            }
                        }
                    }

                    return true;
                }

                // If we reach here, something went wrong
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"[NodeEditor] Error executing node remotely: {ex.Message}");
                return false;
            }
        }
        // Helper method to find an available endpoint
        private ComputeEndpoint FindAvailableEndpoint()
        {
            if (mainForm.ComputeEndpoints == null || mainForm.ComputeEndpoints.Count == 0)
                return null;

            var selected = cmbEndpoints.SelectedItem as ClusterEndpointItem;
            if (selected != null && selected.Endpoint.IsConnected && !selected.Endpoint.IsBusy)
                return selected.Endpoint;

            // If no specific endpoint is selected or it's not available, find any available one
            return mainForm.ComputeEndpoints.FirstOrDefault(e => e.IsConnected && !e.IsBusy);
        }

        private void ClearNodeOutputs(BaseNode node)
        {
            // Use reflection to clear the outputData dictionary for all nodes
            var field = typeof(BaseNode).GetField("outputData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                var outputData = field.GetValue(node) as Dictionary<string, object>;
                if (outputData != null)
                    outputData.Clear();
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (grafx != null)
                    {
                        grafx.Dispose();
                        grafx = null;
                    }

                    // Release static instance reference if this instance is being disposed
                    if (NodeEditorForm.Instance == this)
                    {
                        NodeEditorForm.Instance = null;
                    }
                }
                catch (Exception ex)
                {
                    // Log but don't crash during disposal
                    Logger.Log($"[NodeEditor] Error during disposal: {ex.Message}");
                }
            }
            base.Dispose(disposing);
        }
    }

    // Add Tag property to BaseNode class
    public static class BaseNodeExtensions
    {
        public static object Tag { get; set; }
    }
}