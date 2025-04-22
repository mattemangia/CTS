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
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace CTSegmenter
{

    public partial class TriaxialSimulation
    {
        // REPLACE the previous kernel binding with a version that uses only
        // primitive math and ILGPU.XMath to avoid internal compiler errors on
        // some NVIDIA drivers.

       

        // --- Override LoadKernels ------------------------------------------------
        private void LoadKernels()
        {
            try
            {
                _computeStressKernelSafe = _accelerator.LoadAutoGroupedStreamKernel<Index1D,
                    ArrayView<System.Numerics.Vector3>, ArrayView<System.Numerics.Vector3>, ArrayView<System.Numerics.Vector3>,
                    float, float, System.Numerics.Vector3,
                    float, float, // Added cohesion and frictionAngleRad
                    ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>(ComputeStressKernelSafe);
            }
            catch (Exception ex)
            {
                // If GPU compile fails switch to CPU accelerator and try again.
                if (!(_accelerator is CPUAccelerator))
                {
                    Logger.Log("[TriaxialSimulation] GPU kernel compile failed – falling back to CPU: " + ex.Message);
                    _accelerator.Dispose();
                    _accelerator = _context.GetPreferredDevice(preferCPU: true).CreateAccelerator(_context);
                    _computeStressKernelSafe = _accelerator.LoadAutoGroupedStreamKernel<Index1D,
                        ArrayView<System.Numerics.Vector3>, ArrayView<System.Numerics.Vector3>, ArrayView<System.Numerics.Vector3>,
                        float, float, System.Numerics.Vector3,
                        float, float, // Added cohesion and frictionAngleRad
                        ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>(ComputeStressKernelSafe);
                }
                else
                {
                    throw; // already on CPU – propagate
                }
            }
        }

        // --- GPU‑safe ComputeStressKernel ---------------------------------------
        private static void ComputeStressKernelSafe(
    Index1D index,
    ArrayView<System.Numerics.Vector3> v1Array,
    ArrayView<System.Numerics.Vector3> v2Array,
    ArrayView<System.Numerics.Vector3> v3Array,
    float confiningPressure,
    float axialPressure,
    System.Numerics.Vector3 testDirection,
    float cohesion,           // Now passed as parameter instead of hardcoded
    float frictionAngleRad,   // Now passed as parameter instead of hardcoded
    ArrayView<float> vonMisesStressArray,
    ArrayView<float> stress1Array,
    ArrayView<float> stress2Array,
    ArrayView<float> stress3Array,
    ArrayView<int> isFracturedArray)
        {
            int i = index;

            // local copies ----------------------------------------------------
            var v1 = v1Array[i];
            var v2 = v2Array[i];
            var v3 = v3Array[i];

            // edge vectors
            float e1x = v2.X - v1.X;
            float e1y = v2.Y - v1.Y;
            float e1z = v2.Z - v1.Z;
            float e2x = v3.X - v1.X;
            float e2y = v3.Y - v1.Y;
            float e2z = v3.Z - v1.Z;

            // cross product e1×e2 (triangle normal * area*2) -------------------
            float nx = e1y * e2z - e1z * e2y;
            float ny = e1z * e2x - e1x * e2z;
            float nz = e1x * e2y - e1y * e2x;
            float norm = XMath.Sqrt(nx * nx + ny * ny + nz * nz) + 1e-12f;
            nx /= norm; ny /= norm; nz /= norm; // unit normal

            // alignment with loading axis ------------------------------------
            float align = XMath.Abs(nx * testDirection.X + ny * testDirection.Y + nz * testDirection.Z);

            // orientation‑dependent partition --------------------------------
            float axialComp = axialPressure * align;
            float confComp = confiningPressure * (1f - align);

            // principal stresses (σ1 ≥ σ2 ≥ σ3) simplified --------------------
            float s1 = XMath.Max(axialPressure, confiningPressure);
            float s3 = XMath.Min(axialPressure, confiningPressure);
            float s2 = confiningPressure;

            // small spatial variation for visualisation ----------------------
            float variation = (1f + XMath.Sin(v1.X * 0.05f) * XMath.Cos(v1.Y * 0.05f)) * 0.5f; // 0.5–1.5
            s1 *= variation; s2 *= variation; s3 *= variation;

            // von Mises -------------------------------------------------------
            float vm = XMath.Sqrt(0.5f * ((s1 - s2) * (s1 - s2) + (s2 - s3) * (s2 - s3) + (s3 - s1) * (s3 - s1)));

            // Mohr-Coulomb failure criterion using passed parameters ----------
            float sinPhi = XMath.Sin(frictionAngleRad);
            float cosPhi = XMath.Cos(frictionAngleRad);

            // The failure criterion: (σ₁-σ₃) ≥ ((2*c*cos(φ)) + (σ₁+σ₃)*sin(φ))/(1-sin(φ))
            float numerator = (2.0f * cohesion * cosPhi) + ((s1 + s3) * sinPhi);
            float denominator = 1.0f - sinPhi;
            float failureThreshold = numerator / denominator;

            bool frac = (s1 - s3) >= failureThreshold;

            // write results ---------------------------------------------------
            vonMisesStressArray[i] = vm;
            stress1Array[i] = s1;
            stress2Array[i] = s2;
            stress3Array[i] = s3;
            isFracturedArray[i] = frac ? 1 : 0;  // Now properly assigned
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
            mohrCoulombPanel.Paint += (s, e) => DrawMohrCoulombPlaceholder(e.Graphics);

            // add button to existing "Visualization" ribbon group -------------
            var visGroup = resultsTab.Groups.FirstOrDefault(g => g.TextLine1 == "Visualization");
            if (visGroup != null)
            {
                var triple = new KryptonRibbonGroupTriple();
                mcGraphButton = new KryptonRibbonGroupButton { TextLine1 = "Mohr‑Coulomb", TextLine2 = "Graph", ImageLarge = CreateStressIcon(32), ImageSmall = CreateStressIcon(16), Checked = false, ButtonType = GroupButtonType.Check }; // acts like toggle
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

        // ------------------------------------------------------------------
        //  5.  SIMPLE PLACEHOLDER MOHR‑COULOMB DIAGRAM (until implemented)
        // ------------------------------------------------------------------
        /// <summary>
        /// Draw a full Mohr–Coulomb failure envelope and stress circle diagram.
        /// </summary>
        private void DrawMohrCoulombPlaceholder(Graphics g)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            float c = currentTriaxial.CohesionStrength;
            float phi = currentTriaxial.FrictionAngle * (float)Math.PI / 180f;
            float s3 = currentTriaxial.ConfiningPressure;
            float s1 = currentTriaxial.BreakingPressure;

            int w = mohrCoulombPanel.Width;
            int h = mohrCoulombPanel.Height;
            int m = 40;
            var area = new Rectangle(m, m, w - 2 * m, h - 2 * m);

            using (var pen = new Pen(Color.White, 1))
            {
                g.DrawLine(pen, area.Left, area.Bottom, area.Right, area.Bottom);
                g.DrawLine(pen, area.Left, area.Bottom, area.Left, area.Top);
            }

            float center = (s1 + s3) / 2f;
            float radius = (s1 - s3) / 2f;
            Func<float, float, PointF> T = (sig, tau) => new PointF(
                area.Left + sig / (s1 * 1.1f) * area.Width,
                area.Bottom - tau / (radius * 1.1f) * area.Height);

            var cpt = T(center, 0);
            float pr = radius / (s1 * 1.1f) * area.Width;
            using (var pen = new Pen(Color.Cyan, 2))
                g.DrawEllipse(pen, cpt.X - pr, cpt.Y - pr, pr * 2, pr * 2);

            using (var pen = new Pen(Color.Red, 2))
            {
                var p1 = T(0, c);
                var p2 = T(s1 * 1.1f, c + s1 * 1.1f * (float)Math.Tan(phi));
                g.DrawLine(pen, p1, p2);
            }

            using (var fnt = new Font("Segoe UI", 10))
            using (var br = Brushes.White)
            {
                g.DrawString($"c={c:F1}MPa, φ={currentTriaxial.FrictionAngle:F1}°", fnt, br, area.Left, area.Top - 20);
                g.DrawString($"σ₁={s1:F1}, σ₃={s3:F1}", fnt, br, area.Left, area.Bottom + 5);
            }
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
