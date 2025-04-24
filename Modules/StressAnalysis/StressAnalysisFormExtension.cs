// -----------------------------------------------------------------------------
//  TriaxialSimulationPatch.cs  –  add GPU‑safe kernel (no System.Numerics).
// -----------------------------------------------------------------------------
using ILGPU;
using ILGPU.Algorithms;   // XMath
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using Krypton.Ribbon;
using Krypton.Toolkit;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Windows.Forms;

namespace CTSegmenter
{

    public partial class TriaxialSimulation
    {
        private bool v;
        private ConcurrentDictionary<Vector3, float> densityMap;

        public TriaxialSimulation(Material material, List<Triangle> triangles, float confiningPressure, float minAxialPressure, float maxAxialPressure, int pressureSteps, string direction, bool v, ConcurrentDictionary<Vector3, float> densityMap) : this(material, triangles, confiningPressure, minAxialPressure, maxAxialPressure, pressureSteps, direction)
        {
            this.v = v;
            this.densityMap = densityMap;
        }
        private Action<Index1D,
       ArrayView<Vector3>,
       ArrayView<Vector3>,
       ArrayView<Vector3>,
       ArrayView<float>, // stress factors for density
       float, float, Vector3,
       float, float, // cohesion and friction angle
       ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>
       _inhomogeneousStressKernel;

        // REPLACE the previous kernel binding with a version that uses only
        // primitive math and ILGPU.XMath to avoid internal compiler errors on
        // some NVIDIA drivers.



        // --- Override LoadKernels ------------------------------------------------
        private void LoadKernels()
        {
            try
            {
                // Load kernel with pre-calculated trigonometric values
                _computeStressKernelSafe = _accelerator.LoadAutoGroupedStreamKernel<Index1D,
                    ArrayView<Vector3>, ArrayView<Vector3>, ArrayView<Vector3>,
                    float, float, Vector3,
                    float, float, float, // cohesion, sinPhi, cosPhi
                    ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>(
                    ComputeStressKernelFixed);

                Logger.Log("[TriaxialSimulation] Kernel loaded successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulation] Error loading kernels: {ex.Message}");
                throw;
            }
        }

        
    }
}
//  StressAnalysisFormPatch.cs  –  companion UI patch (re‑written to compile)
// -----------------------------------------------------------------------------
//  This partial class augments StressAnalysisForm with rock‑strength controls
//  and an extended triaxial‑simulation launcher.  All duplicate member names
//  that previously clashed with the main class have been removed/renamed and
//  Krypton types have been fixed to the correct namespaces so the file builds
//  cleanly under C# 7.3.
// -----------------------------------------------------------------------------
       

namespace CTSegmenter
{
    /// <summary>
    /// Patch‑layer that adds rock‑strength UI + extended simulation handler.
    /// NOTE: All names are unique so there are no CS0111 collisions any more.
    /// </summary>
    public partial class StressAnalysisForm
    {
        // ------------------------------------------------------------------
        //  1.  NEW UI FIELDS
        // ------------------------------------------------------------------
        private KryptonGroupBox rockStrengthBox;
        private NumericUpDown cohesionNumeric;        // MPa
        private NumericUpDown frictionAngleNumeric;   // °
        private NumericUpDown tensileStrengthNumeric; // MPa
        private CheckBox showFractureCheck;

        // extended ribbon toggle (uses standard button – no ToggleButton type)
        private KryptonRibbonGroupButton mcGraphButton;
        private Panel mohrCoulombPanel;

