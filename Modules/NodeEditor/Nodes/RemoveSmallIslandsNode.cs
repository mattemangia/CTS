using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class RemoveSmallIslandsNode : BaseNode
    {
        // Parameters
        public int MinimumSize { get; set; } = 100; // Default minimum island size
        public bool PreserveExterior { get; set; } = true; // Whether to preserve exterior (ID 0)
        public bool OnlyProcessSelectedMaterial { get; set; } = false; // Whether to process only a specific material
        public byte SelectedMaterialID { get; set; } = 1; // Material to process if OnlyProcessSelectedMaterial is true

        // UI Controls
        private NumericUpDown minimumSizeInput;
        private CheckBox preserveExteriorCheckbox;
        private CheckBox onlyProcessSelectedCheckbox;
        private ComboBox materialSelectionComboBox;

        // Store the processed data for output
        private ILabelVolumeData processedLabelData;
        private List<Material> inputMaterials;

        // Public accessor for the output data that connected nodes can use
        public ILabelVolumeData CleanedLabels => processedLabelData;

        // For tracking island processing
        private class Island
        {
            public byte MaterialID { get; set; }
            public int Size { get; set; }
            public HashSet<(int X, int Y, int Z)> Voxels { get; set; } = new HashSet<(int X, int Y, int Z)>();
        }

        public RemoveSmallIslandsNode(Point position) : base(position)
        {
            Color = Color.FromArgb(120, 180, 255); // Blue theme for processing nodes
        }

        protected override void SetupPins()
        {
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("CleanedLabels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48),
                Tag = this // Store reference to this node for future lookups
            };

            // Title
            var titleLabel = new Label
            {
                Text = "Remove Small Islands",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            // Minimum size input
            var sizeLabel = new Label
            {
                Text = "Minimum Island Size (voxels):",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            minimumSizeInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1000000, // 1 million voxels
                Value = MinimumSize,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Increment = 10
            };
            minimumSizeInput.ValueChanged += (s, e) => MinimumSize = (int)minimumSizeInput.Value;

            // Preserve Exterior checkbox
            preserveExteriorCheckbox = new CheckBox
            {
                Text = "Preserve Exterior (ID 0)",
                Checked = PreserveExterior,
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = Color.White
            };
            preserveExteriorCheckbox.CheckedChanged += (s, e) => PreserveExterior = preserveExteriorCheckbox.Checked;

            // Process only selected material checkbox
            onlyProcessSelectedCheckbox = new CheckBox
            {
                Text = "Process Only Selected Material",
                Checked = OnlyProcessSelectedMaterial,
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = Color.White
            };
            onlyProcessSelectedCheckbox.CheckedChanged += (s, e) => {
                OnlyProcessSelectedMaterial = onlyProcessSelectedCheckbox.Checked;
                materialSelectionComboBox.Enabled = OnlyProcessSelectedMaterial;
            };

            // Material selection combobox
            var materialLabel = new Label
            {
                Text = "Material to Process:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            materialSelectionComboBox = new ComboBox
            {
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Enabled = OnlyProcessSelectedMaterial
            };

            materialSelectionComboBox.SelectedIndexChanged += (s, e) => {
                if (materialSelectionComboBox.SelectedItem is Material mat)
                {
                    SelectedMaterialID = mat.ID;
                }
            };

            var refreshButton = new Button
            {
                Text = "Refresh Materials List",
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Margin = new Padding(0, 5, 0, 5)
            };
            refreshButton.Click += (s, e) => UpdateMaterialsList();

            // Process button
            var processButton = new Button
            {
                Text = "Remove Small Islands",
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
            panel.Controls.Add(refreshButton);
            panel.Controls.Add(materialSelectionComboBox);
            panel.Controls.Add(materialLabel);
            panel.Controls.Add(onlyProcessSelectedCheckbox);
            panel.Controls.Add(preserveExteriorCheckbox);
            panel.Controls.Add(minimumSizeInput);
            panel.Controls.Add(sizeLabel);
            panel.Controls.Add(titleLabel);

            // Add some explanation text
            var infoLabel = new Label
            {
                Text = "This node identifies connected component islands and removes those smaller than the specified size.",
                Dock = DockStyle.Bottom,
                Height = 40,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.LightGray,
                Font = new Font("Arial", 8)
            };
            panel.Controls.Add(infoLabel);

            // Initially populate materials list
            UpdateMaterialsList();

            return panel;
        }

        private void UpdateMaterialsList()
        {
            // Save the currently selected material ID
            byte currentID = SelectedMaterialID;

            materialSelectionComboBox.Items.Clear();

            // Get the materials from the connected label node
            GetInputData();

            if (inputMaterials != null && inputMaterials.Count > 0)
            {
                foreach (var material in inputMaterials)
                {
                    materialSelectionComboBox.Items.Add(material);

                    // Set the selected index based on the saved material ID
                    if (material.ID == currentID)
                    {
                        materialSelectionComboBox.SelectedItem = material;
                    }
                }

                // If nothing is selected, select the first non-exterior material
                if (materialSelectionComboBox.SelectedIndex < 0 && materialSelectionComboBox.Items.Count > 0)
                {
                    // Try to find a non-exterior material
                    var nonExterior = inputMaterials.FirstOrDefault(m => !m.IsExterior);
                    if (nonExterior != null)
                    {
                        materialSelectionComboBox.SelectedItem = nonExterior;
                        SelectedMaterialID = nonExterior.ID;
                    }
                    else
                    {
                        // Just select the first material
                        materialSelectionComboBox.SelectedIndex = 0;
                        if (materialSelectionComboBox.SelectedItem is Material mat)
                        {
                            SelectedMaterialID = mat.ID;
                        }
                    }
                }
            }
        }

        private void GetInputData()
        {
            try
            {
                // Get the connected label node
                var labelNode = GetConnectedLabelNode();

                if (labelNode != null)
                {
                    // Get label data
                    processedLabelData = labelNode.LabelData;

                    // Get materials
                    inputMaterials = labelNode.Materials;

                    Logger.Log($"[RemoveSmallIslandsNode] Successfully retrieved data from LabelNode. " +
                              $"Label volume: {(processedLabelData != null ? $"{processedLabelData.Width}x{processedLabelData.Height}x{processedLabelData.Depth}" : "null")}, " +
                              $"Materials: {(inputMaterials != null ? inputMaterials.Count.ToString() : "null")}");
                }
                else
                {
                    Logger.Log("[RemoveSmallIslandsNode] No connected LabelNode found on input pin 'Labels'");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[RemoveSmallIslandsNode] Error getting input data: {ex.Message}");
            }
        }

        private LabelNode GetConnectedLabelNode()
        {
            var connections = GetNodeConnections();
            if (connections == null) return null;

            // Find the input pin named "Labels"
            var labelsPin = inputs.FirstOrDefault(p => p.Name == "Labels");
            if (labelsPin == null) return null;

            // Find a connection to this pin
            var connection = connections.FirstOrDefault(c => c.To == labelsPin);
            if (connection == null) return null;

            // Return the connected node if it's a LabelNode
            return connection.From.Node as LabelNode;
        }

        private List<NodeConnection> GetNodeConnections()
        {
            // Find the node editor form
            var nodeEditor = FindNodeEditorForm();
            if (nodeEditor == null) return null;

            // Get connections from node editor using reflection
            var connectionsField = nodeEditor.GetType().GetField("connections",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (connectionsField == null) return null;

            return connectionsField.GetValue(nodeEditor) as List<NodeConnection>;
        }

        private Control FindNodeEditorForm()
        {
            // Look for the parent NodeEditorForm
            Control parent = null;
            Control current = this.CreatePropertyPanel()?.Parent;

            while (current != null)
            {
                if (current.GetType().Name == "NodeEditorForm")
                {
                    parent = current;
                    break;
                }
                current = current.Parent;
            }

            return parent;
        }

        public override void Execute()
        {
            try
            {
                // Get input data from connected nodes
                GetInputData();

                // Validate we have input data
                if (processedLabelData == null)
                {
                    MessageBox.Show("No label data is available. Please connect a Label node to the input.",
                        "Processing Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Check if we have materials defined
                if (OnlyProcessSelectedMaterial && (inputMaterials == null || !inputMaterials.Any(m => m.ID == SelectedMaterialID)))
                {
                    MessageBox.Show("The selected material ID was not found in the current dataset.",
                        "Material Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Validate minimum size
                if (MinimumSize <= 0)
                {
                    MessageBox.Show("Minimum island size must be greater than zero.",
                        "Invalid Size", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Show progress dialog
                using (var progress = new ProgressFormWithProgress("Removing small islands..."))
                {
                    progress.Show();

                    try
                    {
                        // Process the label volume
                        processedLabelData = RemoveSmallIslands(
                            processedLabelData,
                            MinimumSize,
                            PreserveExterior,
                            OnlyProcessSelectedMaterial,
                            SelectedMaterialID,
                            progress);

                        // Execute connected nodes
                        ExecuteConnectedOutputNodes();

                        MessageBox.Show("Small islands removed successfully!",
                            "Processing Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to process volume: {ex.Message}",
                            "Processing Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        progress.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error preparing to process volume: {ex.Message}",
                    "Processing Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExecuteConnectedOutputNodes()
        {
            var connections = GetNodeConnections();
            if (connections == null) return;

            // Find the output pin named "CleanedLabels"
            var outputPin = outputs.FirstOrDefault(p => p.Name == "CleanedLabels");
            if (outputPin == null) return;

            // Find all connections from this output pin
            var connectedNodes = connections
                .Where(c => c.From == outputPin)
                .Select(c => c.To.Node)
                .Distinct()
                .ToList();

            // Execute each connected node
            foreach (var node in connectedNodes)
            {
                Logger.Log($"[RemoveSmallIslandsNode] Notifying connected node: {node.GetType().Name}");

                // We won't directly execute the node here to avoid potential circular dependencies
                // Instead, the connected node will access the data through our CleanedLabels property
            }
        }

        private ChunkedLabelVolume RemoveSmallIslands(
            ILabelVolumeData sourceVolume,
            int minimumSize,
            bool preserveExterior,
            bool onlyProcessSelectedMaterial,
            byte selectedMaterialID,
            IProgress<int> progress = null)
        {
            int width = sourceVolume.Width;
            int height = sourceVolume.Height;
            int depth = sourceVolume.Depth;

            // Create a new label volume as a copy of the source
            ChunkedLabelVolume resultVolume = new ChunkedLabelVolume(width, height, depth, 64, false);

            // First, copy all data to the result volume
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        resultVolume[x, y, z] = sourceVolume[x, y, z];
                    }
                }
            });

            progress?.Report(10); // 10% progress for initial copy

            // Create a boolean array to track visited voxels during island identification
            bool[,,] visited = new bool[width, height, depth];

            // List to store all identified islands
            var islands = new ConcurrentBag<Island>();

            // First pass: Identify all islands using BFS algorithm
            progress?.Report(15);
            Logger.Log("[RemoveSmallIslandsNode] Starting island identification...");

            // Direction vectors for 6-connected neighbors (face-connected)
            int[] dx = { -1, 1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, -1, 1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, -1, 1 };

            // Split into chunks for parallelization (process in Z slabs)
            int numChunks = 16; // Adjustable based on dataset size
            int chunkSize = (depth + numChunks - 1) / numChunks;

            Parallel.For(0, numChunks, chunkIndex =>
            {
                int startZ = chunkIndex * chunkSize;
                int endZ = Math.Min(startZ + chunkSize, depth);

                // Local islands list for this thread
                var localIslands = new List<Island>();

                // Process each voxel in this chunk
                for (int z = startZ; z < endZ; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            // Skip if already visited
                            if (visited[x, y, z])
                                continue;

                            // Get material ID
                            byte materialID = sourceVolume[x, y, z];

                            // Skip exterior if we're preserving it
                            if (preserveExterior && materialID == 0)
                                continue;

                            // Skip if we're only processing a specific material and this isn't it
                            if (onlyProcessSelectedMaterial && materialID != selectedMaterialID)
                                continue;

                            // Start a new island
                            var island = new Island { MaterialID = materialID };

                            // Use a queue for breadth-first search
                            var queue = new Queue<(int X, int Y, int Z)>();
                            queue.Enqueue((x, y, z));
                            visited[x, y, z] = true;
                            island.Voxels.Add((x, y, z));

                            // BFS to find all connected voxels with the same material ID
                            while (queue.Count > 0)
                            {
                                var (cx, cy, cz) = queue.Dequeue();

                                // Check all 6 neighbors
                                for (int i = 0; i < 6; i++)
                                {
                                    int nx = cx + dx[i];
                                    int ny = cy + dy[i];
                                    int nz = cz + dz[i];

                                    // Check bounds
                                    if (nx < 0 || nx >= width || ny < 0 || ny >= height || nz < 0 || nz >= depth)
                                        continue;

                                    // Skip if already visited
                                    if (visited[nx, ny, nz])
                                        continue;

                                    // Check if same material
                                    if (sourceVolume[nx, ny, nz] == materialID)
                                    {
                                        queue.Enqueue((nx, ny, nz));
                                        visited[nx, ny, nz] = true;
                                        island.Voxels.Add((nx, ny, nz));
                                    }
                                }
                            }

                            // Set island size
                            island.Size = island.Voxels.Count;

                            // Add to local islands list
                            localIslands.Add(island);
                        }
                    }
                }

                // Add all local islands to the concurrent bag
                foreach (var island in localIslands)
                {
                    islands.Add(island);
                }
            });

            progress?.Report(60); // 60% progress after island identification
            Logger.Log($"[RemoveSmallIslandsNode] Identified {islands.Count} islands");

            // Second pass: Remove small islands
            int removedCount = 0;
            foreach (var island in islands)
            {
                if (island.Size < minimumSize)
                {
                    // Set all voxels in this island to 0 (exterior)
                    Parallel.ForEach(island.Voxels, voxel =>
                    {
                        resultVolume[voxel.X, voxel.Y, voxel.Z] = 0;
                    });
                    removedCount++;
                }
            }

            progress?.Report(100); // 100% progress
            Logger.Log($"[RemoveSmallIslandsNode] Removed {removedCount} islands smaller than {minimumSize} voxels");

            return resultVolume;
        }
    }
}
