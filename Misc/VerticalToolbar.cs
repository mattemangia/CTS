using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Threading.Tasks;
using Krypton.Toolkit;
using Krypton.Docking;
using Krypton.Navigator;

namespace CTS
{
    public class VerticalToolbar : KryptonPanel
    {
        private ControlForm controlForm;
        private MainForm mainForm;
        private ToolTip toolTip;
        private FlowLayoutPanel buttonPanel;
        private int buttonSize = 24; // Reduced from 36
        private int iconSize = 16;   // Reduced from 24

        // Toggle buttons for tools - use regular KryptonButton
        private KryptonButton panButton;
        private KryptonButton eraserButton;
        private KryptonButton brushButton;
        private KryptonButton measurementButton;
        private KryptonButton thresholdingButton;
        private KryptonButton pointButton;
        private KryptonButton lassoButton;
        // Toggle buttons for view options
        private KryptonButton showMaskButton;
        private KryptonButton showMaterialButton;

        // Track which buttons are toggle buttons and their states
        private System.Collections.Generic.Dictionary<KryptonButton, bool> toggleStates =
            new System.Collections.Generic.Dictionary<KryptonButton, bool>();

        public VerticalToolbar(ControlForm control, MainForm main)
        {
            controlForm = control;
            mainForm = main;

            this.Width = 36; // Reduced from 52
            this.Dock = DockStyle.Fill;
            
            InitializeToolbar();
        }

        private void InitializeToolbar()
        {
            toolTip = new ToolTip();

            // Create panel for buttons without scrolling
            buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoScroll = false, // Changed to false to remove scrollbar
                Padding = new Padding(6, 6, 6, 6), // Reduced padding
                BackColor = Color.FromArgb(45, 45, 48),
                WrapContents = false
            };

            // File operations
            AddButton("New", DrawNewIcon, "Create new project (Restart application)", () =>
            {
                var result = MessageBox.Show(
                    "Are you sure you want to restart the application?",
                    "Confirm Restart",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    Application.Restart();
                    mainForm.Close();
                }
            });

