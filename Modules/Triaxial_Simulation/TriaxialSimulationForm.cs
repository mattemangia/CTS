using CTS;
using Krypton.Toolkit;
using Microsoft.Office.Interop.Word;
using Microsoft.Vbe.Interop;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static MaterialDensityLibrary;
using Font = System.Drawing.Font;
using Point = System.Drawing.Point;
using System.Collections.Concurrent;
using Rectangle = System.Drawing.Rectangle;
using System.IO;
using static CTS.MeshGenerator;
using Task = System.Threading.Tasks.Task;
using CTS.Misc;
using MathHelper = OpenTK.MathHelper;
using System.Text;


namespace CTS
{
    public partial class TriaxialSimulationForm : KryptonForm, IMaterialDensityProvider
    {
        private MainForm mainForm;
        private Material selectedMaterial;
        private KryptonButton btnGenerateMesh;
        private object labelVolume;
        private byte[,,] densityVolume;
        private double pixelSize;
        private int width, height, depth;
        private List<Vector3> vertices = new List<Vector3>();
        private List<Vector3> normals = new List<Vector3>();
        private List<int> indices = new List<int>();
        private List<float> densityValues = new List<float>();
        private List<TetrahedralElement> tetrahedralElements = new List<TetrahedralElement>();
        private KryptonButton btnSaveImage;
        private GLControl glControl;
        private float rotationX = 0.0f;
        private float rotationY = 0.0f;
        private float zoom = 1.0f;
        private bool wireframeMode = true;
        private float minDensity = float.MaxValue;
        private float maxDensity = float.MinValue;
        private bool glControlInitialized = false;
        private Panel colorLegendPanel;
        private DirectTriaxialCompute computeEngine;
        private bool useDirectCompute = true;
        private bool showFailedElements = true; // Default to showing failed elements
        private KryptonCheckBox chkShowFailedElements;
        private KryptonButton btnContinueSimulation;
        private float volumetricStrain = 0.0f;
        private float elasticEnergy = 0.0f;
        private float plasticEnergy = 0.0f;
        private bool failureState = false;
        private KryptonButton btnExportToVolume;

        // Density calibration data
        private List<CalibrationPoint> calibrationPoints = new List<CalibrationPoint>();
        private bool isDensityCalibrated = false;
        private double densityCalibrationSlope = 1.0;
        private double densityCalibrationIntercept = 0.0;
        private double materialVolume = 0.0; // in m³

        // Simulation parameters
        private enum SimulationDirection { X, Y, Z }
        private SimulationDirection selectedDirection = SimulationDirection.Z;
        private bool isElasticEnabled = true;
        private bool isPlasticEnabled = false;
        private bool isBrittleEnabled = false;
        private float minPressure = 0.0f;
        private float maxPressure = 1000.0f; // kPa
        private float youngModulus = 10000.0f; // MPa
        private float poissonRatio = 0.3f;
        private float yieldStrength = 500.0f; // MPa
        private float brittleStrength = 800.0f; // MPa
        private float currentStrain = 0.0f;
        private float maxStrain = 0.2f; // 20% strain
        private bool simulationRunning = false;
        private List<System.Drawing.Point> stressStrainCurve = new List<Point>();
        private List<Vector3> deformedVertices = new List<Vector3>();
        private System.Windows.Forms.Timer simulationTimer;
        private bool vboInitialized = false;
        private int vertexVBO = 0;
        private int normalVBO = 0;
        private int indexVBO = 0;


        // Petrophysical parameters
        private float cohesion = 50.0f; // MPa
        private float frictionAngle = 30.0f; // degrees
        private float normalStress = 0.0f; // MPa
        private float shearStress = 0.0f; // MPa
        private float bulkDensity = 2500.0f; // kg/m³ (default value)
        private float porosity = 0.2f; // Default porosity (0-1)
        private float bulkModulus = 10000.0f; // MPa
        private float permeability = 0.01f; // Darcy
        private Dictionary<int, float> elementStresses = new Dictionary<int, float>();
        private PictureBox mohrCoulombGraph;
        private bool isRotating = false;
        private System.Diagnostics.Stopwatch dragTimer = new System.Diagnostics.Stopwatch();
        private const int REDRAW_INTERVAL_MS = 16;
        // Progress tracking
        private ProgressBar progressBar;
        private Label progressLabel;
        private BackgroundWorker meshWorker;
        private bool meshGenerationComplete = false;
        private bool fastSimulationMode = false;
        private KryptonCheckBox chkFastSimulation;

        // Sampling parameters for mesh generation - to improve performance
        private int samplingRate = 2; // Sample every Nth voxel 

        // UI components
        private KryptonComboBox comboMaterials;
        private KryptonComboBox comboDirection;
        private KryptonCheckBox chkElastic;
        private KryptonCheckBox chkPlastic;
        private KryptonCheckBox chkBrittle;
        private KryptonCheckBox chkShowDensity;
        private KryptonNumericUpDown numMinPressure;
        private KryptonNumericUpDown numMaxPressure;
        private KryptonNumericUpDown numYoungModulus;
        private KryptonNumericUpDown numPoissonRatio;
        private KryptonNumericUpDown numYieldStrength;
        private KryptonNumericUpDown numBrittleStrength;
        private KryptonNumericUpDown numCohesion;
        private KryptonNumericUpDown numFrictionAngle;
        private KryptonNumericUpDown numBulkDensity;
        private KryptonNumericUpDown numPorosity;
        private KryptonNumericUpDown numBulkModulus;
        private KryptonNumericUpDown numPermeability;
        private KryptonButton btnStartSimulation;
        private KryptonButton btnStopSimulation;
        private KryptonButton btnDensitySettings;
        private KryptonCheckBox chkWireframe;
        private KryptonTrackBar trackSamplingRate;
        private KryptonPanel renderPanel;
        private KryptonPanel controlPanel;
        private KryptonPanel simulationPanel;
        private PictureBox stressStrainGraph;
        private KryptonGroupBox petrophysicalGroup;
        private KryptonButton btnExportResults;
        private Point lastMousePos;
        private bool isDragging = false;
        private int vertexBufferId = 0;
        private int normalBufferId = 0;
        private int colorBufferId = 0;
        private int indexBufferId = 0;
        private bool buffersInitialized = false;
        private System.Windows.Forms.Timer rotationTimer;
        private bool hardwareAccelerated = true;
        private TriaxialDiagramsForm diagramsForm;
        private int fontTextureId = 0;
        private bool fontTextureInitialized = false;
        private Bitmap fontTexture = null;
        private readonly Dictionary<char, Rectangle> characterRects = new Dictionary<char, Rectangle>();
        private KryptonCheckBox chkUseDirectCompute;
        private bool enableTransparency = false;
        private KryptonCheckBox chkEnableTransparency;
        private float transparencyLevel = 0.7f; // 0.0 = fully transparent, 1.0 = fully opaque

        // Implement IMaterialDensityProvider interface
        public Material SelectedMaterial => selectedMaterial;
        private void InitializeDirectCompute()
        {
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }
            try
            {
                // Create the DirectCompute engine
                if (computeEngine != null)
                {
                    // Properly dispose existing instance before creating a new one
                    computeEngine.ProgressUpdated -= ComputeEngine_ProgressUpdated;
                    computeEngine.SimulationCompleted -= ComputeEngine_SimulationCompleted;
                    computeEngine.Dispose();
                }

                computeEngine = new DirectTriaxialCompute();
                computeEngine.SetCompatibilityMode(true);
                // Hook up events with explicit delegate references to prevent memory leaks
                computeEngine.ProgressUpdated += new EventHandler<DirectComputeProgressEventArgs>(ComputeEngine_ProgressUpdated);
                computeEngine.SimulationCompleted += new EventHandler<DirectComputeCompletedEventArgs>(ComputeEngine_SimulationCompleted);

                // Enable direct compute by default if available
                useDirectCompute = true;

                // Update UI to reflect hardware acceleration status
                if (chkUseDirectCompute != null && !chkUseDirectCompute.IsDisposed)
                {
                    chkUseDirectCompute.Checked = useDirectCompute;
                }

                // Log success
                Logger.Log("[TriaxialSimulationForm] Direct compute engine initialized successfully");
            }
            catch (Exception ex)
            {
                useDirectCompute = false;

                // Update UI to reflect hardware acceleration status
                if (chkUseDirectCompute != null && !chkUseDirectCompute.IsDisposed)
                {
                    chkUseDirectCompute.Checked = false;
                }

                Logger.Log($"[TriaxialSimulationForm] Error initializing direct compute: {ex.Message}");
                Logger.Log("[TriaxialSimulationForm] Will use standard computation method");

                if (ex.InnerException != null)
                {
                    Logger.Log($"[TriaxialSimulationForm] Inner exception: {ex.InnerException.Message}");
                }
            }
        }
        private void AddVisualizationControls(Panel controlsContent)
        {
            // Visualization Settings Label
            Label lblVisualizationSettings = new Label
            {
                Text = "Visualization Settings",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, 700)
            };
            controlsContent.Controls.Add(lblVisualizationSettings);