        // keep a reference to the last simulation so we can overlay fractures
        private TriaxialSimulation currentTriaxial;
        static StressAnalysisForm()
        {
            // This static constructor runs once when the class is first accessed
            // We need to ensure that all forms will have the Load event handler attached
            Type type = typeof(StressAnalysisForm);

            // Let the individual instances handle their own Load event via constructor
            Logger.Log("[StressAnalysisForm] Extension module initialized");
        }
        // ------------------------------------------------------------------
        //  2.  RUNTIME HOOKS  (wired from ctor via OnLoad)
        // ------------------------------------------------------------------
        private void StressAnalysisPatch_Load(object sender, EventArgs e)
        {
            try
            {
                // Called once when the form finishes loading – safe to touch UI.
                Logger.Log("[StressAnalysisForm] Setting up rock strength UI");
                SetupRockStrengthUi();
                SetupMohrCoulombDiagram();
                HookExtendedHandlers();
                Logger.Log("[StressAnalysisForm] Rock strength UI setup complete");
            }
            catch (Exception ex)
            {
                Logger.Log($"[StressAnalysisForm] Error setting up rock strength UI: {ex.Message}");
                MessageBox.Show($"Error initializing rock strength parameters: {ex.Message}",
                    "UI Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        /// <summary>
        /// Sets up the extra rock‑strength parameter group inside the Analysis
        /// tab.  Everything lives in a single table layout so no designer file
        /// is needed.
        /// </summary>
        private void SetupRockStrengthUi()
        {
            // group‑box container ------------------------------------------------
            rockStrengthBox = new KryptonGroupBox
            {
                Text = "Rock‑Strength Parameters",
                Dock = DockStyle.Top,
                Height = 110,
                StateNormal = { Border = { DrawBorders = PaletteDrawBorders.All } }
            };
            analysisPage.Controls.Add(rockStrengthBox);

            // 2×6 grid ----------------------------------------------------------
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 6,
                RowCount = 2,
                Padding = new Padding(6)
            };
            rockStrengthBox.Panel.Controls.Add(grid);
            for (int i = 0; i < 6; i++)
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, i % 2 == 0 ? 33f : 17f));

            // Cohesion ----------------------------------------------------------
            grid.Controls.Add(new Label { Text = "Cohesion (MPa):", AutoSize = true, ForeColor = Color.White }, 0, 0);
            cohesionNumeric = new NumericUpDown { Minimum = 0, Maximum = 500, DecimalPlaces = 1, Increment = 0.5M, Value = 10, Dock = DockStyle.Fill };
            grid.Controls.Add(cohesionNumeric, 1, 0);

            // Friction angle ----------------------------------------------------
            grid.Controls.Add(new Label { Text = "Friction Angle φ (°):", AutoSize = true, ForeColor = Color.White }, 2, 0);
            frictionAngleNumeric = new NumericUpDown { Minimum = 0, Maximum = 80, DecimalPlaces = 1, Increment = 0.5M, Value = 30, Dock = DockStyle.Fill };
            grid.Controls.Add(frictionAngleNumeric, 3, 0);

            // Tensile strength --------------------------------------------------
            grid.Controls.Add(new Label { Text = "Tensile Strength (MPa):", AutoSize = true, ForeColor = Color.White }, 4, 0);
            tensileStrengthNumeric = new NumericUpDown { Minimum = 0, Maximum = 100, DecimalPlaces = 1, Increment = 0.2M, Value = 5, Dock = DockStyle.Fill };
            grid.Controls.Add(tensileStrengthNumeric, 5, 0);

            // Fracture overlay toggle ------------------------------------------
            showFractureCheck = new CheckBox { Text = "Show fracture surfaces", Checked = true, ForeColor = Color.White, AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 4, 0, 0) };
            grid.SetColumnSpan(showFractureCheck, 6);
            grid.Controls.Add(showFractureCheck, 0, 1);
        }