            AddButton("Open", DrawOpenIcon, "Open dataset", async () =>
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        await mainForm.LoadDatasetAsync(fbd.SelectedPath);
                    }
                }
            });

            AddButton("Save Bin", DrawSaveIcon, "Save dataset (Binary format)", () =>
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "Binary Volume|*.bin";
                    if (sfd.ShowDialog() == DialogResult.OK)
                        mainForm.SaveBinary(sfd.FileName);
                }
            });

            AddButton("Save Images", DrawSaveImagesIcon, "Export image stack", () => mainForm.ExportImages());

            AddButton("Close", DrawCloseIcon, "Close grayscale dataset", () =>
            {
                if (mainForm.volumeData != null)
                {
                    mainForm.CloseDataset();
                    MessageBox.Show("Dataset successfully closed.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                    MessageBox.Show("No dataset is currently loaded.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });

            AddSeparator();

            // View operations
            AddButton("3D View", Draw3DIcon, "Open 3D volume viewer", () =>
            {
                if (mainForm.volumeData == null && mainForm.volumeLabels == null)
                {
                    MessageBox.Show("No volume data loaded. Please load a dataset first.",
                                  "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var viewer3DForm = new SharpDXIntegration.SharpDXViewerForm(mainForm);
                viewer3DForm.Show();
            });

            AddButton("Screenshot", DrawCameraIcon, "Take screenshot", () => mainForm.SaveScreenshot());

            showMaskButton = AddToggleButton("Show Mask", DrawMaskIcon, "Toggle mask visibility", (isChecked) =>
            {
                mainForm.ShowMask = isChecked;
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();
            });

            showMaterialButton = AddToggleButton("Show Material", DrawMaterialIcon, "Render materials", (isChecked) =>
            {
                mainForm.RenderMaterials = isChecked;
                _ = mainForm.RenderOrthoViewsAsync();
                mainForm.RenderViews();
            });

            AddSeparator();

            // Tools
            panButton = AddToolButton("Pan", DrawPanIcon, "Pan tool", SegmentationTool.Pan);
            SetToggleState(panButton, true); // Default tool

            eraserButton = AddToolButton("Eraser", DrawEraserIcon, "Eraser tool", SegmentationTool.Eraser);
            brushButton = AddToolButton("Brush", DrawBrushIcon, "Brush tool", SegmentationTool.Brush);
            measurementButton = AddToolButton("Measure", DrawRulerIcon, "Measurement tool", SegmentationTool.Measurement);
            thresholdingButton = AddToolButton("Threshold", DrawThresholdIcon, "Thresholding tool", SegmentationTool.Thresholding);
            lassoButton = AddToolButton("Lasso", DrawLassoIcon, "Lasso selection tool", SegmentationTool.Lasso);
            pointButton = AddToolButton("Point", DrawPointIcon, "Point annotation tool", SegmentationTool.Point);

            AddButton("Interpolate", DrawInterpolateIcon, "Interpolate selection", () =>
            {
                // Get selected material from control form - directly access the private field
                int idx = controlForm.SelectedMaterialIndex;
                if (idx < 0 || idx >= mainForm.Materials.Count)
                {
                    MessageBox.Show("No material selected for interpolation.");
                    return;
                }

                Material mat = mainForm.Materials[idx];
                if (mat.IsExterior)
                {
                    MessageBox.Show("Cannot interpolate for the Exterior material.");
                    return;
                }

                // Run interpolation
                Task.Run(() =>
                {
                    mainForm.InterpolateSelection(mat.ID);
                });
            });

            AddSeparator();

            // Simulation tools
            AddButton("Pore Network", DrawPoreNetworkIcon, "Pore Network Modeling", () =>
            {
                if (mainForm.volumeData == null || mainForm.volumeLabels == null)
                {
                    DialogResult result = MessageBox.Show(
                        "No dataset is currently loaded. Would you like to load a saved pore network model file?",
                        "Load Pore Network Model",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (result == DialogResult.Yes)
                    {
                        using (OpenFileDialog openDialog = new OpenFileDialog())
                        {
                            openDialog.Filter = "Pore Network Model|*.dat";
                            openDialog.Title = "Load Pore Network Model";

                            if (openDialog.ShowDialog() == DialogResult.OK)
                            {
                                try
                                {
                                    var poreNetworkForm = new PoreNetworkModelingForm(mainForm, openDialog.FileName);
                                    poreNetworkForm.Show();
                                }
                                catch (Exception ex)
                                {
                                    MessageBox.Show($"Error loading pore network model: {ex.Message}",
                                        "Loading Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                }
                            }
                        }
                    }
                }
                else
                {
                    var poreNetworkForm = new PoreNetworkModelingForm(mainForm);
                    poreNetworkForm.Show();
                }
            });

            AddButton("Acoustic", DrawWaveIcon, "Acoustic Simulation", () =>
            {
                try
                {
                    var acousticSimulationForm = new AcousticSimulationForm(mainForm);
                    acousticSimulationForm.Show();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening Acoustic Simulation form: {ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });

            /*AddButton("Stress", DrawStressIcon, "Stress Analysis", () =>
            {
                if (mainForm.volumeData == null || mainForm.volumeLabels == null)
                {
                    MessageBox.Show("Please load a dataset first to perform stress analysis.",
                        "No Dataset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    var stressAnalysisForm = new StressAnalysisForm(mainForm);
                    stressAnalysisForm.Show();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening Stress Analysis form: {ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });*/

            AddButton("Triaxial", DrawTriaxialIcon, "Triaxial Simulation", () =>
            {
                if (mainForm.volumeData == null || mainForm.volumeLabels == null)
                {
                    MessageBox.Show("Please load a dataset first to perform triaxial simulation.",
                        "No Dataset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    var triaxialSimulationForm = new TriaxialSimulationForm(mainForm);
                    triaxialSimulationForm.Show();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening Triaxial Simulation form: {ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });

            AddButton("NMR", DrawNMRIcon, "NMR Simulation", () =>
            {
                if (mainForm.volumeData == null)
                {
                    MessageBox.Show("Please load a dataset first to perform NMR simulation.",
                        "No Dataset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (mainForm.volumeLabels == null)
                {
                    var result = MessageBox.Show(
                        "No labeled materials found. NMR simulation will assume all voxels are the same material.\n\n" +
                        "Would you like to continue anyway?",
                        "No Material Labels",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (result != DialogResult.Yes)
                        return;
                }

                try
                {
                    var nmrForm = new NMRSimulationForm(mainForm);
                    nmrForm.Show();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening NMR Simulation form: {ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });

            this.Controls.Add(buttonPanel);
        }

        private KryptonButton AddToolButton(string name, Action<Graphics> drawIcon, string tooltip, SegmentationTool tool)
        {
            var button = CreateButton(name, drawIcon, tooltip);
            button.Click += (s, e) =>
            {
                SelectTool(tool);
            };
            buttonPanel.Controls.Add(button);
            toggleStates[button] = false;
            return button;
        }

        private void SelectTool(SegmentationTool tool)
        {
            // Clear all tool button states
            SetToggleState(panButton, false);
            SetToggleState(eraserButton, false);
            SetToggleState(brushButton, false);
            SetToggleState(measurementButton, false);
            SetToggleState(thresholdingButton, false);
            SetToggleState(pointButton, false);
            SetToggleState(lassoButton, false);

            // Set the selected tool button state
            KryptonButton selectedButton = null;
            switch (tool)
            {
                case SegmentationTool.Pan:
                    selectedButton = panButton;
                    break;
                case SegmentationTool.Eraser:
                    selectedButton = eraserButton;
                    break;
                case SegmentationTool.Brush:
                    selectedButton = brushButton;
                    break;
                case SegmentationTool.Measurement:
                    selectedButton = measurementButton;
                    break;
                case SegmentationTool.Thresholding:
                    selectedButton = thresholdingButton;
                    break;
                case SegmentationTool.Point:
                    selectedButton = pointButton;
                    break;
                case SegmentationTool.Lasso:
                    selectedButton = lassoButton;
                    break;
            }

            if (selectedButton != null)
            {
                SetToggleState(selectedButton, true);
            }

            // Update the ControlForm UI using the new public method
            controlForm.UpdateToolUI(tool);
        }
        private void SetToggleState(KryptonButton button, bool isToggled)
        {
            if (toggleStates.ContainsKey(button))
            {
                toggleStates[button] = isToggled;

                if (isToggled)
                {
                    // Set "checked" appearance
                    button.StateNormal.Back.Color1 = Color.FromArgb(0, 122, 204);
                    button.StateNormal.Back.Color2 = Color.FromArgb(0, 122, 204);
                }
                else
                {
                    // Set normal appearance
                    button.StateNormal.Back.Color1 = Color.FromArgb(62, 62, 66);
                    button.StateNormal.Back.Color2 = Color.FromArgb(62, 62, 66);
                }

                button.Refresh();
            }
        }

        private KryptonButton AddButton(string name, Action<Graphics> drawIcon, string tooltip, Action onClick)
        {
            var button = CreateButton(name, drawIcon, tooltip);
            button.Click += (s, e) => onClick();
            buttonPanel.Controls.Add(button);
            return button;
        }

        private KryptonButton AddToggleButton(string name, Action<Graphics> drawIcon, string tooltip, Action<bool> onToggle)
        {
            var button = CreateButton(name, drawIcon, tooltip);
            toggleStates[button] = false;

            button.Click += (s, e) =>
            {
                bool newState = !toggleStates[button];
                SetToggleState(button, newState);
                onToggle(newState);
            };

            buttonPanel.Controls.Add(button);
            return button;
        }

        private KryptonButton CreateButton(string name, Action<Graphics> drawIcon, string tooltip)
        {
            var button = new KryptonButton
            {
                Size = new Size(buttonSize, buttonSize),
                Margin = new Padding(0, 1, 0, 1), // Reduced margin
                ButtonStyle = ButtonStyle.Standalone
            };

            // Set common state
            button.StateCommon.Back.Color1 = Color.FromArgb(62, 62, 66);
            button.StateCommon.Back.Color2 = Color.FromArgb(62, 62, 66);
            button.StateCommon.Border.Color1 = Color.FromArgb(86, 86, 86);
            button.StateCommon.Border.Color2 = Color.FromArgb(86, 86, 86);
            button.StateCommon.Border.DrawBorders = PaletteDrawBorders.All;
            button.StateCommon.Border.Rounding = 2; // Reduced rounding

            // Set normal state
            button.StateNormal.Back.Color1 = Color.FromArgb(62, 62, 66);
            button.StateNormal.Back.Color2 = Color.FromArgb(62, 62, 66);

            // Set tracking state
            button.StateTracking.Back.Color1 = Color.FromArgb(75, 75, 80);
            button.StateTracking.Back.Color2 = Color.FromArgb(75, 75, 80);

            // Set pressed state
            button.StatePressed.Back.Color1 = Color.FromArgb(50, 50, 54);
            button.StatePressed.Back.Color2 = Color.FromArgb(50, 50, 54);

            // Create icon
            var iconBitmap = new Bitmap(iconSize, iconSize);
            using (var g = Graphics.FromImage(iconBitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                drawIcon(g);
            }

            button.Values.Image = iconBitmap;
            button.Values.Text = "";

            toolTip.SetToolTip(button, tooltip);

            return button;
        }

        private void AddSeparator()
        {
            var separator = new Panel
            {
                Height = 1, // Reduced from 2
                Width = buttonSize,
                BackColor = Color.FromArgb(86, 86, 86),
                Margin = new Padding(0, 2, 0, 2) // Reduced margin
            };
            buttonPanel.Controls.Add(separator);
        }

        // Icon drawing methods - adjusted for smaller size
        private void DrawNewIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw document
                g.DrawRectangle(pen, 3, 2, 9, 12);

                // Draw folded corner
                g.DrawLines(pen, new Point[] {
                    new Point(12, 2),
                    new Point(12, 6),
                    new Point(8, 6),
                    new Point(8, 2)
                });

                // Draw plus sign
                g.DrawLine(pen, 7, 8, 9, 8);
                g.DrawLine(pen, 8, 7, 8, 9);
            }
        }

        private void DrawOpenIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw folder
                g.DrawLines(pen, new Point[] {
                    new Point(2, 5),
                    new Point(2, 13),
                    new Point(14, 13),
                    new Point(14, 7),
                    new Point(10, 7),
                    new Point(8, 5),
                    new Point(2, 5)
                });

                // Draw tab
                g.DrawLine(pen, 2, 5, 6, 5);
            }
        }

        private void DrawSaveIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw floppy disk outline
                g.DrawRectangle(pen, 3, 3, 10, 10);

                // Draw label area
                g.FillRectangle(brush, 5, 5, 6, 3);

                // Draw write protect tab
                g.FillRectangle(brush, 10, 3, 3, 3);
            }
        }

        private void DrawSaveImagesIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw stack of images
                g.DrawRectangle(pen, 4, 6, 8, 8);
                g.DrawRectangle(pen, 3, 5, 8, 8);
                g.DrawRectangle(pen, 2, 4, 8, 8);

                // Draw save arrow
                g.DrawLine(pen, 13, 8, 13, 12);
                g.DrawLine(pen, 11, 10, 13, 12);
                g.DrawLine(pen, 15, 10, 13, 12);
            }
        }

        private void DrawCloseIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            {
                // Draw X
                g.DrawLine(pen, 4, 4, 12, 12);
                g.DrawLine(pen, 12, 4, 4, 12);
            }
        }

        private void Draw3DIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            {
                // Draw cube in 3D
                var frontRect = new Rectangle(3, 6, 8, 8);
                g.DrawRectangle(pen, frontRect);

                // Draw back face
                g.DrawLine(pen, 6, 3, 14, 3);
                g.DrawLine(pen, 14, 3, 14, 11);

                // Connect corners
                g.DrawLine(pen, 3, 6, 6, 3);
                g.DrawLine(pen, 11, 6, 14, 3);
                g.DrawLine(pen, 11, 14, 14, 11);
            }
        }

        private void DrawCameraIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw camera body
                g.DrawRectangle(pen, 3, 6, 10, 8);

                // Draw lens
                g.DrawEllipse(pen, 6, 8, 4, 4);

                // Draw viewfinder
                g.DrawRectangle(pen, 5, 4, 6, 2);

                // Draw flash
                g.FillRectangle(brush, 4, 7, 2, 2);
            }
        }

        private void DrawMaskIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw eye shape
                var path = new GraphicsPath();
                path.AddArc(new Rectangle(3, 6, 10, 6), 0, -180);
                path.AddArc(new Rectangle(3, 6, 10, 6), 0, 180);
                g.DrawPath(pen, path);

                // Draw pupil
                g.FillEllipse(brush, 7, 8, 3, 3);
            }
        }

        private void DrawMaterialIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw layers
                g.FillRectangle(new SolidBrush(Color.Red), 3, 3, 10, 3);
                g.FillRectangle(new SolidBrush(Color.Green), 3, 7, 10, 3);
                g.FillRectangle(new SolidBrush(Color.Blue), 3, 11, 10, 3);

                // Draw outline
                g.DrawRectangle(pen, 3, 3, 10, 11);
            }
        }

        private void DrawPanIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            {
                // Draw hand
                g.DrawArc(pen, 6, 6, 6, 6, 180, 180);
                g.DrawLine(pen, 6, 9, 6, 12);
                g.DrawLine(pen, 12, 9, 12, 12);

                // Draw fingers
                g.DrawLine(pen, 6, 12, 5, 13);
                g.DrawLine(pen, 8, 12, 7, 13);
                g.DrawLine(pen, 10, 12, 11, 13);
                g.DrawLine(pen, 12, 12, 13, 13);

                // Draw thumb
                g.DrawLine(pen, 6, 9, 5, 8);
            }
        }

        private void DrawEraserIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw eraser
                g.DrawPolygon(pen, new Point[] {
                    new Point(3, 12),
                    new Point(6, 5),
                    new Point(13, 5),
                    new Point(15, 12),
                    new Point(3, 12)
                });

                // Draw eraser tip
                g.FillPolygon(brush, new Point[] {
                    new Point(3, 12),
                    new Point(6, 12),
                    new Point(5, 14),
                    new Point(3, 14)
                });
            }
        }

        private void DrawBrushIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw brush handle
                g.DrawRectangle(pen, 7, 3, 3, 7);

                // Draw brush bristles
                g.FillPolygon(brush, new Point[] {
                    new Point(6, 10),
                    new Point(11, 10),
                    new Point(12, 14),
                    new Point(5, 14)
                });
            }
        }

        private void DrawRulerIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            {
                // Draw ruler
                g.DrawRectangle(pen, 3, 7, 10, 3);

                // Draw marks
                for (int i = 4; i < 13; i += 2)
                {
                    g.DrawLine(pen, i, 7, i, 9);
                }
            }
        }

        private void DrawThresholdIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw histogram
                g.DrawLine(pen, 3, 13, 13, 13);
                g.DrawLine(pen, 3, 13, 3, 3);

                // Draw bars
                g.FillRectangle(brush, 5, 11, 2, 2);
                g.FillRectangle(brush, 8, 8, 2, 5);
                g.FillRectangle(brush, 11, 6, 2, 7);

                // Draw threshold line
                using (var redPen = new Pen(Color.Red, 1.5f))
                {
                    g.DrawLine(redPen, 9, 3, 9, 13);
                }
            }
        }

        private void DrawPointIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw crosshair
                g.DrawLine(pen, 8, 3, 8, 13);
                g.DrawLine(pen, 3, 8, 13, 8);

                // Draw center point
                g.FillEllipse(brush, 7, 7, 3, 3);
            }
        }

        private void DrawInterpolateIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw top slice
                g.DrawEllipse(pen, 4, 3, 8, 3);

                // Draw bottom slice
                g.DrawEllipse(pen, 4, 11, 8, 3);

                // Draw connecting lines
                using (var dashedPen = new Pen(Color.White, 1))
                {
                    dashedPen.DashStyle = DashStyle.Dash;
                    g.DrawLine(dashedPen, 4, 6, 4, 11);
                    g.DrawLine(dashedPen, 12, 6, 12, 11);
                }

                // Draw middle interpolated slice
                g.FillEllipse(brush, 6, 7, 5, 2);
            }
        }

        private void DrawPoreNetworkIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            using (var brush = new SolidBrush(Color.White))
            {
                // Draw network nodes
                g.FillEllipse(brush, 3, 3, 3, 3);
                g.FillEllipse(brush, 11, 3, 3, 3);
                g.FillEllipse(brush, 7, 7, 3, 3);
                g.FillEllipse(brush, 3, 11, 3, 3);
                g.FillEllipse(brush, 11, 11, 3, 3);

                // Draw connections
                g.DrawLine(pen, 4, 4, 8, 8);
                g.DrawLine(pen, 12, 4, 8, 8);
                g.DrawLine(pen, 4, 12, 8, 8);
                g.DrawLine(pen, 12, 12, 8, 8);
            }
        }

        private void DrawWaveIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            {
                // Draw sound waves
                var path = new GraphicsPath();
                path.AddBezier(3, 8, 6, 5, 10, 11, 13, 8);
                g.DrawPath(pen, path);

                // Draw speaker
                g.DrawPolygon(pen, new Point[] {
                    new Point(2, 7),
                    new Point(4, 7),
                    new Point(6, 5),
                    new Point(6, 11),
                    new Point(4, 9),
                    new Point(2, 9)
                });
            }
        }

        private void DrawStressIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            {
                // Draw compression arrows
                g.DrawLine(pen, 8, 3, 8, 6);
                g.DrawLine(pen, 8, 13, 8, 10);

                // Draw arrowheads
                g.DrawLine(pen, 7, 6, 8, 6);
                g.DrawLine(pen, 9, 6, 8, 6);
                g.DrawLine(pen, 7, 10, 8, 10);
                g.DrawLine(pen, 9, 10, 8, 10);

                // Draw sample
                g.DrawRectangle(pen, 6, 7, 5, 3);
            }
        }

        private void DrawTriaxialIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            {
                // Draw cylinder
                g.DrawEllipse(pen, 6, 3, 5, 3);
                g.DrawEllipse(pen, 6, 11, 5, 3);
                g.DrawLine(pen, 6, 4, 6, 12);
                g.DrawLine(pen, 11, 4, 11, 12);

                // Draw radial arrows
                g.DrawLine(pen, 3, 8, 6, 8);
                g.DrawLine(pen, 14, 8, 11, 8);

                // Draw arrowheads
                g.DrawLine(pen, 6, 8, 5, 7);
                g.DrawLine(pen, 6, 8, 5, 9);
                g.DrawLine(pen, 11, 8, 12, 7);
                g.DrawLine(pen, 11, 8, 12, 9);
            }
        }

        private void DrawNMRIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            {
                // Draw magnet poles
                g.DrawRectangle(pen, 3, 3, 3, 10);
                g.DrawRectangle(pen, 11, 3, 3, 10);

                // Draw sample holder
                g.DrawEllipse(pen, 7, 7, 3, 3);

                // Draw magnetic field lines
                using (var dashedPen = new Pen(Color.White, 1))
                {
                    dashedPen.DashStyle = DashStyle.Dash;
                    g.DrawLine(dashedPen, 6, 6, 11, 6);
                    g.DrawLine(dashedPen, 6, 8, 11, 8);
                    g.DrawLine(dashedPen, 6, 10, 11, 10);
                }

                // Draw N and S labels
                using (var font = new Font("Arial", 5))
                using (var brush = new SolidBrush(Color.White))
                {
                    g.DrawString("N", font, brush, 2, 2);
                    g.DrawString("S", font, brush, 12, 2);
                }
            }
        }
        private void DrawLassoIcon(Graphics g)
        {
            using (var pen = new Pen(Color.White, 1.5f))
            {
                // Draw lasso shape
                var path = new GraphicsPath();
                path.AddBezier(
                    new Point(4, 10),
                    new Point(4, 5),
                    new Point(10, 2),
                    new Point(12, 6)
                );
                path.AddBezier(
                    new Point(12, 6),
                    new Point(14, 10),
                    new Point(12, 14),
                    new Point(8, 14)
                );
                path.AddBezier(
                    new Point(8, 14),
                    new Point(4, 14),
                    new Point(4, 12),
                    new Point(4, 10)
                );

                g.DrawPath(pen, path);

                // Draw handle
                g.DrawLine(pen, 8, 14, 6, 16);
                g.DrawLine(pen, 6, 16, 8, 16);
            }
        }
    }

    // Extension to ControlForm to expose needed properties
    public partial class ControlForm
    {
        public int SelectedMaterialIndex
        {
            get { return lstMaterials.SelectedIndex; }
        }
    }
}