            // Wireframe mode
            chkWireframe = new KryptonCheckBox
            {
                Text = "Wireframe Mode",
                Location = new Point(140, 730),
                Checked = true,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkWireframe.CheckedChanged += ChkWireframe_CheckedChanged;
            controlsContent.Controls.Add(chkWireframe);

            // Show density checkbox
            chkShowDensity = new KryptonCheckBox
            {
                Text = "Show Density Map",
                Location = new Point(10, 760),
                Checked = true,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkShowDensity.CheckedChanged += ChkShowDensity_CheckedChanged;
            controlsContent.Controls.Add(chkShowDensity);

            // Failed elements visualization
            chkShowFailedElements = new KryptonCheckBox
            {
                Text = "Highlight Failed Elements",
                Location = new Point(10, 790),
                Checked = true,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkShowFailedElements.CheckedChanged += (s, e) => {
                showFailedElements = chkShowFailedElements.Checked;
                glControl.Invalidate();
            };
            controlsContent.Controls.Add(chkShowFailedElements);

            // Transparency controls
            chkEnableTransparency = new KryptonCheckBox
            {
                Text = "Enable Transparency",
                Location = new Point(10, 820),
                Checked = false,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkEnableTransparency.CheckedChanged += (s, e) => {
                enableTransparency = chkEnableTransparency.Checked;
                glControl.Invalidate();
            };
            controlsContent.Controls.Add(chkEnableTransparency);

            // Transparency level slider
            Label lblTransparency = new Label
            {
                Text = "Transparency Level:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 850)
            };
            controlsContent.Controls.Add(lblTransparency);

            KryptonTrackBar trackTransparency = new KryptonTrackBar
            {
                Location = new Point(140, 850),
                Width = 180,
                Minimum = 0,
                Maximum = 100,
                Value = 70, // Default to 70% opacity (30% transparency)
                TickFrequency = 10,
                StateCommon = {
            Track = { Color1 = Color.FromArgb(80, 80, 80) },
            Tick = { Color1 = Color.Silver }
        },
                Enabled = enableTransparency
            };
            trackTransparency.ValueChanged += (s, e) => {
                transparencyLevel = trackTransparency.Value / 100.0f;
                glControl.Invalidate();
            };
            controlsContent.Controls.Add(trackTransparency);

            // Fast simulation mode checkbox
            chkFastSimulation = new KryptonCheckBox
            {
                Text = "Fast Simulation Mode (Render only final result)",
                Location = new Point(10, 880),
                Checked = false,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkFastSimulation.CheckedChanged += (s, e) => {
                fastSimulationMode = chkFastSimulation.Checked;
            };
            controlsContent.Controls.Add(chkFastSimulation);

            // Hardware acceleration checkbox
            chkUseDirectCompute = new KryptonCheckBox
            {
                Text = "Use Hardware Acceleration",
                Location = new Point(10, 910),
                Checked = true,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkUseDirectCompute.CheckedChanged += (s, e) => {
                useDirectCompute = chkUseDirectCompute.Checked;
            };
            controlsContent.Controls.Add(chkUseDirectCompute);
        }
        private void AddTransparencyControls()
        {
            // Add checkbox for transparency
            chkEnableTransparency = new KryptonCheckBox
            {
                Text = "Enable Transparency",
                Location = new Point(140, 780), // Adjust position as needed
                Checked = false,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkEnableTransparency.CheckedChanged += (s, e) => {
                enableTransparency = chkEnableTransparency.Checked;
                glControl.Invalidate();
            };

            // Add to the same panel as other controls
            foreach (Control control in this.Controls)
            {
                if (control is TableLayoutPanel mainLayout)
                {
                    foreach (Control panelControl in mainLayout.Controls)
                    {
                        if (panelControl is Panel panel)
                        {
                            foreach (Control c in panel.Controls)
                            {
                                if (c is Panel scrollablePanel && scrollablePanel.AutoScroll)
                                {
                                    foreach (Control sc in scrollablePanel.Controls)
                                    {
                                        if (sc is Panel content && content.Height > 800)
                                        {
                                            content.Controls.Add(chkEnableTransparency);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        private void AddFailedElementsVisualization()
        {
            // Add checkbox for failed elements visualization
            chkShowFailedElements = new KryptonCheckBox
            {
                Text = "Highlight Failed Elements",
                Location = new Point(140, 750), // Adjust position as needed
                Checked = true,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkShowFailedElements.CheckedChanged += (s, e) => {
                showFailedElements = chkShowFailedElements.Checked;
                glControl.Invalidate();
            };

            // Find the controls content panel to add the checkbox
            foreach (Control control in this.Controls)
            {
                if (control is TableLayoutPanel mainLayout)
                {
                    foreach (Control panelControl in mainLayout.Controls)
                    {
                        if (panelControl is Panel panel)
                        {
                            foreach (Control c in panel.Controls)
                            {
                                if (c is Panel scrollablePanel && scrollablePanel.AutoScroll)
                                {
                                    foreach (Control sc in scrollablePanel.Controls)
                                    {
                                        if (sc is Panel content && content.Height > 800)
                                        {
                                            content.Controls.Add(chkShowFailedElements);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        private void InitializeDiagramsForm()
        {
            diagramsForm = new TriaxialDiagramsForm();
        }
        private void AddDiagramsButton()
        {
            // Create the button
            KryptonButton btnShowDiagrams = new KryptonButton
            {
                Text = "Show Diagrams",
                Width = 310,
                Height = 30,
                StateCommon = {
            Back = { Color1 = Color.FromArgb(80, 80, 120) },
            Content = { ShortText = { Color1 = Color.White } }
        }
            };

            // Add the button click event
            btnShowDiagrams.Click += (s, e) => {
                if (diagramsForm == null || diagramsForm.IsDisposed)
                {
                    InitializeDiagramsForm();
                }
                diagramsForm.Show();
                UpdateDiagramsForm();
            };

            // Find a suitable container for the button
            // Option 1: Add to the form directly with absolute positioning
            btnShowDiagrams.Location = new Point(10, this.ClientSize.Height - 50);
            this.Controls.Add(btnShowDiagrams);

            // Or Option 2: Find an existing container panel if available
            // Find the last panel in the form that might be our controls panel
            Panel controlPanel = null;
            foreach (Control control in this.Controls)
            {
                if (control is TableLayoutPanel mainLayout)
                {
                    foreach (Control panelControl in mainLayout.Controls)
                    {
                        if (panelControl is Panel panel &&
                            (panel.Name.Contains("control") || panel.Name.Contains("Control") ||
                             panel.Tag?.ToString() == "controls"))
                        {
                            controlPanel = panel;
                            break;
                        }
                    }
                }
            }

            // If we found a control panel, use that instead
            if (controlPanel != null)
            {
                // Remove from form
                this.Controls.Remove(btnShowDiagrams);

                // Find the right position - find the last button and position after it
                int maxY = 10;
                foreach (Control c in controlPanel.Controls)
                {
                    if (c is Button || c is KryptonButton)
                    {
                        int bottom = c.Location.Y + c.Height;
                        if (bottom > maxY)
                            maxY = bottom;
                    }
                }

                btnShowDiagrams.Location = new Point(10, maxY + 10);
                controlPanel.Controls.Add(btnShowDiagrams);
            }
        }
        private void InitializeVBOs()
        {
            if (vboInitialized || deformedVertices.Count == 0)
                return;

            // Generate buffer IDs
            GL.GenBuffers(1, out vertexVBO);
            GL.GenBuffers(1, out normalVBO);
            GL.GenBuffers(1, out indexVBO);

            // Upload vertex data
            float[] vertexData = new float[deformedVertices.Count * 3];
            for (int i = 0; i < deformedVertices.Count; i++)
            {
                vertexData[i * 3] = deformedVertices[i].X;
                vertexData[i * 3 + 1] = deformedVertices[i].Y;
                vertexData[i * 3 + 2] = deformedVertices[i].Z;
            }

            GL.BindBuffer(BufferTarget.ArrayBuffer, vertexVBO);
            GL.BufferData(BufferTarget.ArrayBuffer,
                         (IntPtr)(vertexData.Length * sizeof(float)),
                         vertexData, BufferUsageHint.StaticDraw);

            // Upload normal data
            float[] normalData = new float[normals.Count * 3];
            for (int i = 0; i < normals.Count; i++)
            {
                normalData[i * 3] = normals[i].X;
                normalData[i * 3 + 1] = normals[i].Y;
                normalData[i * 3 + 2] = normals[i].Z;
            }

            GL.BindBuffer(BufferTarget.ArrayBuffer, normalVBO);
            GL.BufferData(BufferTarget.ArrayBuffer,
                         (IntPtr)(normalData.Length * sizeof(float)),
                         normalData, BufferUsageHint.StaticDraw);

            // Upload indices
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexVBO);
            GL.BufferData(BufferTarget.ElementArrayBuffer,
                         (IntPtr)(indices.Count * sizeof(int)),
                         indices.ToArray(), BufferUsageHint.StaticDraw);

            // Unbind buffers
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);

            vboInitialized = true;
        }
        private void ReleaseVBOs()
        {
            if (!vboInitialized)
                return;

            GL.DeleteBuffers(1, ref vertexVBO);
            GL.DeleteBuffers(1, ref normalVBO);
            GL.DeleteBuffers(1, ref indexVBO);

            vboInitialized = false;
        }
        private int highDetailList = 0;
        private int lowDetailList = 0;

        private void BuildDisplayLists()
        {
            // Delete old lists if they exist
            if (highDetailList != 0)
            {
                GL.DeleteLists(highDetailList, 1);
                GL.DeleteLists(lowDetailList, 1);
            }

            // Create high detail display list
            highDetailList = GL.GenLists(1);
            GL.NewList(highDetailList, ListMode.Compile);
            RenderFullMesh(false);
            GL.EndList();

            // Create low detail display list (for rotation)
            lowDetailList = GL.GenLists(1);
            GL.NewList(lowDetailList, ListMode.Compile);
            RenderLowDetailMesh();
            GL.EndList();
        }
        private void RenderFullMesh(bool useVBO)
        {
            bool showDensity = chkShowDensity.Checked;

            // Apply transparency if enabled
            if (enableTransparency)
            {
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            }
            else
            {
                GL.Disable(EnableCap.Blend);
            }

            if (useVBO && vboInitialized)
            {
                // Render using VBOs
                GL.EnableClientState(ArrayCap.VertexArray);
                GL.EnableClientState(ArrayCap.NormalArray);

                GL.BindBuffer(BufferTarget.ArrayBuffer, vertexVBO);
                GL.VertexPointer(3, VertexPointerType.Float, 0, IntPtr.Zero);

                GL.BindBuffer(BufferTarget.ArrayBuffer, normalVBO);
                GL.NormalPointer(NormalPointerType.Float, 0, IntPtr.Zero);

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexVBO);

                // If highlighting failed elements, render in two passes
                if (showFailedElements && elementStresses.Count > 0)
                {
                    // First pass: render all elements
                    for (int i = 0; i < indices.Count; i += 3)
                    {
                        int elementIndex = i / 3;
                        bool isFailed = elementStresses.TryGetValue(elementIndex, out float stress) && stress < 0;

                        if (!isFailed)
                        {
                            // Regular elements - set color based on density or default white
                            if (showDensity)
                            {
                                int vertexIndex = indices[i];
                                if (vertexIndex < densityValues.Count)
                                {
                                    float density = densityValues[vertexIndex];
                                    float normalizedDensity = (density - minDensity) / (maxDensity - minDensity);
                                    normalizedDensity = Math.Max(0.0f, Math.Min(1.0f, normalizedDensity));
                                    float[] color = GetColorComponents(normalizedDensity);

                                    if (enableTransparency)
                                        GL.Color4(color[0], color[1], color[2], transparencyLevel);
                                    else
                                        GL.Color3(color[0], color[1], color[2]);
                                }
                                else
                                {
                                    if (enableTransparency)
                                        GL.Color4(1.0f, 1.0f, 1.0f, transparencyLevel);
                                    else
                                        GL.Color3(1.0f, 1.0f, 1.0f);
                                }
                            }
                            else
                            {
                                if (enableTransparency)
                                    GL.Color4(1.0f, 1.0f, 1.0f, transparencyLevel);
                                else
                                    GL.Color3(1.0f, 1.0f, 1.0f);
                            }

                            // Draw this triangle
                            GL.DrawElements(PrimitiveType.Triangles, 3, DrawElementsType.UnsignedInt, new IntPtr(i * sizeof(int)));
                        }
                    }

                    // Second pass: render failed elements with highlight color
                    for (int i = 0; i < indices.Count; i += 3)
                    {
                        int elementIndex = i / 3;
                        bool isFailed = elementStresses.TryGetValue(elementIndex, out float stress) && stress < 0;

                        if (isFailed)
                        {
                            // Failed elements - use red with higher opacity
                            if (enableTransparency)
                                GL.Color4(1.0f, 0.0f, 0.0f, Math.Min(1.0f, transparencyLevel + 0.3f));
                            else
                                GL.Color3(1.0f, 0.0f, 0.0f);

                            // Draw this triangle
                            GL.DrawElements(PrimitiveType.Triangles, 3, DrawElementsType.UnsignedInt, new IntPtr(i * sizeof(int)));
                        }
                    }
                }
                else
                {
                    // Regular rendering (no failed elements highlight)
                    if (showDensity)
                    {
                        // Render triangles one by one with appropriate colors
                        for (int i = 0; i < indices.Count; i += 3)
                        {
                            int vertexIndex = indices[i];
                            if (vertexIndex < densityValues.Count)
                            {
                                float density = densityValues[vertexIndex];
                                float normalizedDensity = (density - minDensity) / (maxDensity - minDensity);
                                normalizedDensity = Math.Max(0.0f, Math.Min(1.0f, normalizedDensity));
                                float[] color = GetColorComponents(normalizedDensity);

                                if (enableTransparency)
                                    GL.Color4(color[0], color[1], color[2], transparencyLevel);
                                else
                                    GL.Color3(color[0], color[1], color[2]);
                            }
                            else
                            {
                                if (enableTransparency)
                                    GL.Color4(1.0f, 1.0f, 1.0f, transparencyLevel);
                                else
                                    GL.Color3(1.0f, 1.0f, 1.0f);
                            }

                            // Draw this triangle
                            GL.DrawElements(PrimitiveType.Triangles, 3, DrawElementsType.UnsignedInt, new IntPtr(i * sizeof(int)));
                        }
                    }
                    else
                    {
                        // Solid color
                        if (enableTransparency)
                            GL.Color4(1.0f, 1.0f, 1.0f, transparencyLevel);
                        else
                            GL.Color3(1.0f, 1.0f, 1.0f);

                        // Draw all triangles at once
                        GL.DrawElements(PrimitiveType.Triangles, indices.Count, DrawElementsType.UnsignedInt, IntPtr.Zero);
                    }
                }

                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
                GL.DisableClientState(ArrayCap.VertexArray);
                GL.DisableClientState(ArrayCap.NormalArray);
            }
            else
            {
                // Render using immediate mode as fallback
                GL.Begin(PrimitiveType.Triangles);

                for (int i = 0; i < indices.Count; i += 3)
                {
                    int i1 = indices[i];
                    int i2 = indices[i + 1];
                    int i3 = indices[i + 2];

                    if (i1 < deformedVertices.Count && i2 < deformedVertices.Count && i3 < deformedVertices.Count)
                    {
                        // Check if this triangle belongs to a failed element
                        int elementIndex = i / 3;
                        bool isFailed = showFailedElements &&
                                        elementStresses.TryGetValue(elementIndex, out float stress) &&
                                        stress < 0;

                        if (showDensity && !isFailed && i1 < densityValues.Count)
                        {
                            float density = densityValues[i1];
                            float normalizedDensity = (density - minDensity) / (maxDensity - minDensity);
                            normalizedDensity = Math.Max(0.0f, Math.Min(1.0f, normalizedDensity));
                            float[] color = GetColorComponents(normalizedDensity);

                            if (enableTransparency)
                                GL.Color4(color[0], color[1], color[2], transparencyLevel);
                            else
                                GL.Color3(color[0], color[1], color[2]);
                        }
                        else if (isFailed)
                        {
                            // Failed element - red color
                            if (enableTransparency)
                                GL.Color4(1.0f, 0.0f, 0.0f, Math.Min(1.0f, transparencyLevel + 0.3f));
                            else
                                GL.Color3(1.0f, 0.0f, 0.0f);
                        }
                        else
                        {
                            // Default white
                            if (enableTransparency)
                                GL.Color4(1.0f, 1.0f, 1.0f, transparencyLevel);
                            else
                                GL.Color3(1.0f, 1.0f, 1.0f);
                        }

                        if (i1 < normals.Count) GL.Normal3(normals[i1]);
                        GL.Vertex3(deformedVertices[i1]);

                        if (i2 < normals.Count) GL.Normal3(normals[i2]);
                        GL.Vertex3(deformedVertices[i2]);

                        if (i3 < normals.Count) GL.Normal3(normals[i3]);
                        GL.Vertex3(deformedVertices[i3]);
                    }
                }

                GL.End();
            }

            // Disable blending after rendering if we enabled it
            if (enableTransparency)
            {
                GL.Disable(EnableCap.Blend);
            }
        }
        private void RenderMeshWithDepthSorting()
        {
            // Create a list of triangles with distance to camera
            List<TriangleDepth> triangles = new List<TriangleDepth>();

            // Calculate camera position (inverse of the view matrix)
            float[] viewMatrix = new float[16];
            GL.GetFloat(GetPName.ModelviewMatrix, viewMatrix);

            // Camera position in world space
            Vector3 cameraPos = new Vector3(-viewMatrix[12], -viewMatrix[13], -viewMatrix[14]);

            // Process all triangles
            for (int i = 0; i < indices.Count; i += 3)
            {
                int i1 = indices[i];
                int i2 = indices[i + 1];
                int i3 = indices[i + 2];

                if (i1 >= deformedVertices.Count || i2 >= deformedVertices.Count || i3 >= deformedVertices.Count)
                    continue;

                // Calculate triangle centroid for depth sorting
                Vector3 centroid = (deformedVertices[i1] + deformedVertices[i2] + deformedVertices[i3]) / 3.0f;

                // Calculate distance to camera
                float distance = (centroid - cameraPos).LengthSquared;

                // Store triangle with distance
                triangles.Add(new TriangleDepth
                {
                    Index1 = i1,
                    Index2 = i2,
                    Index3 = i3,
                    Distance = distance,
                    ElementIndex = i / 3
                });
            }

            // Sort triangles back-to-front for correct transparency
            triangles.Sort((a, b) => b.Distance.CompareTo(a.Distance));

            // Render sorted triangles
            GL.Begin(PrimitiveType.Triangles);

            foreach (var triangle in triangles)
            {
                int i1 = triangle.Index1;
                int i2 = triangle.Index2;
                int i3 = triangle.Index3;

                // Check if this triangle belongs to a failed element
                bool isFailed = showFailedElements &&
                                elementStresses.TryGetValue(triangle.ElementIndex, out float stress) &&
                                stress < 0;

                if (chkShowDensity.Checked && !isFailed && i1 < densityValues.Count)
                {
                    float density = densityValues[i1];
                    float normalizedDensity = (density - minDensity) / (maxDensity - minDensity);
                    normalizedDensity = Math.Max(0.0f, Math.Min(1.0f, normalizedDensity));
                    float[] color = GetColorComponents(normalizedDensity);

                    GL.Color4(color[0], color[1], color[2], transparencyLevel);
                }
                else if (isFailed)
                {
                    // Failed element - red color with higher opacity
                    GL.Color4(1.0f, 0.0f, 0.0f, Math.Min(1.0f, transparencyLevel + 0.3f));
                }
                else
                {
                    // Default white
                    GL.Color4(1.0f, 1.0f, 1.0f, transparencyLevel);
                }

                // Render triangle
                if (i1 < normals.Count) GL.Normal3(normals[i1]);
                GL.Vertex3(deformedVertices[i1]);

                if (i2 < normals.Count) GL.Normal3(normals[i2]);
                GL.Vertex3(deformedVertices[i2]);

                if (i3 < normals.Count) GL.Normal3(normals[i3]);
                GL.Vertex3(deformedVertices[i3]);
            }

            GL.End();
        }
        private struct TriangleDepth
        {
            public int Index1;
            public int Index2;
            public int Index3;
            public float Distance;
            public int ElementIndex;
        }
        private void RenderLowDetailMesh()
        {
            // Calculate adaptive stride based on mesh size
            int stride = GetAdaptiveStride();

            GL.Color3(1.0f, 1.0f, 1.0f); // Simple white in low detail mode
            GL.Begin(PrimitiveType.Triangles);

            for (int i = 0; i < indices.Count; i += 3 * stride)
            {
                if (i + 2 >= indices.Count) break;

                int i1 = indices[i];
                int i2 = indices[i + 1];
                int i3 = indices[i + 2];

                if (i1 < deformedVertices.Count && i2 < deformedVertices.Count && i3 < deformedVertices.Count)
                {
                    if (i1 < normals.Count) GL.Normal3(normals[i1]);
                    GL.Vertex3(deformedVertices[i1]);

                    if (i2 < normals.Count) GL.Normal3(normals[i2]);
                    GL.Vertex3(deformedVertices[i2]);

                    if (i3 < normals.Count) GL.Normal3(normals[i3]);
                    GL.Vertex3(deformedVertices[i3]);
                }
            }

            GL.End();
        }

        private int GetAdaptiveStride()
        {
            // Calculate appropriate stride based on model complexity
            int triangleCount = indices.Count / 3;

            if (triangleCount > 10000000) return 200;      // 10M+ triangles: extremely aggressive reduction
            else if (triangleCount > 5000000) return 100;  // 5-10M triangles: very aggressive reduction
            else if (triangleCount > 1000000) return 50;   // 1-5M triangles: aggressive reduction
            else if (triangleCount > 500000) return 25;    // 500K-1M triangles: moderate reduction
            else if (triangleCount > 100000) return 10;    // 100-500K triangles: light reduction
            else return 5;                                 // <100K triangles: minimal reduction
        }
        private void AddColorLegendPanel()
        {
            // Create a panel for the color legend
            colorLegendPanel = new Panel
            {
                BackColor = Color.FromArgb(40, 40, 40),
                Size = new Size(100, 300),
                Location = new Point(glControl.Width - 105, 10),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Visible = chkShowDensity.Checked
            };

            // Paint handler for the legend
            colorLegendPanel.Paint += ColorLegendPanel_Paint;

            // Add the panel to the render panel (alongside the GLControl)
            renderPanel.Controls.Add(colorLegendPanel);

            // Make sure panel is on top
            colorLegendPanel.BringToFront();

            // Update the checkbox event to toggle visibility
            chkShowDensity.CheckedChanged -= ChkShowDensity_CheckedChanged;
            chkShowDensity.CheckedChanged += (s, e) => {
                colorLegendPanel.Visible = chkShowDensity.Checked;
                glControl.Invalidate();
            };
        }

        private void CreateGLControl()
        {
            // Create a hardware-accelerated graphics mode with anti-aliasing
            var graphicsMode = new OpenTK.Graphics.GraphicsMode(
                new OpenTK.Graphics.ColorFormat(8, 8, 8, 8),
                24, // Depth bits
                8,  // Stencil bits
                4   // Anti-aliasing samples
            );

            // Create GLControl with hardware acceleration
            glControl = new GLControl(graphicsMode);
            glControl.VSync = true;

            glControl.Dock = DockStyle.Fill;
            glControl.Load += GlControl_Load;
            glControl.Paint += GlControl_Paint;
            glControl.Resize += GlControl_Resize;
            glControl.MouseDown += GlControl_MouseDown;
            glControl.MouseMove += GlControl_MouseMove;
            glControl.MouseUp += GlControl_MouseUp;
            glControl.MouseWheel += GlControl_MouseWheel;
        }
        
        public double CalculateTotalVolume()
        {
            try
            {
                // Calculate material volume if needed
                if (materialVolume <= 0)
                {
                    CalculateMaterialVolume();
                }

                // Double-check we have a valid volume
                if (materialVolume <= 0)
                {
                    // Use a safe default value
                    materialVolume = 0.0001; // 100 cm³
                    Logger.Log("[TriaxialSimulationForm] Warning: Using default volume");
                }

                return materialVolume;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error in CalculateTotalVolume: {ex.Message}");
                return 0.0001; // Return a non-zero default
            }
        }
        public void SetMaterialDensity(double density)
        {
            try
            {
                Logger.Log($"[TriaxialSimulationForm] SetMaterialDensity called with value: {density} kg/m³");

                // Store the original bulk density value
                float originalBulkDensity = bulkDensity;
                bulkDensity = (float)density;

                // We're now calibrated to show actual density values
                isDensityCalibrated = true;

                // IMPORTANT: Preserve the relative density variations
                if (densityValues != null && densityValues.Count > 0)
                {
                    // Save original min/max before scaling
                    float origMin = minDensity;
                    float origMax = maxDensity;
                    float origRange = origMax - origMin;

                    // If the original range is too small, we need to create variation
                    if (origRange < 0.01f)
                    {
                        // Use default variation of 30% around the mean
                        origMin = 0.85f;
                        origMax = 1.15f;
                        origRange = 0.3f;
                    }

                    // Calculate target density range (±25% of the bulk density)
                    float targetMin = (float)(density * 0.75);
                    float targetMax = (float)(density * 1.25);
                    float targetRange = targetMax - targetMin;

                    // Apply scaling to maintain relative density differences
                    for (int i = 0; i < densityValues.Count; i++)
                    {
                        // Convert from old range to 0-1 normalized value
                        float normalizedValue = (densityValues[i] - origMin) / origRange;
                        // Clamp to 0-1 range to handle outliers
                        normalizedValue = Math.Max(0.0f, Math.Min(1.0f, normalizedValue));

                        // Map to new target range
                        densityValues[i] = targetMin + (normalizedValue * targetRange);
                    }

                    // Recalculate min/max after transformation
                    minDensity = float.MaxValue;
                    maxDensity = float.MinValue;

                    foreach (float d in densityValues)
                    {
                        minDensity = Math.Min(minDensity, d);
                        maxDensity = Math.Max(maxDensity, d);
                    }

                    Logger.Log($"[TriaxialSimulationForm] Updated density range: {minDensity} - {maxDensity} kg/m³");
                }
                else
                {
                    // If no density values are available, use reasonable defaults
                    minDensity = (float)(density * 0.75);
                    maxDensity = (float)(density * 1.25);
                }

                // Rest of the method remains unchanged
                UpdateMaterialPropertiesNoUI();

                // Update UI controls
                SafeUpdateControl(numBulkDensity, (decimal)bulkDensity);
                SafeUpdateControl(numYoungModulus, (decimal)youngModulus);
                SafeUpdateControl(numPorosity, (decimal)porosity);
                SafeUpdateControl(numBulkModulus, (decimal)bulkModulus);
                SafeUpdateControl(numYieldStrength, (decimal)yieldStrength);
                SafeUpdateControl(numBrittleStrength, (decimal)brittleStrength);
                SafeUpdateControl(numFrictionAngle, (decimal)frictionAngle);
                SafeUpdateControl(numPermeability, (decimal)permeability);

                // Refresh visuals
                if (colorLegendPanel != null && !colorLegendPanel.IsDisposed)
                {
                    colorLegendPanel.Invalidate();
                }

                if (glControl != null && !glControl.IsDisposed && glControl.IsHandleCreated)
                {
                    glControl.Invalidate();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error in SetMaterialDensity: {ex.Message}");
            }
        }


        private void SafeUpdateControl(KryptonNumericUpDown ctrl, decimal value)
        {
            if (ctrl == null || ctrl.IsDisposed) return;

            try
            {
                // Try to get the min/max with defaults if properties are null
                decimal min, max;
                try { min = ctrl.Minimum; } catch { min = 0.0001m; }
                try { max = ctrl.Maximum; } catch { max = 100000m; }

                // Try to get decimal places with default if property is null
                int decimalPlaces;
                try { decimalPlaces = ctrl.DecimalPlaces; } catch { decimalPlaces = 1; }

                // Apply limits and rounding
                decimal v = Math.Max(min, Math.Min(max, value));
                v = Math.Round(v, decimalPlaces);

                // Set value
                ctrl.Value = v;
            }
            catch
            {
                // Completely ignore any errors
            }
        }
        private void UpdateMaterialPropertiesNoUI()
        {
            // Calculate based on bulk density
            const float grainDensity = 2650f;
            const float a = 0.1f, b = 3.0f;

            float ρ = bulkDensity;
            float ρ_g_cm3 = ρ / 1000f;

            youngModulus = a * (float)Math.Pow(ρ_g_cm3, b) * 10_000f;
            porosity = 1f - ρ / grainDensity;
            porosity = Math.Max(0.01f, Math.Min(0.50f, porosity));

            if (poissonRatio <= 0 || poissonRatio >= 0.5)
            {
                poissonRatio = 0.3f; // Set a default if invalid
            }

            // Safe calculation of bulk modulus
            float denominator = 3f * (1f - 2f * poissonRatio);
            if (denominator < 0.01f) denominator = 0.01f; // Prevent division by zero
            bulkModulus = youngModulus / denominator;

            yieldStrength = youngModulus * 0.05f;
            brittleStrength = youngModulus * 0.08f;
            cohesion = yieldStrength * 0.10f;
            frictionAngle = 25f + (ρ / grainDensity) * 20f;

            float porTerm = (float)Math.Pow(porosity, 3) / (float)Math.Pow(1f - porosity, 2);
            permeability = Math.Max(0.0001f, 0.1f * porTerm * 1_000f);
        }
        public void ApplyDensityCalibration(List<CalibrationPoint> points)
        {
            try
            {
                if (points == null || points.Count < 2)
                    return;

                // Make a protective copy of the points
                calibrationPoints = new List<CalibrationPoint>();
                foreach (var point in points)
                {
                    if (point != null)
                        calibrationPoints.Add(point);
                }

                if (calibrationPoints.Count < 2)
                    return;

                // Calculate calibration parameters
                double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
                int n = calibrationPoints.Count;

                foreach (var pt in calibrationPoints)
                {
                    sumX += pt.AvgGrayValue;
                    sumY += pt.Density;
                    sumXX += pt.AvgGrayValue * pt.AvgGrayValue;
                    sumXY += pt.AvgGrayValue * pt.Density;
                }

                // Avoid division by zero
                double denominator = (n * sumXX - sumX * sumX);
                if (Math.Abs(denominator) < 0.0001)
                {
                    Logger.Log("[TriaxialSimulationForm] Warning: Cannot calculate density calibration (division by zero)");
                    return;
                }

                densityCalibrationSlope = (n * sumXY - sumX * sumY) / denominator;
                densityCalibrationIntercept = (sumY - densityCalibrationSlope * sumX) / n;
                isDensityCalibrated = true;

                // Recalculate density values if we have them
                if (densityValues != null && densityValues.Count > 0)
                {
                    RecalibrateDensityValues();
                }

                // Calculate average bulk density
                CalculateAverageBulkDensity();

                // Update UI on the correct thread
                if (InvokeRequired)
                {
                    try
                    {
                        Invoke(new Action(() => UpdateUIWithDensity(bulkDensity)));
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[TriaxialSimulationForm] Error during Invoke in calibration: {ex.Message}");
                    }
                }
                else
                {
                    UpdateUIWithDensity(bulkDensity);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error in ApplyDensityCalibration: {ex.Message}");
            }
        }
        private void UpdateUIWithDensity(double density)
        {
            try
            {
                // First, verify density is in reasonable range (kg/m³)
                if (density <= 0 || double.IsNaN(density) || double.IsInfinity(density))
                {
                    Logger.Log($"[TriaxialSimulationForm] Warning: Invalid density value {density}, using default");
                    density = 2500.0; // Use a default density value
                }

                // Update the bulkDensity field
                bulkDensity = (float)density;

                // Update UI controls - only if they exist and are properly initialized
                bool uiInitialized = numBulkDensity != null && !numBulkDensity.IsDisposed;

                if (uiInitialized)
                {
                    try
                    {
                        // Update bulk density control with proper clamping
                        decimal minAllowed = numBulkDensity.Minimum;
                        decimal maxAllowed = numBulkDensity.Maximum;
                        decimal clampedValue = Math.Min(Math.Max((decimal)bulkDensity, minAllowed), maxAllowed);
                        numBulkDensity.Value = clampedValue;

                        // Now also update all related material properties based on the new density
                        UpdateMaterialPropertiesFromDensity();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[TriaxialSimulationForm] Error updating numeric controls: {ex.Message}");
                        // Continue execution
                    }
                }

                // Update visual elements that display density
                try
                {
                    // Refresh density legend if it exists
                    if (colorLegendPanel != null && !colorLegendPanel.IsDisposed)
                    {
                        colorLegendPanel.Invalidate();
                    }

                    // Refresh OpenGL viewport if it exists
                    if (glControl != null && !glControl.IsDisposed)
                    {
                        if (glControl.IsHandleCreated)
                        {
                            glControl.Invalidate();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[TriaxialSimulationForm] Error refreshing visual elements: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error in UpdateUIWithDensity: {ex.Message}");
            }
        }
        private void CalculateAverageBulkDensity()
        {
            if (densityValues.Count == 0)
                return;

            float totalDensity = 0;
            foreach (float density in densityValues)
            {
                totalDensity += density;
            }

            bulkDensity = totalDensity / densityValues.Count;
        }

        private void RecalibrateDensityValues()
        {
            if (densityValues == null || densityValues.Count == 0)
                return;

            for (int i = 0; i < densityValues.Count; i++)
            {
                float grayValue = densityValues[i] * 255f;
                float calibratedDensity = (float)(grayValue * densityCalibrationSlope + densityCalibrationIntercept);
                densityValues[i] = calibratedDensity;
            }

            // Check if there are values to compute min/max
            if (densityValues.Count > 0)
            {
                minDensity = densityValues.Min();
                maxDensity = densityValues.Max();
            }
            else
            {
                minDensity = 0f;
                maxDensity = 0f;
            }

            colorLegendPanel?.Invalidate();
        }
        /// <summary>
        /// Compute the center and radius of the mesh’s bounding sphere.
        /// </summary>
        private Vector3 ComputeBoundingSphere(out float radius)
        {
            if (deformedVertices == null || deformedVertices.Count == 0)
            {
                radius = 10.0f;
                return Vector3.Zero;
            }

            // Find the mesh center by averaging vertex positions
            Vector3 meshCenter = Vector3.Zero;
            foreach (var v in deformedVertices)
                meshCenter += v;
            meshCenter /= deformedVertices.Count;

            // Find maximum distance from center with outlier detection
            float maxDist2 = 0f;
            List<float> distances = new List<float>();

            foreach (var v in deformedVertices)
            {
                float d2 = (v - meshCenter).LengthSquared;
                distances.Add(d2);
                if (d2 > maxDist2) maxDist2 = d2;
            }

            // Sort distances and check for outliers
            distances.Sort();
            float medianDist2 = distances[distances.Count / 2];

            // If max distance is more than 100x the median, we likely have outliers
            if (maxDist2 > medianDist2 * 100 && distances.Count > 10)
            {
                // Use 95th percentile instead of maximum to avoid outliers
                int idx95 = (int)(distances.Count * 0.95);
                maxDist2 = distances[idx95];
                Logger.Log($"[TriaxialSimulationForm] Outliers detected! Using 95th percentile for radius.");
            }

            // Compute radius and set a reasonable minimum value
            radius = (float)Math.Sqrt(maxDist2);
            if (radius < 1.0f)
            {
                Logger.Log($"[TriaxialSimulationForm] Mesh is too small (r={radius}). Scaling to visible size.");
                radius = 1.0f;
            }

            //Logger.Log($"[TriaxialSimulationForm] Mesh center: {meshCenter}, radius: {radius}");
            return meshCenter;
        }

        /// <summary>
        /// Given a sphere radius and a vertical FOV, returns the distance
        /// so that the sphere just fits in view (with 10% margin).
        /// </summary>
        private float ComputeCameraDistance(float radius, float fovRadians)
        {
            // Calculate distance so sphere fills most of view (with some margin)
            float distance = radius / (float)Math.Sin(fovRadians * 0.5f) * 1.5f;

            // Ensure a minimum distance
            distance = Math.Max(distance, radius * 3.0f);

            // Log for debugging
            //Logger.Log($"[TriaxialSimulationForm] Camera distance: {distance}");

            return distance;
        }
        /// <summary>
        /// Rebuilds the projection matrix based on current control size,
        /// FOV, and the sphere’s extent.
        /// </summary>
        private void UpdateProjection(int width, int height, float fovRadians, float camDistance, float sphereRadius)
        {
            float aspect = width / (float)height;
            float near = Math.Max(0.01f, camDistance - sphereRadius);
            float far = camDistance + sphereRadius;

            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();

            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                fovRadians, aspect, near, far);
            GL.LoadMatrix(ref proj);

            GL.MatrixMode(MatrixMode.Modelview);
        }
        private void UpdateMaterialPropertiesFromDensity()
        {
            try
            {
                // Ensure default values for critical properties if they're not set
                if (poissonRatio <= 0 || poissonRatio >= 0.5) poissonRatio = 0.3f;

                // Constants
                const float grainDensity = 2650f;      // kg/m³ - quartzo-feldspathic grains
                const float a = 0.1f, b = 3.0f;        // E = a·ρᵇ

                // Get bulk density and convert properly
                float ρ = Math.Max(500f, bulkDensity); // Ensure minimum reasonable density
                float ρ_g_cm3 = ρ / 1000f;             // Convert kg/m³ to g/cm³

                // Calculate material properties using empirical relationships
                youngModulus = a * (float)Math.Pow(ρ_g_cm3, b) * 10_000f;  // MPa

                // Calculate porosity with bounds checking
                porosity = 1f - ρ / grainDensity;
                porosity = Math.Max(0.01f, Math.Min(0.50f, porosity));

                // Safely calculate bulk modulus
                bulkModulus = youngModulus / (3f * Math.Max(0.01f, 1f - 2f * poissonRatio));

                // Calculate strength parameters
                yieldStrength = youngModulus * 0.05f;
                brittleStrength = youngModulus * 0.08f;
                cohesion = yieldStrength * 0.10f;
                frictionAngle = 25f + (ρ / grainDensity) * 20f;  // Range: 25° → 45°

                // Kozeny-Carman permeability (Darcy → mD)
                float porTerm = (float)Math.Pow(porosity, 3) / (float)Math.Pow(Math.Max(0.01f, 1f - porosity), 2);
                permeability = Math.Max(0.0001f, 0.1f * porTerm * 1_000f);  // mD

                // Adjust parameters based on UI limits - CtrlMax already handles null controls
                youngModulus = Math.Min(youngModulus, CtrlMax(numYoungModulus));
                bulkModulus = Math.Min(bulkModulus, CtrlMax(numBulkModulus));
                yieldStrength = Math.Min(yieldStrength, CtrlMax(numYieldStrength));
                brittleStrength = Math.Min(brittleStrength, CtrlMax(numBrittleStrength));
                cohesion = Math.Min(cohesion, CtrlMax(numCohesion));
                frictionAngle = Math.Min(frictionAngle, CtrlMax(numFrictionAngle));
                permeability = Math.Min(permeability, CtrlMax(numPermeability));
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error in UpdateMaterialPropertiesFromDensity details: {ex.Message}");
                // Continue execution without rethrowing
            }

            try
            {
                // Update UI controls with calculated values - in a separate try/catch block
                SafeSetNumericValue(numYoungModulus, (decimal)youngModulus);
                SafeSetNumericValue(numPorosity, (decimal)porosity);
                SafeSetNumericValue(numBulkModulus, (decimal)bulkModulus);
                SafeSetNumericValue(numYieldStrength, (decimal)yieldStrength);
                SafeSetNumericValue(numBrittleStrength, (decimal)brittleStrength);
                SafeSetNumericValue(numCohesion, (decimal)cohesion);
                SafeSetNumericValue(numFrictionAngle, (decimal)frictionAngle);
                SafeSetNumericValue(numPermeability, (decimal)permeability);
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error updating UI controls: {ex.Message}");
                // Continue execution without rethrowing
            }
        }
        private void UpdateMaterialPropertiesFromDensityWithLogging()
        {
            Logger.Log("[DEBUG] Entering UpdateMaterialPropertiesFromDensityWithLogging");

            /* ---------- 1.  empirical relationships ---------- */
            Logger.Log($"[DEBUG] Current bulkDensity: {bulkDensity}");
            Logger.Log($"[DEBUG] poissonRatio is {(poissonRatio <= 0 ? "INVALID" : "valid")}: {poissonRatio}");

            const float grainDensity = 2650f;      // kg/m³   – quartzo-feldspathic grains
            const float a = 0.1f, b = 3.0f;        // E = a·ρᵇ

            float ρ = bulkDensity;         // kg/m³
            float ρ_g_cm3 = ρ / 1000f;     // g/cm³

            // Log values for debugging
            Logger.Log($"[DEBUG] ρ = {ρ}, ρ_g_cm3 = {ρ_g_cm3}");

            youngModulus = a * (float)Math.Pow(ρ_g_cm3, b) * 10_000f;      // MPa
            Logger.Log($"[DEBUG] Calculated youngModulus = {youngModulus}");

            porosity = 1f - ρ / grainDensity;
            porosity = Math.Max(0.01f, Math.Min(0.50f, porosity));
            Logger.Log($"[DEBUG] Calculated porosity = {porosity}");

            try
            {
                // This could be a division by zero issue if poissonRatio is too close to 0.5
                float denominator = 3f * (1f - 2f * poissonRatio);
                Logger.Log($"[DEBUG] Bulk modulus denominator = {denominator}");
                bulkModulus = youngModulus / denominator;
                Logger.Log($"[DEBUG] Calculated bulkModulus = {bulkModulus}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DEBUG] Error calculating bulkModulus: {ex.Message}");
                bulkModulus = youngModulus * 0.6f; // Fallback value
            }

            yieldStrength = youngModulus * 0.05f;
            brittleStrength = youngModulus * 0.08f;
            cohesion = yieldStrength * 0.10f;
            frictionAngle = 25f + (ρ / grainDensity) * 20f;                 // 25° → 45°

            /* Kozeny-Carman (Darcy ► mD) */
            float porTerm = (float)Math.Pow(porosity, 3) / (float)Math.Pow(1f - porosity, 2);
            permeability = Math.Max(0.0001f, 0.1f * porTerm * 1_000f);     // mD

            Logger.Log("[DEBUG] All calculations completed successfully");

            /* ---------- 2.  check UI controls ---------- */
            Logger.Log($"[DEBUG] numYoungModulus is {(numYoungModulus == null ? "NULL" : "not null")}");
            Logger.Log($"[DEBUG] numPorosity is {(numPorosity == null ? "NULL" : "not null")}");
            Logger.Log($"[DEBUG] numBulkModulus is {(numBulkModulus == null ? "NULL" : "not null")}");
            Logger.Log($"[DEBUG] numYieldStrength is {(numYieldStrength == null ? "NULL" : "not null")}");
            Logger.Log($"[DEBUG] numBrittleStrength is {(numBrittleStrength == null ? "NULL" : "not null")}");
            Logger.Log($"[DEBUG] numCohesion is {(numCohesion == null ? "NULL" : "not null")}");
            Logger.Log($"[DEBUG] numFrictionAngle is {(numFrictionAngle == null ? "NULL" : "not null")}");
            Logger.Log($"[DEBUG] numPermeability is {(numPermeability == null ? "NULL" : "not null")}");

            /* ---------- 3.  push values to UI safely ---------- */
            try
            {
                Logger.Log("[DEBUG] About to update numYoungModulus");
                SafeSetNumericValue(numYoungModulus, (decimal)youngModulus);
                Logger.Log("[DEBUG] Successfully updated numYoungModulus");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DEBUG] Error updating numYoungModulus: {ex.Message}");
            }

            try
            {
                Logger.Log("[DEBUG] About to update numPorosity");
                SafeSetNumericValue(numPorosity, (decimal)porosity);
                Logger.Log("[DEBUG] Successfully updated numPorosity");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DEBUG] Error updating numPorosity: {ex.Message}");
            }

            try
            {
                Logger.Log("[DEBUG] About to update numBulkModulus");
                SafeSetNumericValue(numBulkModulus, (decimal)bulkModulus);
                Logger.Log("[DEBUG] Successfully updated numBulkModulus");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DEBUG] Error updating numBulkModulus: {ex.Message}");
            }

            try
            {
                Logger.Log("[DEBUG] About to update numYieldStrength");
                SafeSetNumericValue(numYieldStrength, (decimal)yieldStrength);
                Logger.Log("[DEBUG] Successfully updated numYieldStrength");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DEBUG] Error updating numYieldStrength: {ex.Message}");
            }

            try
            {
                Logger.Log("[DEBUG] About to update numBrittleStrength");
                SafeSetNumericValue(numBrittleStrength, (decimal)brittleStrength);
                Logger.Log("[DEBUG] Successfully updated numBrittleStrength");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DEBUG] Error updating numBrittleStrength: {ex.Message}");
            }

            try
            {
                Logger.Log("[DEBUG] About to update numCohesion");
                SafeSetNumericValue(numCohesion, (decimal)cohesion);
                Logger.Log("[DEBUG] Successfully updated numCohesion");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DEBUG] Error updating numCohesion: {ex.Message}");
            }

            try
            {
                Logger.Log("[DEBUG] About to update numFrictionAngle");
                SafeSetNumericValue(numFrictionAngle, (decimal)frictionAngle);
                Logger.Log("[DEBUG] Successfully updated numFrictionAngle");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DEBUG] Error updating numFrictionAngle: {ex.Message}");
            }

            try
            {
                Logger.Log("[DEBUG] About to update numPermeability");
                SafeSetNumericValue(numPermeability, (decimal)permeability);
                Logger.Log("[DEBUG] Successfully updated numPermeability");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DEBUG] Error updating numPermeability: {ex.Message}");
            }

            Logger.Log("[DEBUG] Finished UpdateMaterialPropertiesFromDensityWithLogging");
        }
        public TriaxialSimulationForm(MainForm mainForm)
        {
            Logger.Log("[TriaxialSimulationForm] Constructor Called");
            this.mainForm = mainForm;
            this.pixelSize = mainForm.GetPixelSize();

            InitializeComponent();

            // Check if we got hardware acceleration
            CheckHardwareAcceleration();

            InitializeBackgroundWorker();
            LoadMaterialsFromMainForm();
            InitializeDiagramsForm();
            AddDiagramsButton();
            InitializeDirectCompute();
            Logger.Log("[TriaxialSimulationForm] Constructor Ended");
        }
        private void CheckHardwareAcceleration()
        {
            if (glControl != null && glControl.IsHandleCreated)
            {
                // Force current context
                glControl.MakeCurrent();

                // Check if hardware acceleration is working
                string renderer = GL.GetString(StringName.Renderer);
                string vendor = GL.GetString(StringName.Vendor);

                // Software renderers usually contain these strings
                hardwareAccelerated = !(
                    renderer.Contains("Software") ||
                    renderer.Contains("software") ||
                    renderer.Contains("Mesa") ||
                    vendor.Contains("Microsoft")
                );

                // Show warning if we're in software mode
                if (!hardwareAccelerated)
                {
                    MessageBox.Show(
                        "WARNING: OpenGL is running in software mode which will be extremely slow.\n\n" +
                        "Renderer: " + renderer + "\n" +
                        "Vendor: " + vendor + "\n\n" +
                        "Please ensure you have up-to-date graphics drivers installed.",
                        "Hardware Acceleration Not Available",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }

        private void InitializeComponent()
        {
            // Set up rotation timer
            SetupRotationTimer();

            // Form properties
            this.Text = "Triaxial Simulation";
            this.Size = new Size(1200, 800);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(32, 32, 32);

            // Create the main 2-column layout
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 1,
                ColumnCount = 2,
                BackColor = Color.FromArgb(32, 32, 32)
            };

            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70F)); // 3D view
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F)); // Controls & results

            // Create the left panel for OpenGL
            renderPanel = new KryptonPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(3)
            };

            // Create the right panel for controls and results
            Panel rightPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(42, 42, 42)
            };

            // Add panels to main layout
            mainLayout.Controls.Add(renderPanel, 0, 0);
            mainLayout.Controls.Add(rightPanel, 1, 0);

            // Create a vertical layout for controls and results
            TableLayoutPanel rightLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 1,
                ColumnCount = 1,
                BackColor = Color.FromArgb(42, 42, 42)
            };

            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // Controls area

            // Create scrollable panel for controls
            Panel scrollablePanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(42, 42, 42)
            };

            // Add to right layout
            rightLayout.Controls.Add(scrollablePanel, 0, 0);

            rightPanel.Controls.Add(rightLayout);

            // Create controls for the scrollable panel
            Panel controlsContent = new Panel
            {
                Width = 330,
                Height = 1200, // Increased height to accommodate all controls
                BackColor = Color.FromArgb(42, 42, 42),
                AutoSize = false,
                Dock = DockStyle.Top
            };

            scrollablePanel.Controls.Add(controlsContent);

            // ================= CONTROLS CONTENT =================
            // Add control sections in a clear, organized layout

            // 1. SIMULATION SETTINGS SECTION
            AddSimulationSettingsControls(controlsContent, 10);

            // 2. MATERIAL PROPERTIES SECTION
            AddMaterialPropertiesControls(controlsContent, 230);

            // 3. PETROPHYSICAL PROPERTIES SECTION
            AddPetrophysicalPropertiesControls(controlsContent, 450);

            // 4. VISUALIZATION SETTINGS SECTION
            AddVisualizationControls(controlsContent, 690);

            // 5. PROGRESS SECTION
            AddProgressControls(controlsContent, 900);

            // 6. ACTION BUTTONS SECTION
            AddActionButtonsControls(controlsContent, 970);

            // Initialize OpenGL Control FIRST
            CreateGLControl();

            glControl.Load += GlControl_Load;
            glControl.Paint += GlControl_Paint;
            glControl.Resize += GlControl_Resize;
            glControl.MouseDown += GlControl_MouseDown;
            glControl.MouseMove += GlControl_MouseMove;
            glControl.MouseUp += GlControl_MouseUp;
            glControl.MouseWheel += GlControl_MouseWheel;

            // Add GLControl to render panel
            renderPanel.Controls.Add(glControl);
            AddColorLegendPanel();

            // Create simulation timer
            simulationTimer = new System.Windows.Forms.Timer
            {
                Interval = 50
            };
            simulationTimer.Tick += SimulationTimer_Tick;

            // Add save image button
            AddSaveImageButton();

            // Add main layout to form
            this.Controls.Add(mainLayout);
        }
        private void AddSimulationSettingsControls(Panel controlsContent, int startY)
        {
            // Simulation Settings Label
            Label lblSimSettings = new Label
            {
                Text = "Simulation Settings",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, startY)
            };
            controlsContent.Controls.Add(lblSimSettings);

            int yPos = startY + 30;

            // Material selection
            Label lblMaterial = new Label
            {
                Text = "Material:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblMaterial);

            comboMaterials = new KryptonComboBox
            {
                Location = new Point(140, yPos),
                Width = 180,
                DropDownWidth = 180,
                StateCommon = {
            ComboBox = { Back = { Color1 = Color.FromArgb(60, 60, 60) } },
            Item = { Content = { ShortText = { Color1 = Color.White } } }
        }
            };
            comboMaterials.StateActive.ComboBox.Content.Color1 = Color.White;
            comboMaterials.StateNormal.ComboBox.Content.Color1 = Color.White;
            comboMaterials.SelectedIndexChanged += ComboMaterials_SelectedIndexChanged;
            controlsContent.Controls.Add(comboMaterials);

            yPos += 30;

            // Density settings button
            btnDensitySettings = new KryptonButton
            {
                Text = "Density Settings",
                Location = new Point(140, yPos),
                Width = 180
            };
            btnDensitySettings.Click += BtnDensitySettings_Click;
            controlsContent.Controls.Add(btnDensitySettings);

            yPos += 30;

            // Show density checkbox
            chkShowDensity = new KryptonCheckBox
            {
                Text = "Show Density Map",
                Location = new Point(140, yPos),
                Checked = true,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkShowDensity.CheckedChanged += ChkShowDensity_CheckedChanged;
            controlsContent.Controls.Add(chkShowDensity);

            yPos += 30;

            // Sampling rate
            Label lblSampling = new Label
            {
                Text = "Quality (Sampling Rate):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblSampling);

            trackSamplingRate = new KryptonTrackBar
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 1,
                Maximum = 5,
                Value = 2,
                TickFrequency = 1,
                StateCommon = {
            Track = { Color1 = Color.FromArgb(80, 80, 80) },
            Tick = { Color1 = Color.Silver }
        }
            };
            trackSamplingRate.ValueChanged += TrackSamplingRate_ValueChanged;
            controlsContent.Controls.Add(trackSamplingRate);

            yPos += 30;

            // Direction selection
            Label lblDirection = new Label
            {
                Text = "Test Direction:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblDirection);

            comboDirection = new KryptonComboBox
            {
                Location = new Point(140, yPos),
                Width = 180,
                StateCommon = {
            ComboBox = { Back = { Color1 = Color.FromArgb(60, 60, 60) } },
            Item = { Content = { ShortText = { Color1 = Color.White } } }
        }
            };
            comboDirection.StateActive.ComboBox.Content.Color1 = Color.White;
            comboDirection.StateNormal.ComboBox.Content.Color1 = Color.White;
            comboDirection.Items.AddRange(new object[] { "X", "Y", "Z" });
            comboDirection.SelectedIndex = 2; // Default to Z
            comboDirection.SelectedIndexChanged += ComboDirection_SelectedIndexChanged;
            controlsContent.Controls.Add(comboDirection);

            yPos += 30;

            // Material behavior
            Label lblBehavior = new Label
            {
                Text = "Material Behavior:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblBehavior);

            // Elastic checkbox
            chkElastic = new KryptonCheckBox
            {
                Text = "Elastic",
                Checked = true,
                Location = new Point(140, yPos),
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkElastic.CheckedChanged += MaterialBehavior_CheckedChanged;
            controlsContent.Controls.Add(chkElastic);

            // Plastic checkbox
            chkPlastic = new KryptonCheckBox
            {
                Text = "Plastic",
                Checked = false,
                Location = new Point(200, yPos),
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkPlastic.CheckedChanged += MaterialBehavior_CheckedChanged;
            controlsContent.Controls.Add(chkPlastic);

            // Brittle checkbox
            chkBrittle = new KryptonCheckBox
            {
                Text = "Brittle",
                Checked = false,
                Location = new Point(260, yPos),
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkBrittle.CheckedChanged += MaterialBehavior_CheckedChanged;
            controlsContent.Controls.Add(chkBrittle);
        }
        private void AddMaterialPropertiesControls(Panel controlsContent, int startY)
        {
            // Material Properties Label
            Label lblMatProperties = new Label
            {
                Text = "Material Properties",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, startY)
            };
            controlsContent.Controls.Add(lblMatProperties);

            int yPos = startY + 30;

            // Min pressure
            Label lblMinPressure = new Label
            {
                Text = "Min Pressure (kPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblMinPressure);

            numMinPressure = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 0,
                Maximum = 10000,
                Value = 0,
                DecimalPlaces = 1,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            controlsContent.Controls.Add(numMinPressure);

            yPos += 30;

            // Max pressure
            Label lblMaxPressure = new Label
            {
                Text = "Max Pressure (kPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblMaxPressure);

            numMaxPressure = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 1,
                Maximum = 10000,
                Value = 1000,
                DecimalPlaces = 1,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            controlsContent.Controls.Add(numMaxPressure);

            yPos += 30;

            // Young's modulus
            Label lblYoungModulus = new Label
            {
                Text = "Young's Modulus (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblYoungModulus);

            numYoungModulus = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 100,
                Maximum = 100000,
                Value = 10000,
                DecimalPlaces = 1,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            numYoungModulus.ValueChanged += MaterialProperty_ValueChanged;
            controlsContent.Controls.Add(numYoungModulus);

            yPos += 30;

            // Poisson's ratio
            Label lblPoissonRatio = new Label
            {
                Text = "Poisson's Ratio:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblPoissonRatio);

            numPoissonRatio = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 0.01m,
                Maximum = 0.49m,
                Value = 0.3m,
                DecimalPlaces = 2,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            numPoissonRatio.ValueChanged += MaterialProperty_ValueChanged;
            controlsContent.Controls.Add(numPoissonRatio);

            yPos += 30;

            // Yield strength
            Label lblYieldStrength = new Label
            {
                Text = "Yield Strength (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblYieldStrength);

            numYieldStrength = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 10,
                Maximum = 10000,
                Value = 500,
                DecimalPlaces = 1,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            controlsContent.Controls.Add(numYieldStrength);

            yPos += 30;

            // Brittle strength
            Label lblBrittleStrength = new Label
            {
                Text = "Brittle Strength (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblBrittleStrength);

            numBrittleStrength = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 10,
                Maximum = 10000,
                Value = 800,
                DecimalPlaces = 1,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            controlsContent.Controls.Add(numBrittleStrength);
        }
        private void AddPetrophysicalPropertiesControls(Panel controlsContent, int startY)
        {
            // Petrophysical Properties Label
            Label lblPetroProperties = new Label
            {
                Text = "Petrophysical Properties",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, startY)
            };
            controlsContent.Controls.Add(lblPetroProperties);

            int yPos = startY + 30;

            // Bulk Density
            Label lblBulkDensity = new Label
            {
                Text = "Bulk Density (kg/m³):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblBulkDensity);

            numBulkDensity = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 500,
                Maximum = 5000,
                Value = 2500,
                DecimalPlaces = 1,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            numBulkDensity.ValueChanged += MaterialProperty_ValueChanged;
            controlsContent.Controls.Add(numBulkDensity);

            yPos += 30;

            // Porosity
            Label lblPorosity = new Label
            {
                Text = "Porosity (fraction):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblPorosity);

            numPorosity = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 0.01m,
                Maximum = 0.5m,
                Value = 0.2m,
                DecimalPlaces = 2,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            numPorosity.ValueChanged += MaterialProperty_ValueChanged;
            controlsContent.Controls.Add(numPorosity);

            yPos += 30;

            // Bulk Modulus
            Label lblBulkModulus = new Label
            {
                Text = "Bulk Modulus (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblBulkModulus);

            numBulkModulus = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 100,
                Maximum = 100000,
                Value = 10000,
                DecimalPlaces = 1,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            controlsContent.Controls.Add(numBulkModulus);

            yPos += 30;

            // Permeability
            Label lblPermeability = new Label
            {
                Text = "Permeability (mDarcy):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblPermeability);

            numPermeability = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 0.0001m,
                Maximum = 10m,
                Value = 0.01m,
                DecimalPlaces = 4,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            controlsContent.Controls.Add(numPermeability);

            yPos += 30;

            // Cohesion
            Label lblCohesion = new Label
            {
                Text = "Cohesion (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblCohesion);

            numCohesion = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 0,
                Maximum = 1000,
                Value = 50,
                DecimalPlaces = 1,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            numCohesion.ValueChanged += MohrCoulombParameters_Changed;
            controlsContent.Controls.Add(numCohesion);

            yPos += 30;

            // Friction angle
            Label lblFrictionAngle = new Label
            {
                Text = "Friction Angle (°):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblFrictionAngle);

            numFrictionAngle = new KryptonNumericUpDown
            {
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 0,
                Maximum = 90,
                Value = 30,
                DecimalPlaces = 1,
                StateCommon = {
            Content = { Color1 = Color.White },
            Back = { Color1 = Color.FromArgb(60, 60, 60) }
        }
            };
            numFrictionAngle.ValueChanged += MohrCoulombParameters_Changed;
            controlsContent.Controls.Add(numFrictionAngle);
        }
        private void AddVisualizationControls(Panel controlsContent, int startY)
        {
            // Visualization Settings Label
            Label lblVisualizationSettings = new Label
            {
                Text = "Visualization Settings",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, startY)
            };
            controlsContent.Controls.Add(lblVisualizationSettings);

            int yPos = startY + 30;

            // Wireframe mode
            chkWireframe = new KryptonCheckBox
            {
                Text = "Wireframe Mode",
                Location = new Point(10, yPos),
                Checked = true,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkWireframe.CheckedChanged += ChkWireframe_CheckedChanged;
            controlsContent.Controls.Add(chkWireframe);

            yPos += 30;

            // Failed elements visualization
            chkShowFailedElements = new KryptonCheckBox
            {
                Text = "Highlight Failed Elements",
                Location = new Point(10, yPos),
                Checked = true,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkShowFailedElements.CheckedChanged += (s, e) => {
                showFailedElements = chkShowFailedElements.Checked;
                glControl.Invalidate();
            };
            controlsContent.Controls.Add(chkShowFailedElements);

            yPos += 30;

            // Transparency controls
            chkEnableTransparency = new KryptonCheckBox
            {
                Text = "Enable Transparency",
                Location = new Point(10, yPos),
                Checked = false,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            controlsContent.Controls.Add(chkEnableTransparency);

            yPos += 30;

            // Transparency level slider
            Label lblTransparency = new Label
            {
                Text = "Transparency Level:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, yPos)
            };
            controlsContent.Controls.Add(lblTransparency);

            KryptonTrackBar trackTransparency = new KryptonTrackBar
            {
                Name = "trackTransparency",
                Location = new Point(140, yPos),
                Width = 180,
                Minimum = 0,
                Maximum = 100,
                Value = 70, // Default to 70% opacity (30% transparency)
                TickFrequency = 10,
                StateCommon = {
            Track = { Color1 = Color.FromArgb(80, 80, 80) },
            Tick = { Color1 = Color.Silver }
        },
                Enabled = false // Initially disabled until transparency is enabled
            };
            controlsContent.Controls.Add(trackTransparency);

            // Wire up events for transparency controls
            chkEnableTransparency.CheckedChanged += (s, e) => {
                enableTransparency = chkEnableTransparency.Checked;
                trackTransparency.Enabled = enableTransparency;
                transparencyLevel = trackTransparency.Value / 100.0f;
                glControl.Invalidate();
            };

            trackTransparency.ValueChanged += (s, e) => {
                transparencyLevel = trackTransparency.Value / 100.0f;
                glControl.Invalidate();
            };

            yPos += 30;

            // Fast simulation mode
            chkFastSimulation = new KryptonCheckBox
            {
                Text = "Fast Simulation Mode",
                Location = new Point(10, yPos),
                Checked = false,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkFastSimulation.CheckedChanged += (s, e) => {
                fastSimulationMode = chkFastSimulation.Checked;
            };
            controlsContent.Controls.Add(chkFastSimulation);

            yPos += 30;

            // Hardware acceleration
            chkUseDirectCompute = new KryptonCheckBox
            {
                Text = "Use Hardware Acceleration",
                Location = new Point(10, yPos),
                Checked = true,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkUseDirectCompute.CheckedChanged += (s, e) => {
                useDirectCompute = chkUseDirectCompute.Checked;
            };
            controlsContent.Controls.Add(chkUseDirectCompute);
        }
        private void AddProgressControls(Panel controlsContent, int startY)
        {
            // Progress bar
            Label lblProgress = new Label
            {
                Text = "Progress:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, startY)
            };
            controlsContent.Controls.Add(lblProgress);

            progressBar = new ProgressBar
            {
                Location = new Point(140, startY),
                Width = 180,
                Height = 20,
                Style = ProgressBarStyle.Continuous,
                BackColor = Color.FromArgb(64, 64, 64),
                ForeColor = Color.LightSkyBlue
            };
            controlsContent.Controls.Add(progressBar);

            int yPos = startY + 30;

            // Progress label
            progressLabel = new Label
            {
                Text = "Ready",
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(140, yPos),
                Width = 180,
                Height = 20
            };
            controlsContent.Controls.Add(progressLabel);
        }
        private void AddActionButtonsControls(Panel controlsContent, int startY)
        {
            // Generate Mesh Button
            btnGenerateMesh = new KryptonButton
            {
                Text = "Generate Mesh",
                Location = new Point(10, startY),
                Width = 310,
                Height = 30,
                StateCommon = {
            Back = { Color1 = Color.FromArgb(80, 80, 120) },
            Content = { ShortText = { Color1 = Color.White } }
        }
            };
            btnGenerateMesh.Click += BtnGenerateMesh_Click;
            controlsContent.Controls.Add(btnGenerateMesh);

            int yPos = startY + 40;

            // Start simulation button
            btnStartSimulation = new KryptonButton
            {
                Text = "Start Simulation",
                Location = new Point(10, yPos),
                Width = 310,
                Height = 30,
                Enabled = false,
                StateCommon = {
            Back = { Color1 = Color.FromArgb(0, 100, 0) },
            Content = { ShortText = { Color1 = Color.White } }
        },
                StateDisabled = {
            Back = { Color1 = Color.FromArgb(60, 60, 60) },
            Content = { ShortText = { Color1 = Color.Silver } }
        }
            };
            btnStartSimulation.Click += BtnStartSimulation_Click;
            controlsContent.Controls.Add(btnStartSimulation);

            yPos += 40;

            // Stop simulation button
            btnStopSimulation = new KryptonButton
            {
                Text = "Stop Simulation",
                Location = new Point(10, yPos),
                Width = 310,
                Height = 30,
                Enabled = false,
                StateCommon = {
            Back = { Color1 = Color.FromArgb(120, 0, 0) },
            Content = { ShortText = { Color1 = Color.White } }
        },
                StateDisabled = {
            Back = { Color1 = Color.FromArgb(60, 60, 60) },
            Content = { ShortText = { Color1 = Color.Silver } }
        }
            };
            btnStopSimulation.Click += BtnStopSimulation_Click;
            controlsContent.Controls.Add(btnStopSimulation);

            yPos += 40;

            // NEW: Continue after failure button
            btnContinueSimulation = new KryptonButton
            {
                Text = "Continue After Failure",
                Location = new Point(10, yPos),
                Width = 310,
                Height = 30,
                Enabled = false,
                StateCommon = {
            Back = { Color1 = Color.FromArgb(180, 120, 0) }, // Orange color to indicate caution
            Content = { ShortText = { Color1 = Color.White } }
        },
                StateDisabled = {
            Back = { Color1 = Color.FromArgb(60, 60, 60) },
            Content = { ShortText = { Color1 = Color.Silver } }
        }
            };
            btnContinueSimulation.Click += BtnContinueSimulation_Click;
            controlsContent.Controls.Add(btnContinueSimulation);

            yPos += 40;

            // Place both export buttons side by side
            // Export to Volume Button (half width)
            btnExportToVolume = new KryptonButton
            {
                Text = "Export to Volume",
                Location = new Point(10, yPos),
                Width = 150, // Half the original width
                Height = 30,
                Enabled = false, // Initially disabled
                StateCommon = {
            Back = { Color1 = Color.FromArgb(60, 100, 120) },
            Content = { ShortText = { Color1 = Color.White } }
        },
                StateDisabled = {
            Back = { Color1 = Color.FromArgb(60, 60, 60) },
            Content = { ShortText = { Color1 = Color.Silver } }
        }
            };
            btnExportToVolume.Click += BtnExportToVolume_Click;
            controlsContent.Controls.Add(btnExportToVolume);

            // NEW: Export Results Button (half width)
            btnExportResults = new KryptonButton
            {
                Text = "Export Results",
                Location = new Point(170, yPos), // Position beside first button
                Width = 150, // Same width as first button
                Height = 30,
                Enabled = false, // Initially disabled
                StateCommon = {
            Back = { Color1 = Color.FromArgb(100, 100, 60) },
            Content = { ShortText = { Color1 = Color.White } }
        },
                StateDisabled = {
            Back = { Color1 = Color.FromArgb(60, 60, 60) },
            Content = { ShortText = { Color1 = Color.Silver } }
        }
            };
            btnExportResults.Click += BtnExportResults_Click;
            controlsContent.Controls.Add(btnExportResults);

            yPos += 40;

            // Save Image Button
            KryptonButton btnSaveImage = new KryptonButton
            {
                Text = "Save Image",
                Location = new Point(10, yPos),
                Width = 310,
                Height = 30,
                StateCommon = {
            Back = { Color1 = Color.FromArgb(60, 60, 120) },
            Content = { ShortText = { Color1 = Color.White } }
        }
            };
            btnSaveImage.Click += BtnSaveImage_Click;
            controlsContent.Controls.Add(btnSaveImage);
        }
        private void ColorLegendPanel_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            // Enable high quality rendering
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle panelRect = colorLegendPanel.ClientRectangle;

            // Legend parameters
            int barWidth = 25;
            int barHeight = 200;
            int barX = 60;
            int barY = 40;

            // Draw title
            using (Font titleFont = new Font("Arial", 10, FontStyle.Bold))
            {
                g.DrawString("Density", titleFont, Brushes.White, 10, 10);

                // Draw unit
                string unitText = isDensityCalibrated ? "kg/m³" : "(normalized)";
                g.DrawString(unitText, titleFont, Brushes.LightGray, 10, 25);
            }

            // Check for valid density range
            float displayMinDensity = minDensity;
            float displayMaxDensity = maxDensity;

            // If min and max are the same or invalid, set reasonable defaults
            if (float.IsNaN(displayMinDensity) || float.IsNaN(displayMaxDensity) ||
                displayMinDensity >= displayMaxDensity)
            {
                if (isDensityCalibrated)
                {
                    displayMinDensity = 1000f;
                    displayMaxDensity = 3000f;
                }
                else
                {
                    displayMinDensity = 0f;
                    displayMaxDensity = 1f;
                }
            }

            // Draw color gradient
            for (int i = 0; i < barHeight; i++)
            {
                float normalizedValue = 1.0f - (i / (float)barHeight);
                using (Brush brush = new SolidBrush(GetColorForValue(normalizedValue)))
                {
                    g.FillRectangle(brush, barX, barY + i, barWidth, 1);
                }
            }

            // Draw border
            g.DrawRectangle(Pens.White, barX, barY, barWidth, barHeight);

            // Draw tick marks and labels
            using (Font labelFont = new Font("Arial", 8))
            {
                int numTicks = 6;
                for (int i = 0; i < numTicks; i++)
                {
                    float normalizedValue = 1.0f - (i / (float)(numTicks - 1));
                    float value = displayMinDensity + (displayMaxDensity - displayMinDensity) * normalizedValue;
                    int y = barY + (int)(i * barHeight / (float)(numTicks - 1));

                    // Draw tick mark
                    g.DrawLine(Pens.White, barX - 5, y, barX, y);

                    // Format value
                    string valueText = isDensityCalibrated
                        ? $"{value:F0}"
                        : $"{normalizedValue:F1}";

                    // Draw value label
                    g.DrawString(valueText, labelFont, Brushes.White, barX - 45, y - 6);
                }

                // Draw min/max labels if calibrated
                if (isDensityCalibrated)
                {
                    g.DrawString("MAX", labelFont, Brushes.White, barX - 25, barY - 15);
                    g.DrawString("MIN", labelFont, Brushes.White, barX - 25, barY + barHeight + 5);

                    // Debug info to verify values
                    g.DrawString($"Range: {displayMinDensity:F0} - {displayMaxDensity:F0}",
                                 labelFont, Brushes.Yellow, 5, barHeight + barY + 20);
                }
            }
        }
        private void MaterialProperty_ValueChanged(object sender, EventArgs e)
        {
            if (sender == numBulkDensity)        // user edited density manually
            {
                bulkDensity = (float)numBulkDensity.Value;
                UpdateMaterialPropertiesFromDensity();
            }
            else if (sender == numPorosity)      // update permeability from porosity
            {
                porosity = (float)numPorosity.Value;

                // Kozeny-Carman – convert to mD, then clamp
                float porTerm = (float)Math.Pow(porosity, 3) / (float)Math.Pow(1 - porosity, 2);
                float perm = 0.1f * porTerm * 1000.0f;                // D → mD
                perm = Math.Max(0.0001f, perm);                       // fit control range
                perm = Math.Min(perm, (float)numPermeability.Maximum);

                permeability = perm;
                SafeSetNumericValue(numPermeability, (decimal)permeability);
            }
            else if (sender == numYoungModulus)  // recalc bulk modulus
            {
                youngModulus = (float)numYoungModulus.Value;
                bulkModulus = youngModulus / (3 * (1 - 2 * poissonRatio));
                SafeSetNumericValue(numBulkModulus, (decimal)bulkModulus);
            }
            else if (sender == numPoissonRatio)  // recalc bulk modulus
            {
                poissonRatio = (float)numPoissonRatio.Value;
                bulkModulus = youngModulus / (3 * (1 - 2 * poissonRatio));
                SafeSetNumericValue(numBulkModulus, (decimal)bulkModulus);
            }
        }
        private void ChkShowDensity_CheckedChanged(object sender, EventArgs e)
        {
            // Redraw the mesh with/without density coloring
            glControl.Invalidate();
        }

        private void BtnDensitySettings_Click(object sender, EventArgs e)
        {
            try
            {
                // Before opening the form, ensure materialVolume is calculated
                CalculateMaterialVolume();

                // Now open the density settings dialog
                using (DensitySettingsForm densityForm = new DensitySettingsForm(this, mainForm))
                {
                    if (densityForm.ShowDialog() == DialogResult.OK)
                    {
                        // The form will call SetMaterialDensity or ApplyDensityCalibration
                        glControl.Invalidate();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error opening DensitySettingsForm: {ex.Message}");
                MessageBox.Show($"Error opening density settings: {ex.Message}", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void CalculateMaterialVolume()
        {
            // Calculate the volume of the material in m³
            try
            {
                if (vertices.Count == 0)
                {
                    // Estimate from the material voxels in the volume
                    int materialVoxelCount = 0;

                    if (densityVolume != null)
                    {
                        for (int z = 0; z < depth; z++)
                        {
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    if (densityVolume[x, y, z] > 0)
                                    {
                                        materialVoxelCount++;
                                    }
                                }
                            }
                        }
                    }

                    // Calculate volume based on voxel count and pixel size
                    materialVolume = materialVoxelCount * pixelSize * pixelSize * pixelSize;
                }
                else
                {
                    // More accurate volume calculation using tetrahedra
                    materialVolume = CalculateTetrahedralVolume();
                }

                // Ensure we have a valid volume
                if (materialVolume <= 0)
                {
                    materialVolume = 0.000001; // 1 cm³ in m³
                    Logger.Log("[TriaxialSimulationForm] Warning: Material volume is zero, using small default.");
                }
            }
            catch (Exception ex)
            {
                materialVolume = 0.000001; // 1 cm³ in m³
                Logger.Log($"[TriaxialSimulationForm] Error calculating material volume: {ex.Message}");
            }
        }
        private double CalculateTetrahedralVolume()
        {
            try
            {
                // Calculate volume using tetrahedra
                double totalVolume = 0.0;

                foreach (var tetra in tetrahedralElements)
                {
                    int[] vIndices = tetra.Vertices;

                    if (vIndices.Length == 4 &&
                        vIndices[0] < vertices.Count &&
                        vIndices[1] < vertices.Count &&
                        vIndices[2] < vertices.Count &&
                        vIndices[3] < vertices.Count)
                    {
                        Vector3 v0 = vertices[vIndices[0]];
                        Vector3 v1 = vertices[vIndices[1]];
                        Vector3 v2 = vertices[vIndices[2]];
                        Vector3 v3 = vertices[vIndices[3]];

                        // Calculate tetrahedron volume
                        Vector3 v01 = v1 - v0;
                        Vector3 v02 = v2 - v0;
                        Vector3 v03 = v3 - v0;

                        double vol = Math.Abs(Vector3.Dot(Vector3.Cross(v01, v02), v03)) / 6.0;
                        totalVolume += vol;
                    }
                }

                // Convert from model units (mm³) to m³
                totalVolume = totalVolume * 1.0e-9;

                // Ensure a minimum volume
                if (totalVolume <= 0)
                {
                    totalVolume = 0.000001; // 1 cm³ in m³
                }

                return totalVolume;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error in CalculateTetrahedralVolume: {ex.Message}");
                return 0.000001; // Return a small default volume
            }
        }
        private void MaterialBehavior_CheckedChanged(object sender, EventArgs e)
        {
            isElasticEnabled = chkElastic.Checked;
            isPlasticEnabled = chkPlastic.Checked;
            isBrittleEnabled = chkBrittle.Checked;

            // Ensure at least one behavior is selected
            if (!isElasticEnabled && !isPlasticEnabled && !isBrittleEnabled)
            {
                ((KryptonCheckBox)sender).Checked = true;
            }
        }

        private void MohrCoulombParameters_Changed(object sender, EventArgs e)
        {
            cohesion = (float)numCohesion.Value;
            frictionAngle = (float)numFrictionAngle.Value;
            mohrCoulombGraph.Invalidate();
        }

        private void MohrCoulombGraph_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Set up coordinate system with padding
            int padding = 30;
            int width = mohrCoulombGraph.Width - 2 * padding;
            int height = mohrCoulombGraph.Height - 2 * padding;

            // Draw axes
            using (Pen axisPen = new Pen(Color.Black, 2))
            {
                // X-axis (normal stress)
                g.DrawLine(axisPen, padding, mohrCoulombGraph.Height - padding,
                           mohrCoulombGraph.Width - padding, mohrCoulombGraph.Height - padding);

                // Y-axis (shear stress)
                g.DrawLine(axisPen, padding, mohrCoulombGraph.Height - padding,
                           padding, padding);
            }

            // Draw axis labels
            using (Font labelFont = new Font("Arial", 8))
            {
                g.DrawString("Normal Stress (MPa)", labelFont, Brushes.Black,
                            mohrCoulombGraph.Width / 2, mohrCoulombGraph.Height - 15);

                // Rotate Y-axis label
                g.TranslateTransform(15, mohrCoulombGraph.Height / 2);
                g.RotateTransform(-90);
                g.DrawString("Shear Stress (MPa)", labelFont, Brushes.Black, 0, 0);
                g.ResetTransform();
            }

            // Calculate maximum stress for scaling
            float maxStress = Math.Max(1000, (float)numMaxPressure.Value * 2);

            // Draw Mohr-Coulomb failure envelope
            using (Pen failurePen = new Pen(Color.Red, 2))
            {
                // Convert friction angle from degrees to radians
                float frictionAngleRad = (float)(frictionAngle * Math.PI / 180.0);

                // Calculate points for the failure line
                float tanPhi = (float)Math.Tan(frictionAngleRad);

                // Start at cohesion on y-axis
                int x1 = padding;
                int y1 = mohrCoulombGraph.Height - padding - (int)(cohesion * height / maxStress);

                // End at right edge
                int x2 = mohrCoulombGraph.Width - padding;
                float normalStressAtX2 = (x2 - padding) * maxStress / width;
                int y2 = mohrCoulombGraph.Height - padding - (int)((cohesion + normalStressAtX2 * tanPhi) * height / maxStress);

                // Draw the failure envelope line
                g.DrawLine(failurePen, x1, y1, x2, y2);

                // Label the line
                string equation = $"τ = {cohesion:F1} + σ·tan({frictionAngle:F1}°)";
                g.DrawString(equation, new Font("Arial", 9, FontStyle.Bold), Brushes.Red, x1 + 10, y1 - 20);
            }

            // Draw tensile cutoff if applicable
            float tensileStrength = cohesion / (float)Math.Tan(frictionAngle * Math.PI / 180.0f);
            if (tensileStrength > 0)
            {
                using (Pen tensionPen = new Pen(Color.Blue, 2))
                {
                    int tensileX = padding - (int)(tensileStrength * width / maxStress);
                    tensileX = Math.Max(padding - 50, tensileX); // Don't go too far left

                    g.DrawLine(tensionPen,
                        tensileX, mohrCoulombGraph.Height - padding,
                        padding, mohrCoulombGraph.Height - padding - (int)(cohesion * height / maxStress));

                    // Add label
                    g.DrawString($"Tensile\nStrength", new Font("Arial", 8), Brushes.Blue,
                        tensileX, mohrCoulombGraph.Height - padding - 40);
                }
            }

            // Draw Mohr circles if simulation is running
            if (simulationRunning && stressStrainCurve.Count > 0)
            {
                // Get current stress values for the Mohr circle
                float sigma1 = maxPressure; // Major principal stress (vertical load)
                float sigma3 = minPressure; // Minor principal stress (confining pressure)

                float currentStress = stressStrainCurve.Last().Y / 10.0f; // Convert from graph units to MPa

                // For a triaxial test, major principal stress increases during loading
                sigma1 = sigma3 + currentStress;

                // Calculate circle center and radius
                float center = (sigma1 + sigma3) / 2;
                float radius = (sigma1 - sigma3) / 2;

                // Scale to pixel coordinates
                int centerX = padding + (int)(center * width / maxStress);
                int radiusPixels = (int)(radius * width / maxStress);

                // Draw the Mohr circle
                using (Pen circlePen = new Pen(Color.Blue, 2))
                {
                    g.DrawEllipse(circlePen,
                        centerX - radiusPixels,
                        mohrCoulombGraph.Height - padding - radiusPixels,
                        radiusPixels * 2,
                        radiusPixels * 2);
                }

                // Draw the center line
                using (Pen centerPen = new Pen(Color.Gray, 1))
                {
                    g.DrawLine(centerPen,
                        centerX,
                        mohrCoulombGraph.Height - padding - radiusPixels,
                        centerX,
                        mohrCoulombGraph.Height - padding + radiusPixels);
                }

                // Label principal stresses
                using (Font stressFont = new Font("Arial", 8))
                {
                    g.DrawString($"σ₃ = {sigma3:F1} MPa", stressFont, Brushes.Blue,
                        padding + (int)(sigma3 * width / maxStress) - 50,
                        mohrCoulombGraph.Height - padding + 5);

                    g.DrawString($"σ₁ = {sigma1:F1} MPa", stressFont, Brushes.Blue,
                        padding + (int)(sigma1 * width / maxStress) - 50,
                        mohrCoulombGraph.Height - padding + 5);
                }

                // Update current stresses for display
                normalStress = center;
                shearStress = radius;

                // Display current stress state
                using (Font valueFont = new Font("Arial", 10, FontStyle.Bold))
                {
                    g.DrawString($"Normal Stress: {normalStress:F1} MPa", valueFont, Brushes.Black, padding, padding);
                    g.DrawString($"Shear Stress: {shearStress:F1} MPa", valueFont, Brushes.Black, padding, padding + 20);

                    // Check if failure envelope is exceeded
                    float failureShear = cohesion + normalStress * (float)Math.Tan(frictionAngle * Math.PI / 180.0);
                    if (shearStress >= failureShear)
                    {
                        g.DrawString("Status: FAILURE", valueFont, Brushes.Red, padding, padding + 40);
                    }
                    else
                    {
                        g.DrawString("Status: Stable", valueFont, Brushes.Green, padding, padding + 40);
                    }

                    // Display petrophysical parameters
                    g.DrawString($"Bulk Density: {bulkDensity:F0} kg/m³", valueFont, Brushes.DarkBlue, padding, padding + 70);
                    g.DrawString($"Porosity: {porosity:P1}", valueFont, Brushes.DarkBlue, padding, padding + 90);
                }
            }
        }

        private void InitializeBackgroundWorker()
        {
            meshWorker = new BackgroundWorker();
            meshWorker.WorkerReportsProgress = true;
            meshWorker.WorkerSupportsCancellation = true;
            meshWorker.DoWork += MeshWorker_DoWork;
            meshWorker.ProgressChanged += MeshWorker_ProgressChanged;
            meshWorker.RunWorkerCompleted += MeshWorker_RunWorkerCompleted;
        }

        private void MeshWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker worker = sender as BackgroundWorker;
            MeshGenerationParameters parameters = (MeshGenerationParameters)e.Argument;

            // Extract volume data for the selected material
            worker.ReportProgress(0, "Extracting volume data...");
            ExtractVolumeData(parameters.Material);

            // Generate mesh with the selected sampling rate
            worker.ReportProgress(30, "Generating mesh...");
            GenerateMesh(parameters.SamplingRate, worker);

            // Calculate total volume
            worker.ReportProgress(90, "Calculating material volume...");
            materialVolume = CalculateTetrahedralVolume();

            // Result is true if mesh generation was successful
            e.Result = vertices.Count > 0 && indices.Count > 0;
        }

        private void MeshWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            progressBar.Value = e.ProgressPercentage;
            progressLabel.Text = e.UserState.ToString();
        }
        private void ScaleMeshForDisplay()
        {
            if (deformedVertices == null || deformedVertices.Count == 0 || vertices == null || vertices.Count == 0)
            {
                return;
            }

            // Calculate the true mesh bounds without any artificial minimum
            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);

            foreach (var v in vertices)
            {
                min.X = Math.Min(min.X, v.X);
                min.Y = Math.Min(min.Y, v.Y);
                min.Z = Math.Min(min.Z, v.Z);

                max.X = Math.Max(max.X, v.X);
                max.Y = Math.Max(max.Y, v.Y);
                max.Z = Math.Max(max.Z, v.Z);
            }

            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            float maxDim = Math.Max(Math.Max(size.X, size.Y), size.Z);

            // Target bounding box size
            float targetSize = 10.0f;

            // Determine if we need to scale the mesh (too small or too large)
            bool needsScaling = (maxDim < 1.0f || maxDim > 100.0f);

            if (needsScaling)
            {
                // Calculate scale factor
                float scaleFactor = targetSize / maxDim;

                // Create a scaled copy for display purposes only
                deformedVertices = new List<Vector3>(vertices.Count);

                foreach (var v in vertices)
                {
                    // Center then scale
                    Vector3 centered = v - center;
                    Vector3 scaled = centered * scaleFactor;
                    deformedVertices.Add(scaled);
                }
            }
            else
            {
                // No scaling needed, just copy the vertices
                deformedVertices = new List<Vector3>(vertices);
            }
        }
        private void MeshWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            Logger.Log("[TriaxialSimulationForm] MeshWorker_RunWorkerCompleted() - Start");

            if (e.Error != null)
            {
                MessageBox.Show($"Error generating mesh: {e.Error.Message}", "Error",
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
                progressLabel.Text = "Mesh generation failed";
                progressBar.Value = 0;
                Logger.Log($"[TriaxialSimulationForm] Mesh generation failed with error: {e.Error.Message}");
                Logger.Log($"[TriaxialSimulationForm] Error stack trace: {e.Error.StackTrace}");
            }
            else if (e.Cancelled)
            {
                progressLabel.Text = "Mesh generation cancelled";
                progressBar.Value = 0;
                Logger.Log("[TriaxialSimulationForm] Mesh generation was cancelled by user");
            }
            else
            {
                bool success = (bool)e.Result;
                if (success)
                {
                    meshGenerationComplete = true;

                    Logger.Log($"[TriaxialSimulationForm] Mesh generation successful: {vertices.Count} vertices, {indices.Count / 3} triangles");

                    // Log original mesh scale
                    if (vertices.Count > 0)
                    {
                        Vector3 min = new Vector3(float.MaxValue);
                        Vector3 max = new Vector3(float.MinValue);

                        foreach (var v in vertices)
                        {
                            min.X = Math.Min(min.X, v.X);
                            min.Y = Math.Min(min.Y, v.Y);
                            min.Z = Math.Min(min.Z, v.Z);

                            max.X = Math.Max(max.X, v.X);
                            max.Y = Math.Max(max.Y, v.Y);
                            max.Z = Math.Max(max.Z, v.Z);
                        }

                        Vector3 size = max - min;
                        float maxDim = Math.Max(Math.Max(size.X, size.Y), size.Z);

                        Logger.Log($"[TriaxialSimulationForm] Original mesh bounds: min={min}, max={max}, size={size}, maxDim={maxDim}");

                        // Log a few sample vertices
                        for (int i = 0; i < Math.Min(5, vertices.Count); i++)
                        {
                            Logger.Log($"[TriaxialSimulationForm] Original vertex {i}: {vertices[i]}");
                        }
                    }

                    // First calculate the material volume from original vertices (before scaling)
                    // This ensures volume calculations are accurate
                    materialVolume = CalculateTetrahedralVolume();
                    Logger.Log($"[TriaxialSimulationForm] Calculated material volume: {materialVolume} m³");

                    // Fix mesh scaling for display purposes
                    Logger.Log("[TriaxialSimulationForm] Calling FixMeshScale() to adjust mesh size");
                    FixMeshScale();

                    // Create deformed vertices by copying current vertices
                    Logger.Log("[TriaxialSimulationForm] Creating deformed vertices for display");
                    deformedVertices = new List<Vector3>(vertices);

                    // Log deformed vertices scale
                    if (deformedVertices.Count > 0)
                    {
                        Vector3 min = new Vector3(float.MaxValue);
                        Vector3 max = new Vector3(float.MinValue);

                        foreach (var v in deformedVertices)
                        {
                            min.X = Math.Min(min.X, v.X);
                            min.Y = Math.Min(min.Y, v.Y);
                            min.Z = Math.Min(min.Z, v.Z);

                            max.X = Math.Max(max.X, v.X);
                            max.Y = Math.Max(max.Y, v.Y);
                            max.Z = Math.Max(max.Z, v.Z);
                        }

                        Vector3 size = max - min;
                        float maxDim = Math.Max(Math.Max(size.X, size.Y), size.Z);

                        Logger.Log($"[TriaxialSimulationForm] Deformed vertices bounds: min={min}, max={max}, size={size}, maxDim={maxDim}");

                        // Log a few sample deformed vertices
                        for (int i = 0; i < Math.Min(5, deformedVertices.Count); i++)
                        {
                            Logger.Log($"[TriaxialSimulationForm] Deformed vertex {i}: {deformedVertices[i]}");
                        }
                    }

                    // Update UI
                    btnStartSimulation.Enabled = true;
                    progressLabel.Text = $"Mesh generated: {vertices.Count} vertices, {indices.Count / 3} triangles";
                    progressBar.Value = 100;

                    // Set bulk density based on average grayscale value if calibrated
                    if (isDensityCalibrated)
                    {
                        Logger.Log("[TriaxialSimulationForm] Applying density calibration");
                        CalculateAverageBulkDensity();

                        try
                        {
                            numBulkDensity.Value = (decimal)Math.Min(Math.Max((decimal)bulkDensity, numBulkDensity.Minimum), numBulkDensity.Maximum);
                            Logger.Log($"[TriaxialSimulationForm] Set bulk density to {bulkDensity} kg/m³");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[TriaxialSimulationForm] Error setting bulk density UI: {ex.Message}");
                        }

                        UpdateMaterialPropertiesFromDensity();
                    }

                    // Enable/reenable UI controls
                    comboMaterials.Enabled = true;
                    trackSamplingRate.Enabled = true;

                    // Force update of 3D view
                    Logger.Log("[TriaxialSimulationForm] Invalidating GL control to update display");
                    glControl.Invalidate();
                }
                else
                {
                    progressLabel.Text = "No voxels found for selected material";
                    progressBar.Value = 0;
                    Logger.Log("[TriaxialSimulationForm] No voxels found for selected material - mesh generation failed");

                    // Enable/reenable UI controls
                    comboMaterials.Enabled = true;
                    trackSamplingRate.Enabled = true;
                }
            }
            comboMaterials.Enabled = true;
            trackSamplingRate.Enabled = true;

            btnGenerateMesh.Enabled = true;
            Logger.Log("[TriaxialSimulationForm] MeshWorker_RunWorkerCompleted() - End");
        }
        private void LoadMaterialsFromMainForm()
        {
            comboMaterials.Items.Clear();

            foreach (Material material in mainForm.Materials)
            {
                // Skip exterior material (usually ID 0)
                if (!material.IsExterior)
                {
                    comboMaterials.Items.Add(material);
                }
            }

            // Display material name in dropdown
            comboMaterials.DisplayMember = "Name";

            if (comboMaterials.Items.Count > 0)
            {
                comboMaterials.SelectedIndex = 0;
            }
        }

        private void BtnGenerateMesh_Click(object sender, EventArgs e)
        {
            Logger.Log("[TriaxialSimulationForm] BtnGenerateMesh_Click() - Start");

            if (comboMaterials.SelectedItem is Material material)
            {
                selectedMaterial = material;
                samplingRate = trackSamplingRate.Value;

                // Reset progress
                progressBar.Value = 0;
                progressLabel.Text = "Starting mesh generation...";

                // Disable UI during processing
                comboMaterials.Enabled = false;
                trackSamplingRate.Enabled = false;
                btnStartSimulation.Enabled = false;
                btnGenerateMesh.Enabled = false;  // Disable the Generate Mesh button

                // Clear old mesh data
                vertices.Clear();
                indices.Clear();
                normals.Clear();
                densityValues.Clear();
                tetrahedralElements.Clear();

                Logger.Log($"[TriaxialSimulationForm] Starting mesh generation for material: {material.Name}, samplingRate: {samplingRate}");

                // Start mesh generation in background
                MeshGenerationParameters parameters = new MeshGenerationParameters
                {
                    Material = material,
                    SamplingRate = samplingRate
                };

                meshWorker.RunWorkerAsync(parameters);
            }

            Logger.Log("[TriaxialSimulationForm] BtnGenerateMesh_Click() - End");
        }
        private void FixMeshScale()
        {
            Logger.Log("[TriaxialSimulationForm] FixMeshScale() - Start");

            if (vertices == null || vertices.Count == 0)
            {
                Logger.Log("[TriaxialSimulationForm] No vertices to fix - returning");
                return;
            }

            // Find the current mesh scale
            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);

            foreach (var v in vertices)
            {
                min.X = Math.Min(min.X, v.X);
                min.Y = Math.Min(min.Y, v.Y);
                min.Z = Math.Min(min.Z, v.Z);

                max.X = Math.Max(max.X, v.X);
                max.Y = Math.Max(max.Y, v.Y);
                max.Z = Math.Max(max.Z, v.Z);
            }

            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            float maxDim = Math.Max(Math.Max(size.X, size.Y), size.Z);

            Logger.Log($"[TriaxialSimulationForm] Original mesh bounds: min={min}, max={max}, center={center}, size={size}, maxDim={maxDim}");

            // Target bounding box size - make it a reasonable size for visualization
            float targetSize = 10.0f;

            // Determine if we need to scale the mesh (too small or too large)
            bool needsRescaling = (maxDim < 1.0f || maxDim > 100.0f);

            if (needsRescaling)
            {
                float scaleFactor = targetSize / maxDim;

                Logger.Log($"[TriaxialSimulationForm] Rescaling mesh: scaleFactor={scaleFactor}, targetSize={targetSize}");

                // Create a backup copy of the original vertices for volume calculations
                List<Vector3> originalVertices = new List<Vector3>(vertices);

                // Save the scale factor for future reference
                float originalScale = maxDim;
                Logger.Log($"[TriaxialSimulationForm] Saving originalScale={originalScale} for volume calculations");

                // Scale all vertex positions relative to center
                for (int i = 0; i < vertices.Count; i++)
                {
                    // Store original position
                    Vector3 original = vertices[i];

                    // Center, scale, and replace
                    Vector3 centered = original - center;
                    vertices[i] = center + (centered * scaleFactor);

                    // Log a few vertices for debugging
                    if (i < 5)
                    {
                        Logger.Log($"[TriaxialSimulationForm] Vertex {i}: Original={original}, Centered={centered}, Scaled={vertices[i]}");
                    }
                }

                // Check the new scale
                min = new Vector3(float.MaxValue);
                max = new Vector3(float.MinValue);

                foreach (var v in vertices)
                {
                    min.X = Math.Min(min.X, v.X);
                    min.Y = Math.Min(min.Y, v.Y);
                    min.Z = Math.Min(min.Z, v.Z);

                    max.X = Math.Max(max.X, v.X);
                    max.Y = Math.Max(max.Y, v.Y);
                    max.Z = Math.Max(max.Z, v.Z);
                }

                Vector3 newCenter = (min + max) * 0.5f;
                Vector3 newSize = max - min;
                float newMaxDim = Math.Max(Math.Max(newSize.X, newSize.Y), newSize.Z);

                Logger.Log($"[TriaxialSimulationForm] Rescaled mesh bounds: min={min}, max={max}, center={newCenter}, size={newSize}, maxDim={newMaxDim}");
            }
            else
            {
                Logger.Log("[TriaxialSimulationForm] Mesh scale is within reasonable range, no rescaling needed");
            }

            Logger.Log("[TriaxialSimulationForm] FixMeshScale() - End");
        }
        private void BtnExportToVolume_Click(object sender, EventArgs e)
        {
            if (deformedVertices == null || deformedVertices.Count == 0 ||
                indices == null || indices.Count == 0)
            {
                MessageBox.Show("No valid mesh data available to export.",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                // Create a form to get export parameters
                using (var exportForm = new Form
                {
                    Text = "Volume Export Parameters",
                    Size = new Size(400, 200),
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false
                })
                {
                    // Add resolution input
                    Label lblResolution = new Label
                    {
                        Text = "Resolution (voxels per unit):",
                        Location = new Point(20, 20),
                        AutoSize = true
                    };
                    exportForm.Controls.Add(lblResolution);

                    NumericUpDown numResolution = new NumericUpDown
                    {
                        Location = new Point(200, 18),
                        Width = 120,
                        Minimum = 10,
                        Maximum = 200,
                        Value = 50,
                        DecimalPlaces = 0
                    };
                    exportForm.Controls.Add(numResolution);

                    // Add pixel size display
                    Label lblPixelSize = new Label
                    {
                        Text = $"Pixel Size: {pixelSize:E6} m",
                        Location = new Point(20, 60),
                        AutoSize = true
                    };
                    exportForm.Controls.Add(lblPixelSize);

                    // Add OK and Cancel buttons
                    Button btnOK = new Button
                    {
                        Text = "OK",
                        DialogResult = DialogResult.OK,
                        Location = new Point(200, 120),
                        Width = 80
                    };
                    exportForm.Controls.Add(btnOK);

                    Button btnCancel = new Button
                    {
                        Text = "Cancel",
                        DialogResult = DialogResult.Cancel,
                        Location = new Point(290, 120),
                        Width = 80
                    };
                    exportForm.Controls.Add(btnCancel);

                    // Show dialog
                    if (exportForm.ShowDialog() == DialogResult.OK)
                    {
                        // Get parameters
                        float resolution = (float)numResolution.Value;

                        // Show progress form
                        var progressForm = new ProgressFormWithProgress("Exporting mesh to volume...");
                        progressForm.Show();

                        // Run the mesh conversion in a background thread to keep UI responsive
                        Task.Run(() =>
                        {
                            try
                            {
                                // Create ScanToVolume instance
                                ScanToVolume scanner = new ScanToVolume();

                                // Update progress
                                this.SafeInvokeAsync(() => progressForm.UpdateProgress(10));

                                // Convert mesh to volume
                                scanner.ConvertMeshToVolume(deformedVertices, indices, densityValues, resolution, pixelSize);

                                // Get volume dimensions for user output
                                scanner.GetVolumeDimensions(out int width, out int height, out int depth);

                                // Update progress
                                this.SafeInvokeAsync(() => progressForm.UpdateProgress(50));

                                // Show folder browser dialog on UI thread
                                string outputFolder = "";
                                this.SafeInvokeAsync(() =>
                                {
                                    using (var dialog = new FolderBrowserDialog())
                                    {
                                        dialog.Description = "Select folder to save volume slices";
                                        if (dialog.ShowDialog() == DialogResult.OK)
                                        {
                                            outputFolder = dialog.SelectedPath;
                                        }
                                    }
                                });

                                if (string.IsNullOrEmpty(outputFolder))
                                {
                                    this.SafeInvokeAsync(() => progressForm.Close());
                                    return;
                                }

                                // Update progress
                                this.SafeInvokeAsync(() => progressForm.UpdateProgress(70));

                                // Export the volume
                                scanner.ExportVolumeToImages(outputFolder, "slice_");

                                // Complete
                                this.SafeInvokeAsync(() =>
                                {
                                    progressForm.Close();
                                    MessageBox.Show($"Volume exported successfully!\nDimensions: {width}x{height}x{depth}" +
                                                  $"\nPixel Size: {scanner.GetPixelSize():E6} m", "Export Complete");
                                });
                            }
                            catch (Exception ex)
                            {
                                this.SafeInvokeAsync(() =>
                                {
                                    progressForm.Close();
                                    MessageBox.Show($"Error exporting volume: {ex.Message}", "Export Error",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                                    Logger.Log($"[TriaxialSimulationForm] Error in volume export: {ex.Message}");
                                });
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting up export dialog: {ex.Message}",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[TriaxialSimulationForm] Error setting up export dialog: {ex.Message}");
            }
        }
        private void ComboMaterials_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Reset mesh generation status
            meshGenerationComplete = false;
            btnStartSimulation.Enabled = false;

            // Clear old mesh data
            vertices.Clear();
            indices.Clear();
            normals.Clear();
            densityValues.Clear();
            tetrahedralElements.Clear();

            // Set the selected material
            selectedMaterial = comboMaterials.SelectedItem as Material;

            glControl.Invalidate();
        }

        private void TrackSamplingRate_ValueChanged(object sender, EventArgs e)
        {
            samplingRate = trackSamplingRate.Value;
            string qualityLevel = "";

            switch (samplingRate)
            {
                case 1: qualityLevel = "Highest (very slow)"; break;
                case 2: qualityLevel = "High"; break;
                case 3: qualityLevel = "Medium"; break;
                case 4: qualityLevel = "Low"; break;
                case 5: qualityLevel = "Lowest (very fast)"; break;
            }

            trackSamplingRate.ToolTipValues.Description = $"Sampling Rate: {samplingRate} - Quality: {qualityLevel}";
        }

        private void ComboDirection_SelectedIndexChanged(object sender, EventArgs e)
        {
            selectedDirection = (SimulationDirection)comboDirection.SelectedIndex;
        }

        private void ChkWireframe_CheckedChanged(object sender, EventArgs e)
        {
            wireframeMode = chkWireframe.Checked;
            glControl.Invalidate();
        }

        private void BtnStartSimulation_Click(object sender, EventArgs e)
        {
            if (!meshGenerationComplete || tetrahedralElements.Count == 0)
            {
                MessageBox.Show("No mesh available. Please generate a mesh first.", "Error",
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                // Get simulation parameters from UI
                minPressure = (float)numMinPressure.Value;
                maxPressure = (float)numMaxPressure.Value;
                youngModulus = (float)numYoungModulus.Value;
                poissonRatio = (float)numPoissonRatio.Value;
                yieldStrength = (float)numYieldStrength.Value;
                brittleStrength = (float)numBrittleStrength.Value;
                cohesion = (float)numCohesion.Value;
                frictionAngle = (float)numFrictionAngle.Value;
                bulkDensity = (float)numBulkDensity.Value;
                porosity = (float)numPorosity.Value;
                bulkModulus = (float)numBulkModulus.Value;
                permeability = (float)numPermeability.Value;

                // Get behavior flags
                isElasticEnabled = chkElastic.Checked;
                isPlasticEnabled = chkPlastic.Checked;
                isBrittleEnabled = chkBrittle.Checked;

                // Reset simulation state
                stressStrainCurve.Clear();
                currentStrain = 0.0f;

                // Create a new deformed vertices list to ensure proper sizing
                deformedVertices = new List<Vector3>(vertices.Count);
                for (int i = 0; i < vertices.Count; i++)
                {
                    deformedVertices.Add(vertices[i]);
                }

                elementStresses.Clear();

                // Update UI state
                btnStartSimulation.Enabled = false;
                btnStopSimulation.Enabled = true;
                comboMaterials.Enabled = false;
                comboDirection.Enabled = false;
                progressBar.Value = 0;  // Reset progress bar

                // Start simulation
                simulationRunning = true;

                // Show diagrams form
                try
                {
                    if (diagramsForm == null || diagramsForm.IsDisposed)
                    {
                        InitializeDiagramsForm();
                    }

                    if (diagramsForm != null)
                    {
                        diagramsForm.Show();
                        UpdateDiagramsForm();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[TriaxialSimulationForm] Error initializing DiagramsForm: {ex.Message}");
                }

                // Choose computation method
                bool useDirectComputeForThisRun = useDirectCompute && chkUseDirectCompute.Checked;

                if (useDirectComputeForThisRun)
                {
                    // Log the start of accelerated computation
                    progressLabel.Text = fastSimulationMode ?
                        "Hardware accelerated simulation started (updating at completion)" :
                        "Hardware accelerated simulation started";

                    Logger.Log("[TriaxialSimulationForm] Starting simulation with hardware acceleration");

                    // Initialize DirectCompute if needed
                    if (computeEngine == null)
                    {
                        InitializeDirectCompute();
                    }

                    if (computeEngine != null)
                    {
                        Task.Run(async () => {
                            try
                            {
                                // Make deep copies of collections to prevent threading issues
                                var verticesCopy = new List<Vector3>(vertices);
                                var normalsCopy = new List<Vector3>(normals);
                                var indicesCopy = new List<int>(indices);
                                var densityValuesCopy = new List<float>(densityValues);
                                var tetrahedralElementsCopy = new List<TetrahedralElement>(tetrahedralElements);

                                // Initialize compute engine with copied mesh data
                                computeEngine.InitializeFromMesh(
                                    verticesCopy,
                                    normalsCopy,
                                    indicesCopy,
                                    densityValuesCopy,
                                    tetrahedralElementsCopy);

                                // Set material properties
                                computeEngine.SetMaterialProperties(
                                    bulkDensity,
                                    youngModulus,
                                    poissonRatio,
                                    yieldStrength,
                                    brittleStrength,
                                    cohesion,
                                    frictionAngle,
                                    porosity,
                                    bulkModulus,
                                    permeability,
                                    minPressure,
                                    maxPressure,
                                    isElasticEnabled,
                                    isPlasticEnabled,
                                    isBrittleEnabled);

                                // Run the simulation
                                SimulationDirection direction = (SimulationDirection)comboDirection.SelectedIndex;
                                await computeEngine.RunFullSimulationAsync(maxStrain, 0.001f, (CTS.SimulationDirection)direction);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[TriaxialSimulationForm] Error in accelerated simulation: {ex.Message}");

                                if (ex.InnerException != null)
                                {
                                    Logger.Log($"[TriaxialSimulationForm] Inner exception: {ex.InnerException.Message}");
                                }

                                Logger.Log($"[TriaxialSimulationForm] Stack trace: {ex.StackTrace}");

                                // Fall back to original method if direct compute fails
                                if (InvokeRequired)
                                {
                                    BeginInvoke(new Action(() => {
                                        progressLabel.Text = "Falling back to standard simulation";
                                        simulationTimer.Start();
                                    }));
                                }
                                else
                                {
                                    progressLabel.Text = "Falling back to standard simulation";
                                    simulationTimer.Start();
                                }
                            }
                        });
                    }
                    else
                    {
                        // Could not initialize DirectCompute, fall back to CPU
                        progressLabel.Text = "Hardware acceleration unavailable, using standard simulation";
                        simulationTimer.Start();
                    }
                }
                else
                {
                    // Use original timer-based method
                    progressLabel.Text = fastSimulationMode ?
                        "Standard simulation started (updating at completion)" :
                        "Standard simulation started";

                    Logger.Log("[TriaxialSimulationForm] Starting simulation with standard method");
                    simulationTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error starting simulation: {ex.Message}");

                if (ex.InnerException != null)
                {
                    Logger.Log($"[TriaxialSimulationForm] Inner exception: {ex.InnerException.Message}");
                }

                MessageBox.Show($"Error starting simulation: {ex.Message}", "Error",
                                 MessageBoxButtons.OK, MessageBoxIcon.Error);

                // Reset UI state
                ResetUIAfterSimulation();
            }
        }
        private void ResetUIAfterSimulation()
        {
            // Reset UI state
            btnStartSimulation.Enabled = true;
            btnStopSimulation.Enabled = false;
            btnContinueSimulation.Enabled = failureState && currentStrain < maxStrain;
            comboMaterials.Enabled = true;
            comboDirection.Enabled = true;
            btnExportResults.Enabled = true;
            btnExportToVolume.Enabled = true;
            chkElastic.Enabled = true;
            chkPlastic.Enabled = true;
            chkBrittle.Enabled = true;
            numMinPressure.Enabled = true;
            numMaxPressure.Enabled = true;
            numYoungModulus.Enabled = true;
            numPoissonRatio.Enabled = true;
            numYieldStrength.Enabled = true;
            numBrittleStrength.Enabled = true;
            numCohesion.Enabled = true;
            numFrictionAngle.Enabled = true;
            numBulkDensity.Enabled = true;
            numPorosity.Enabled = true;
            numBulkModulus.Enabled = true;
            numPermeability.Enabled = true;

            // Reset simulation state
            simulationRunning = false;

            // Reset progress
            progressBar.Value = 0;
            progressLabel.Text = "Ready";
            progressLabel.ForeColor = SystemColors.ControlText;
        }
        private void BtnStopSimulation_Click(object sender, EventArgs e)
        {
            StopSimulation();
        }

        private void StopSimulation()
        {
            simulationRunning = false;
            simulationTimer.Stop();

            // Also cancel the direct compute simulation if running
            if (computeEngine != null)
            {
                try
                {
                    // The Dispose method will cancel any running simulation
                    computeEngine.Dispose();
                    computeEngine = null;

                    // Create a new instance for next time
                    InitializeDirectCompute();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[TriaxialSimulationForm] Error stopping direct compute: {ex.Message}");
                }
            }

            // Enable/disable buttons
            btnStartSimulation.Enabled = true;
            btnStopSimulation.Enabled = false;
            comboMaterials.Enabled = true;
            comboDirection.Enabled = true;
        }
        private void SimulationTimer_Tick(object sender, EventArgs e)
        {
            if (!simulationRunning)
                return;

            // Increment strain (scaled by test conditions)
            float strainRate = 0.001f; // Base rate 0.1% strain per tick

            // Strain rate affected by confining pressure - higher pressure = slower deformation
            float normalizedPressure = minPressure / 1000.0f; // Convert to MPa
            strainRate *= Math.Max(0.5f, 1.0f - normalizedPressure * 0.02f);

            currentStrain += strainRate;

            if (currentStrain >= maxStrain)
            {
                StopSimulation();
                return;
            }

            // Calculate current stress based on strain and material model
            float averageStress = CalculateStress(currentStrain);

            // Convert stress to chart units (MPa to display units)
            int stressForChart = (int)(averageStress * 10.0f);

            // Update Mohr-Coulomb parameters
            UpdateMohrCoulombParameters();

            // Add to stress-strain curve
            stressStrainCurve.Add(new Point((int)(currentStrain * 1000), stressForChart));

            // Update mesh deformation based on original vertex positions
            UpdateDeformation(currentStrain, averageStress);

            // Only redraw UI if not in fast simulation mode
            if (!fastSimulationMode || currentStrain >= maxStrain - 0.001f)
            {
                // Update progress display
                float completionPercent = currentStrain / maxStrain * 100f;
                progressBar.Value = (int)Math.Min(100, completionPercent);
                progressLabel.Text = $"Progress: {completionPercent:F1}% (Strain: {currentStrain * 100:F2}%)";

                // Redraw charts
                if (stressStrainGraph != null)
                    stressStrainGraph.Invalidate();

                if (mohrCoulombGraph != null)
                    mohrCoulombGraph.Invalidate();

                // Update DiagramsForm if available
                if (diagramsForm != null && !diagramsForm.IsDisposed)
                {
                    try
                    {
                        UpdateDiagramsForm();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[TriaxialSimulationForm] Error updating diagrams: {ex.Message}");
                    }
                }

                // Update 3D view
                glControl.Invalidate();
            }
        }
        private void UpdateMohrCoulombParameters()
        {
            if (stressStrainCurve.Count == 0)
                return;

            // Get current stress values in MPa
            float currentStress = stressStrainCurve.Last().Y / 10.0f;
            float confiningPressure = minPressure / 1000.0f; // kPa to MPa

            // For a triaxial test, major principal stress increases during loading
            float sigma1 = confiningPressure + currentStress;
            float sigma3 = confiningPressure;

            // Calculate Mohr circle parameters for display
            normalStress = (sigma1 + sigma3) / 2.0f; // Center of Mohr circle
            shearStress = (sigma1 - sigma3) / 2.0f;  // Radius of Mohr circle

            // Calculate failure envelope parameters
            float phi = frictionAngle * (float)Math.PI / 180.0f;
            float tanPhi = (float)Math.Tan(phi);

            // Calculate distance from stress state to failure envelope
            float maxShear = cohesion + normalStress * tanPhi;
            float failureRatio = shearStress / maxShear;

            // Update UI when approaching failure
            if (failureRatio > 0.9f && !fastSimulationMode)
            {
                try
                {
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(() => {
                            progressLabel.Text = $"WARNING: Approaching failure ({failureRatio:P0})";
                            progressLabel.ForeColor = Color.Red;
                        }));
                    }
                    else
                    {
                        progressLabel.Text = $"WARNING: Approaching failure ({failureRatio:P0})";
                        progressLabel.ForeColor = Color.Red;
                    }
                }
                catch { /* Ignore UI update errors */ }
            }
        }
        private void UpdateDiagramsForm()
{
    if (diagramsForm == null || diagramsForm.IsDisposed)
        return;

    // Current stress in MPa
    float currentStressMPa = stressStrainCurve.Count > 0 ? stressStrainCurve.Last().Y / 10.0f : 0;

    // Get confining pressures
    float confiningPressureMPa = minPressure / 1000.0f; // kPa to MPa
    float maxPressureMPa = maxPressure / 1000.0f; // kPa to MPa

    // Calculate pore pressure
    float porePressureMPa = CalculatePorePressure(currentStrain);

    // Calculate principal stresses for triaxial state
    float sigma3 = confiningPressureMPa;
    float sigma1 = sigma3 + currentStressMPa;

    // Calculate effective stresses accounting for pore pressure
    float biotCoeff = Biot(porosity);
    float effSigma3 = sigma3 - porePressureMPa * biotCoeff;
    float effSigma1 = sigma1 - porePressureMPa * biotCoeff;

    // Calculate volumetric strain
    float volStrain = currentStrain * (1.0f - 2.0f * poissonRatio);

    // Calculate permeability change ratio
    float initialPerm = 0.01f; // Default initial permeability
    float permRatio = permeability / initialPerm;

    // Calculate elastic energy stored
    float elasticEnergyVal = elasticEnergy > 0 ? elasticEnergy : 0.5f * currentStressMPa * currentStrain;

    // Calculate plastic energy dissipated
    float plasticEnergyVal = plasticEnergy;

    // Determine if failure occurred
    bool failureOccurred = elementStresses.Any(kv => kv.Value < 0) || 
                          (stressStrainCurve.Count > 10 && 
                           stressStrainCurve.Last().Y < stressStrainCurve.Max(p => p.Y) * 0.9);

    // Get peak stress information
    float peakStress = 0;
    float strainAtPeak = 0;
    
    if (stressStrainCurve.Count > 0) {
        // Find peak stress
        int maxY = stressStrainCurve.Max(p => p.Y);
        int peakIndex = stressStrainCurve.FindIndex(p => p.Y == maxY);
        
        if (peakIndex >= 0) {
            peakStress = stressStrainCurve[peakIndex].Y / 10.0f;
            strainAtPeak = stressStrainCurve[peakIndex].X / 10.0f;
        }
    }

    // Call the enhanced data update method with all parameters
    diagramsForm.UpdateEnhancedData(
        stressStrainCurve,         // Stress-strain curve
        currentStrain,             // Current strain value
        currentStressMPa,          // Current stress in MPa
        cohesion,                  // Cohesion
        frictionAngle,             // Friction angle
        normalStress,              // Normal stress (center of Mohr circle)
        shearStress,               // Shear stress (radius of Mohr circle)
        bulkDensity,               // Bulk density
        porosity,                  // Current porosity
        minPressure / 1000.0f,     // Confining pressure in MPa
        maxPressure / 1000.0f,     // Max pressure in MPa
        yieldStrength,             // Yield strength
        brittleStrength,           // Brittle strength
        isElasticEnabled,          // Elastic behavior enabled
        isPlasticEnabled,          // Plastic behavior enabled
        isBrittleEnabled,          // Brittle behavior enabled
        simulationRunning,         // Simulation running status
        porePressureMPa,           // Pore pressure in MPa
        effSigma1,                 // Effective major principal stress
        effSigma3,                 // Effective minor principal stress
        permeability,              // Current permeability
        permRatio,                 // Permeability change ratio
        volStrain,                 // Volumetric strain
        elasticEnergyVal,          // Elastic energy stored
        plasticEnergyVal,          // Plastic energy dissipated
        failureOccurred,           // Failure state
        failureOccurred ? 100.0f : 0.0f,  // Percent of failure criterion
        peakStress,                // Peak stress reached
        strainAtPeak               // Strain at peak stress
    );
}
        /// <summary>
        /// Sets <paramref name="control"/>'s <see cref="KryptonNumericUpDown.Value"/>
        /// while automatically clamping the input to the control’s <c>Minimum</c> /
        /// <c>Maximum</c> range and rounding to the declared <c>DecimalPlaces</c>.
        /// </summary>

        private void SafeSetNumericValue(KryptonNumericUpDown ctrl, decimal value)
        {
            if (ctrl == null) return;                               // UI not ready yet

            decimal v = value;
            v = Math.Max(ctrl.Minimum, Math.Min(ctrl.Maximum, v));  // clamp
            v = Math.Round(v, ctrl.DecimalPlaces, MidpointRounding.AwayFromZero);

            if (ctrl.Value != v) ctrl.Value = v;
        }
        /// <summary>Return ctrl.Maximum or a huge number when the control is still null.</summary>
        private float CtrlMax(KryptonNumericUpDown ctrl) =>
            ctrl != null ? (float)ctrl.Maximum : float.MaxValue;
        private float CalculateStress(float strain)
        {
            // Initialize thread-safe collections for parallel processing
            ConcurrentDictionary<int, float> concurrentElementStresses = new ConcurrentDictionary<int, float>();
            ConcurrentDictionary<int, Vector3> concurrentElementStrains = new ConcurrentDictionary<int, Vector3>();

            // Use thread-safe counter for total stress accumulation
            double totalStressSum = 0;
            double totalVolumetricStrain = 0;

            // Get confining pressures in MPa
            float confiningPressure = minPressure / 1000f; // kPa to MPa
            float axialPressure = maxPressure / 1000f; // kPa to MPa

            // Current pore pressure based on permeability and deformation
            float porePressure = CalculatePorePressure(strain);

            // Process all tetrahedral elements in parallel
            Parallel.For(0, tetrahedralElements.Count,
                // Initialize local thread state
                () => new { LocalStressSum = 0.0f, LocalVolStrain = 0.0f },
                // Process each element and update local state
                (i, loop, local) => {
                    TetrahedralElement element = tetrahedralElements[i];

                    // Calculate average density and properties for this element
                    float elementDensity = GetElementDensity(element);
                    float densityRatio = isDensityCalibrated && elementDensity > 0
                        ? elementDensity / bulkDensity
                        : 1.0f;

                    // Scale modulus using an empirical power law relationship (realistic for rocks)
                    // E ∝ ρ^n where n~2-3 for most rocks
                    float elementYoungModulus = youngModulus * (float)Math.Pow(densityRatio, 2.5);
                    float elementPoissonRatio = Math.Min(poissonRatio * (float)Math.Pow(densityRatio, -0.2), 0.49f);

                    // Calculate element-specific strength parameters
                    float elementYieldStrength = yieldStrength * (float)Math.Pow(densityRatio, 1.5);
                    float elementBrittleStrength = brittleStrength * (float)Math.Pow(densityRatio, 1.2);
                    float elementCohesion = cohesion * (float)Math.Pow(densityRatio, 1.3);
                    float elementFrictionAngle = frictionAngle * (float)Math.Min(1.2f, Math.Max(0.8f, densityRatio));

                    // Strain thresholds
                    float elementYieldStrain = elementYieldStrength / elementYoungModulus;
                    float elementBrittleStrain = elementBrittleStrength / elementYoungModulus;

                    // Calculate element bulk modulus
                    float elementBulkModulus = elementYoungModulus / (3f * (1f - 2f * elementPoissonRatio));

                    // Calculate element porosity
                    float elementPorosity = Math.Max(0.01f, Math.Min(0.5f, porosity * (2f - densityRatio)));

                    // Calculate effective stress (accounting for pore pressure)
                    float effectiveConfining = Math.Max(0.01f, confiningPressure - porePressure * Biot(elementPorosity));

                    // Principal stresses array (σ1, σ2, σ3)
                    Vector3 principalStresses = new Vector3();
                    Vector3 principalStrains = new Vector3();

                    // Set initial stress state based on confining pressure
                    principalStresses.X = effectiveConfining; // σ3
                    principalStresses.Y = effectiveConfining; // σ2
                    principalStresses.Z = effectiveConfining; // σ1

                    // Apply strain along the loading direction
                    switch (selectedDirection)
                    {
                        case SimulationDirection.X:
                            principalStrains.X = strain;
                            principalStrains.Y = -strain * elementPoissonRatio;
                            principalStrains.Z = -strain * elementPoissonRatio;
                            break;
                        case SimulationDirection.Y:
                            principalStrains.X = -strain * elementPoissonRatio;
                            principalStrains.Y = strain;
                            principalStrains.Z = -strain * elementPoissonRatio;
                            break;
                        default: // Z
                            principalStrains.X = -strain * elementPoissonRatio;
                            principalStrains.Y = -strain * elementPoissonRatio;
                            principalStrains.Z = strain;
                            break;
                    }

                    // Calculate volumetric strain
                    float volumetricStrain = principalStrains.X + principalStrains.Y + principalStrains.Z;

                    // Calculate stress increment based on material behavior models
                    float stressIncrement = 0;
                    bool elementFailure = false;

                    // Elastic behavior
                    if (isElasticEnabled)
                    {
                        if (strain <= elementYieldStrain || !isPlasticEnabled)
                        {
                            // Linear elastic relationship
                            stressIncrement = elementYoungModulus * strain;
                        }
                        else
                        {
                            // Elastic contribution up to yield
                            stressIncrement = elementYieldStrength;
                        }
                    }

                    // Plastic behavior
                    if (isPlasticEnabled && strain > elementYieldStrain)
                    {
                        float plasticStrain = strain - elementYieldStrain;

                        // Hardening based on Ramberg-Osgood model
                        float hardeningExponent = 0.1f + 0.2f * densityRatio;
                        float plasticModulus = elementYoungModulus * 0.05f * densityRatio;

                        float plasticComponent = plasticModulus * (float)Math.Pow(plasticStrain, hardeningExponent);

                        // Combine with elastic component if enabled
                        if (isElasticEnabled)
                        {
                            stressIncrement += plasticComponent;
                        }
                        else
                        {
                            stressIncrement = elementYieldStrength + plasticComponent;
                        }
                    }

                    // Brittle behavior
                    if (isBrittleEnabled && strain > elementBrittleStrain)
                    {
                        // Calculate post-failure residual strength
                        float residualFactor = Math.Max(0.05f, 0.2f + 0.3f / densityRatio);
                        float postFailureStrain = strain - elementBrittleStrain;
                        float decayRate = 20.0f + 10.0f * (1.0f - densityRatio); // Denser materials fracture more abruptly
                        float decayFactor = (float)Math.Exp(-decayRate * postFailureStrain);

                        float residualStrength = elementBrittleStrength * residualFactor;
                        float brittleStress = residualStrength + (elementBrittleStrength - residualStrength) * decayFactor;

                        // Mark as failed for stress redistribution
                        elementFailure = true;

                        // Apply brittle stress limit
                        if (!isElasticEnabled && !isPlasticEnabled)
                        {
                            stressIncrement = brittleStress;
                        }
                        else
                        {
                            stressIncrement = Math.Min(stressIncrement, brittleStress);
                        }
                    }

                    // Update principal stress based on loading direction
                    switch (selectedDirection)
                    {
                        case SimulationDirection.X:
                            principalStresses.X = effectiveConfining + stressIncrement;
                            break;
                        case SimulationDirection.Y:
                            principalStresses.Y = effectiveConfining + stressIncrement;
                            break;
                        default: // Z
                            principalStresses.Z = effectiveConfining + stressIncrement;
                            break;
                    }

                    // Check Mohr-Coulomb failure criterion
                    if (!elementFailure && CheckMohrCoulombFailure(principalStresses, elementCohesion, elementFrictionAngle))
                    {
                        elementFailure = true;

                        // Calculate post-failure stress based on residual strength
                        float frictionRad = elementFrictionAngle * (float)Math.PI / 180.0f;
                        float sinPhi = (float)Math.Sin(frictionRad);
                        float residualRatio = (1.0f - sinPhi) / (1.0f + sinPhi);

                        // Apply residual strength ratio to differential stress
                        float sigma3 = Math.Min(principalStresses.X, Math.Min(principalStresses.Y, principalStresses.Z));
                        float sigmaMax = Math.Max(principalStresses.X, Math.Max(principalStresses.Y, principalStresses.Z));
                        float differentialStress = sigmaMax - sigma3;
                        float residualDifferential = differentialStress * residualRatio;

                        // Replace the maximum principal stress
                        switch (selectedDirection)
                        {
                            case SimulationDirection.X:
                                principalStresses.X = sigma3 + residualDifferential;
                                break;
                            case SimulationDirection.Y:
                                principalStresses.Y = sigma3 + residualDifferential;
                                break;
                            default: // Z
                                principalStresses.Z = sigma3 + residualDifferential;
                                break;
                        }

                        // Recalculate stress increment
                        switch (selectedDirection)
                        {
                            case SimulationDirection.X:
                                stressIncrement = principalStresses.X - effectiveConfining;
                                break;
                            case SimulationDirection.Y:
                                stressIncrement = principalStresses.Y - effectiveConfining;
                                break;
                            default: // Z
                                stressIncrement = principalStresses.Z - effectiveConfining;
                                break;
                        }
                    }

                    // Store element data in concurrent collections
                    concurrentElementStresses[i] = stressIncrement;
                    concurrentElementStrains[i] = principalStrains;

                    // Update local sums
                    return new
                    {
                        LocalStressSum = local.LocalStressSum + stressIncrement,
                        LocalVolStrain = local.LocalVolStrain + volumetricStrain
                    };
                },
                // Combine all local results
                (local) => {
                    Interlocked.Exchange(ref totalStressSum, totalStressSum + local.LocalStressSum);
                    Interlocked.Exchange(ref totalVolumetricStrain, totalVolumetricStrain + local.LocalVolStrain);
                }
            );

            // Update instance fields with calculated values
            elementStresses.Clear();
            foreach (var pair in concurrentElementStresses)
            {
                elementStresses[pair.Key] = pair.Value;
            }

            // Calculate average volumetric strain for permeability updates
            float avgVolumetricStrain = (float)(totalVolumetricStrain / tetrahedralElements.Count);
            UpdatePermeabilityFromStrain(avgVolumetricStrain);

            // Calculate average stress
            float averageStress = tetrahedralElements.Count > 0
                ? (float)(totalStressSum / tetrahedralElements.Count)
                : 0;

            return averageStress;
        }
        private float GetElementDensity(TetrahedralElement element)
        {
            if (!isDensityCalibrated || element.Vertices.Length != 4)
                return bulkDensity;

            // Calculate average density from vertices
            float sumDensity = 0;
            int validVertices = 0;

            foreach (int vertexIndex in element.Vertices)
            {
                if (vertexIndex < densityValues.Count)
                {
                    sumDensity += densityValues[vertexIndex];
                    validVertices++;
                }
            }

            return validVertices > 0 ? sumDensity / validVertices : bulkDensity;
        }
        private bool CheckMohrCoulombFailure(Vector3 principalStresses, float elCohesion, float elFrictionAngle)
        {
            // Find minimum and maximum principal stresses
            float sigma1 = Math.Max(principalStresses.X, Math.Max(principalStresses.Y, principalStresses.Z));
            float sigma3 = Math.Min(principalStresses.X, Math.Min(principalStresses.Y, principalStresses.Z));

            // Convert friction angle to radians
            float frictionRad = elFrictionAngle * (float)Math.PI / 180.0f;
            float sinPhi = (float)Math.Sin(frictionRad);
            float cosPhi = (float)Math.Cos(frictionRad);

            // Calculate Mohr-Coulomb criterion
            // Failure when: (σ1 - σ3) ≥ 2c·cos(φ) + (σ1 + σ3)·sin(φ)
            float leftSide = sigma1 - sigma3;
            float rightSide = 2.0f * elCohesion * cosPhi + (sigma1 + sigma3) * sinPhi;

            // Tensile cutoff check
            float tensileStrength = elCohesion / (float)Math.Tan(frictionRad);
            bool tensileFailure = sigma3 < -tensileStrength;

            // Return true if failure criterion is met
            return leftSide >= rightSide || tensileFailure;
        }
        private float Biot(float poro)
        {
            // Biot coefficient: α = 1 - K/Ks
            // where K is bulk modulus, Ks is solid grain bulk modulus
            float grainBulkModulus = 36000.0f; // Typical for quartz, MPa
            return 1.0f - (bulkModulus / grainBulkModulus);
        }
        private float CalculatePorePressure(float strain)
        {
            // Initial pore pressure (could be from user input)
            float initialPorePressure = 0.1f; // MPa

            // Modify based on volumetric strain
            float avgPorosity = porosity;
            float compressibility = 1.0f / (bulkModulus * 10.0f); // Convert to MPa⁻¹

            // Estimate volumetric strain - simplified approach
            float volumetricStrain = strain * (1.0f - 2.0f * poissonRatio);

            // Skempton's B parameter - relates mean stress to pore pressure
            float skemptonB = CalculateSkemptonB(avgPorosity);

            // Pore pressure buildup due to undrained conditions inversely related to permeability
            float drainageFactor = Math.Max(0.01f, Math.Min(1.0f, permeability * 10.0f));
            float undrained = (1.0f - drainageFactor);

            // Calculate pore pressure buildup
            float porePressureChange = -volumetricStrain * bulkModulus * skemptonB * undrained;

            // Return total pore pressure
            return initialPorePressure + porePressureChange;
        }
        private float CalculateSkemptonB(float poro)
        {
            // Skempton's B parameter calculation
            // B = 1 / (1 + n·Kf/K), where:
            // n is porosity, Kf is fluid bulk modulus, K is rock bulk modulus
            float fluidBulkModulus = 2200.0f; // Water, MPa
            float skemptonB = 1.0f / (1.0f + poro * fluidBulkModulus / bulkModulus);

            // Clamp to physical range
            return Math.Max(0.01f, Math.Min(0.99f, skemptonB));
        }
        private void UpdatePermeabilityFromStrain(float volumetricStrain)
        {
            // Kozeny-Carman permeability change with porosity
            // New porosity from volumetric strain
            float newPorosity = porosity * (1.0f - volumetricStrain);
            newPorosity = Math.Max(0.01f, Math.Min(0.5f, newPorosity));

            // Update the porosity value
            porosity = newPorosity;

            // Update permeability using Kozeny-Carman relationship
            // k ∝ φ³/(1-φ)²
            float oldPermeabilityFactor = (float)(Math.Pow(porosity, 3.0) / Math.Pow(1.0 - porosity, 2.0));
            float newPermeabilityFactor = (float)(Math.Pow(newPorosity, 3.0) / Math.Pow(1.0 - newPorosity, 2.0));

            // Relative change
            float permeabilityRatio = newPermeabilityFactor / oldPermeabilityFactor;

            // Apply change to permeability
            permeability *= permeabilityRatio;
            permeability = Math.Max(0.0001f, Math.Min(1000.0f, permeability));

            // Update UI if we're on the UI thread
            if (!fastSimulationMode)
            {
                try
                {
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(() => {
                            if (numPorosity != null && !numPorosity.IsDisposed)
                                numPorosity.Value = (decimal)Math.Max(numPorosity.Minimum,
                                                    Math.Min(numPorosity.Maximum, (decimal)porosity));

                            if (numPermeability != null && !numPermeability.IsDisposed)
                                numPermeability.Value = (decimal)Math.Max(numPermeability.Minimum,
                                                      Math.Min(numPermeability.Maximum, (decimal)permeability));
                        }));
                    }
                    else
                    {
                        if (numPorosity != null && !numPorosity.IsDisposed)
                            numPorosity.Value = (decimal)Math.Max(numPorosity.Minimum,
                                                Math.Min(numPorosity.Maximum, (decimal)porosity));

                        if (numPermeability != null && !numPermeability.IsDisposed)
                            numPermeability.Value = (decimal)Math.Max(numPermeability.Minimum,
                                                  Math.Min(numPermeability.Maximum, (decimal)permeability));
                    }
                }
                catch { /* Ignore UI update errors */ }
            }
        }
        private void UpdateDeformation(float strain, float stress)
        {
            // Create a copy of vertices if needed
            if (deformedVertices == null || deformedVertices.Count != vertices.Count)
            {
                ScaleMeshForDisplay();
            }

            // Get current pore pressure
            float porePressure = CalculatePorePressure(strain);
            float confiningPressure = minPressure / 1000f; // kPa to MPa

            // Calculate effective stresses
            float effectiveConfining = Math.Max(0.01f, confiningPressure - porePressure * Biot(porosity));

            // Create thread-local storage for vertex positions
            Vector3[] newPositions = new Vector3[vertices.Count];

            // Copy initial vertex positions
            for (int i = 0; i < vertices.Count; i++)
            {
                newPositions[i] = vertices[i];
            }

            // Process all vertices in parallel
            Parallel.For(0, vertices.Count, i =>
            {
                // Get original vertex position
                Vector3 vertex = newPositions[i];

                // Get vertex density for property scaling
                float vertexDensity = (i < densityValues.Count) ? densityValues[i] : bulkDensity;
                float densityRatio = isDensityCalibrated && vertexDensity > 0
                                    ? vertexDensity / bulkDensity
                                    : 1.0f;

                // Calculate effective Poisson's ratio based on porosity and density
                float grainDensity = 2650.0f; // kg/m³ (typical for quartz/feldspar)
                float vertexPorosity = Math.Max(0.01f, Math.Min(0.5f, 1.0f - (vertexDensity / grainDensity)));

                // Density-dependent Poisson ratio (higher porosity = higher Poisson's ratio)
                float effectivePoissonRatio = Math.Min(
                    poissonRatio * (1.0f + 0.3f * vertexPorosity),
                    0.49f
                );

                // Enhanced Young's modulus relation with density
                float effectiveYoungModulus = youngModulus * (float)Math.Pow(densityRatio, 2.5);

                // Calculate anisotropic strain tensor
                float axialStrain = strain;
                float lateralStrain = -strain * effectivePoissonRatio;

                // Coordinate transformations for different loading directions
                Vector3 strainVector = new Vector3();
                switch (selectedDirection)
                {
                    case SimulationDirection.X:
                        strainVector.X = axialStrain;
                        strainVector.Y = lateralStrain;
                        strainVector.Z = lateralStrain;
                        break;
                    case SimulationDirection.Y:
                        strainVector.X = lateralStrain;
                        strainVector.Y = axialStrain;
                        strainVector.Z = lateralStrain;
                        break;
                    default: // Z
                        strainVector.X = lateralStrain;
                        strainVector.Y = lateralStrain;
                        strainVector.Z = axialStrain;
                        break;
                }

                // Apply deformation based on strain tensor
                Vector3 origPos = vertices[i];
                Vector3 deformedPos = new Vector3(
                    origPos.X * (1f + strainVector.X),
                    origPos.Y * (1f + strainVector.Y),
                    origPos.Z * (1f + strainVector.Z)
                );

                // Apply heterogeneous deformation based on local material properties
                if (isDensityCalibrated && i < densityValues.Count)
                {
                    // Calculate stiffness ratio - softer materials deform more
                    float stiffnessRatio = (bulkDensity / vertexDensity);
                    stiffnessRatio = Math.Max(0.5f, Math.Min(2.0f, stiffnessRatio));

                    // Apply differential deformation
                    Vector3 deformationVector = deformedPos - origPos;
                    Vector3 scaledDeformation = deformationVector * stiffnessRatio;
                    deformedPos = origPos + scaledDeformation;
                }

                // Apply brittle fracturing effects if applicable
                if (isBrittleEnabled && strain > brittleStrength / youngModulus)
                {
                    // Check if this vertex belongs to failed elements
                    bool inFailedElement = false;
                    float maxStressFactor = 0.0f;

                    // Find elements containing this vertex
                    foreach (var tetra in tetrahedralElements)
                    {
                        if (Array.IndexOf(tetra.Vertices, i) >= 0)
                        {
                            int tetraIndex = tetrahedralElements.IndexOf(tetra);
                            if (elementStresses.TryGetValue(tetraIndex, out float elementStress))
                            {
                                // Check if element exceeds brittle strength
                                float stressFactor = elementStress / brittleStrength;
                                if (stressFactor > 0.95f)
                                {
                                    inFailedElement = true;
                                    maxStressFactor = Math.Max(maxStressFactor, stressFactor);
                                }
                            }
                        }
                    }

                    // Add randomized fracture displacement to failed elements
                    if (inFailedElement)
                    {
                        // Use a deterministic seed for reproducibility
                        int seed = (int)(currentStrain * 10000) + i;
                        Random rand = new Random(seed);

                        // Displacement magnitude increases with stress and strain beyond failure
                        float excessStrain = strain - (brittleStrength / youngModulus);
                        float displacementMagnitude = 0.05f * maxStressFactor * excessStrain;

                        // Create displacement with preferential direction based on loading
                        Vector3 fracDisplacement = new Vector3(
                            (float)(rand.NextDouble() - 0.5) * displacementMagnitude,
                            (float)(rand.NextDouble() - 0.5) * displacementMagnitude,
                            (float)(rand.NextDouble() - 0.5) * displacementMagnitude
                        );

                        // Bias displacement to open perpendicular to maximum stress direction
                        switch (selectedDirection)
                        {
                            case SimulationDirection.X:
                                fracDisplacement.X *= 0.1f; // Less displacement in loading direction
                                break;
                            case SimulationDirection.Y:
                                fracDisplacement.Y *= 0.1f;
                                break;
                            default: // Z
                                fracDisplacement.Z *= 0.1f;
                                break;
                        }

                        // Apply fracture displacement
                        deformedPos += fracDisplacement;
                    }
                }

                // Apply pore pressure effects if permeability is low
                if (permeability < 0.1f && porePressure > 0.5f)
                {
                    float swellingFactor = Math.Max(0.0f, porePressure - effectiveConfining) * 0.001f;

                    // Isotropic expansion proportional to pore pressure
                    deformedPos.X *= (1f + swellingFactor);
                    deformedPos.Y *= (1f + swellingFactor);
                    deformedPos.Z *= (1f + swellingFactor);
                }

                // Save updated position
                newPositions[i] = deformedPos;
            });

            // Update the deformed vertices list
            lock (deformedVertices)
            {
                deformedVertices.Clear();
                for (int i = 0; i < newPositions.Length; i++)
                {
                    deformedVertices.Add(newPositions[i]);
                }
            }

            // Recalculate normals for proper lighting
            RecalculateNormals();

            // Update OpenGL display
            if (buffersInitialized)
            {
                if (highDetailList != 0)
                {
                    GL.DeleteLists(highDetailList, 1);
                    GL.DeleteLists(lowDetailList, 1);
                    highDetailList = 0;
                    lowDetailList = 0;
                }

                ReleaseVBOs();

                // Force display list recreation
                if (meshDisplayList != 0)
                {
                    GL.DeleteLists(meshDisplayList, 1);
                    meshDisplayList = 0;
                }
            }
        }
        private void RecalculateNormals()
        {
            int vertexCount = deformedVertices.Count;

            // Create thread-safe accumulators for normals
            Vector3[] tempNormals = new Vector3[vertexCount];

            // Reset normals array first
            for (int i = 0; i < normals.Count; i++)
            {
                normals[i] = Vector3.Zero;
            }

            // Calculate face normals and accumulate to vertices - in parallel
            Parallel.For(0, indices.Count / 3, faceIndex =>
            {
                int i = faceIndex * 3;

                // Skip if out of bounds
                if (i + 2 >= indices.Count)
                    return;

                int i1 = indices[i];
                int i2 = indices[i + 1];
                int i3 = indices[i + 2];

                // Skip if any vertex index is out of bounds
                if (i1 >= deformedVertices.Count ||
                    i2 >= deformedVertices.Count ||
                    i3 >= deformedVertices.Count)
                    return;

                // Get vertex positions
                Vector3 v1 = deformedVertices[i1];
                Vector3 v2 = deformedVertices[i2];
                Vector3 v3 = deformedVertices[i3];

                // Calculate face normal
                Vector3 edge1 = v2 - v1;
                Vector3 edge2 = v3 - v1;
                Vector3 faceNormal = Vector3.Cross(edge1, edge2);

                // Skip degenerate triangles
                if (faceNormal.LengthSquared < 0.000001f)
                    return;

                // Normalize the face normal
                float length = faceNormal.Length;
                if (length > 0.000001f)
                    faceNormal = faceNormal / length;

                // Add weighted face normal to vertex normals (using Interlocked methods for thread safety)
                // Weight by face area for more accurate normals on irregular meshes
                float area = length * 0.5f;

                // Accumulate to thread-local arrays
                Vector3 weightedNormal = faceNormal * area;

                // Using lock per vertex is more granular than a global lock
                if (i1 < tempNormals.Length)
                    lock (tempNormals)
                        tempNormals[i1] += weightedNormal;

                if (i2 < tempNormals.Length)
                    lock (tempNormals)
                        tempNormals[i2] += weightedNormal;

                if (i3 < tempNormals.Length)
                    lock (tempNormals)
                        tempNormals[i3] += weightedNormal;
            });

            // Transfer accumulated normals to the main normals array
            for (int i = 0; i < vertexCount && i < normals.Count; i++)
            {
                normals[i] = tempNormals[i];
            }

            // Normalize all vertex normals in parallel
            Parallel.For(0, vertexCount, i =>
            {
                // Skip if out of bounds
                if (i >= normals.Count)
                    return;

                if (normals[i].LengthSquared > 0.000001f)
                {
                    normals[i].Normalize();
                }
                else
                {
                    // Default normal for degenerate cases
                    normals[i] = new Vector3(0, 1, 0);
                }
            });

            // Update normal buffer if using VBOs
            if (buffersInitialized)
            {
                float[] normalData = new float[normals.Count * 3];
                for (int i = 0; i < normals.Count; i++)
                {
                    normalData[i * 3] = normals[i].X;
                    normalData[i * 3 + 1] = normals[i].Y;
                    normalData[i * 3 + 2] = normals[i].Z;
                }

                GL.BindBuffer(BufferTarget.ArrayBuffer, normalBufferId);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(normalData.Length * sizeof(float)),
                              normalData, BufferUsageHint.StaticDraw);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            }
        }
        private void ExtractVolumeData(Material material)
        {
            if (material == null)
            {
                Logger.Log("[TriaxialSimulationForm] Error: Null material in ExtractVolumeData");
                return;
            }

            // Get volume data from MainForm
            labelVolume = mainForm.volumeLabels;

            if (labelVolume == null)
            {
                Logger.Log("[TriaxialSimulationForm] Error: Null label volume in ExtractVolumeData");
                return;
            }

            // Get dimensions
            width = LabelVolumeHelper.GetWidth(labelVolume);
            height = LabelVolumeHelper.GetHeight(labelVolume);
            depth = LabelVolumeHelper.GetDepth(labelVolume);

            Logger.Log($"[TriaxialSimulationForm] Volume dimensions: {width}x{height}x{depth}");

            // Create a byte array to store density values
            densityVolume = new byte[width, height, depth];

            // Extract density data from grayscale volume for selected material
            byte materialID = material.ID;

            Logger.Log($"[TriaxialSimulationForm] Extracting volume data for material ID {materialID}, name: {material.Name}");

            // Count material voxels for validation
            int materialVoxelCount = 0;

            // Use parallel processing to speed up extraction
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Check if this voxel belongs to the selected material
                        if (LabelVolumeHelper.GetLabel(labelVolume, x, y, z) == materialID)
                        {
                            // Store the grayscale value as a proxy for density
                            densityVolume[x, y, z] = mainForm.volumeData[x, y, z];
                            Interlocked.Increment(ref materialVoxelCount);
                        }
                        else
                        {
                            // Not part of the material
                            densityVolume[x, y, z] = 0;
                        }
                    }
                }
            });

            Logger.Log($"[TriaxialSimulationForm] Found {materialVoxelCount} voxels of material ID {materialID}");
        }
        /// <summary>
        /// Build a watertight surface mesh of the selected material.
        /// – samplingRate ≥ 1 : 1 = full resolution, 2 = every second voxel, …  
        /// – worker        : BackgroundWorker for UI progress/cancel
        /// 
        /// Global lists filled:
        ///     vertices, normals, indices, densityValues, tetrahedralElements
        /// Flags set/updated:
        ///     minDensity, maxDensity, meshGenerationComplete (in caller)
        /// </summary>
        /// 
        private struct Dir
        {
            public int dx, dy, dz;
            public Vector3 n;
            public Dir(int dx, int dy, int dz, Vector3 n) { this.dx = dx; this.dy = dy; this.dz = dz; this.n = n; }
        }
        private void GenerateMesh(int samplingRate, BackgroundWorker worker)
        {
            /* ---------- 0. reset ---------- */
            vertices.Clear();
            normals.Clear();
            indices.Clear();
            densityValues.Clear();
            tetrahedralElements.Clear();
            minDensity = float.MaxValue;
            maxDensity = float.MinValue;

            /* ---------- 1. collect voxel centres that belong to the specimen ---------- */
            var voxels = new List<Tuple<int, int, int>>();

            float vSize = (float)pixelSize;                 // isotropic voxel size
            float cx = width * 0.5f;
            float cy = height * 0.5f;
            float cz = depth * 0.5f;

            float radius = Math.Min(Math.Min(width, height), depth) * vSize * 0.30f;
            float halfLen;
            switch (selectedDirection)
            {
                case SimulationDirection.X: halfLen = width * vSize * 0.40f; break;
                case SimulationDirection.Y: halfLen = height * vSize * 0.40f; break;
                default: halfLen = depth * vSize * 0.40f; break; // Z
            }

            for (int z = 0; z < depth; z += samplingRate)
            {
                float pz = (z - cz) * vSize;
                for (int y = 0; y < height; y += samplingRate)
                {
                    float py = (y - cy) * vSize;
                    for (int x = 0; x < width; x += samplingRate)
                    {
                        if (densityVolume[x, y, z] == 0) continue;

                            voxels.Add(Tuple.Create(x, y, z));
                    }
                }
                if (z % Math.Max(1, depth / 10) == 0)
                    worker.ReportProgress(10 + z * 30 / depth, $"Scanning… {z}/{depth}");
                if (worker.CancellationPending) return;
            }

            if (voxels.Count == 0) return;
            worker.ReportProgress(40, $"Core voxels: {voxels.Count}");

            /* ---------- 2. build faces – every exposed side gets its own quad ---------- */
            Dir[] dirs =
            {
        new Dir( 1, 0, 0, new Vector3( 1, 0, 0)),
        new Dir(-1, 0, 0, new Vector3(-1, 0, 0)),
        new Dir( 0, 1, 0, new Vector3( 0, 1, 0)),
        new Dir( 0,-1, 0, new Vector3( 0,-1, 0)),
        new Dir( 0, 0, 1, new Vector3( 0, 0, 1)),
        new Dir( 0, 0,-1, new Vector3( 0, 0,-1))
    };

            /* quick lookup so we know if the neighbour is inside material */
            var voxelSet = new HashSet<Tuple<int, int, int>>(voxels);

            float half = vSize * samplingRate * 0.5f;       // half-edge of a sampled “supervoxel”

            foreach (var vxc in voxels)
            {
                int x = vxc.Item1, y = vxc.Item2, z = vxc.Item3;
                float dens = densityVolume[x, y, z] / 255f;
                minDensity = Math.Min(minDensity, dens);
                maxDensity = Math.Max(maxDensity, dens);
                /* voxel centre in physical space */
                float cX = (x - cx) * vSize;
                float cY = (y - cy) * vSize;
                float cZ = (z - cz) * vSize;

                foreach (Dir d in dirs)
                {
                    var nb = Tuple.Create(x + d.dx, y + d.dy, z + d.dz);
                    if (voxelSet.Contains(nb)) continue;    // neighbour also inside → hidden face

                    /* four corners of the face */
                    Vector3 p1, p2, p3, p4;     // order: CCW seen from outside
                    if (d.dx != 0)             /* ±X face */
                    {
                        float sign = d.dx > 0 ? +half : -half;
                        p1 = new Vector3(cX + sign, cY - half, cZ - half);
                        p2 = new Vector3(cX + sign, cY + half, cZ - half);
                        p3 = new Vector3(cX + sign, cY + half, cZ + half);
                        p4 = new Vector3(cX + sign, cY - half, cZ + half);
                    }
                    else if (d.dy != 0)        /* ±Y face */
                    {
                        float sign = d.dy > 0 ? +half : -half;
                        p1 = new Vector3(cX - half, cY + sign, cZ - half);
                        p2 = new Vector3(cX + half, cY + sign, cZ - half);
                        p3 = new Vector3(cX + half, cY + sign, cZ + half);
                        p4 = new Vector3(cX - half, cY + sign, cZ + half);
                    }
                    else                       /* ±Z face */
                    {
                        float sign = d.dz > 0 ? +half : -half;
                        p1 = new Vector3(cX - half, cY - half, cZ + sign);
                        p2 = new Vector3(cX + half, cY - half, cZ + sign);
                        p3 = new Vector3(cX + half, cY + half, cZ + sign);
                        p4 = new Vector3(cX - half, cY + half, cZ + sign);
                    }

                    /* add vertices & attributes */
                    int i1 = vertices.Count; vertices.Add(p1);
                    int i2 = vertices.Count; vertices.Add(p2);
                    int i3 = vertices.Count; vertices.Add(p3);
                    int i4 = vertices.Count; vertices.Add(p4);

                    normals.Add(d.n); normals.Add(d.n); normals.Add(d.n); normals.Add(d.n);

                    densityValues.Add(dens); densityValues.Add(dens);
                    densityValues.Add(dens); densityValues.Add(dens);

                    /* two triangles */
                    indices.Add(i1); indices.Add(i2); indices.Add(i3);
                    indices.Add(i3); indices.Add(i4); indices.Add(i1);
                }
            }

            worker.ReportProgress(90, $"Vertices {vertices.Count}, faces {indices.Count / 3}");

            /* ---------- 3. (unchanged) lightweight tetrahedral fill ---------- */
            int made = 0, limit = 5000, stride = samplingRate;
            foreach (var vxc in voxels)
            {
                if (made >= limit) break;
                int x = vxc.Item1, y = vxc.Item2, z = vxc.Item3;

                var v000 = Tuple.Create(x, y, z);
                var v100 = Tuple.Create(x + stride, y, z);
                var v010 = Tuple.Create(x, y + stride, z);
                var v001 = Tuple.Create(x, y, z + stride);
                var v111 = Tuple.Create(x + stride, y + stride, z + stride);

                if (!voxelSet.Contains(v100) ||
                    !voxelSet.Contains(v010) ||
                    !voxelSet.Contains(v001) ||
                    !voxelSet.Contains(v111))
                    continue;

                /* indices for the *centre* vertices, not the face vertices – use voxel order */
                int i000 = vertices.Count;    // add voxel centre as a tet node
                vertices.Add(new Vector3((x - cx) * vSize, (y - cy) * vSize, (z - cz) * vSize));
                normals.Add(Vector3.UnitY); densityValues.Add(0);

                int i100 = vertices.Count;
                vertices.Add(new Vector3((x + stride - cx) * vSize, (y - cy) * vSize, (z - cz) * vSize));
                normals.Add(Vector3.UnitY); densityValues.Add(0);

                int i010 = vertices.Count;
                vertices.Add(new Vector3((x - cx) * vSize, (y + stride - cy) * vSize, (z - cz) * vSize));
                normals.Add(Vector3.UnitY); densityValues.Add(0);

                int i001 = vertices.Count;
                vertices.Add(new Vector3((x - cx) * vSize, (y - cy) * vSize, (z + stride - cz) * vSize));
                normals.Add(Vector3.UnitY); densityValues.Add(0);

                int i111 = vertices.Count;
                vertices.Add(new Vector3((x + stride - cx) * vSize, (y + stride - cy) * vSize, (z + stride - cz) * vSize));
                normals.Add(Vector3.UnitY); densityValues.Add(0);

                tetrahedralElements.Add(new TetrahedralElement(i000, i100, i010, i001));
                if (++made >= limit) break;
                tetrahedralElements.Add(new TetrahedralElement(i111, i100, i010, i001));
                if (++made >= limit) break;
            }
            if (Math.Abs(maxDensity - minDensity) < 0.001f)
            {
                maxDensity = minDensity + 1.0f;
            }
            worker.ReportProgress(100, "Mesh generation finished");
        }
        private void SetupRotationTimer()
        {
            rotationTimer = new System.Windows.Forms.Timer();
            rotationTimer.Interval = 16; // ~60 FPS
            rotationTimer.Tick += (s, e) => glControl.Invalidate();
        }

        private void GlControl_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                lastMousePos = e.Location;
                isDragging = true;

                // Apply immediate optimizations
                GL.Disable(EnableCap.Lighting);
                GL.Disable(EnableCap.LineSmooth);

                glControl.Invalidate();
            }
        }

        private void GlControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDragging)
            {
                int deltaX = e.X - lastMousePos.X;
                int deltaY = e.Y - lastMousePos.Y;

                // Scale rotation amount based on mesh complexity for smooth experience
                float rotationScale = 0.5f;
                if (indices.Count > 5000000) rotationScale = 0.2f;

                rotationY += deltaX * rotationScale;
                rotationX += deltaY * rotationScale;

                lastMousePos = e.Location;

                // Request a redraw
                glControl.Invalidate();
            }
        }
        private void StressStrainGraph_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Draw axes
            int padding = 30;
            int width = stressStrainGraph.Width - 2 * padding;
            int height = stressStrainGraph.Height - 2 * padding;

            using (Pen axisPen = new Pen(Color.Black, 2))
            {
                // X-axis
                g.DrawLine(axisPen, padding, stressStrainGraph.Height - padding,
                           stressStrainGraph.Width - padding, stressStrainGraph.Height - padding);

                // Y-axis
                g.DrawLine(axisPen, padding, stressStrainGraph.Height - padding,
                           padding, padding);
            }

            // Draw axis labels
            using (Font labelFont = new Font("Arial", 8))
            {
                g.DrawString("Strain (%)", labelFont, Brushes.Black,
                            stressStrainGraph.Width / 2, stressStrainGraph.Height - 15);

                // Rotate Y-axis label
                g.TranslateTransform(15, stressStrainGraph.Height / 2);
                g.RotateTransform(-90);
                g.DrawString("Stress (MPa)", labelFont, Brushes.Black, 0, 0);
                g.ResetTransform();
            }

            // Calculate appropriate scale for Y-axis
            float maxStressValue = youngModulus * 0.3f; // Default max stress scale (30% of Young's modulus)

            // If we have stress-strain data, adjust scale based on actual maximum stress
            if (stressStrainCurve.Count > 0)
            {
                float maxCurrentStress = stressStrainCurve.Max(p => p.Y) / 10.0f; // Convert to MPa
                maxStressValue = Math.Max(maxStressValue, maxCurrentStress * 1.2f); // Add 20% margin
            }

            // Calculate maximum yield/failure markers
            float maxMarkerStress = Math.Max(yieldStrength, brittleStrength);
            if (maxMarkerStress > 0)
            {
                maxStressValue = Math.Max(maxStressValue, maxMarkerStress * 1.1f);
            }

            // Round max stress to a nice value
            maxStressValue = (float)Math.Ceiling(maxStressValue / 100) * 100;

            // Draw stress-strain curve
            if (stressStrainCurve.Count >= 2)
            {
                using (Pen curvePen = new Pen(Color.Blue, 2))
                {
                    // Scale points to fit in the graph
                    int maxStrain = 200; // 20% strain
                    int maxStress = (int)(maxStressValue * 10); // Convert to graph units

                    List<Point> scaledPoints = new List<Point>();
                    foreach (var point in stressStrainCurve)
                    {
                        int x = padding + (int)(point.X * width / maxStrain);
                        int y = stressStrainGraph.Height - padding - (int)(point.Y * height / maxStress);
                        scaledPoints.Add(new Point(x, y));
                    }

                    // Draw curve
                    g.DrawLines(curvePen, scaledPoints.ToArray());
                }
            }

            // Draw yield and brittle strength lines if behaviors are enabled
            if (isPlasticEnabled || isBrittleEnabled)
            {
                // Scale for graph
                int maxStress = (int)(maxStressValue * 10); // Convert to graph units

                // Draw yield strength line if plastic behavior is enabled
                if (isPlasticEnabled && yieldStrength > 0)
                {
                    using (Pen yieldPen = new Pen(Color.Orange, 1) { DashStyle = DashStyle.Dash })
                    {
                        int y = stressStrainGraph.Height - padding - (int)(yieldStrength * 10 * height / maxStress);
                        g.DrawLine(yieldPen, padding, y, stressStrainGraph.Width - padding, y);
                        g.DrawString($"Yield: {yieldStrength:F0} MPa", new Font("Arial", 8), Brushes.Orange,
                                    stressStrainGraph.Width - padding - 120, y - 15);
                    }
                }

                // Draw brittle strength line if brittle behavior is enabled
                if (isBrittleEnabled && brittleStrength > 0)
                {
                    using (Pen brittlePen = new Pen(Color.Red, 1) { DashStyle = DashStyle.Dash })
                    {
                        int y = stressStrainGraph.Height - padding - (int)(brittleStrength * 10 * height / maxStress);
                        g.DrawLine(brittlePen, padding, y, stressStrainGraph.Width - padding, y);
                        g.DrawString($"Failure: {brittleStrength:F0} MPa", new Font("Arial", 8), Brushes.Red,
                                    stressStrainGraph.Width - padding - 120, y - 15);
                    }
                }
            }

            // Draw grid lines
            using (Pen gridPen = new Pen(Color.LightGray, 1))
            {
                // X-axis grid (strain)
                for (int i = 5; i <= 20; i += 5)
                {
                    int x = padding + (int)(i * 10 * width / 200);
                    g.DrawLine(gridPen, x, stressStrainGraph.Height - padding, x, padding);
                    g.DrawString(i.ToString() + "%", new Font("Arial", 8), Brushes.Black,
                                x - 10, stressStrainGraph.Height - padding + 5);
                }

                // Y-axis grid (stress) - more intelligent division
                int numDivisions = 5;
                float divisionSize = maxStressValue / numDivisions;

                // Round division size to a nice value
                if (divisionSize > 200) divisionSize = (float)Math.Ceiling(divisionSize / 100) * 100;
                else if (divisionSize > 20) divisionSize = (float)Math.Ceiling(divisionSize / 10) * 10;
                else divisionSize = (float)Math.Ceiling(divisionSize);

                for (int i = 1; i <= numDivisions; i++)
                {
                    float stressValue = i * divisionSize;
                    int maxStressPixels = (int)(maxStressValue * 10); // Convert to graph units
                    int y = stressStrainGraph.Height - padding - (int)(stressValue * 10 * height / maxStressPixels);
                    g.DrawLine(gridPen, padding, y, stressStrainGraph.Width - padding, y);
                    g.DrawString(stressValue.ToString("0"), new Font("Arial", 8),
                                Brushes.Black, padding - 25, y - 5);
                }
            }

            // Add title showing bulk density and any other relevant material properties
            using (Font titleFont = new Font("Arial", 9, FontStyle.Bold))
            {
                string densityInfo = $"Material: {(selectedMaterial != null ? selectedMaterial.Name : "Unknown")}";
                densityInfo += $" | Density: {bulkDensity:F0} kg/m³";

                if (porosity > 0)
                {
                    densityInfo += $" | Porosity: {porosity:P1}";
                }

                g.DrawString(densityInfo, titleFont, Brushes.Navy,
                           (stressStrainGraph.Width / 2) - 150, 10);
            }

            // Draw current stress/strain point
            if (stressStrainCurve.Count > 0)
            {
                Point lastPoint = stressStrainCurve.Last();

                // Scale point to fit in the graph
                int maxStrain = 200; // 20% strain
                int maxStress = (int)(maxStressValue * 10); // Convert to graph units

                int x = padding + (int)(lastPoint.X * width / maxStrain);
                int y = stressStrainGraph.Height - padding - (int)(lastPoint.Y * height / maxStress);

                // Draw a larger crosshair for current point
                using (Pen pointPen = new Pen(Color.Red, 2))
                {
                    g.DrawLine(pointPen, x - 5, y, x + 5, y);
                    g.DrawLine(pointPen, x, y - 5, x, y + 5);
                }

                using (SolidBrush pointBrush = new SolidBrush(Color.Red))
                {
                    g.FillEllipse(pointBrush, x - 4, y - 4, 8, 8);
                }

                // Display current values
                using (Font valueFont = new Font("Arial", 10, FontStyle.Bold))
                {
                    float currentStress = lastPoint.Y / 10.0f; // Convert to MPa
                    float currentStrain = lastPoint.X / 10.0f; // Convert to %

                    string stressStr = $"{currentStress:F1} MPa";
                    string strainStr = $"{currentStrain:F1}%";

                    // Create a semi-transparent background for readability
                    using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(200, Color.White)))
                    {
                        g.FillRectangle(bgBrush, padding, padding, 200, 80);
                    }

                    g.DrawString("Strain: " + strainStr, valueFont, Brushes.Black, padding + 5, padding + 5);
                    g.DrawString("Stress: " + stressStr, valueFont, Brushes.Black, padding + 5, padding + 25);

                    // Calculate Young's modulus from curve slope at origin (elastic region)
                    if (stressStrainCurve.Count >= 10)
                    {
                        int initialPoints = Math.Min(10, stressStrainCurve.Count / 4);
                        float initialStrain = stressStrainCurve[initialPoints - 1].X / 10.0f / 100.0f; // Convert to decimal
                        float initialStress = stressStrainCurve[initialPoints - 1].Y / 10.0f; // MPa

                        // Calculate modulus if strain is non-zero
                        if (initialStrain > 0.0001f)
                        {
                            float measuredModulus = initialStress / initialStrain;
                            g.DrawString($"Measured E: {measuredModulus:F0} MPa", valueFont, Brushes.Green, padding + 5, padding + 45);
                        }
                    }

                    // Show active behaviors
                    string behaviors = "Behaviors: ";
                    if (isElasticEnabled) behaviors += "Elastic ";
                    if (isPlasticEnabled) behaviors += "Plastic ";
                    if (isBrittleEnabled) behaviors += "Brittle";
                    g.DrawString(behaviors, valueFont, Brushes.Blue, padding + 5, padding + 65);
                }
            }

            // Add legend for material stages if both plastic and brittle are enabled
            if (isElasticEnabled && (isPlasticEnabled || isBrittleEnabled))
            {
                // Show material phase regions
                int legendWidth = 200;
                int legendHeight = 80;
                int legendX = stressStrainGraph.Width - padding - legendWidth;
                int legendY = padding;

                // Draw background
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(240, Color.White)))
                {
                    g.FillRectangle(bgBrush, legendX, legendY, legendWidth, legendHeight);
                }

                // Draw border
                using (Pen borderPen = new Pen(Color.Gray, 1))
                {
                    g.DrawRectangle(borderPen, legendX, legendY, legendWidth, legendHeight);
                }

                // Add title
                using (Font titleFont = new Font("Arial", 8, FontStyle.Bold))
                {
                    g.DrawString("Material Phases", titleFont, Brushes.Black, legendX + 5, legendY + 5);
                }

                // Draw phase indicators
                int lineY = legendY + 30;
                int lineLength = 15;
                int textOffset = 20;

                // Elastic phase (solid blue line)
                using (Pen elasticPen = new Pen(Color.Blue, 2))
                {
                    g.DrawLine(elasticPen, legendX + 5, lineY, legendX + 5 + lineLength, lineY);
                    g.DrawString("Elastic", new Font("Arial", 8), Brushes.Black, legendX + 5 + textOffset, lineY - 5);
                }

                // Plastic phase if enabled (solid orange line)
                if (isPlasticEnabled)
                {
                    lineY += 20;
                    using (Pen plasticPen = new Pen(Color.Orange, 2))
                    {
                        g.DrawLine(plasticPen, legendX + 5, lineY, legendX + 5 + lineLength, lineY);
                        g.DrawString("Plastic (Yielding)", new Font("Arial", 8), Brushes.Black, legendX + 5 + textOffset, lineY - 5);
                    }
                }

                // Brittle phase if enabled (dashed red line)
                if (isBrittleEnabled)
                {
                    lineY += 20;
                    using (Pen brittlePen = new Pen(Color.Red, 2) { DashStyle = DashStyle.Dash })
                    {
                        g.DrawLine(brittlePen, legendX + 5, lineY, legendX + 5 + lineLength, lineY);
                        g.DrawString("Brittle Failure", new Font("Arial", 8), Brushes.Black, legendX + 5 + textOffset, lineY - 5);
                    }
                }
            }
        }
        private void GlControl_Paint(object sender, PaintEventArgs e)
        {
            // Ensure valid context
            if (!glControl.IsHandleCreated) return;
            if (!glControl.Context.IsCurrent) glControl.MakeCurrent();

            // Clear buffers
            GL.ClearColor(0.2f, 0.2f, 0.2f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // Skip if no mesh
            if (deformedVertices.Count == 0)
            {
                glControl.SwapBuffers();
                return;
            }

            // Ultra-large mesh handling
            bool useUltraSimplification = isDragging && indices.Count > 15000000;
            if (useUltraSimplification)
            {
                // Set up camera matrices
                SetupCamera();
                DrawExtremeFallback();
                glControl.SwapBuffers();
                return;
            }

            // Normal rendering pipeline
            SetupCamera();

            // Draw mesh
            DrawMesh();

            // Draw color legend if enabled and not dragging
            if (chkShowDensity.Checked && !isDragging)
            {
                DrawColorLegendGL();
            }

            // Present
            glControl.SwapBuffers();
        }

        private void SetupCamera()
        {
            // Set up projection matrix
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            float aspect = glControl.Width / (float)glControl.Height;
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(40.0f),
                aspect,
                0.1f,
                1000.0f);
            GL.LoadMatrix(ref proj);

            // Set up modelview matrix
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            // Position camera
            GL.Translate(0, 0, -20.0f * zoom);
            GL.Rotate(rotationX, 1.0f, 0.0f, 0.0f);
            GL.Rotate(rotationY, 0.0f, 1.0f, 0.0f);

            // Set up basic lighting
            if (!wireframeMode && !isDragging)
            {
                GL.Enable(EnableCap.Lighting);
                GL.Enable(EnableCap.Light0);

                float[] lightPos = { 10.0f, 10.0f, 10.0f, 1.0f };
                float[] lightAmbient = { 0.4f, 0.4f, 0.4f, 1.0f };
                float[] lightDiffuse = { 0.8f, 0.8f, 0.8f, 1.0f };

                GL.Light(LightName.Light0, LightParameter.Position, lightPos);
                GL.Light(LightName.Light0, LightParameter.Ambient, lightAmbient);
                GL.Light(LightName.Light0, LightParameter.Diffuse, lightDiffuse);
            }
        }
        private void DisposeDisplayList()
        {
            if (meshDisplayList != 0)
            {
                GL.DeleteLists(meshDisplayList, 1);
                meshDisplayList = 0;
            }
        }
        private void DrawMesh()
        {
            if (deformedVertices == null || deformedVertices.Count == 0)
                return;

            // Set rendering mode
            if (wireframeMode)
            {
                GL.Disable(EnableCap.Lighting);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
            }
            else
            {
                if (!isDragging) GL.Enable(EnableCap.Lighting);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }

            // Apply additional optimizations during rotation
            if (isDragging)
            {
                // Disable these features during rotation for better performance
                GL.Disable(EnableCap.Lighting);
                GL.Disable(EnableCap.LineSmooth);
                GL.ShadeModel(ShadingModel.Flat);

                // Disable transparency during rotation for performance
                enableTransparency = false;
            }

            // Set up blending if transparency is enabled
            if (enableTransparency)
            {
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

                // Sort triangles back-to-front for correct transparency
                if (!isDragging && !wireframeMode)
                {
                    RenderMeshWithDepthSorting();

                    // Reset polygon mode and disable blending
                    GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                    GL.Disable(EnableCap.Blend);
                    return;
                }
            }

            // Try to build display lists if they don't exist yet
            if (highDetailList == 0 && deformedVertices.Count > 0)
            {
                InitializeVBOs(); // Initialize VBOs first
                BuildDisplayLists();
            }

            // Use the appropriate display list based on rotation state
            if (highDetailList != 0 && !enableTransparency && !showFailedElements)
            {
                // Only use display lists when not using transparency or failed elements highlight
                // (those features require per-triangle rendering)
                if (isDragging)
                    GL.CallList(lowDetailList);
                else
                    GL.CallList(highDetailList);
            }
            else
            {
                // Fallback to direct rendering
                if (isDragging && !enableTransparency && !showFailedElements)
                    RenderLowDetailMesh();
                else
                    RenderFullMesh(true);
            }

            // Reset polygon mode
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

            // Disable blending after rendering if we enabled it
            if (enableTransparency)
            {
                GL.Disable(EnableCap.Blend);
            }
        }
        private float[] GetColorComponents(float normalizedValue)
        {
            float[] color = new float[3];

            // Blue to red color spectrum
            if (normalizedValue < 0.25f)
            {
                // Blue to Cyan
                color[0] = 0.0f;
                color[1] = normalizedValue * 4.0f;
                color[2] = 1.0f;
            }
            else if (normalizedValue < 0.5f)
            {
                // Cyan to Green
                color[0] = 0.0f;
                color[1] = 1.0f;
                color[2] = 1.0f - (normalizedValue - 0.25f) * 4.0f;
            }
            else if (normalizedValue < 0.75f)
            {
                // Green to Yellow
                color[0] = (normalizedValue - 0.5f) * 4.0f;
                color[1] = 1.0f;
                color[2] = 0.0f;
            }
            else
            {
                // Yellow to Red
                color[0] = 1.0f;
                color[1] = 1.0f - (normalizedValue - 0.75f) * 4.0f;
                color[2] = 0.0f;
            }

            return color;
        }
        private Bitmap CaptureGLControl()
        {
            if (!glControl.IsHandleCreated)
                return new Bitmap(1, 1);

            glControl.MakeCurrent();

            // Create a bitmap to hold the image
            Bitmap bitmap = new Bitmap(glControl.Width, glControl.Height);

            // Render the scene exactly like in GlControl_Paint
            GL.ClearColor(0.2f, 0.2f, 0.2f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (deformedVertices.Count > 0)
            {
                // Set up camera
                GL.MatrixMode(MatrixMode.Projection);
                GL.LoadIdentity();
                float aspect = glControl.Width / (float)glControl.Height;
                Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                    MathHelper.DegreesToRadians(40.0f),
                    aspect,
                    0.1f,
                    1000.0f);
                GL.LoadMatrix(ref proj);

                // Set up modelview matrix
                GL.MatrixMode(MatrixMode.Modelview);
                GL.LoadIdentity();

                // Position camera
                GL.Translate(0, 0, -20.0f * zoom);
                GL.Rotate(rotationX, 1.0f, 0.0f, 0.0f);
                GL.Rotate(rotationY, 0.0f, 1.0f, 0.0f);

                // Set up lighting
                bool lightingWasEnabled = GL.IsEnabled(EnableCap.Lighting);
                if (!wireframeMode)
                {
                    GL.Enable(EnableCap.Lighting);
                    GL.Enable(EnableCap.Light0);

                    float[] lightPos = { 10.0f, 10.0f, 10.0f, 1.0f };
                    float[] lightAmbient = { 0.4f, 0.4f, 0.4f, 1.0f };
                    float[] lightDiffuse = { 0.8f, 0.8f, 0.8f, 1.0f };

                    GL.Light(LightName.Light0, LightParameter.Position, lightPos);
                    GL.Light(LightName.Light0, LightParameter.Ambient, lightAmbient);
                    GL.Light(LightName.Light0, LightParameter.Diffuse, lightDiffuse);
                }
                else
                {
                    GL.Disable(EnableCap.Lighting);
                }

                // Draw mesh
                DrawMesh();

                // Draw density legend if showing density
                if (chkShowDensity.Checked)
                {
                    DrawColorLegendGL();
                }

                // Restore lighting state
                if (lightingWasEnabled && !GL.IsEnabled(EnableCap.Lighting))
                    GL.Enable(EnableCap.Lighting);
                else if (!lightingWasEnabled && GL.IsEnabled(EnableCap.Lighting))
                    GL.Disable(EnableCap.Lighting);
            }

            // Make sure rendering is complete
            GL.Flush();
            GL.Finish();

            // Copy the framebuffer to the bitmap
            System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            GL.ReadPixels(0, 0, bitmap.Width, bitmap.Height,
                PixelFormat.Bgr, PixelType.UnsignedByte, data.Scan0);

            bitmap.UnlockBits(data);

            // OpenGL's origin is bottom-left, while GDI+'s is top-left, so flip the image
            bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);

            // Force a redraw to ensure normal rendering continues
            glControl.Invalidate();

            return bitmap;
        }

        
        private void DrawSimpleColorLegend()
        {
            // Save current matrices
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, 0, glControl.Height, -1, 1);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.LoadIdentity();

            // Save OpenGL state
            bool lightingEnabled = GL.IsEnabled(EnableCap.Lighting);
            bool depthTestEnabled = GL.IsEnabled(EnableCap.DepthTest);

            // Disable 3D features for 2D overlay
            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.DepthTest);

            // Legend dimensions and position
            int x = glControl.Width - 30;
            int width = 20;
            int y = 20;
            int height = 200;

            // Draw color gradient
            GL.Begin(PrimitiveType.Quads);

            // Draw gradient in 10 segments for smoother appearance
            for (int i = 0; i < 10; i++)
            {
                float top = y + i * (height / 10.0f);
                float bottom = y + (i + 1) * (height / 10.0f);
                float value = 1.0f - (i / 10.0f);  // Map from 1.0 to 0.0 (high to low)

                // Set color for this segment
                SetColorForValue(value);

                // Draw segment
                GL.Vertex2(x, top);
                GL.Vertex2(x + width, top);
                GL.Vertex2(x + width, bottom);
                GL.Vertex2(x, bottom);
            }

            GL.End();

            // Draw border
            GL.Color3(1.0f, 1.0f, 1.0f);
            GL.Begin(PrimitiveType.LineLoop);
            GL.Vertex2(x, y);
            GL.Vertex2(x + width, y);
            GL.Vertex2(x + width, y + height);
            GL.Vertex2(x, y + height);
            GL.End();

            // Draw tick marks
            GL.Begin(PrimitiveType.Lines);
            for (int i = 0; i <= 5; i++)
            {
                float tickY = y + i * (height / 5.0f);
                GL.Vertex2(x, tickY);
                GL.Vertex2(x - 5, tickY);
            }
            GL.End();

            // Restore 3D settings if they were enabled
            if (lightingEnabled) GL.Enable(EnableCap.Lighting);
            if (depthTestEnabled) GL.Enable(EnableCap.DepthTest);

            // Restore matrices
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PopMatrix();

            // Now draw the text using GDI+ (keeping the same approach)
            using (Graphics g = Graphics.FromHwnd(glControl.Handle))
            {
                // Rest of the text drawing code remains the same
                if (g != null)
                {
                    // Set high quality text rendering
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                    using (Font font = new Font("Arial", 8, FontStyle.Bold))
                    {
                        // Draw title label with dark background for better visibility
                        using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
                        {
                            // Title background
                            g.FillRectangle(bgBrush, x - 65, 5, 60, 20);
                            g.DrawString("Density", font, Brushes.White, x - 60, 10);

                            // Units background and text
                            string unitText = isDensityCalibrated ? "kg/m³" : "(norm)";
                            g.FillRectangle(bgBrush, x - 65, 25, 60, 15);
                            g.DrawString(unitText, font, Brushes.White, x - 60, 25);
                        }

                        // Draw tick labels
                        for (int i = 0; i <= 5; i++)
                        {
                            float normalizedValue = 1.0f - (i / 5.0f);  // 1.0 to 0.0 (top to bottom)
                            float value = minDensity + (maxDensity - minDensity) * normalizedValue;

                            // Calculate screen coordinates - y is inverted in GDI+
                            int tickY = (int)(y + i * (height / 5.0f));
                            int screenY = glControl.Height - tickY - 6;  // Adjust y for GDI+

                            // Format the value based on calibration
                            string valueText = isDensityCalibrated
                                ? $"{value:F0}" // Integer format for density
                                : $"{normalizedValue:F1}"; // One decimal place for normalized value

                            // Draw background for better visibility
                            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
                            {
                                g.FillRectangle(bgBrush, x - 45, screenY - 6, 40, 15);
                            }

                            // Draw text
                            g.DrawString(valueText, font, Brushes.White, x - 40, screenY - 6);
                        }

                        // Add min/max labels if calibrated
                        if (isDensityCalibrated)
                        {
                            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
                            {
                                int maxY = glControl.Height - y - height - 6;
                                int minY = glControl.Height - y - 6;

                                g.FillRectangle(bgBrush, x - 65, maxY - 6, 20, 15);
                                g.DrawString("Max", font, Brushes.White, x - 60, maxY - 6);

                                g.FillRectangle(bgBrush, x - 65, minY - 6, 20, 15);
                                g.DrawString("Min", font, Brushes.White, x - 60, minY - 6);
                            }
                        }
                    }
                }
            }
        }
        private void SetColorForValue(float normalizedValue)
        {
            // Clamp value to 0-1 range
            normalizedValue = Math.Max(0.0f, Math.Min(1.0f, normalizedValue));

            // Blue to red color spectrum
            float r, g, b;

            if (normalizedValue < 0.25f)
            {
                // Blue to Cyan
                r = 0.0f;
                g = normalizedValue * 4.0f;
                b = 1.0f;
            }
            else if (normalizedValue < 0.5f)
            {
                // Cyan to Green
                r = 0.0f;
                g = 1.0f;
                b = 1.0f - (normalizedValue - 0.25f) * 4.0f;
            }
            else if (normalizedValue < 0.75f)
            {
                // Green to Yellow
                r = (normalizedValue - 0.5f) * 4.0f;
                g = 1.0f;
                b = 0.0f;
            }
            else
            {
                // Yellow to Red
                r = 1.0f;
                g = 1.0f - (normalizedValue - 0.75f) * 4.0f;
                b = 0.0f;
            }

            // Set color
            GL.Color3(r, g, b);
        }
        
        
        private int meshDisplayList = 0;
        private void AddSaveImageButton()
        {
            // Create the save image button
            btnSaveImage = new KryptonButton
            {
                Text = "Save Image",
                Location = new Point(10, 910), // Position below the Stop Simulation button
                Width = 310,
                Height = 30,
                StateCommon = {
            Back = { Color1 = Color.FromArgb(60, 60, 120) },
            Content = { ShortText = { Color1 = Color.White } }
        }
            };
            btnSaveImage.Click += BtnSaveImage_Click;

            // Find the controls content panel to add the button to
            Panel controlsContent = null;
            foreach (Control control in this.Controls)
            {
                if (control is TableLayoutPanel mainLayout)
                {
                    foreach (Control panelControl in mainLayout.Controls)
                    {
                        if (panelControl is Panel panel)
                        {
                            foreach (Control c in panel.Controls)
                            {
                                if (c is Panel scrollablePanel && scrollablePanel.AutoScroll)
                                {
                                    foreach (Control sc in scrollablePanel.Controls)
                                    {
                                        if (sc is Panel content && content.Height > 800)
                                        {
                                            controlsContent = content;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (controlsContent != null)
            {
                controlsContent.Controls.Add(btnSaveImage);
            }
            else
            {
                // Fallback - add directly to form
                this.Controls.Add(btnSaveImage);
            }
        }
        private void GlControl_Load(object sender, EventArgs e)
        {
            if (!glControl.IsHandleCreated) return;

            glControl.MakeCurrent();

            // Check hardware acceleration
            CheckHardwareAcceleration();

            // Set up OpenGL state
            GL.ClearColor(0.2f, 0.2f, 0.2f, 1.0f);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.ShadeModel(ShadingModel.Smooth);

            // Disable unused features
            GL.Disable(EnableCap.Texture2D);
            GL.Disable(EnableCap.Fog);
            GL.Disable(EnableCap.Dither);

            // Enable backface culling for better performance
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);

            // Set up viewport
            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            glControlInitialized = true;
        }
        private void GlControl_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDragging = false;

                // Restore high-quality rendering
                if (!wireframeMode) GL.Enable(EnableCap.Lighting);
                GL.ShadeModel(ShadingModel.Smooth);

                // Ensure a final high-quality render
                glControl.Invalidate();
            }
        }

        private void GlControl_MouseWheel(object sender, MouseEventArgs e)
        {
            zoom *= (float)Math.Pow(1.1, e.Delta / 120.0);
            zoom = Math.Max(0.1f, Math.Min(10.0f, zoom));
            glControl.Invalidate();
        }
        private void GlControl_Resize(object sender, EventArgs e)
        {
            if (!glControl.IsHandleCreated) return;
            if (!glControl.Context.IsCurrent) glControl.MakeCurrent();
            if (glControl.Width <= 0 || glControl.Height <= 0) return;

            // Update viewport
            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            // Reposition legend panel
            if (colorLegendPanel != null)
            {
                colorLegendPanel.Location = new Point(glControl.Width - 105, 10);
            }

            // Force redraw
            glControl.Invalidate();
        }
        private void DrawExtremeFallback()
        {
            // For massive meshes, draw a bounding box during rotation
            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);

            // Sample vertices to find bounds
            int step = Math.Max(1, deformedVertices.Count / 1000);
            for (int i = 0; i < deformedVertices.Count; i += step)
            {
                Vector3 v = deformedVertices[i];
                min.X = Math.Min(min.X, v.X);
                min.Y = Math.Min(min.Y, v.Y);
                min.Z = Math.Min(min.Z, v.Z);

                max.X = Math.Max(max.X, v.X);
                max.Y = Math.Max(max.Y, v.Y);
                max.Z = Math.Max(max.Z, v.Z);
            }

            // Draw bounding box as wireframe
            GL.Color3(1.0f, 1.0f, 1.0f);
            GL.Begin(PrimitiveType.LineLoop);
            GL.Vertex3(min.X, min.Y, min.Z);
            GL.Vertex3(max.X, min.Y, min.Z);
            GL.Vertex3(max.X, max.Y, min.Z);
            GL.Vertex3(min.X, max.Y, min.Z);
            GL.End();

            GL.Begin(PrimitiveType.LineLoop);
            GL.Vertex3(min.X, min.Y, max.Z);
            GL.Vertex3(max.X, min.Y, max.Z);
            GL.Vertex3(max.X, max.Y, max.Z);
            GL.Vertex3(min.X, max.Y, max.Z);
            GL.End();

            GL.Begin(PrimitiveType.Lines);
            GL.Vertex3(min.X, min.Y, min.Z); GL.Vertex3(min.X, min.Y, max.Z);
            GL.Vertex3(max.X, min.Y, min.Z); GL.Vertex3(max.X, min.Y, max.Z);
            GL.Vertex3(max.X, max.Y, min.Z); GL.Vertex3(max.X, max.Y, max.Z);
            GL.Vertex3(min.X, max.Y, min.Z); GL.Vertex3(min.X, max.Y, max.Z);
            GL.End();

            // Draw message
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, 0, glControl.Height, -1, 1);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.LoadIdentity();

            // Draw message about simplified view
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            // Text will be rendered with the font texture system
            // Display "Ultra-simplified view during rotation - release mouse for details"

            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PopMatrix();
        }
        private void BtnContinueSimulation_Click(object sender, EventArgs e)
        {
            if (!meshGenerationComplete || tetrahedralElements.Count == 0)
            {
                MessageBox.Show("No mesh available. Please generate a mesh first.", "Error",
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                // Initialize parameters similar to BtnStartSimulation_Click
                // but we'll continue from current strain
                float startingStrain = currentStrain;

                // Update UI state
                btnStartSimulation.Enabled = false;
                btnStopSimulation.Enabled = true;
                btnContinueSimulation.Enabled = false;
                comboMaterials.Enabled = false;
                comboDirection.Enabled = false;
                progressBar.Value = (int)(startingStrain / maxStrain * 100);  // Set progress to current position

                // Start simulation
                simulationRunning = true;

                // Update diagrams form if available
                try
                {
                    if (diagramsForm == null || diagramsForm.IsDisposed)
                    {
                        InitializeDiagramsForm();
                    }

                    if (diagramsForm != null)
                    {
                        diagramsForm.Show();
                        UpdateDiagramsForm();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[TriaxialSimulationForm] Error initializing DiagramsForm: {ex.Message}");
                }

                // Choose computation method
                bool useDirectComputeForThisRun = useDirectCompute && chkUseDirectCompute.Checked;

                if (useDirectComputeForThisRun)
                {
                    // Initialize DirectCompute if needed
                    if (computeEngine == null)
                    {
                        InitializeDirectCompute();
                    }

                    if (computeEngine != null)
                    {
                        progressLabel.Text = fastSimulationMode ?
                            "Continuing simulation after failure (updating at completion)" :
                            "Continuing simulation after failure";

                        Logger.Log("[TriaxialSimulationForm] Continuing simulation after failure with hardware acceleration");

                        // Execute the simulation in a background task
                        Task.Run(async () => {
                            try
                            {
                                // Make deep copies of collections to prevent threading issues
                                var verticesCopy = new List<Vector3>(vertices);
                                var normalsCopy = new List<Vector3>(normals);
                                var indicesCopy = new List<int>(indices);
                                var densityValuesCopy = new List<float>(densityValues);
                                var tetrahedralElementsCopy = new List<TetrahedralElement>(tetrahedralElements);

                                // Initialize compute engine with copied mesh data if not already done
                                if (!computeEngine.IsInitialized)
                                {
                                    computeEngine.InitializeFromMesh(
                                        verticesCopy,
                                        normalsCopy,
                                        indicesCopy,
                                        densityValuesCopy,
                                        tetrahedralElementsCopy);

                                    // Set material properties
                                    computeEngine.SetMaterialProperties(
                                        bulkDensity,
                                        youngModulus,
                                        poissonRatio,
                                        yieldStrength,
                                        brittleStrength,
                                        cohesion,
                                        frictionAngle,
                                        porosity,
                                        bulkModulus,
                                        permeability,
                                        minPressure,
                                        maxPressure,
                                        isElasticEnabled,
                                        isPlasticEnabled,
                                        isBrittleEnabled);
                                }

                                // Set the ignore failure flag to continue after failure
                                computeEngine.SetIgnoreFailure(true);

                                // Run the simulation from current strain to maxStrain
                                SimulationDirection direction = (SimulationDirection)comboDirection.SelectedIndex;
                                await computeEngine.ContinueSimulationAsync(startingStrain, maxStrain, 0.001f, (CTS.SimulationDirection)direction);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[TriaxialSimulationForm] Error in accelerated simulation: {ex.Message}");

                                if (ex.InnerException != null)
                                {
                                    Logger.Log($"[TriaxialSimulationForm] Inner exception: {ex.InnerException.Message}");
                                }

                                Logger.Log($"[TriaxialSimulationForm] Stack trace: {ex.StackTrace}");

                                // Fall back to standard method
                                if (InvokeRequired)
                                {
                                    BeginInvoke(new Action(() => {
                                        progressLabel.Text = "Falling back to standard simulation after failure";
                                        ContinueWithStandardSimulation(startingStrain);
                                    }));
                                }
                                else
                                {
                                    progressLabel.Text = "Falling back to standard simulation after failure";
                                    ContinueWithStandardSimulation(startingStrain);
                                }
                            }
                        });
                    }
                    else
                    {
                        // Could not initialize DirectCompute, fall back to CPU
                        progressLabel.Text = "Hardware acceleration unavailable, using standard simulation";
                        ContinueWithStandardSimulation(startingStrain);
                    }
                }
                else
                {
                    // Use original timer-based method
                    progressLabel.Text = fastSimulationMode ?
                        "Continuing simulation after failure (updating at completion)" :
                        "Continuing simulation after failure";

                    Logger.Log("[TriaxialSimulationForm] Continuing simulation after failure with standard method");
                    ContinueWithStandardSimulation(startingStrain);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error continuing simulation: {ex.Message}");

                if (ex.InnerException != null)
                {
                    Logger.Log($"[TriaxialSimulationForm] Inner exception: {ex.InnerException.Message}");
                }

                MessageBox.Show($"Error continuing simulation: {ex.Message}", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);

                // Reset UI state
                ResetUIAfterSimulation();
            }
        }

        private void ContinueWithStandardSimulation(float startingStrain)
        {
            // Setup state for continuation
            currentStrain = startingStrain;

            // Start the simulation timer
            simulationTimer.Start();
        }

        private void BtnSaveImage_Click(object sender, EventArgs e)
        {
            try
            {
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp";
                    saveDialog.Title = "Save Simulation Image";
                    saveDialog.DefaultExt = "png";
                    saveDialog.FileName = $"TriaxialSimulation_{DateTime.Now:yyyyMMdd_HHmmss}";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        // Render the scene once to ensure it's up-to-date
                        glControl.Invalidate();
                        glControl.Update();

                        // Create the composite image
                        Bitmap compositeImage = CreateCompositeImage();

                        // Save the image
                        string extension = Path.GetExtension(saveDialog.FileName).ToLower();
                        switch (extension)
                        {
                            case ".jpg":
                            case ".jpeg":
                                compositeImage.Save(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Jpeg);
                                break;
                            case ".bmp":
                                compositeImage.Save(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Bmp);
                                break;
                            case ".png":
                            default:
                                compositeImage.Save(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
                                break;
                        }

                        MessageBox.Show($"Image saved to {saveDialog.FileName}", "Save Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);

                        // Force a final redraw to ensure the display is correct
                        glControl.Invalidate();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving image: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[TriaxialSimulationForm] Error saving image: {ex.Message}");

                // Force a redraw in case of error
                glControl.Invalidate();
            }
        }
        private Bitmap CreateCompositeImage()
        {
            // Capture the OpenGL render
            Bitmap glRender = CaptureGLControl();

            // Create the composite bitmap
            Bitmap compositeImage = new Bitmap(glRender.Width, glRender.Height);

            using (Graphics g = Graphics.FromImage(compositeImage))
            {
                // Draw the OpenGL render
                g.DrawImage(glRender, 0, 0, glRender.Width, glRender.Height);

                // Draw legend if enabled
                if (chkShowDensity.Checked)
                {
                    // Temporarily create a bitmap of the legend panel
                    using (Bitmap legendBitmap = new Bitmap(colorLegendPanel.Width, colorLegendPanel.Height))
                    {
                        colorLegendPanel.DrawToBitmap(legendBitmap, new Rectangle(0, 0, colorLegendPanel.Width, colorLegendPanel.Height));
                        g.DrawImage(legendBitmap, colorLegendPanel.Location);
                    }
                }

                // Add simulation info
                string info = $"Material: {(selectedMaterial != null ? selectedMaterial.Name : "Unknown")}";
                info += $"\nDensity: {bulkDensity:F0} kg/m³";

                if (simulationRunning || stressStrainCurve.Count > 0)
                {
                    float currentStress = stressStrainCurve.Count > 0 ?
                        stressStrainCurve.Last().Y / 10.0f : 0; // Convert to MPa
                    float currentStrainPercent = currentStrain * 100; // Convert to percentage

                    info += $"\nStrain: {currentStrainPercent:F1}%";
                    info += $"\nStress: {currentStress:F1} MPa";
                }

                using (Font font = new Font("Arial", 10, FontStyle.Bold))
                using (Brush brush = new SolidBrush(Color.White))
                using (Brush shadowBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
                {
                    // Draw text shadow and then text for better readability
                    g.DrawString(info, font, shadowBrush, glRender.Width + 5 + 1, 11);
                    g.DrawString(info, font, brush, glRender.Width + 5, 10);
                }
            }

            return compositeImage;
        }
       
        
        private Color GetColorForValue(float normalizedValue)
        {
            // Clamp value to 0-1 range
            normalizedValue = Math.Max(0.0f, Math.Min(1.0f, normalizedValue));

            // Blue to red color spectrum (same as in SetColorForValue method)
            float r, g, b;

            if (normalizedValue < 0.25f)
            {
                // Blue to Cyan
                r = 0.0f;
                g = normalizedValue * 4.0f;
                b = 1.0f;
            }
            else if (normalizedValue < 0.5f)
            {
                // Cyan to Green
                r = 0.0f;
                g = 1.0f;
                b = 1.0f - (normalizedValue - 0.25f) * 4.0f;
            }
            else if (normalizedValue < 0.75f)
            {
                // Green to Yellow
                r = (normalizedValue - 0.5f) * 4.0f;
                g = 1.0f;
                b = 0.0f;
            }
            else
            {
                // Yellow to Red
                r = 1.0f;
                g = 1.0f - (normalizedValue - 0.75f) * 4.0f;
                b = 0.0f;
            }

            return Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
        }
        
        private void InitializeFontTexture()
        {
            if (fontTextureInitialized)
                return;

            // Create a texture bitmap for rendering text
            fontTexture = new Bitmap(256, 256);
            using (Graphics g = Graphics.FromImage(fontTexture))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                // Use a monospace font for simplicity
                Font font = new Font("Consolas", 16, FontStyle.Bold);

                // Generate all ASCII printable characters
                for (char c = ' '; c <= '~'; c++)
                {
                    int index = c - ' ';
                    int row = index / 16;
                    int col = index % 16;

                    Rectangle rect = new Rectangle(col * 16, row * 16, 16, 16);
                    characterRects[c] = rect;

                    // Draw character
                    g.DrawString(c.ToString(), font, Brushes.White, rect.X, rect.Y);
                }
            }

            // Create OpenGL texture
            GL.GenTextures(1, out fontTextureId);
            GL.BindTexture(TextureTarget.Texture2D, fontTextureId);

            // Set texture parameters
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            // Get bitmap data
            System.Drawing.Imaging.BitmapData data = fontTexture.LockBits(
                new Rectangle(0, 0, fontTexture.Width, fontTexture.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            // Upload texture
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                data.Width, data.Height, 0, PixelFormat.Bgra,
                PixelType.UnsignedByte, data.Scan0);

            fontTexture.UnlockBits(data);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            fontTextureInitialized = true;
        }
        private void DrawColorLegend()
        {
            // Keep the 3D rendering intact by saving the current matrices and states
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();

            // Save relevant OpenGL states
            bool lighting = GL.IsEnabled(EnableCap.Lighting);
            bool texture = GL.IsEnabled(EnableCap.Texture2D);
            bool depthTest = GL.IsEnabled(EnableCap.DepthTest);

            // Set up for 2D overlay drawing
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, 0, glControl.Height, -1, 1);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            // Disable features that would interfere with 2D drawing
            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.Texture2D);
            GL.Disable(EnableCap.DepthTest);

            // Draw the colorbar
            int barWidth = 20;
            int barHeight = 200;
            int barX = glControl.Width - barWidth - 10;
            int barY = 50;

            // Draw the gradient bar
            GL.Begin(PrimitiveType.Quads);
            for (int i = 0; i < barHeight; i++)
            {
                float normalizedValue = 1.0f - (i / (float)barHeight);
                SetColorForValue(normalizedValue);

                GL.Vertex2(barX, barY + i);
                GL.Vertex2(barX + barWidth, barY + i);

                normalizedValue = 1.0f - ((i + 1) / (float)barHeight);
                SetColorForValue(normalizedValue);

                GL.Vertex2(barX + barWidth, barY + i + 1);
                GL.Vertex2(barX, barY + i + 1);
            }
            GL.End();

            // Draw border around the colorbar
            GL.Color3(1.0f, 1.0f, 1.0f);
            GL.Begin(PrimitiveType.LineLoop);
            GL.Vertex2(barX, barY);
            GL.Vertex2(barX + barWidth, barY);
            GL.Vertex2(barX + barWidth, barY + barHeight);
            GL.Vertex2(barX, barY + barHeight);
            GL.End();

            // Draw tick marks
            GL.Begin(PrimitiveType.Lines);
            for (int i = 0; i <= 5; i++)
            {
                float y = barY + i * (barHeight / 5.0f);
                GL.Vertex2(barX, y);
                GL.Vertex2(barX - 5, y);
            }
            GL.End();

            // Finish OpenGL rendering
            GL.Flush();

            // Restore OpenGL state
            if (lighting) GL.Enable(EnableCap.Lighting);
            if (texture) GL.Enable(EnableCap.Texture2D);
            if (depthTest) GL.Enable(EnableCap.DepthTest);

            // Restore matrices
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PopMatrix();

            // Now draw text using GDI+ in a safe, separate pass
            try
            {
                using (Graphics g = Graphics.FromHwnd(glControl.Handle))
                {
                    using (Font font = new Font("Arial", 8, FontStyle.Bold))
                    {
                        // Draw title
                        g.DrawString("Density", font, Brushes.White, glControl.Width - barWidth - 50, 20);

                        // Draw unit label
                        string unitText = isDensityCalibrated ? "kg/m³" : "(norm)";
                        g.DrawString(unitText, font, Brushes.White, glControl.Width - barWidth - 50, 35);

                        // Draw tick labels
                        for (int i = 0; i <= 5; i++)
                        {
                            float normalizedValue = 1.0f - (i / 5.0f);
                            float value = minDensity + (maxDensity - minDensity) * normalizedValue;

                            int screenY = glControl.Height - (barY + (int)(i * barHeight / 5.0f)) - 6;

                            string label = isDensityCalibrated ?
                                $"{value:F0}" :
                                $"{normalizedValue:F1}";

                            g.DrawString(label, font, Brushes.White, glControl.Width - barWidth - 40, screenY);
                        }

                        // Draw min/max labels if calibrated
                        if (isDensityCalibrated)
                        {
                            g.DrawString("Max", font, Brushes.White, glControl.Width - barWidth - 60,
                                glControl.Height - barY - barHeight);
                            g.DrawString("Min", font, Brushes.White, glControl.Width - barWidth - 60,
                                glControl.Height - barY - 15);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Just log the error but continue rendering
                Logger.Log($"[TriaxialSimulationForm] Error drawing legend text: {ex.Message}");
            }
        }
        private void ComputeEngine_ProgressUpdated(object sender, DirectComputeProgressEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => HandleComputeProgress(e)));
            }
            else
            {
                HandleComputeProgress(e);
            }
        }
        
        private void ComputeEngine_SimulationCompleted(object sender, DirectComputeCompletedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => HandleComputeCompleted(e)));
            }
            else
            {
                HandleComputeCompleted(e);
            }
        }
        private void HandleComputeCompleted(DirectComputeCompletedEventArgs e)
        {
            try
            {
                // Update the stress-strain curve with thread-safety
                lock (stressStrainCurve)
                {
                    stressStrainCurve.Clear();
                    stressStrainCurve.AddRange(e.StressStrainCurve);
                }

                // Update deformed vertices
                if (deformedVertices == null || deformedVertices.Count != e.DeformedVertices.Length)
                {
                    deformedVertices = new List<Vector3>(e.DeformedVertices.Length);
                    for (int i = 0; i < e.DeformedVertices.Length; i++)
                    {
                        deformedVertices.Add(Vector3.Zero);
                    }
                }

                // Copy vertex data
                for (int i = 0; i < e.DeformedVertices.Length; i++)
                {
                    if (i < deformedVertices.Count)
                        deformedVertices[i] = e.DeformedVertices[i];
                }

                // Update element stress data
                elementStresses.Clear();
                for (int i = 0; i < e.ElementStresses.Length; i++)
                {
                    elementStresses[i] = e.ElementStresses[i];
                }

                // Update volumetric strain and energy values for diagrams
                volumetricStrain = e.VolumetricStrain;
                elasticEnergy = e.ElasticEnergy;
                plasticEnergy = e.PlasticEnergy;

                // Update permeability
                permeability = e.Permeability;

                // Update UI controls with simulated values
                try
                {
                    SafeSetNumericValue(numPorosity, (decimal)Math.Max(0.01, Math.Min(0.5, porosity)));
                    SafeSetNumericValue(numPermeability, (decimal)Math.Max(0.0001f, Math.Min(10, permeability)));
                }
                catch (Exception ex)
                {
                    Logger.Log($"[TriaxialSimulationForm] Error updating UI controls: {ex.Message}");
                }

                // Update UI state
                simulationRunning = false;
                btnStartSimulation.Enabled = true;
                btnStopSimulation.Enabled = false;
                comboMaterials.Enabled = true;
                comboDirection.Enabled = true;
                btnExportResults.Enabled = true;

                btnExportToVolume.Enabled = true;

                // Enable continue button if failure was detected and we haven't reached max strain
                btnContinueSimulation.Enabled = e.HasFailed && currentStrain < maxStrain;

                // Update progress
                progressBar.Value = 100;

                // CRITICAL FIX: Properly report failure
                if (e.HasFailed)
                {
                    progressLabel.Text = "Simulation completed - Material failed";
                    progressLabel.ForeColor = Color.Red;
                }
                else
                {
                    progressLabel.Text = "Simulation completed";
                    progressLabel.ForeColor = SystemColors.ControlText;
                }

                // Force normals recalculation with the updated vertex positions
                RecalculateNormals();

                // Release and rebuild OpenGL resources
                ReleaseVBOs();

                if (highDetailList != 0)
                {
                    GL.DeleteLists(highDetailList, 1);
                    GL.DeleteLists(lowDetailList, 1);
                    highDetailList = 0;
                    lowDetailList = 0;
                }

                if (meshDisplayList != 0)
                {
                    GL.DeleteLists(meshDisplayList, 1);
                    meshDisplayList = 0;
                }

                // Redraw everything
                glControl.Invalidate();
                if (stressStrainGraph != null && stressStrainGraph.IsHandleCreated)
                    stressStrainGraph.Invalidate();
                if (mohrCoulombGraph != null && mohrCoulombGraph.IsHandleCreated)
                    mohrCoulombGraph.Invalidate();

                // Update diagrams form
                UpdateDiagramsForm();

                // Log results with more details
                Logger.Log($"[TriaxialSimulationForm] Simulation completed. Max stress: {stressStrainCurve.Max(p => p.Y / 10.0f):F2} MPa");

                if (e.PeakStress > 0)
                {
                    Logger.Log($"[TriaxialSimulationForm] Peak stress: {e.PeakStress:F2} MPa at strain {e.StrainAtPeak:P2}");
                }

                if (e.HasFailed)
                {
                    Logger.Log("[TriaxialSimulationForm] Sample failed during simulation");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error processing simulation results: {ex.Message}");

                // Ensure UI state is reset even if processing fails
                simulationRunning = false;
                btnStartSimulation.Enabled = true;
                btnStopSimulation.Enabled = false;
                btnContinueSimulation.Enabled = false;
                comboMaterials.Enabled = true;
                comboDirection.Enabled = true;
            }
        }
        private void HandleComputeProgress(DirectComputeProgressEventArgs e)
        {
            try
            {
                // Update progress bar
                progressBar.Value = (int)Math.Min(100, e.ProgressPercent);
                progressLabel.Text = $"Progress: {e.ProgressPercent:F1}%";

                // Update current stats
                currentStrain = e.ProgressPercent / 100f * maxStrain;
                normalStress = e.CurrentStress;

                // Create or update stress-strain point for current step
                int stressPoint = (int)(e.CurrentStress * 10);  // Match the scaling used in CPU sim
                int strainPoint = (int)(currentStrain * 1000);  // Convert to 0.1% units

                // Update or add point to stress-strain curve with thread safety
                lock (stressStrainCurve)
                {
                    bool pointUpdated = false;
                    for (int i = 0; i < stressStrainCurve.Count; i++)
                    {
                        if (stressStrainCurve[i].X == strainPoint)
                        {
                            stressStrainCurve[i] = new Point(strainPoint, stressPoint);
                            pointUpdated = true;
                            break;
                        }
                    }

                    if (!pointUpdated)
                    {
                        stressStrainCurve.Add(new Point(strainPoint, stressPoint));
                    }
                }

                // Update volumetric strain and energy values
                volumetricStrain = e.VolumetricStrain;

                // Store values for the diagrams
                shearStress = e.CurrentStress / 2.0f;  // Approximate for progress updates
                normalStress = e.CurrentStress / 2.0f + (float)minPressure / 1000f;

                // Update porosity consistently with the calculation in DirectTriaxialCompute
                float newPorosity = porosity * (1.0f - e.VolumetricStrain);
                porosity = Math.Max(0.01f, Math.Min(0.5f, newPorosity));

                // Update permeability
                permeability = e.Permeability;

                // Update UI controls with simulation values (only if not in fast mode)
                if (!fastSimulationMode)
                {
                    try
                    {
                        SafeSetNumericValue(numPorosity, (decimal)Math.Max(0.01, Math.Min(0.5, porosity)));
                        SafeSetNumericValue(numPermeability, (decimal)Math.Max(0.0001f, Math.Min(10, permeability)));
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[TriaxialSimulationForm] Error updating UI controls: {ex.Message}");
                    }
                }

                // Display warning for approaching failure
                if (e.FailurePercentage > 90)
                {
                    progressLabel.Text = $"WARNING: Approaching failure ({e.FailurePercentage:F0}%)";
                    progressLabel.ForeColor = Color.Red;
                }

                // Only update UI in non-fast mode
                if (!fastSimulationMode)
                {
                    // Unlike the completed event, the progress event doesn't provide the deformed vertices
                    // So we don't update the mesh during progress updates

                    // Force UI updates
                    glControl.Invalidate();

                    if (stressStrainGraph != null && stressStrainGraph.IsHandleCreated)
                        stressStrainGraph.Invalidate();

                    if (mohrCoulombGraph != null && mohrCoulombGraph.IsHandleCreated)
                        mohrCoulombGraph.Invalidate();

                    // Update diagrams form with current data
                    if (diagramsForm != null && !diagramsForm.IsDisposed)
                    {
                        UpdateDiagramsWithAcceleratedData(e);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error handling progress update: {ex.Message}");
            }
        }
        private void UpdateDiagramsWithAcceleratedData(DirectComputeProgressEventArgs e)
        {
            try
            {
                // Make a copy of the current stress-strain data to avoid threading issues
                List<Point> currentData = new List<Point>(stressStrainCurve);

                // Call the standard update method in diagram form
                diagramsForm.UpdateData(
                    currentData,                   // Current stress-strain curve
                    currentStrain,                 // Current strain
                    e.CurrentStress,               // Current stress
                    cohesion,                      // Cohesion
                    frictionAngle,                 // Friction angle
                    normalStress,                  // Normal stress
                    shearStress,                   // Shear stress
                    bulkDensity,                   // Bulk density
                    porosity,                      // Porosity (updated based on volumetric strain)
                    minPressure,                   // Min pressure
                    maxPressure,                   // Max pressure
                    yieldStrength,                 // Yield strength
                    brittleStrength,               // Brittle strength
                    isElasticEnabled,              // Elastic enabled
                    isPlasticEnabled,              // Plastic enabled
                    isBrittleEnabled,              // Brittle enabled
                    true                           // Simulation is running
                );

                // If using the enhanced diagrams form, update with additional data
                if (diagramsForm is TriaxialDiagramsForm diagForm)
                {
                    try
                    {
                        // Calculate peak stress and strain at peak if available
                        float peakStress = 0;
                        float strainAtPeak = 0;

                        if (currentData.Count > 0)
                        {
                            // Find maximum stress point
                            int maxY = currentData.Max(p => p.Y);
                            int peakIndex = currentData.FindIndex(p => p.Y == maxY);

                            if (peakIndex >= 0)
                            {
                                peakStress = maxY / 10.0f; // Convert back to MPa
                                strainAtPeak = currentData[peakIndex].X / 10.0f; // Convert to percentage
                            }
                        }

                        float porePressure = e.PorePressure;
                        float effSigma1 = e.CurrentStress + (minPressure / 1000f) - porePressure;
                        float effSigma3 = minPressure / 1000f - porePressure;

                        // Directly call the method with all required parameters
                        diagForm.UpdateEnhancedData(
                            currentData,
                            currentStrain,
                            e.CurrentStress,
                            cohesion,
                            frictionAngle,
                            normalStress,
                            shearStress,
                            bulkDensity,
                            porosity,
                            minPressure / 1000f, // Convert to MPa
                            maxPressure / 1000f, // Convert to MPa
                            yieldStrength,
                            brittleStrength,
                            isElasticEnabled,
                            isPlasticEnabled,
                            isBrittleEnabled,
                            true,               // Simulation running
                            porePressure,       // Pore pressure
                            effSigma1,          // Effective major principal stress
                            effSigma3,          // Effective minor principal stress
                            e.Permeability,     // Current permeability
                            e.Permeability / permeability, // Permeability ratio
                            e.VolumetricStrain, // Volumetric strain
                            elasticEnergy,      // Elastic energy (estimate)
                            plasticEnergy,      // Plastic energy (estimate)
                            e.HasFailed,        // Failure occurred
                            e.FailurePercentage, // Percent of failure criterion
                            peakStress,         // Peak stress - MISSING PARAMETER
                            strainAtPeak        // Strain at peak - MISSING PARAMETER
                        );
                    }
                    catch (Exception ex)
                    {
                        // Log the error but don't rethrow - fall back to standard update
                        Logger.Log($"[TriaxialSimulationForm] Enhanced diagram update failed: {ex.Message}. Using standard update.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error updating diagrams: {ex.Message}");
            }
        }
        private void DrawColorLegendGL()
        {
            // Save OpenGL state
            bool depthTestEnabled = GL.IsEnabled(EnableCap.DepthTest);
            bool lightingEnabled = GL.IsEnabled(EnableCap.Lighting);
            bool blendEnabled = GL.IsEnabled(EnableCap.Blend);

            // Setup 2D orthographic projection
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, glControl.Height, 0, -1, 1);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.LoadIdentity();

            // Set up for 2D rendering
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Lighting);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            // Colorbar parameters
            int barWidth = 25;
            int barHeight = 200;
            int barX = glControl.Width - barWidth - 10;
            int barY = 50;

            // Draw colorbar background
            GL.Color4(0.1f, 0.1f, 0.1f, 0.5f);
            GL.Begin(PrimitiveType.Quads);
            GL.Vertex2(barX - 60, barY - 30);
            GL.Vertex2(barX + barWidth + 5, barY - 30);
            GL.Vertex2(barX + barWidth + 5, barY + barHeight + 5);
            GL.Vertex2(barX - 60, barY + barHeight + 5);
            GL.End();

            // Draw colorbar gradient
            GL.Begin(PrimitiveType.Quads);
            for (int i = 0; i < barHeight; i++)
            {
                float normalizedValue = 1.0f - (i / (float)barHeight);

                // Set color for current position
                SetColorForValue(normalizedValue);

                GL.Vertex2(barX, barY + i);
                GL.Vertex2(barX + barWidth, barY + i);

                SetColorForValue(1.0f - ((i + 1) / (float)barHeight));

                GL.Vertex2(barX + barWidth, barY + i + 1);
                GL.Vertex2(barX, barY + i + 1);
            }
            GL.End();

            // Draw colorbar border
            GL.Color3(1.0f, 1.0f, 1.0f);
            GL.Begin(PrimitiveType.LineLoop);
            GL.Vertex2(barX, barY);
            GL.Vertex2(barX + barWidth, barY);
            GL.Vertex2(barX + barWidth, barY + barHeight);
            GL.Vertex2(barX, barY + barHeight);
            GL.End();

            // Draw tick marks and labels
            GL.Begin(PrimitiveType.Lines);
            int numTicks = 6;
            for (int i = 0; i < numTicks; i++)
            {
                float yPos = barY + i * (barHeight / (numTicks - 1));
                GL.Vertex2(barX, yPos);
                GL.Vertex2(barX - 5, yPos);
            }
            GL.End();

            // Draw title
            RenderText("Density", barX - 55, barY - 25, 1.0f);

            // Draw unit label
            string unitText = isDensityCalibrated ? "kg/m³" : "(norm)";
            RenderText(unitText, barX - 55, barY - 5, 0.8f);

            // Draw tick labels
            for (int i = 0; i < numTicks; i++)
            {
                float normalizedValue = 1.0f - (i / (float)(numTicks - 1));
                float value = minDensity + (maxDensity - minDensity) * normalizedValue;
                float yPos = barY + i * (barHeight / (numTicks - 1)) - 8;

                string label = isDensityCalibrated ?
                    $"{value:F0}" : // Density in kg/m³
                    $"{normalizedValue:F1}"; // Normalized value

                RenderText(label, barX - 45, yPos, 0.8f);
            }

            // Draw max/min labels if calibrated
            if (isDensityCalibrated)
            {
                RenderText("Max", barX - 55, barY - 15, 0.8f);
                RenderText("Min", barX - 55, barY + barHeight - 10, 0.8f);
            }

            // Restore OpenGL state
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PopMatrix();

            if (depthTestEnabled) GL.Enable(EnableCap.DepthTest);
            if (lightingEnabled) GL.Enable(EnableCap.Lighting);
            if (!blendEnabled) GL.Disable(EnableCap.Blend);
        }
        private void BtnExportResults_Click(object sender, EventArgs e)
        {
            // Check if we have simulation results to export
            if (stressStrainCurve == null || stressStrainCurve.Count == 0)
            {
                MessageBox.Show("No simulation results available to export.",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                // Ask user for the file format
                string fileFormat = AskForFileFormat();
                if (string.IsNullOrEmpty(fileFormat))
                    return; // User cancelled

                // Create save file dialog
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Title = "Save Simulation Results";
                    saveDialog.Filter = fileFormat == "csv" ?
                        "CSV Files (*.csv)|*.csv" :
                        "Excel Files (*.xlsx)|*.xlsx";
                    saveDialog.DefaultExt = fileFormat;
                    saveDialog.FileName = $"TriaxialSimulation_{DateTime.Now:yyyyMMdd_HHmmss}";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        // Show progress dialog
                        var progressForm = new ProgressFormWithProgress("Exporting simulation results...");
                        progressForm.Show();

                        // Export in background to keep UI responsive
                        Task.Run(() =>
                        {
                            try
                            {
                                if (fileFormat == "csv")
                                    ExportToCsv(saveDialog.FileName, progressForm);
                                else
                                    ExportToExcel(saveDialog.FileName, progressForm);

                                this.SafeInvokeAsync(() =>
                                {
                                    progressForm.Close();
                                    MessageBox.Show("Simulation results exported successfully!",
                                                   "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                });
                            }
                            catch (Exception ex)
                            {
                                this.SafeInvokeAsync(() =>
                                {
                                    progressForm.Close();
                                    MessageBox.Show($"Error exporting results: {ex.Message}",
                                                  "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                    Logger.Log($"[TriaxialSimulationForm] Error exporting results: {ex.Message}");
                                });
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting up export: {ex.Message}",
                              "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[TriaxialSimulationForm] Error setting up export: {ex.Message}");
            }
        }
        private string AskForFileFormat()
        {
            string result = null;

            using (Form formatForm = new Form()
            {
                Text = "Choose Export Format",
                Width = 300,
                Height = 150,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            })
            {
                Label label = new Label()
                {
                    Text = "Choose export format:",
                    Location = new Point(20, 20),
                    AutoSize = true
                };
                formatForm.Controls.Add(label);

                Button btnCsv = new Button()
                {
                    Text = "CSV Format",
                    Location = new Point(20, 60),
                    Width = 120,
                    Height = 30
                };
                btnCsv.Click += (s, e) => { result = "csv"; formatForm.DialogResult = DialogResult.OK; };
                formatForm.Controls.Add(btnCsv);

                Button btnExcel = new Button()
                {
                    Text = "Excel Format",
                    Location = new Point(150, 60),
                    Width = 120,
                    Height = 30
                };
                btnExcel.Click += (s, e) => { result = "xlsx"; formatForm.DialogResult = DialogResult.OK; };
                formatForm.Controls.Add(btnExcel);

                formatForm.ShowDialog();
            }

            return result;
        }
        private void ExportToCsv(string filePath, ProgressFormWithProgress progressForm)
        {
            try
            {
                this.SafeInvokeAsync(() => progressForm.UpdateProgress(10));

                // Create a CSV string builder
                StringBuilder csv = new StringBuilder();

                // Add header
                csv.AppendLine("Simulation Results");
                csv.AppendLine($"Date: {DateTime.Now}");
                csv.AppendLine($"Material: {(selectedMaterial != null ? selectedMaterial.Name : "Unknown")}");
                csv.AppendLine();

                // Add material properties section
                csv.AppendLine("Material Properties:");
                csv.AppendLine($"Bulk Density (kg/m³),{bulkDensity}");
                csv.AppendLine($"Young's Modulus (MPa),{youngModulus}");
                csv.AppendLine($"Poisson's Ratio,{poissonRatio}");
                csv.AppendLine($"Yield Strength (MPa),{yieldStrength}");
                csv.AppendLine($"Brittle Strength (MPa),{brittleStrength}");
                csv.AppendLine($"Cohesion (MPa),{cohesion}");
                csv.AppendLine($"Friction Angle (°),{frictionAngle}");
                csv.AppendLine($"Porosity,{porosity}");
                csv.AppendLine($"Permeability (mD),{permeability}");
                csv.AppendLine();

                // Add simulation parameters
                csv.AppendLine("Simulation Parameters:");
                csv.AppendLine($"Direction,{selectedDirection}");
                csv.AppendLine($"Min Pressure (kPa),{minPressure}");
                csv.AppendLine($"Max Pressure (kPa),{maxPressure}");
                csv.AppendLine($"Elastic Behavior,{isElasticEnabled}");
                csv.AppendLine($"Plastic Behavior,{isPlasticEnabled}");
                csv.AppendLine($"Brittle Behavior,{isBrittleEnabled}");
                csv.AppendLine();

                this.SafeInvokeAsync(() => progressForm.UpdateProgress(40));

                // Add stress-strain curve data
                csv.AppendLine("Stress-Strain Data:");
                csv.AppendLine("Strain (%),Stress (MPa)");

                foreach (var point in stressStrainCurve)
                {
                    // Convert units: point.X is in 0.1% units, point.Y is in 0.1 MPa units
                    double strain = point.X / 10.0; // Convert to %
                    double stress = point.Y / 10.0; // Convert to MPa
                    csv.AppendLine($"{strain:F4},{stress:F4}");
                }
                csv.AppendLine();

                // Add final results section
                csv.AppendLine("Final Results:");
                csv.AppendLine($"Max Strain (%),{currentStrain * 100:F4}");

                // Find peak stress
                double peakStress = 0;
                double strainAtPeak = 0;
                if (stressStrainCurve.Count > 0)
                {
                    int maxY = stressStrainCurve.Max(p => p.Y);
                    int peakIndex = stressStrainCurve.FindIndex(p => p.Y == maxY);

                    if (peakIndex >= 0)
                    {
                        peakStress = maxY / 10.0; // Convert to MPa
                        strainAtPeak = stressStrainCurve[peakIndex].X / 10.0; // Convert to %
                    }
                }

                csv.AppendLine($"Peak Stress (MPa),{peakStress:F4}");
                csv.AppendLine($"Strain at Peak (%),{strainAtPeak:F4}");
                csv.AppendLine($"Volumetric Strain,{volumetricStrain:F6}");
                csv.AppendLine($"Final Porosity,{porosity:F4}");
                csv.AppendLine($"Final Permeability (mD),{permeability:F6}");
                csv.AppendLine($"Failure Status,{(failureState ? "Failed" : "Intact")}");

                this.SafeInvokeAsync(() => progressForm.UpdateProgress(80));

                // Write to file
                System.IO.File.WriteAllText(filePath, csv.ToString());

                this.SafeInvokeAsync(() => progressForm.UpdateProgress(100));
            }
            catch (Exception ex)
            {
                throw new Exception("Error creating CSV file: " + ex.Message, ex);
            }
        }
        private void ExportToExcel(string filePath, ProgressFormWithProgress progressForm)
        {
            try
            {
                this.SafeInvokeAsync(() => progressForm.UpdateProgress(10));

                // Create Excel application
                Microsoft.Office.Interop.Excel.Application excelApp = new Microsoft.Office.Interop.Excel.Application();
                excelApp.Visible = false;

                // Create workbook
                Microsoft.Office.Interop.Excel.Workbook workbook = excelApp.Workbooks.Add();

                // Create worksheets
                Microsoft.Office.Interop.Excel.Worksheet summarySheet = workbook.Sheets[1];
                summarySheet.Name = "Summary";

                Microsoft.Office.Interop.Excel.Worksheet dataSheet = workbook.Sheets.Add();
                dataSheet.Name = "Stress-Strain Data";

                this.SafeInvokeAsync(() => progressForm.UpdateProgress(30));

                // Add header to summary sheet
                summarySheet.Cells[1, 1] = "Simulation Results";
                summarySheet.Cells[2, 1] = "Date:";
                summarySheet.Cells[2, 2] = DateTime.Now.ToString();
                summarySheet.Cells[3, 1] = "Material:";
                summarySheet.Cells[3, 2] = selectedMaterial != null ? selectedMaterial.Name : "Unknown";

                // Add material properties
                summarySheet.Cells[5, 1] = "Material Properties";
                summarySheet.Cells[6, 1] = "Bulk Density (kg/m³)";
                summarySheet.Cells[6, 2] = bulkDensity;
                summarySheet.Cells[7, 1] = "Young's Modulus (MPa)";
                summarySheet.Cells[7, 2] = youngModulus;
                summarySheet.Cells[8, 1] = "Poisson's Ratio";
                summarySheet.Cells[8, 2] = poissonRatio;
                summarySheet.Cells[9, 1] = "Yield Strength (MPa)";
                summarySheet.Cells[9, 2] = yieldStrength;
                summarySheet.Cells[10, 1] = "Brittle Strength (MPa)";
                summarySheet.Cells[10, 2] = brittleStrength;
                summarySheet.Cells[11, 1] = "Cohesion (MPa)";
                summarySheet.Cells[11, 2] = cohesion;
                summarySheet.Cells[12, 1] = "Friction Angle (°)";
                summarySheet.Cells[12, 2] = frictionAngle;
                summarySheet.Cells[13, 1] = "Porosity";
                summarySheet.Cells[13, 2] = porosity;
                summarySheet.Cells[14, 1] = "Permeability (mD)";
                summarySheet.Cells[14, 2] = permeability;

                // Add simulation parameters
                summarySheet.Cells[16, 1] = "Simulation Parameters";
                summarySheet.Cells[17, 1] = "Direction";
                summarySheet.Cells[17, 2] = selectedDirection.ToString();
                summarySheet.Cells[18, 1] = "Min Pressure (kPa)";
                summarySheet.Cells[18, 2] = minPressure;
                summarySheet.Cells[19, 1] = "Max Pressure (kPa)";
                summarySheet.Cells[19, 2] = maxPressure;
                summarySheet.Cells[20, 1] = "Elastic Behavior";
                summarySheet.Cells[20, 2] = isElasticEnabled ? "Yes" : "No";
                summarySheet.Cells[21, 1] = "Plastic Behavior";
                summarySheet.Cells[21, 2] = isPlasticEnabled ? "Yes" : "No";
                summarySheet.Cells[22, 1] = "Brittle Behavior";
                summarySheet.Cells[22, 2] = isBrittleEnabled ? "Yes" : "No";

                this.SafeInvokeAsync(() => progressForm.UpdateProgress(50));

                // Add final results
                summarySheet.Cells[24, 1] = "Final Results";
                summarySheet.Cells[25, 1] = "Max Strain (%)";
                summarySheet.Cells[25, 2] = currentStrain * 100;

                // Find peak stress
                double peakStress = 0;
                double strainAtPeak = 0;
                if (stressStrainCurve.Count > 0)
                {
                    int maxY = stressStrainCurve.Max(p => p.Y);
                    int peakIndex = stressStrainCurve.FindIndex(p => p.Y == maxY);

                    if (peakIndex >= 0)
                    {
                        peakStress = maxY / 10.0; // Convert to MPa
                        strainAtPeak = stressStrainCurve[peakIndex].X / 10.0; // Convert to %
                    }
                }

                summarySheet.Cells[26, 1] = "Peak Stress (MPa)";
                summarySheet.Cells[26, 2] = peakStress;
                summarySheet.Cells[27, 1] = "Strain at Peak (%)";
                summarySheet.Cells[27, 2] = strainAtPeak;
                summarySheet.Cells[28, 1] = "Volumetric Strain";
                summarySheet.Cells[28, 2] = volumetricStrain;
                summarySheet.Cells[29, 1] = "Final Porosity";
                summarySheet.Cells[29, 2] = porosity;
                summarySheet.Cells[30, 1] = "Final Permeability (mD)";
                summarySheet.Cells[30, 2] = permeability;
                summarySheet.Cells[31, 1] = "Failure Status";
                summarySheet.Cells[31, 2] = failureState ? "Failed" : "Intact";

                // Format summary sheet
                summarySheet.Columns.AutoFit();

                this.SafeInvokeAsync(() => progressForm.UpdateProgress(70));

                // Add stress-strain data to data sheet
                dataSheet.Cells[1, 1] = "Strain (%)";
                dataSheet.Cells[1, 2] = "Stress (MPa)";

                for (int i = 0; i < stressStrainCurve.Count; i++)
                {
                    // Convert units: point.X is in 0.1% units, point.Y is in 0.1 MPa units
                    double strain = stressStrainCurve[i].X / 10.0; // Convert to %
                    double stress = stressStrainCurve[i].Y / 10.0; // Convert to MPa

                    dataSheet.Cells[i + 2, 1] = strain;
                    dataSheet.Cells[i + 2, 2] = stress;
                }

                // Create chart of stress-strain curve
                if (stressStrainCurve.Count > 0)
                {
                    Microsoft.Office.Interop.Excel.ChartObjects charts = dataSheet.ChartObjects();
                    Microsoft.Office.Interop.Excel.ChartObject chartObj = charts.Add(300, 10, 500, 300);
                    Microsoft.Office.Interop.Excel.Chart chart = chartObj.Chart;

                    // Define data range for chart
                    Microsoft.Office.Interop.Excel.Range dataRange = dataSheet.Range[
                        dataSheet.Cells[2, 1],
                        dataSheet.Cells[stressStrainCurve.Count + 1, 2]
                    ];

                    chart.SetSourceData(dataRange);
                    chart.ChartType = Microsoft.Office.Interop.Excel.XlChartType.xlXYScatterSmooth;
                    chart.HasTitle = true;
                    chart.ChartTitle.Text = "Stress-Strain Curve";

                    // Format chart
                    chart.Axes(Microsoft.Office.Interop.Excel.XlAxisType.xlCategory).HasTitle = true;
                    chart.Axes(Microsoft.Office.Interop.Excel.XlAxisType.xlCategory).AxisTitle.Text = "Strain (%)";
                    chart.Axes(Microsoft.Office.Interop.Excel.XlAxisType.xlValue).HasTitle = true;
                    chart.Axes(Microsoft.Office.Interop.Excel.XlAxisType.xlValue).AxisTitle.Text = "Stress (MPa)";
                }

                // Format data sheet
                dataSheet.Columns.AutoFit();

                this.SafeInvokeAsync(() => progressForm.UpdateProgress(90));

                // Save workbook
                workbook.SaveAs(filePath);

                // Clean up Excel objects
                workbook.Close(false);
                excelApp.Quit();

                // Release COM objects to avoid memory leaks
                System.Runtime.InteropServices.Marshal.ReleaseComObject(dataSheet);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(summarySheet);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(workbook);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(excelApp);

                this.SafeInvokeAsync(() => progressForm.UpdateProgress(100));
            }
            catch (Exception ex)
            {
                throw new Exception("Error creating Excel file: " + ex.Message, ex);
            }
        }

        private void RenderText(string text, float x, float y, float scale, bool centerX = false)
        {
            if (!fontTextureInitialized)
                InitializeFontTexture();

            // Bind texture
            GL.Enable(EnableCap.Texture2D);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.BindTexture(TextureTarget.Texture2D, fontTextureId);

            // If centering, calculate width
            if (centerX)
            {
                float width = text.Length * 16 * scale;
                x -= width / 2;
            }

            GL.Begin(PrimitiveType.Quads);
            GL.Color4(1.0f, 1.0f, 1.0f, 1.0f); // White text

            float xPos = x;
            foreach (char c in text)
            {
                if (characterRects.TryGetValue(c, out Rectangle rect))
                {
                    float texX = rect.X / 256.0f;
                    float texY = rect.Y / 256.0f;
                    float texWidth = rect.Width / 256.0f;
                    float texHeight = rect.Height / 256.0f;

                    float charWidth = rect.Width * scale;
                    float charHeight = rect.Height * scale;

                    // Draw character quad
                    GL.TexCoord2(texX, texY);
                    GL.Vertex2(xPos, y);

                    GL.TexCoord2(texX + texWidth, texY);
                    GL.Vertex2(xPos + charWidth, y);

                    GL.TexCoord2(texX + texWidth, texY + texHeight);
                    GL.Vertex2(xPos + charWidth, y + charHeight);

                    GL.TexCoord2(texX, texY + texHeight);
                    GL.Vertex2(xPos, y + charHeight);

                    xPos += charWidth;
                }
            }
            GL.End();

            // Restore state
            GL.Disable(EnableCap.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose DirectCompute engine
                if (computeEngine != null)
                {
                    computeEngine.Dispose();
                    computeEngine = null;
                }

                // Dispose OpenGL resources
                DisposeDisplayList();
                if (meshDisplayList != 0)
                {
                    GL.DeleteLists(meshDisplayList, 1);
                    meshDisplayList = 0;
                }
                if (fontTextureInitialized && fontTextureId != 0)
                {
                    GL.DeleteTextures(1, ref fontTextureId);
                    fontTextureId = 0;
                }
                if (highDetailList != 0)
                {
                    GL.DeleteLists(highDetailList, 1);
                    GL.DeleteLists(lowDetailList, 1);
                    highDetailList = 0;
                    lowDetailList = 0;
                }

                ReleaseVBOs();

                if (fontTexture != null)
                {
                    fontTexture.Dispose();
                    fontTexture = null;
                }

                // Delete VBOs
                if (buffersInitialized)
                {
                    GL.DeleteBuffers(1, ref vertexBufferId);
                    GL.DeleteBuffers(1, ref normalBufferId);
                    GL.DeleteBuffers(1, ref colorBufferId);
                    GL.DeleteBuffers(1, ref indexBufferId);
                    buffersInitialized = false;
                }

                if (rotationTimer != null)
                {
                    rotationTimer.Stop();
                    rotationTimer.Dispose();
                    rotationTimer = null;
                }

                // Dispose other managed resources if needed
                if (meshWorker != null)
                {
                    meshWorker.Dispose();
                }

                if (simulationTimer != null)
                {
                    simulationTimer.Stop();
                    simulationTimer.Dispose();
                }

                if (glControl != null)
                {
                    glControl.Dispose();
                }

                if (diagramsForm != null && !diagramsForm.IsDisposed)
                {
                    diagramsForm.Dispose();
                    diagramsForm = null;
                }
            }

            base.Dispose(disposing);
        }
    }
    
    public class TetrahedralElement
    {
        public int[] Vertices { get; private set; }

        public TetrahedralElement(int v1, int v2, int v3, int v4)
        {
            Vertices = new int[] { v1, v2, v3, v4 };
        }
    }

    public class MeshGenerationParameters
    {
        public Material Material { get; set; }
        public int SamplingRate { get; set; }
    }

    
}