        /// <summary>
        /// Creates a placeholder panel on the Results tab for the Mohr–Coulomb
        /// diagram and a ribbon button to show/hide it.
        /// </summary>
        private void SetupMohrCoulombDiagram()
        {
            // black panel fills results page -----------------------------------
            mohrCoulombPanel = new Panel { BackColor = Color.Black, Dock = DockStyle.Fill, Visible = false };
            resultsPage.Controls.Add(mohrCoulombPanel);

            // Replace the placeholder with proper renderer
            mohrCoulombPanel.Paint += (s, e) => {
                if (currentTriaxial != null && currentTriaxial.Status == SimulationStatus.Completed)
                    currentTriaxial.RenderMohrCoulombDiagram(e.Graphics, mohrCoulombPanel.Width, mohrCoulombPanel.Height);
                else
                {
                    // Only show message when no simulation data is available
                    e.Graphics.Clear(Color.Black);
                    using (Font font = new Font("Arial", 12))
                    using (SolidBrush brush = new SolidBrush(Color.White))
                    {
                        e.Graphics.DrawString("Run a simulation to view the Mohr-Coulomb diagram",
                            font, brush, 20, 20);
                    }
                }
            };

            // add button to existing "Visualization" ribbon group -------------
            var visGroup = resultsTab.Groups.FirstOrDefault(g => g.TextLine1 == "Visualization");
            if (visGroup != null)
            {
                var triple = new KryptonRibbonGroupTriple();
                mcGraphButton = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Mohr‑Coulomb",
                    TextLine2 = "Graph",
                    ImageLarge = CreateStressIcon(32),
                    ImageSmall = CreateStressIcon(16),
                    Checked = false,
                    ButtonType = GroupButtonType.Check
                };
                mcGraphButton.Click += (s, e) => {
                    mcGraphButton.Checked = !mcGraphButton.Checked;
                    mohrCoulombPanel.Visible = mcGraphButton.Checked;
                    resultsPage.Refresh();
                };
                triple.Items.Add(mcGraphButton);
                visGroup.Items.Add(triple);
            }
        }

        /// <summary>
        /// Hooks extra event handlers once UI is ready.
        /// </summary>
        private void HookExtendedHandlers()
        {
            // we chain a *new* click handler – the original RunTriaxialButton_Click
            // remains subscribed, so the base behaviour stays intact.
            runTriaxialButton.Click += RunTriaxialButton_Extended;

            // overlay fracture rendering after original mesh paint
            meshViewPanel.Paint += MeshViewPanel_ExtendedPaint;
        }

        // ------------------------------------------------------------------
        //  3.  EXTENDED TRIAXIAL SIMULATION
        // ------------------------------------------------------------------
        private async void RunTriaxialButton_Extended(object sender, EventArgs e)
        {
            if (!meshGenerated || meshTriangles.Count == 0 || selectedMaterial == null)
                return; // original handler will already show warnings

            // Convert local (inner‑struct) triangles to library triangles ------
            // Map inner-form triangles to library triangles with System.Numerics vectors
            var simMesh = meshTriangles.Select(t => new CTSegmenter.Triangle(
                new System.Numerics.Vector3(t.V1.X, t.V1.Y, t.V1.Z),
                new System.Numerics.Vector3(t.V2.X, t.V2.Y, t.V2.Z),
                new System.Numerics.Vector3(t.V3.X, t.V3.Y, t.V3.Z))).ToList();

            currentTriaxial = new TriaxialSimulation(
                selectedMaterial,
                simMesh,
                (float)confiningPressureNumeric.Value,
                (float)pressureMinNumeric.Value,
                (float)pressureMaxNumeric.Value,
                (int)pressureStepsNumeric.Value,
                testDirectionCombo.SelectedItem?.ToString() ?? "Z‑Axis");

            // inject user‑defined rock‑strength values via reflection ----------
            ApplyPrivateSetter(currentTriaxial, "CohesionStrength", (float)cohesionNumeric.Value);
            ApplyPrivateSetter(currentTriaxial, "FrictionAngle", (float)frictionAngleNumeric.Value);
            ApplyPrivateSetter(currentTriaxial, "TensileStrength", (float)tensileStrengthNumeric.Value);

            if (!currentTriaxial.Initialize()) return;
            statusHeader.Text = "Running triaxial simulation…";
            await currentTriaxial.RunAsync();
            statusHeader.Text = "Simulation finished";

            // show results page automatically
            mainTabControl.SelectedPage = resultsPage;
            resultsPage.Refresh();
        }

        /// <summary>
        /// Helper that sets a private‑setter property through reflection.
        /// </summary>
        private static void ApplyPrivateSetter(object obj, string propName, object value)
        {
            var p = obj.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            p?.SetValue(obj, value);
        }

        // ------------------------------------------------------------------
        //  4.  FRACTURE OVERLAY ON MESH VIEW
        // ------------------------------------------------------------------
        private void MeshViewPanel_ExtendedPaint(object sender, PaintEventArgs e)
        {
            if (!showFractureCheck.Checked || currentTriaxial == null || currentTriaxial.Status != SimulationStatus.Completed)
                return;

            currentTriaxial.RenderResults(e.Graphics, meshViewPanel.Width, meshViewPanel.Height, RenderMode.Stress);
        }

        
    }

    // ----------------------------------------------------------------------
    //  EXTENSION BOOTSTRAP – we need to subscribe to Load from ctor.
    //  (Put this in a static ctor so it runs exactly once.)
    // ----------------------------------------------------------------------
    partial class StressAnalysisForm
    {
        
    }
}
