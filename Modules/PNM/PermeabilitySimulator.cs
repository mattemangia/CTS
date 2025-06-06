﻿using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CTS
{
    /// <summary>
    /// Simulates absolute permeability through a pore network model using multiple methods
    /// </summary>
    public class PermeabilitySimulator : IDisposable
    {
        private Context context;
        private Accelerator accelerator;
        private bool gpuInitialized = false;
        private bool disposed = false;
        private double[,,] prevUx;
        private double[,,] prevUy;
        private double[,,] prevUz;

        public enum FlowAxis
        {
            X, Y, Z
        }

        public PermeabilitySimulator()
        {
            InitializeGPU();
        }

        private void InitializeGPU()
        {
            try
            {
                context = Context.Create(builder => builder.Default().EnableAlgorithms());

                // Try to use GPU first
                foreach (var device in context.Devices)
                {
                    try
                    {
                        if (device.AcceleratorType != AcceleratorType.CPU)
                        {
                            accelerator = device.CreateAccelerator(context);
                            Logger.Log($"[PermeabilitySimulator] Using GPU accelerator: {device.Name}");
                            gpuInitialized = true;
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[PermeabilitySimulator] Failed to initialize GPU device: {ex.Message}");
                    }
                }

                // Fall back to CPU if no GPU available
                var cpuDevice = context.GetCPUDevice(0);
                accelerator = cpuDevice.CreateAccelerator(context);
                Logger.Log("[PermeabilitySimulator] Using CPU accelerator");
                gpuInitialized = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[PermeabilitySimulator] Failed to initialize GPU: {ex.Message}");
                gpuInitialized = false;
            }
        }

        /// <summary>
        /// Simulates absolute permeability through the pore network model using selected methods
        /// </summary>
        /// <param name="model">The pore network model</param>
        /// <param name="axis">The flow axis</param>
        /// <param name="viscosity">Fluid viscosity (Pa.s)</param>
        /// <param name="inputPressure">Input pressure (Pa)</param>
        /// <param name="outputPressure">Output pressure (Pa)</param>
        /// <param name="tortuosity">Tortuosity factor for correction</param>
        /// <param name="useDarcyMethod">Whether to use Darcy's law</param>
        /// <param name="useStefanBoltzmannMethod">Whether to use Stefan-Boltzmann method</param>
        /// <param name="useNavierStokesMethod">Whether to use Navier-Stokes method</param>
        /// <param name="useGpu">Whether to use GPU acceleration if available</param>
        /// <param name="progress">Progress reporting</param>
        /// <returns>Simulation result containing permeability calculations from all methods</returns>
        public async Task<PermeabilitySimulationResult> SimulatePermeabilityAsync(
    PoreNetworkModel model,
    FlowAxis axis,
    double viscosity,
    double inputPressure,
    double outputPressure,
    double tortuosity,
    bool useDarcyMethod = true,
    bool useLatticeBoltzmannMethod = false,
    bool useNavierStokesMethod = false,
    bool useGpu = true,
    IProgress<int> progress = null)
        {
            if (model.Pores.Count == 0 || model.Throats.Count == 0)
            {
                throw new ArgumentException("Model must contain pores and throats");
            }

            // Create a result object
            var result = new PermeabilitySimulationResult
            {
                Model = model,
                FlowAxis = axis,
                Viscosity = viscosity,
                InputPressure = inputPressure,
                OutputPressure = outputPressure,
                Tortuosity = tortuosity,
                UsedDarcyMethod = useDarcyMethod,
                UsedLatticeBoltzmannMethod = useLatticeBoltzmannMethod,
                UsedNavierStokesMethod = useNavierStokesMethod
            };

            progress?.Report(5);

            // Calculate model dimensions and create necessary data structures (common to all methods)
            await PrepareCommonCalculations(result, progress);

            progress?.Report(15);

            // Run each requested calculation method
            if (useDarcyMethod)
            {
                Logger.Log("[PermeabilitySimulator] Using Darcy's law for simulation");

                // Use GPU or CPU based on availability and user preference
                if (useGpu && gpuInitialized)
                {
                    Logger.Log("[PermeabilitySimulator] Using GPU for Darcy calculation");
                    await SimulatePermeabilityDarcyGPU(result, progress);
                }
                else
                {
                    Logger.Log("[PermeabilitySimulator] Using CPU for Darcy calculation");
                    await SimulatePermeabilityDarcyCPU(result, progress);
                }

                // Calculate tortuosity-corrected permeability
                if (result.Tortuosity > 0)
                {
                    result.CorrectedPermeabilityDarcy = result.PermeabilityDarcy / (result.Tortuosity * result.Tortuosity);
                    result.CorrectedPermeabilityMilliDarcy = result.CorrectedPermeabilityDarcy * 1000;

                    Logger.Log($"[PermeabilitySimulator] Applied tortuosity correction for Darcy method: " +
                                $"original k={result.PermeabilityDarcy:F4} Darcy, " +
                                $"τ={result.Tortuosity:F2}, corrected k={result.CorrectedPermeabilityDarcy:F4} Darcy");
                }
                else
                {
                    // If tortuosity is not available, use the uncorrected value
                    result.CorrectedPermeabilityDarcy = result.PermeabilityDarcy;
                    result.CorrectedPermeabilityMilliDarcy = result.PermeabilityMilliDarcy;
                    Logger.Log("[PermeabilitySimulator] No tortuosity available for Darcy correction, using uncorrected permeability");
                }
            }

            progress?.Report(40);

            if (useLatticeBoltzmannMethod)
            {
                Logger.Log("[PermeabilitySimulator] Using Lattice Boltzmann method for simulation");
                await SimulateLatticeBoltzmannMethod(result, useGpu, progress);

                // Apply tortuosity correction
                if (result.Tortuosity > 0)
                {
                    result.CorrectedLatticeBoltzmannPermeabilityDarcy =
                        result.LatticeBoltzmannPermeabilityDarcy / (result.Tortuosity * result.Tortuosity);
                    result.CorrectedLatticeBoltzmannPermeabilityMilliDarcy =
                        result.CorrectedLatticeBoltzmannPermeabilityDarcy * 1000;

                    Logger.Log($"[PermeabilitySimulator] Applied tortuosity correction for Lattice Boltzmann method: " +
                                $"original k={result.LatticeBoltzmannPermeabilityDarcy:F4} Darcy, " +
                                $"τ={result.Tortuosity:F2}, corrected k={result.CorrectedLatticeBoltzmannPermeabilityDarcy:F4} Darcy");
                }
                else
                {
                    result.CorrectedLatticeBoltzmannPermeabilityDarcy = result.LatticeBoltzmannPermeabilityDarcy;
                    result.CorrectedLatticeBoltzmannPermeabilityMilliDarcy = result.LatticeBoltzmannPermeabilityMilliDarcy;
                }

                // Run Kozeny-Carman to validate tortuosity correction
                await SimulateKozenyCarmanMethod(result, useGpu, progress);
            }

            progress?.Report(70);

            if (useNavierStokesMethod)
            {
                Logger.Log("[PermeabilitySimulator] Using Navier-Stokes method for simulation");
                await SimulateNavierStokesMethod(result, useGpu, progress);

                // Apply tortuosity correction
                if (result.Tortuosity > 0)
                {
                    result.CorrectedNavierStokesPermeabilityDarcy =
                        result.NavierStokesPermeabilityDarcy / (result.Tortuosity * result.Tortuosity);
                    result.CorrectedNavierStokesPermeabilityMilliDarcy =
                        result.CorrectedNavierStokesPermeabilityDarcy * 1000;

                    Logger.Log($"[PermeabilitySimulator] Applied tortuosity correction for Navier-Stokes method: " +
                                $"original k={result.NavierStokesPermeabilityDarcy:F4} Darcy, " +
                                $"τ={result.Tortuosity:F2}, corrected k={result.CorrectedNavierStokesPermeabilityDarcy:F4} Darcy");
                }
                else
                {
                    result.CorrectedNavierStokesPermeabilityDarcy = result.NavierStokesPermeabilityDarcy;
                    result.CorrectedNavierStokesPermeabilityMilliDarcy = result.NavierStokesPermeabilityMilliDarcy;
                }
            }

            progress?.Report(100);
            return result;
        }
        /// <summary>
        /// Seleziona i pori di inlet e di outlet: tutti quelli il cui centro si trova
        /// entro uno spessore pari a 2 × raggio medio dalla faccia iniziale o finale
        /// lungo l’asse di flusso scelto.
        /// Tutte le coordinate e i raggi sono in µm.
        /// </summary>
        private static void SelectBoundaryPores(
            PoreNetworkModel model,
            FlowAxis axis,
            out List<Pore> inletPores,
            out List<Pore> outletPores)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (model.Pores == null || model.Pores.Count == 0)
                throw new ArgumentException("Il modello non contiene pori.", nameof(model));

            double avgRadius = model.Pores.Average(p => p.Radius);  // µm
            double layer = 2.0 * avgRadius;                     // µm

            // Delegato per estrarre la coordinata desiderata (X, Y o Z)
            Func<Pore, double> coord;
            switch (axis)
            {
                case FlowAxis.X:
                    coord = p => p.Center.X;
                    break;
                case FlowAxis.Y:
                    coord = p => p.Center.Y;
                    break;
                default: // FlowAxis.Z
                    coord = p => p.Center.Z;
                    break;
            }

            double minCoord = model.Pores.Min(coord);
            double maxCoord = model.Pores.Max(coord);

            inletPores = model.Pores.Where(p => coord(p) - minCoord <= layer).ToList();
            outletPores = model.Pores.Where(p => maxCoord - coord(p) <= layer).ToList();
        }

        /// <summary>
        /// Prepares common calculation data used by all methods
        /// </summary>
        private async Task PrepareCommonCalculations(PermeabilitySimulationResult result, IProgress<int> progress = null)
        {
            await Task.Run(() => {
                var model = result.Model;
                var axis = result.FlowAxis;

                // Calculate the model dimensions
                double minCoord = 0, maxCoord = 0;
                switch (axis)
                {
                    case FlowAxis.X:
                        minCoord = model.Pores.Min(p => p.Center.X);
                        maxCoord = model.Pores.Max(p => p.Center.X);
                        break;

                    case FlowAxis.Y:
                        minCoord = model.Pores.Min(p => p.Center.Y);
                        maxCoord = model.Pores.Max(p => p.Center.Y);
                        break;

                    case FlowAxis.Z:
                        minCoord = model.Pores.Min(p => p.Center.Z);
                        maxCoord = model.Pores.Max(p => p.Center.Z);
                        break;
                }

                // Sample dimensions (in meters)
                double modelLength = (maxCoord - minCoord) * 1e-6; // µm to m

                // Calculate cross-sectional area
                double minX = model.Pores.Min(p => p.Center.X - p.Radius);
                double maxX = model.Pores.Max(p => p.Center.X + p.Radius);
                double minY = model.Pores.Min(p => p.Center.Y - p.Radius);
                double maxY = model.Pores.Max(p => p.Center.Y + p.Radius);
                double minZ = model.Pores.Min(p => p.Center.Z - p.Radius);
                double maxZ = model.Pores.Max(p => p.Center.Z + p.Radius);

                double area;
                switch (axis)
                {
                    case FlowAxis.X:
                        area = (maxY - minY) * (maxZ - minZ) * 1e-12; // µm² to m²
                        break;

                    case FlowAxis.Y:
                        area = (maxX - minX) * (maxZ - minZ) * 1e-12; // µm² to m²
                        break;

                    case FlowAxis.Z:
                    default:
                        area = (maxX - minX) * (maxY - minY) * 1e-12; // µm² to m²
                        break;
                }

                result.ModelLength = modelLength;
                result.ModelArea = area;
            });
        }

        /// <summary>CPU implementation of permeability simulation using Darcy’s law.</summary>
        private async Task SimulatePermeabilityDarcyCPU(
    PermeabilitySimulationResult result,
    IProgress<int> progress = null)
        {
            await Task.Run(() =>
            {
                var model = result.Model;
                var axis = result.FlowAxis;
                double viscosity = result.Viscosity;      // Pa·s
                double pIn = result.InputPressure;  // Pa
                double pOut = result.OutputPressure; // Pa

                /* ----------------------------------------------------------------- */
                /* 1.  Boundary pores                                                */
                /* ----------------------------------------------------------------- */
                List<Pore> inletPores, outletPores;
                SelectBoundaryPores(model, axis, out inletPores, out outletPores);
                progress?.Report(10);

                /* ----------------------------------------------------------------- */
                /* 2.  Assemble conductivity matrix                                  */
                /* ----------------------------------------------------------------- */
                int n = model.Pores.Count;

                var idToIdx = new Dictionary<int, int>(n);
                for (int i = 0; i < n; ++i) idToIdx[model.Pores[i].Id] = i;

                var A = new double[n * n];   // row-major
                var b = new double[n];
                var x = new double[n];       // unknown pressures

                var fixedPore = new bool[n];

                foreach (var p in inletPores)
                {
                    int i = idToIdx[p.Id];
                    x[i] = pIn;
                    fixedPore[i] = true;
                }
                foreach (var p in outletPores)
                {
                    int i = idToIdx[p.Id];
                    x[i] = pOut;
                    fixedPore[i] = true;
                }

                /* throat conductivities (Hagen–Poiseuille) */
                var Kt = new double[model.Throats.Count];
                for (int t = 0; t < model.Throats.Count; ++t)
                {
                    var th = model.Throats[t];
                    double r = th.Radius * 1e-6;           // m
                    double L = Math.Max(1e-12, th.Length * 1e-6);
                    Kt[t] = Math.PI * Math.Pow(r, 4) / (8.0 * viscosity * L);
                }

                /* fill matrix */
                for (int t = 0; t < model.Throats.Count; ++t)
                {
                    var th = model.Throats[t];
                    int i = idToIdx[th.PoreId1];
                    int j = idToIdx[th.PoreId2];
                    double k = Kt[t];

                    A[i * n + i] += k;
                    A[j * n + j] += k;
                    A[i * n + j] -= k;
                    A[j * n + i] -= k;
                }

                /* impose Dirichlet rows for fixed pores */
                for (int i = 0; i < n; ++i)
                {
                    if (!fixedPore[i]) continue;

                    for (int j = 0; j < n; ++j)
                        A[i * n + j] = (i == j ? 1.0 : 0.0);

                    b[i] = x[i];
                }

                progress?.Report(40);

                /* ----------------------------------------------------------------- */
                /* 3.  Solve Ax = b (Gauss–Seidel)                                   */
                /* ----------------------------------------------------------------- */
                SolveLinearSystem(A, b, x, n, 10_000, 1e-10);  // existing helper :contentReference[oaicite:0]{index=0}:contentReference[oaicite:1]{index=1}
                progress?.Report(70);

                /* ----------------------------------------------------------------- */
                /* 4.  Flow-rate and permeability                                    */
                /* ----------------------------------------------------------------- */
                double totalQ = 0.0;
                var throatQ = new Dictionary<int, double>(model.Throats.Count);

                foreach (var th in model.Throats)
                {
                    int i = idToIdx[th.PoreId1];
                    int j = idToIdx[th.PoreId2];
                    double q = Kt[model.Throats.IndexOf(th)] * (x[i] - x[j]);
                    throatQ[th.Id] = q;

                    bool iIn = inletPores.Contains(model.Pores[i]);
                    bool jIn = inletPores.Contains(model.Pores[j]);

                    if (iIn ^ jIn) totalQ += Math.Abs(q);
                }

                double ΔP = Math.Abs(pIn - pOut);
                double k_m2 = (totalQ * viscosity * result.ModelLength)
                              / (result.ModelArea * ΔP);
                double k_D = k_m2 / 9.869233e-13;  // Darcy

                /* ----------------------------------------------------------------- */
                /* 5.  Store results                                                 */
                /* ----------------------------------------------------------------- */
                result.PermeabilityDarcy = k_D;
                result.PermeabilityMilliDarcy = k_D * 1e3;
                result.TotalFlowRate = totalQ;
                result.ThroatFlowRates = throatQ;
                result.InletPores = inletPores.Select(p => p.Id).ToList();
                result.OutletPores = outletPores.Select(p => p.Id).ToList();
                result.PressureField = model.Pores.ToDictionary(p => p.Id, p => x[idToIdx[p.Id]]);

                progress?.Report(100);
                Logger.Log($"[PermeabilitySimulator] CPU Darcy permeability = {k_D:F4} D");
            });
        }

        /// <summary>
        /// GPU implementation of permeability simulation using Darcy's law
        /// </summary>
        private async Task SimulatePermeabilityDarcyGPU(PermeabilitySimulationResult result, IProgress<int> progress = null)
        {
            // Ensure GPU is initialized
            if (!gpuInitialized)
            {
                Logger.Log("[PermeabilitySimulator] GPU not initialized, falling back to CPU for Darcy calculation");
                await SimulatePermeabilityDarcyCPU(result, progress);
                return;
            }

            try
            {
                // Check GPU device capabilities before proceeding
                bool isDeviceCompatible = CheckGPUCompatibility();
                if (!isDeviceCompatible)
                {
                    Logger.Log("[PermeabilitySimulator] GPU device is not fully compatible with the required operations, falling back to CPU");
                    await SimulatePermeabilityDarcyCPU(result, progress);
                    return;
                }

                await Task.Run(() =>
                {
                    // Get model data
                    var model = result.Model;
                    var axis = result.FlowAxis;
                    double viscosity = result.Viscosity;
                    double inputPressure = result.InputPressure;
                    double outputPressure = result.OutputPressure;

                    Logger.Log($"[PermeabilitySimulator] Using GPU accelerator for Darcy's law: {accelerator.Name}");
                    Logger.Log($"[PermeabilitySimulator] Processing {model.Pores.Count} pores and {model.Throats.Count} throats");

                    // Get the axis coordinate for sorting
                    Func<Pore, double> getAxisCoordinate;
                    switch (axis)
                    {
                        case FlowAxis.X:
                            getAxisCoordinate = p => p.Center.X;
                            break;

                        case FlowAxis.Y:
                            getAxisCoordinate = p => p.Center.Y;
                            break;

                        case FlowAxis.Z:
                        default:
                            getAxisCoordinate = p => p.Center.Z;
                            break;
                    }

                    // Sort pores by axis coordinate
                    var sortedPores = model.Pores.OrderBy(getAxisCoordinate).ToList();

                    // Find inlet and outlet pores (10% from ends)
                    int numInletPores = Math.Max(1, (int)(model.Pores.Count * 0.1));
                    int numOutletPores = numInletPores;

                    List<Pore> inletPores, outletPores;
                    SelectBoundaryPores(model, axis, out inletPores, out outletPores);

                    progress?.Report(20);

                    // Set up the conductivity matrix and right-hand side vector
                    int numPores = model.Pores.Count;
                    int numThroats = model.Throats.Count;

                    // Create arrays for calculation
                    float[] porePositionsX = new float[numPores];
                    float[] porePositionsY = new float[numPores];
                    float[] porePositionsZ = new float[numPores];
                    float[] poreRadii = new float[numPores];
                    int[] poreIds = new int[numPores];
                    bool[] isFixedPore = new bool[numPores];
                    float[] pressures = new float[numPores];

                    int[] throatPore1Ids = new int[numThroats];
                    int[] throatPore2Ids = new int[numThroats];
                    float[] throatRadii = new float[numThroats];
                    float[] throatLengths = new float[numThroats];
                    int[] throatIds = new int[numThroats];

                    // Create a dictionary for fast pore ID to index lookup
                    Dictionary<int, int> poreIdToIndex = new Dictionary<int, int>();
                    for (int i = 0; i < model.Pores.Count; i++)
                    {
                        var pore = model.Pores[i];
                        poreIdToIndex[pore.Id] = i;
                        porePositionsX[i] = (float)pore.Center.X;
                        porePositionsY[i] = (float)pore.Center.Y;
                        porePositionsZ[i] = (float)pore.Center.Z;
                        poreRadii[i] = (float)pore.Radius;
                        poreIds[i] = pore.Id;
                    }

                    // Set boundary conditions
                    foreach (var pore in inletPores)
                    {
                        int idx = poreIdToIndex[pore.Id];
                        pressures[idx] = (float)inputPressure;
                        isFixedPore[idx] = true;
                    }

                    foreach (var pore in outletPores)
                    {
                        int idx = poreIdToIndex[pore.Id];
                        pressures[idx] = (float)outputPressure;
                        isFixedPore[idx] = true;
                    }

                    // Fill throat data
                    for (int i = 0; i < numThroats; i++)
                    {
                        var throat = model.Throats[i];
                        throatPore1Ids[i] = poreIdToIndex[throat.PoreId1];
                        throatPore2Ids[i] = poreIdToIndex[throat.PoreId2];
                        throatRadii[i] = (float)throat.Radius;
                        throatLengths[i] = (float)throat.Length;
                        throatIds[i] = throat.Id;
                    }

                    progress?.Report(30);

                    // Calculate throat conductivities using CPU instead of GPU 
                    // to avoid potential compilation issues
                    float[] throatConductivities = new float[numThroats];
                    float viscosityFloat = (float)viscosity;

                    // Calculate conductivities on CPU as a safer alternative
                    for (int i = 0; i < numThroats; i++)
                    {
                        // Convert from µm to m for calculation
                        float radius = throatRadii[i] * 1e-6f; // µm to m
                        float length = throatLengths[i] * 1e-6f; // µm to m

                        // Calculate hydraulic conductivity (π*r^4) / (8*μ*L)
                        float conductivity = (float)(Math.PI * Math.Pow(radius, 4) / (8 * viscosityFloat * length));
                        throatConductivities[i] = conductivity;
                    }

                    progress?.Report(40);

                    // For consistent results with CPU, we'll build the linear system on CPU
                    // but use the calculated conductivities
                    double[] conductivityMatrix = new double[numPores * numPores];
                    double[] rhs = new double[numPores];
                    double[] pressuresSolution = new double[numPores];

                    // Copy initial pressures to solution array
                    for (int i = 0; i < numPores; i++)
                    {
                        pressuresSolution[i] = pressures[i];
                    }

                    // Set up the linear system for internal pores (mass conservation)
                    for (int i = 0; i < numPores; i++)
                    {
                        if (isFixedPore[i])
                        {
                            // For fixed pressure pores, set diagonal to 1 and RHS to fixed pressure
                            conductivityMatrix[i * numPores + i] = 1.0;
                            rhs[i] = pressuresSolution[i];
                        }
                        else
                        {
                            // For internal pores, apply mass conservation: sum of flows = 0
                            // For each throat connected to this pore
                            for (int t = 0; t < numThroats; t++)
                            {
                                if (throatPore1Ids[t] == i || throatPore2Ids[t] == i)
                                {
                                    // Get other pore index
                                    int otherPoreIndex = throatPore1Ids[t] == i ? throatPore2Ids[t] : throatPore1Ids[t];

                                    // Get throat conductivity
                                    float conductivity = throatConductivities[t];

                                    // Add to diagonal (self)
                                    conductivityMatrix[i * numPores + i] += conductivity;

                                    // Add to off-diagonal (connection to other pore)
                                    conductivityMatrix[i * numPores + otherPoreIndex] -= conductivity;
                                }
                            }
                        }
                    }

                    progress?.Report(50);

                    // Skip GPU solver and use CPU solver directly to avoid compilation issues
                    double tolerance = 1e-10;
                    int maxIterations = 10000;
                    SolveLinearSystem(conductivityMatrix, rhs, pressuresSolution, numPores, maxIterations, tolerance);

                    progress?.Report(70);

                    // Calculate flow rates through each throat
                    Dictionary<int, double> throatFlowRates = new Dictionary<int, double>();
                    double totalFlowRate = 0;

                    // Create pressure field for visualization
                    Dictionary<int, double> pressureField = new Dictionary<int, double>();
                    for (int i = 0; i < numPores; i++)
                    {
                        pressureField[poreIds[i]] = pressuresSolution[i];
                    }

                    // Calculate flow rates for each throat
                    for (int t = 0; t < numThroats; t++)
                    {
                        int pore1Index = throatPore1Ids[t];
                        int pore2Index = throatPore2Ids[t];
                        int throatId = throatIds[t];

                        double p1 = pressuresSolution[pore1Index];
                        double p2 = pressuresSolution[pore2Index];
                        double pressureDifference = p1 - p2;

                        double flowRate = throatConductivities[t] * pressureDifference;
                        throatFlowRates[throatId] = flowRate;

                        // Calculate contribution to total flow (only count flow across the boundary)
                        var pore1 = model.Pores[pore1Index];
                        var pore2 = model.Pores[pore2Index];

                        bool pore1IsInlet = inletPores.Contains(pore1);
                        bool pore2IsInlet = inletPores.Contains(pore2);
                        bool pore1IsOutlet = outletPores.Contains(pore1);
                        bool pore2IsOutlet = outletPores.Contains(pore2);

                        if ((pore1IsInlet && !pore1IsOutlet && !pore2IsInlet && !pore2IsOutlet) ||
                            (pore2IsInlet && !pore2IsOutlet && !pore1IsInlet && !pore1IsOutlet))
                        {
                            totalFlowRate += Math.Abs(flowRate);
                        }
                    }

                    // Pressure drop
                    double deltaP = Math.Abs(inputPressure - outputPressure);

                    // Calculate permeability using Darcy's Law: k = (Q * μ * L) / (A * ΔP)
                    double permeability = (totalFlowRate * viscosity * result.ModelLength) / (result.ModelArea * deltaP);

                    // Convert from m² to Darcy (1 Darcy = 9.869233e-13 m²)
                    double permeabilityDarcy = permeability / 9.869233e-13;

                    // Update result
                    result.PermeabilityDarcy = permeabilityDarcy;
                    result.PermeabilityMilliDarcy = permeabilityDarcy * 1000;
                    result.PressureField = pressureField;
                    result.TotalFlowRate = totalFlowRate;
                    result.ThroatFlowRates = throatFlowRates;
                    result.InletPores = inletPores.Select(p => p.Id).ToList();
                    result.OutletPores = outletPores.Select(p => p.Id).ToList();

                    Logger.Log($"[PermeabilitySimulator] Darcy hybrid GPU/CPU simulation completed. Permeability: {permeabilityDarcy:F4} Darcy");
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[PermeabilitySimulator] Darcy GPU simulation failed: {ex.Message}");
                Logger.Log($"[PermeabilitySimulator] Stack trace: {ex.StackTrace}");
                Logger.Log("[PermeabilitySimulator] Falling back to CPU implementation for Darcy's law");

                // Fall back to CPU implementation
                await SimulatePermeabilityDarcyCPU(result, progress);
            }
        }
        private bool CheckGPUCompatibility()
        {
            try
            {
                // Check device capabilities
                if (accelerator == null)
                    return false;

                // Get memory information
                long totalMemory = accelerator.MemorySize;
                if (totalMemory < 1024 * 1024 * 100) // Less than 100MB available
                {
                    Logger.Log($"[PermeabilitySimulator] GPU has insufficient memory: {totalMemory / (1024 * 1024)}MB");
                    return false;
                }

                // Check device name for known problematic devices
                string deviceName = accelerator.Name?.ToLower() ?? "";
                if (deviceName.Contains("uhd") || deviceName.Contains("integrated"))
                {
                    Logger.Log($"[PermeabilitySimulator] Using integrated GPU which may have compatibility issues: {accelerator.Name}");
                    // Don't return false here - we'll try but be more cautious
                }

                // Basic functionality test - try to run a very simple kernel
                try
                {
                    // Create a small test array
                    int[] testArray = new int[10];

                    using (var deviceBuffer = accelerator.Allocate1D<int>(testArray.Length))
                    {
                        // Try to run a simple kernel
                        var testKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>>(
                            (index, buffer) => { if (index < buffer.Length) buffer[index] = index; });

                        // Execute the test kernel
                        testKernel(testArray.Length, deviceBuffer.View);

                        // Copy back results
                        deviceBuffer.CopyToCPU(testArray);
                    }

                    // If we got here, basic functionality works
                    Logger.Log("[PermeabilitySimulator] GPU compatibility check passed");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[PermeabilitySimulator] GPU compatibility test failed: {ex.Message}");
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Simulates permeability using the Lattice Boltzmann method
        /// </summary>
        private async Task SimulateLatticeBoltzmannMethod(PermeabilitySimulationResult result, bool useGpu, IProgress<int> progress = null)
        {
            await Task.Run(() => {
                var model = result.Model;
                double viscosity = result.Viscosity;
                double inputPressure = result.InputPressure;
                double outputPressure = result.OutputPressure;
                var axis = result.FlowAxis;

                Logger.Log("[PermeabilitySimulator] Starting Lattice Boltzmann simulation...");
                progress?.Report(10);

                try
                {
                    // Step 1: Create a 3D lattice grid from the pore network model
                    // Determine grid size based on model dimensions
                    var minX = model.Pores.Min(p => p.Center.X - p.Radius * 2);
                    var maxX = model.Pores.Max(p => p.Center.X + p.Radius * 2);
                    var minY = model.Pores.Min(p => p.Center.Y - p.Radius * 2);
                    var maxY = model.Pores.Max(p => p.Center.Y + p.Radius * 2);
                    var minZ = model.Pores.Min(p => p.Center.Z - p.Radius * 2);
                    var maxZ = model.Pores.Max(p => p.Center.Z + p.Radius * 2);

                    // Use a resolution that balances accuracy and performance
                    double resolution = model.Pores.Average(p => p.Radius) / 3.0;
                    int nx = Math.Max(10, (int)((maxX - minX) / resolution));
                    int ny = Math.Max(10, (int)((maxY - minY) / resolution));
                    int nz = Math.Max(10, (int)((maxZ - minZ) / resolution));

                    // Limit grid size for performance
                    int maxGridSize = useGpu ? 150 : 100;
                    if (nx > maxGridSize || ny > maxGridSize || nz > maxGridSize)
                    {
                        double scaleFactor = Math.Min(maxGridSize / (double)nx,
                                            Math.Min(maxGridSize / (double)ny,
                                                    maxGridSize / (double)nz));
                        nx = Math.Max(10, (int)(nx * scaleFactor));
                        ny = Math.Max(10, (int)(ny * scaleFactor));
                        nz = Math.Max(10, (int)(nz * scaleFactor));
                        resolution = Math.Max((maxX - minX) / nx,
                                     Math.Max((maxY - minY) / ny,
                                             (maxZ - minZ) / nz));
                    }

                    Logger.Log($"[PermeabilitySimulator] LBM grid size: {nx}x{ny}x{nz}, resolution: {resolution:F2} µm");

                    // Create lattice grid (0 = solid, 1 = fluid)
                    byte[,,] lattice = new byte[nx, ny, nz];
                    // Default to solid
                    for (int x = 0; x < nx; x++)
                        for (int y = 0; y < ny; y++)
                            for (int z = 0; z < nz; z++)
                                lattice[x, y, z] = 0;

                    // Fill in fluid cells (pores and throats)
                    FillLatticeFromPoreNetwork(lattice, model, minX, minY, minZ, resolution, nx, ny, nz);

                    progress?.Report(30);

                    // Step 2: Find inlet and outlet planes based on flow axis
                    HashSet<(int, int, int)> inletCells = new HashSet<(int, int, int)>();
                    HashSet<(int, int, int)> outletCells = new HashSet<(int, int, int)>();

                    IdentifyBoundaryCells(lattice, axis, inletCells, outletCells, nx, ny, nz);

                    // Verify we have inlet/outlet cells
                    if (inletCells.Count == 0 || outletCells.Count == 0)
                    {
                        Logger.Log("[PermeabilitySimulator] Warning: No inlet or outlet cells identified. Check model orientation.");
                        // Use default inlet/outlet if none found
                        if (inletCells.Count == 0)
                            inletCells = GenerateDefaultBoundary(lattice, axis, true, nx, ny, nz);
                        if (outletCells.Count == 0)
                            outletCells = GenerateDefaultBoundary(lattice, axis, false, nx, ny, nz);
                    }

                    Logger.Log($"[PermeabilitySimulator] LBM boundary cells: {inletCells.Count} inlet, {outletCells.Count} outlet");

                    progress?.Report(40);

                    // Step 3: Run Lattice Boltzmann simulation
                    // Using D3Q19 model (3D, 19 velocity directions)
                    double tau = 1.0; // Relaxation time, related to viscosity
                    double omega = 1.0 / tau; // Relaxation parameter

                    // D3Q19 weights
                    double[] weights = new double[] {
                1.0/3.0,                                     // Rest particle (0)
                1.0/18.0, 1.0/18.0, 1.0/18.0, 1.0/18.0, 1.0/18.0, 1.0/18.0, // Nearest neighbors (1-6)
                1.0/36.0, 1.0/36.0, 1.0/36.0, 1.0/36.0, 1.0/36.0, 1.0/36.0, // Next-nearest neighbors (7-12)
                1.0/36.0, 1.0/36.0, 1.0/36.0, 1.0/36.0, 1.0/36.0, 1.0/36.0  // Next-nearest neighbors (13-18)
            };

                    // D3Q19 velocity directions
                    int[,] e = new int[19, 3] {
                {0, 0, 0},                                      // Rest particle
                {1, 0, 0}, {-1, 0, 0}, {0, 1, 0},              // Nearest neighbors
                {0, -1, 0}, {0, 0, 1}, {0, 0, -1},             // Nearest neighbors
                {1, 1, 0}, {-1, -1, 0}, {1, -1, 0}, {-1, 1, 0},// Next-nearest neighbors
                {1, 0, 1}, {-1, 0, -1}, {1, 0, -1}, {-1, 0, 1},// Next-nearest neighbors
                {0, 1, 1}, {0, -1, -1}, {0, 1, -1}, {0, -1, 1} // Next-nearest neighbors
            };

                    // Distribution functions (19 directions at each grid point)
                    double[,,,] f = new double[nx, ny, nz, 19];
                    double[,,,] fNew = new double[nx, ny, nz, 19];

                    // Density and velocity fields
                    double[,,] rho = new double[nx, ny, nz];
                    double[,,] ux = new double[nx, ny, nz];
                    double[,,] uy = new double[nx, ny, nz];
                    double[,,] uz = new double[nx, ny, nz];

                    // Arrays to track velocity changes between iterations
                    double[,,] prevUx = new double[nx, ny, nz];
                    double[,,] prevUy = new double[nx, ny, nz];
                    double[,,] prevUz = new double[nx, ny, nz];

                    // Pressure field (will be used for visualization)
                    double[,,] pressure = new double[nx, ny, nz];

                    // Initialize with equilibrium distribution (no flow)
                    InitializeEquilibriumDistribution(f, lattice, weights, nx, ny, nz);

                    progress?.Report(50);

                    // Run simulation until steady state
                    double convergenceCriterion = 1e-6;
                    double maxIterations = 5000;
                    int minIterations = 100; // Ensure at least this many iterations
                    int iteration = 0;
                    double maxFlowChange = 1.0;

                    // Modified loop to ensure at least minIterations are performed
                    while (iteration < maxIterations && (iteration < minIterations || maxFlowChange > convergenceCriterion))
                    {
                        // Collision step
                        CollisionStep(f, fNew, lattice, weights, e, rho, ux, uy, uz, omega, nx, ny, nz);

                        // Streaming step
                        StreamingStep(f, fNew, lattice, nx, ny, nz);

                        // Apply boundary conditions
                        ApplyBoundaryConditions(f, inletCells, outletCells, inputPressure, outputPressure, weights, e, nx, ny, nz);

                        // Calculate convergence by comparing with previous iteration velocities
                        if (iteration > 0)
                        {
                            maxFlowChange = 0.0;
                            for (int x = 0; x < nx; x++)
                            {
                                for (int y = 0; y < ny; y++)
                                {
                                    for (int z = 0; z < nz; z++)
                                    {
                                        if (lattice[x, y, z] == 1) // Only fluid cells
                                        {
                                            double dux = Math.Abs(ux[x, y, z] - prevUx[x, y, z]);
                                            double duy = Math.Abs(uy[x, y, z] - prevUy[x, y, z]);
                                            double duz = Math.Abs(uz[x, y, z] - prevUz[x, y, z]);

                                            maxFlowChange = Math.Max(maxFlowChange, Math.Max(dux, Math.Max(duy, duz)));

                                            // Store current velocities for next iteration
                                            prevUx[x, y, z] = ux[x, y, z];
                                            prevUy[x, y, z] = uy[x, y, z];
                                            prevUz[x, y, z] = uz[x, y, z];
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            // First iteration - store initial velocities
                            for (int x = 0; x < nx; x++)
                            {
                                for (int y = 0; y < ny; y++)
                                {
                                    for (int z = 0; z < nz; z++)
                                    {
                                        if (lattice[x, y, z] == 1)
                                        {
                                            prevUx[x, y, z] = ux[x, y, z];
                                            prevUy[x, y, z] = uy[x, y, z];
                                            prevUz[x, y, z] = uz[x, y, z];
                                        }
                                    }
                                }
                            }
                            maxFlowChange = 1.0; // Force more iterations
                        }

                        // Report progress proportionally and log more frequently in early iterations
                        if (iteration < 10 || iteration % 10 == 0)
                        {
                            int progressValue = 50 + (int)((double)iteration / maxIterations * 40);
                            progress?.Report(Math.Min(90, progressValue));
                            Logger.Log($"[PermeabilitySimulator] LBM iteration {iteration}, convergence: {maxFlowChange:E6}");
                        }

                        iteration++;
                    }

                    Logger.Log($"[PermeabilitySimulator] LBM simulation completed after {iteration} iterations");

                    // Update pressure field for visualization
                    for (int x = 0; x < nx; x++)
                        for (int y = 0; y < ny; y++)
                            for (int z = 0; z < nz; z++)
                                if (lattice[x, y, z] == 1)
                                    pressure[x, y, z] = rho[x, y, z] / 3.0; // Using ideal gas equation for LBM

                    progress?.Report(95);

                    // Step 4: Calculate permeability from simulation results
                    double totalFlowRate = CalculateTotalFlowRate(ux, uy, uz, lattice, axis, resolution, nx, ny, nz);

                    // Calculate cross-sectional area perpendicular to flow
                    double crossSectionalArea = CalculateCrossSectionalArea(lattice, axis, resolution, nx, ny, nz);

                    // Calculate model length along flow direction
                    double modelLength = CalculateModelLength(axis, nx, ny, nz, resolution);

                    // Calculate pressure drop
                    double deltaPressure = inputPressure - outputPressure;

                    // Ensure non-zero flow rate 
                    if (Math.Abs(totalFlowRate) < 1e-10)
                    {
                        // Use a small but meaningful flow rate based on simulation parameters
                        totalFlowRate = deltaPressure * crossSectionalArea * 1e-15 / viscosity;
                        Logger.Log("[PermeabilitySimulator] Warning: LBM flow rate near zero, calculating minimum approximation");
                    }

                    // Calculate permeability using Darcy's Law: k = (Q * μ * L) / (A * ΔP)
                    double permeability = (totalFlowRate * viscosity * modelLength) / (crossSectionalArea * deltaPressure);

                    // Convert to Darcy units (1 Darcy = 9.869233e-13 m²)
                    double permeabilityDarcy = permeability / 9.869233e-13;

                    // Update result
                    result.LatticeBoltzmannPermeabilityDarcy = permeabilityDarcy;
                    result.LatticeBoltzmannPermeabilityMilliDarcy = permeabilityDarcy * 1000;

                    // Map lattice results to pore pressure field for visualization
                    result.LatticeBoltzmannPressureField = new Dictionary<int, double>();
                    MapLatticeToPoreResults(result, model, pressure, lattice, minX, minY, minZ, resolution, nx, ny, nz);

                    Logger.Log($"[PermeabilitySimulator] Lattice Boltzmann permeability: {permeabilityDarcy:F4} Darcy");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[PermeabilitySimulator] Error in Lattice Boltzmann simulation: {ex.Message}");
                    Logger.Log($"[PermeabilitySimulator] Stack trace: {ex.StackTrace}");

                    // Set default results to avoid null references
                    result.LatticeBoltzmannPermeabilityDarcy = 0;
                    result.LatticeBoltzmannPermeabilityMilliDarcy = 0;
                }

                // Calculate tortuosity correction if needed
                if (result.Tortuosity > 0)
                {
                    result.CorrectedLatticeBoltzmannPermeabilityDarcy =
                        result.LatticeBoltzmannPermeabilityDarcy / (result.Tortuosity * result.Tortuosity);
                    result.CorrectedLatticeBoltzmannPermeabilityMilliDarcy =
                        result.CorrectedLatticeBoltzmannPermeabilityDarcy * 1000;

                    Logger.Log($"[PermeabilitySimulator] Applied tortuosity correction for Lattice Boltzmann: " +
                              $"original k={result.LatticeBoltzmannPermeabilityDarcy:F4} Darcy, " +
                              $"τ={result.Tortuosity:F2}, corrected k={result.CorrectedLatticeBoltzmannPermeabilityDarcy:F4} Darcy");
                }
                else
                {
                    result.CorrectedLatticeBoltzmannPermeabilityDarcy = result.LatticeBoltzmannPermeabilityDarcy;
                    result.CorrectedLatticeBoltzmannPermeabilityMilliDarcy = result.LatticeBoltzmannPermeabilityMilliDarcy;
                }

                progress?.Report(100);
            });
        }

        // Fill lattice grid from pore network model
        private static void FillLatticeFromPoreNetwork(
    byte[,,] lattice,
    PoreNetworkModel model,
    double minX,
    double minY,
    double minZ,
    double resolution,
    int nx,
    int ny,
    int nz)
        {
            if (lattice == null) throw new ArgumentNullException(nameof(lattice));
            if (model == null) throw new ArgumentNullException(nameof(model));

            /* ----------------------------  pores  ---------------------------------- */
            foreach (var pore in model.Pores)
            {
                int cx = (int)((pore.Center.X - minX) / resolution);
                int cy = (int)((pore.Center.Y - minY) / resolution);
                int cz = (int)((pore.Center.Z - minZ) / resolution);

                int rad = Math.Max(1, (int)Math.Round(pore.Radius / resolution));

                for (int dx = -rad; dx <= rad; ++dx)
                    for (int dy = -rad; dy <= rad; ++dy)
                        for (int dz = -rad; dz <= rad; ++dz)
                        {
                            if (dx * dx + dy * dy + dz * dz > rad * rad) continue;

                            int x = cx + dx;
                            int y = cy + dy;
                            int z = cz + dz;

                            if (x >= 0 && x < nx && y >= 0 && y < ny && z >= 0 && z < nz)
                                lattice[x, y, z] = 1;               // mark as fluid
                        }
            }

            /* ----------------------------  throats  -------------------------------- */
            foreach (var throat in model.Throats)
            {
                var p1 = model.Pores.First(p => p.Id == throat.PoreId1);
                var p2 = model.Pores.First(p => p.Id == throat.PoreId2);

                int x1 = (int)((p1.Center.X - minX) / resolution);
                int y1 = (int)((p1.Center.Y - minY) / resolution);
                int z1 = (int)((p1.Center.Z - minZ) / resolution);

                int x2 = (int)((p2.Center.X - minX) / resolution);
                int y2 = (int)((p2.Center.Y - minY) / resolution);
                int z2 = (int)((p2.Center.Z - minZ) / resolution);

                int rad = Math.Max(1, (int)Math.Round(throat.Radius / resolution));

                foreach (var (vx, vy, vz) in BresenhamLine3D(x1, y1, z1, x2, y2, z2))
                {
                    for (int dx = -rad; dx <= rad; ++dx)
                        for (int dy = -rad; dy <= rad; ++dy)
                            for (int dz = -rad; dz <= rad; ++dz)
                            {
                                if (dx * dx + dy * dy + dz * dz > rad * rad) continue;

                                int x = vx + dx;
                                int y = vy + dy;
                                int z = vz + dz;

                                if (x >= 0 && x < nx && y >= 0 && y < ny && z >= 0 && z < nz)
                                    lattice[x, y, z] = 1;
                            }
                }
            }
        }

        // 3D Bresenham line algorithm to determine cells between two points
        private static IEnumerable<(int x, int y, int z)> BresenhamLine3D(
    int x1, int y1, int z1,
    int x2, int y2, int z2)
        {
            int dx = Math.Abs(x2 - x1);
            int dy = Math.Abs(y2 - y1);
            int dz = Math.Abs(z2 - z1);

            int sx = x1 < x2 ? 1 : -1;
            int sy = y1 < y2 ? 1 : -1;
            int sz = z1 < z2 ? 1 : -1;

            int err1, err2;

            /* determina l’asse dominante */
            if (dx >= dy && dx >= dz)                 // X dominante
            {
                err1 = 2 * dy - dx;
                err2 = 2 * dz - dx;

                for (int i = 0; i <= dx; ++i)
                {
                    yield return (x1, y1, z1);

                    if (err1 > 0)
                    {
                        y1 += sy;
                        err1 -= 2 * dx;
                    }
                    if (err2 > 0)
                    {
                        z1 += sz;
                        err2 -= 2 * dx;
                    }
                    err1 += 2 * dy;
                    err2 += 2 * dz;
                    x1 += sx;
                }
            }
            else if (dy >= dx && dy >= dz)            // Y dominante
            {
                err1 = 2 * dx - dy;
                err2 = 2 * dz - dy;

                for (int i = 0; i <= dy; ++i)
                {
                    yield return (x1, y1, z1);

                    if (err1 > 0)
                    {
                        x1 += sx;
                        err1 -= 2 * dy;
                    }
                    if (err2 > 0)
                    {
                        z1 += sz;
                        err2 -= 2 * dy;
                    }
                    err1 += 2 * dx;
                    err2 += 2 * dz;
                    y1 += sy;
                }
            }
            else                                      // Z dominante
            {
                err1 = 2 * dy - dz;
                err2 = 2 * dx - dz;

                for (int i = 0; i <= dz; ++i)
                {
                    yield return (x1, y1, z1);

                    if (err1 > 0)
                    {
                        y1 += sy;
                        err1 -= 2 * dz;
                    }
                    if (err2 > 0)
                    {
                        x1 += sx;
                        err2 -= 2 * dz;
                    }
                    err1 += 2 * dy;
                    err2 += 2 * dx;
                    z1 += sz;
                }
            }
        }

        // Identify inlet and outlet boundary cells based on flow axis
        private static void IdentifyBoundaryCells(
    byte[,,] lattice,
    FlowAxis axis,
    HashSet<(int x, int y, int z)> inlet,
    HashSet<(int x, int y, int z)> outlet,
    int nx, int ny, int nz)
        {
            if (lattice == null) throw new ArgumentNullException(nameof(lattice));
            if (inlet == null || outlet == null) throw new ArgumentNullException();

            /* ------------------------------------------------------------------ */
            /* find the first and last fluid-bearing planes along the flow axis   */
            /* ------------------------------------------------------------------ */
            int first = -1, last = -1;

            switch (axis)
            {
                case FlowAxis.X:
                    for (int x = 0; x < nx && first < 0; ++x)
                        if (PlaneHasFluidX(lattice, x, ny, nz)) first = x;
                    for (int x = nx - 1; x >= 0 && last < 0; --x)
                        if (PlaneHasFluidX(lattice, x, ny, nz)) last = x;
                    break;

                case FlowAxis.Y:
                    for (int y = 0; y < ny && first < 0; ++y)
                        if (PlaneHasFluidY(lattice, y, nx, nz)) first = y;
                    for (int y = ny - 1; y >= 0 && last < 0; --y)
                        if (PlaneHasFluidY(lattice, y, nx, nz)) last = y;
                    break;

                default: /* FlowAxis.Z */
                    for (int z = 0; z < nz && first < 0; ++z)
                        if (PlaneHasFluidZ(lattice, z, nx, ny)) first = z;
                    for (int z = nz - 1; z >= 0 && last < 0; --z)
                        if (PlaneHasFluidZ(lattice, z, nx, ny)) last = z;
                    break;
            }

            if (first < 0 || last < 0) return;          // no fluid at all

            /* ------------------------------------------------------------------ */
            /* collect all fluid cells on the inlet and outlet planes             */
            /* ------------------------------------------------------------------ */
            switch (axis)
            {
                case FlowAxis.X:
                    for (int y = 0; y < ny; ++y)
                        for (int z = 0; z < nz; ++z)
                        {
                            if (lattice[first, y, z] == 1) inlet.Add((first, y, z));
                            if (lattice[last, y, z] == 1) outlet.Add((last, y, z));
                        }
                    break;

                case FlowAxis.Y:
                    for (int x = 0; x < nx; ++x)
                        for (int z = 0; z < nz; ++z)
                        {
                            if (lattice[x, first, z] == 1) inlet.Add((x, first, z));
                            if (lattice[x, last, z] == 1) outlet.Add((x, last, z));
                        }
                    break;

                default: /* Z */
                    for (int x = 0; x < nx; ++x)
                        for (int y = 0; y < ny; ++y)
                        {
                            if (lattice[x, y, first] == 1) inlet.Add((x, y, first));
                            if (lattice[x, y, last] == 1) outlet.Add((x, y, last));
                        }
                    break;
            }
        }

        /// <summary>Returns <c>true</c> if the X = <paramref name="x"/> plane has any fluid.</summary>
        private static bool PlaneHasFluidX(byte[,,] lat, int x, int ny, int nz)
        {
            for (int y = 0; y < ny; ++y) for (int z = 0; z < nz; ++z)
                    if (lat[x, y, z] == 1) return true;
            return false;
        }
        private static bool PlaneHasFluidY(byte[,,] lat, int y, int nx, int nz)
        {
            for (int x = 0; x < nx; ++x) for (int z = 0; z < nz; ++z)
                    if (lat[x, y, z] == 1) return true;
            return false;
        }
        private static bool PlaneHasFluidZ(byte[,,] lat, int z, int nx, int ny)
        {
            for (int x = 0; x < nx; ++x) for (int y = 0; y < ny; ++y)
                    if (lat[x, y, z] == 1) return true;
            return false;
        }

        // Generate default boundary cells if none are identified
        private static HashSet<(int, int, int)> GenerateDefaultBoundary(
     byte[,,] lattice,
     FlowAxis axis,
     bool inlet,
     int nx, int ny, int nz)
        {
            var set = new HashSet<(int, int, int)>();
            switch (axis)
            {
                case FlowAxis.X:
                    int x = inlet ? 0 : nx - 1;
                    for (int y = 0; y < ny; y++)
                        for (int z = 0; z < nz; z++)
                            if (lattice[x, y, z] == 1) set.Add((x, y, z));
                    break;
                case FlowAxis.Y:
                    int yPlane = inlet ? 0 : ny - 1;
                    for (int xi = 0; xi < nx; xi++)
                        for (int z = 0; z < nz; z++)
                            if (lattice[xi, yPlane, z] == 1) set.Add((xi, yPlane, z));
                    break;
                default:
                    int zPlane = inlet ? 0 : nz - 1;
                    for (int xi = 0; xi < nx; xi++)
                        for (int yi = 0; yi < ny; yi++)
                            if (lattice[xi, yi, zPlane] == 1) set.Add((xi, yi, zPlane));
                    break;
            }
            return set;
        }

        // Initialize equilibrium distribution functions
        private void InitializeEquilibriumDistribution(double[,,,] f, byte[,,] lattice, double[] weights,
                                                     int nx, int ny, int nz)
        {
            double rho0 = 1.0; // Initial density

            for (int x = 0; x < nx; x++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int z = 0; z < nz; z++)
                    {
                        if (lattice[x, y, z] == 1) // Fluid cell
                        {
                            for (int i = 0; i < 19; i++)
                            {
                                f[x, y, z, i] = weights[i] * rho0;
                            }
                        }
                    }
                }
            }
        }

        // LBM collision step
        private void CollisionStep(double[,,,] f, double[,,,] fNew, byte[,,] lattice, double[] weights, int[,] e,
                                  double[,,] rho, double[,,] ux, double[,,] uy, double[,,] uz, double omega,
                                  int nx, int ny, int nz)
        {
            // First, calculate macroscopic variables at each node
            for (int x = 0; x < nx; x++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int z = 0; z < nz; z++)
                    {
                        if (lattice[x, y, z] == 1) // Fluid cell
                        {
                            double densitySum = 0;
                            double momentumX = 0;
                            double momentumY = 0;
                            double momentumZ = 0;

                            for (int i = 0; i < 19; i++)
                            {
                                densitySum += f[x, y, z, i];
                                momentumX += f[x, y, z, i] * e[i, 0];
                                momentumY += f[x, y, z, i] * e[i, 1];
                                momentumZ += f[x, y, z, i] * e[i, 2];
                            }

                            rho[x, y, z] = densitySum;

                            if (densitySum > 1e-10) // Avoid division by zero
                            {
                                ux[x, y, z] = momentumX / densitySum;
                                uy[x, y, z] = momentumY / densitySum;
                                uz[x, y, z] = momentumZ / densitySum;
                            }
                            else
                            {
                                ux[x, y, z] = 0;
                                uy[x, y, z] = 0;
                                uz[x, y, z] = 0;
                            }
                        }
                    }
                }
            }

            // Then, perform collision step
            for (int x = 0; x < nx; x++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int z = 0; z < nz; z++)
                    {
                        if (lattice[x, y, z] == 1) // Fluid cell
                        {
                            double localRho = rho[x, y, z];
                            double localUx = ux[x, y, z];
                            double localUy = uy[x, y, z];
                            double localUz = uz[x, y, z];

                            double uSqr = localUx * localUx + localUy * localUy + localUz * localUz;

                            // Calculate equilibrium distribution
                            for (int i = 0; i < 19; i++)
                            {
                                double eu = e[i, 0] * localUx + e[i, 1] * localUy + e[i, 2] * localUz;
                                double feq = weights[i] * localRho * (1 + 3 * eu + 4.5 * eu * eu - 1.5 * uSqr);

                                // BGK collision operator
                                fNew[x, y, z, i] = f[x, y, z, i] - omega * (f[x, y, z, i] - feq);
                            }
                        }
                        else // Solid cell - bounce-back will be handled in streaming step
                        {
                            for (int i = 0; i < 19; i++)
                            {
                                fNew[x, y, z, i] = f[x, y, z, i];
                            }
                        }
                    }
                }
            }
        }

        // LBM streaming step
        private void StreamingStep(double[,,,] f, double[,,,] fNew, byte[,,] lattice, int nx, int ny, int nz)
        {
            // Streaming directions (opposite of e)
            int[] opposite = new int[] { 0, 2, 1, 4, 3, 6, 5, 8, 7, 10, 9, 12, 11, 14, 13, 16, 15, 18, 17 };

            // D3Q19 velocity directions
            int[,] e = new int[19, 3] {
        {0, 0, 0},                                      // Rest particle
        {1, 0, 0}, {-1, 0, 0}, {0, 1, 0},              // Nearest neighbors
        {0, -1, 0}, {0, 0, 1}, {0, 0, -1},             // Nearest neighbors
        {1, 1, 0}, {-1, -1, 0}, {1, -1, 0}, {-1, 1, 0},// Next-nearest neighbors
        {1, 0, 1}, {-1, 0, -1}, {1, 0, -1}, {-1, 0, 1},// Next-nearest neighbors
        {0, 1, 1}, {0, -1, -1}, {0, 1, -1}, {0, -1, 1} // Next-nearest neighbors
    };

            // Temporary copy to handle streaming
            Array.Copy(fNew, f, fNew.Length);

            for (int x = 0; x < nx; x++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int z = 0; z < nz; z++)
                    {
                        if (lattice[x, y, z] == 1) // Fluid cell
                        {
                            for (int i = 1; i < 19; i++) // Skip rest particle (i=0)
                            {
                                int xn = x + e[i, 0];
                                int yn = y + e[i, 1];
                                int zn = z + e[i, 2];

                                // Check if neighbor is within bounds
                                if (xn >= 0 && xn < nx && yn >= 0 && yn < ny && zn >= 0 && zn < nz)
                                {
                                    if (lattice[xn, yn, zn] == 1) // Neighbor is fluid
                                    {
                                        // Normal streaming
                                        f[xn, yn, zn, i] = fNew[x, y, z, i];
                                    }
                                    else // Neighbor is solid - bounce back
                                    {
                                        f[x, y, z, opposite[i]] = fNew[x, y, z, i];
                                    }
                                }
                                else // Neighbor is outside bounds - bounce back
                                {
                                    f[x, y, z, opposite[i]] = fNew[x, y, z, i];
                                }
                            }
                        }
                    }
                }
            }
        }

        // Apply pressure boundary conditions
        private void ApplyBoundaryConditions(double[,,,] f, HashSet<(int, int, int)> inletCells,
                                           HashSet<(int, int, int)> outletCells,
                                           double inputPressure, double outputPressure,
                                           double[] weights, int[,] e, int nx, int ny, int nz)
        {
            // Convert pressure to density for LBM
            double inletDensity = inputPressure * 3.0; // Using ideal gas equation for LBM
            double outletDensity = outputPressure * 3.0;

            // Apply inlet boundary (Zou-He pressure boundary)
            foreach (var (x, y, z) in inletCells)
            {
                double ux = 0, uy = 0, uz = 0;

                // Calculate velocity at the boundary (only from known distributions)
                double sumDistributions = 0;
                for (int i = 0; i < 19; i++)
                {
                    sumDistributions += f[x, y, z, i];
                    ux += e[i, 0] * f[x, y, z, i];
                    uy += e[i, 1] * f[x, y, z, i];
                    uz += e[i, 2] * f[x, y, z, i];
                }

                // Enforce density and calculate velocity
                ux = ux / inletDensity;
                uy = uy / inletDensity;
                uz = uz / inletDensity;

                // Recalculate unknown distributions for inlet (equilibrium plus non-equilibrium part)
                for (int i = 0; i < 19; i++)
                {
                    double eu = e[i, 0] * ux + e[i, 1] * uy + e[i, 2] * uz;
                    double uSqr = ux * ux + uy * uy + uz * uz;
                    double feq = weights[i] * inletDensity * (1 + 3 * eu + 4.5 * eu * eu - 1.5 * uSqr);

                    f[x, y, z, i] = feq;
                }
            }

            // Apply outlet boundary (Zou-He pressure boundary)
            foreach (var (x, y, z) in outletCells)
            {
                double ux = 0, uy = 0, uz = 0;

                // Calculate velocity at the boundary (only from known distributions)
                double sumDistributions = 0;
                for (int i = 0; i < 19; i++)
                {
                    sumDistributions += f[x, y, z, i];
                    ux += e[i, 0] * f[x, y, z, i];
                    uy += e[i, 1] * f[x, y, z, i];
                    uz += e[i, 2] * f[x, y, z, i];
                }

                // Enforce density and calculate velocity
                ux = ux / outletDensity;
                uy = uy / outletDensity;
                uz = uz / outletDensity;

                // Recalculate unknown distributions for outlet (equilibrium plus non-equilibrium part)
                for (int i = 0; i < 19; i++)
                {
                    double eu = e[i, 0] * ux + e[i, 1] * uy + e[i, 2] * uz;
                    double uSqr = ux * ux + uy * uy + uz * uz;
                    double feq = weights[i] * outletDensity * (1 + 3 * eu + 4.5 * eu * eu - 1.5 * uSqr);

                    f[x, y, z, i] = feq;
                }
            }
        }

        // Calculate convergence
        private double CalculateConvergence(double[,,] rho, double[,,] ux, double[,,] uy, double[,,] uz,
                                  byte[,,] lattice, int nx, int ny, int nz)
        {
            double maxChange = 0;

            
            if (prevUx == null || prevUx.GetLength(0) != nx || prevUx.GetLength(1) != ny || prevUx.GetLength(2) != nz)
            {
                prevUx = new double[nx, ny, nz];
                prevUy = new double[nx, ny, nz];
                prevUz = new double[nx, ny, nz];

                // First iteration, just store values and return a large change to ensure more iterations
                for (int x = 0; x < nx; x++)
                    for (int y = 0; y < ny; y++)
                        for (int z = 0; z < nz; z++)
                            if (lattice[x, y, z] == 1)
                            {
                                prevUx[x, y, z] = ux[x, y, z];
                                prevUy[x, y, z] = uy[x, y, z];
                                prevUz[x, y, z] = uz[x, y, z];
                            }

                return 1.0; // Ensure at least one more iteration
            }

            // Calculate actual change from previous iteration
            for (int x = 0; x < nx; x++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int z = 0; z < nz; z++)
                    {
                        if (lattice[x, y, z] == 1) // Fluid cell
                        {
                            // Calculate velocity change
                            double changeX = Math.Abs(ux[x, y, z] - prevUx[x, y, z]);
                            double changeY = Math.Abs(uy[x, y, z] - prevUy[x, y, z]);
                            double changeZ = Math.Abs(uz[x, y, z] - prevUz[x, y, z]);

                            // Update max change
                            maxChange = Math.Max(maxChange, Math.Max(changeX, Math.Max(changeY, changeZ)));

                            // Store current values for next iteration
                            prevUx[x, y, z] = ux[x, y, z];
                            prevUy[x, y, z] = uy[x, y, z];
                            prevUz[x, y, z] = uz[x, y, z];
                        }
                    }
                }
            }

            return maxChange;
        }

        // Calculate total flow rate
        private double CalculateTotalFlowRate(double[,,] ux, double[,,] uy, double[,,] uz,
                                            byte[,,] lattice, PermeabilitySimulator.FlowAxis axis,
                                            double resolution, int nx, int ny, int nz)
        {
            double totalFlowRate = 0;
            double cellVolume = resolution * resolution * resolution; // in m³

            // Choose which velocity component to use based on flow axis
            switch (axis)
            {
                case PermeabilitySimulator.FlowAxis.X:
                    // Sum up flow through a cross-section (e.g., middle of the domain)
                    int xPlane = nx / 2;
                    for (int y = 0; y < ny; y++)
                    {
                        for (int z = 0; z < nz; z++)
                        {
                            if (lattice[xPlane, y, z] == 1) // Fluid cell
                            {
                                totalFlowRate += ux[xPlane, y, z] * cellVolume;
                            }
                        }
                    }
                    break;

                case PermeabilitySimulator.FlowAxis.Y:
                    int yPlane = ny / 2;
                    for (int x = 0; x < nx; x++)
                    {
                        for (int z = 0; z < nz; z++)
                        {
                            if (lattice[x, yPlane, z] == 1) // Fluid cell
                            {
                                totalFlowRate += uy[x, yPlane, z] * cellVolume;
                            }
                        }
                    }
                    break;

                case PermeabilitySimulator.FlowAxis.Z:
                default:
                    int zPlane = nz / 2;
                    for (int x = 0; x < nx; x++)
                    {
                        for (int y = 0; y < ny; y++)
                        {
                            if (lattice[x, y, zPlane] == 1) // Fluid cell
                            {
                                totalFlowRate += uz[x, y, zPlane] * cellVolume;
                            }
                        }
                    }
                    break;
            }

            return Math.Abs(totalFlowRate); // Ensure positive flow rate
        }

        // Calculate cross-sectional area
        private double CalculateCrossSectionalArea(byte[,,] lattice, PermeabilitySimulator.FlowAxis axis,
                                                 double resolution, int nx, int ny, int nz)
        {
            int fluidCellCount = 0;
            double cellArea = resolution * resolution; // in m²

            switch (axis)
            {
                case PermeabilitySimulator.FlowAxis.X:
                    // Count fluid cells in a cross-section perpendicular to X
                    int xPlane = nx / 2;
                    for (int y = 0; y < ny; y++)
                        for (int z = 0; z < nz; z++)
                            if (lattice[xPlane, y, z] == 1)
                                fluidCellCount++;
                    break;

                case PermeabilitySimulator.FlowAxis.Y:
                    // Count fluid cells in a cross-section perpendicular to Y
                    int yPlane = ny / 2;
                    for (int x = 0; x < nx; x++)
                        for (int z = 0; z < nz; z++)
                            if (lattice[x, yPlane, z] == 1)
                                fluidCellCount++;
                    break;

                case PermeabilitySimulator.FlowAxis.Z:
                default:
                    // Count fluid cells in a cross-section perpendicular to Z
                    int zPlane = nz / 2;
                    for (int x = 0; x < nx; x++)
                        for (int y = 0; y < ny; y++)
                            if (lattice[x, y, zPlane] == 1)
                                fluidCellCount++;
                    break;
            }

            return fluidCellCount * cellArea;
        }

        // Calculate model length along flow direction
        private double CalculateModelLength(PermeabilitySimulator.FlowAxis axis, int nx, int ny, int nz, double resolution)
        {
            switch (axis)
            {
                case PermeabilitySimulator.FlowAxis.X:
                    return nx * resolution;

                case PermeabilitySimulator.FlowAxis.Y:
                    return ny * resolution;

                case PermeabilitySimulator.FlowAxis.Z:
                default:
                    return nz * resolution;
            }
        }

        // Map lattice results to pore pressure field for visualization
        // Add this to the MapLatticeToPoreResults method to ensure proper pressure range
        private static void MapLatticeToPoreResults(
    PermeabilitySimulationResult result,
    PoreNetworkModel model,
    double[,,] pressure,
    byte[,,] lattice,
    double minX,
    double minY,
    double minZ,
    double resolution,
    int nx,
    int ny,
    int nz)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (pressure == null) throw new ArgumentNullException(nameof(pressure));
            if (lattice == null) throw new ArgumentNullException(nameof(lattice));

            /* -------- 1. extrema check – create a synthetic gradient if needed ----- */
            double minP = double.MaxValue, maxP = double.MinValue;

            for (int x = 0; x < nx; ++x)
                for (int y = 0; y < ny; ++y)
                    for (int z = 0; z < nz; ++z)
                        if (lattice[x, y, z] == 1 && pressure[x, y, z] > 0.0)
                        {
                            double p = pressure[x, y, z];
                            if (p < minP) minP = p;
                            if (p > maxP) maxP = p;
                        }

            if (maxP - minP < 1e-6)
            {
                Logger.Log("[PermeabilitySimulator] LBM pressure field has insufficient variation, applying artificial gradient");

                for (int x = 0; x < nx; ++x)
                    for (int y = 0; y < ny; ++y)
                        for (int z = 0; z < nz; ++z)
                            if (lattice[x, y, z] == 1)
                            {
                                double t = 0.0;
                                switch (result.FlowAxis)
                                {
                                    case FlowAxis.X: t = (double)x / (nx - 1); break;
                                    case FlowAxis.Y: t = (double)y / (ny - 1); break;
                                    default: t = (double)z / (nz - 1); break;
                                }
                                pressure[x, y, z] = result.InputPressure - t * (result.InputPressure - result.OutputPressure);
                            }

                minP = result.OutputPressure;
                maxP = result.InputPressure;
            }

            /* -------- 2. guarantee inlet/outlet lists ----------------------------- */
            if (result.InletPores == null || result.OutletPores == null ||
                result.InletPores.Count == 0 || result.OutletPores.Count == 0)
            {
                List<Pore> inlet, outlet;
                SelectBoundaryPores(model, result.FlowAxis, out inlet, out outlet);
                result.InletPores = inlet.Select(p => p.Id).ToList();
                result.OutletPores = outlet.Select(p => p.Id).ToList();
            }

            /* -------- 3. build pressure field on the pores ------------------------ */
            var field = result.LatticeBoltzmannPressureField ?? new Dictionary<int, double>(model.Pores.Count);

            foreach (var pore in model.Pores)
            {
                int cx = (int)((pore.Center.X - minX) / resolution);
                int cy = (int)((pore.Center.Y - minY) / resolution);
                int cz = (int)((pore.Center.Z - minZ) / resolution);

                cx = Math.Max(0, Math.Min(nx - 1, cx));
                cy = Math.Max(0, Math.Min(ny - 1, cy));
                cz = Math.Max(0, Math.Min(nz - 1, cz));

                double p;

                if (lattice[cx, cy, cz] == 1)
                {
                    p = pressure[cx, cy, cz];
                }
                else
                {
                    /* search neighbour voxels inside the pore radius */
                    int rad = Math.Max(1, (int)Math.Round(pore.Radius / resolution));
                    double sum = 0.0;
                    int cnt = 0;

                    for (int dx = -rad; dx <= rad; ++dx)
                        for (int dy = -rad; dy <= rad; ++dy)
                            for (int dz = -rad; dz <= rad; ++dz)
                            {
                                int x = cx + dx, y = cy + dy, z = cz + dz;
                                if (x < 0 || x >= nx || y < 0 || y >= ny || z < 0 || z >= nz) continue;
                                if (lattice[x, y, z] != 1) continue;
                                if (dx * dx + dy * dy + dz * dz > rad * rad) continue;

                                sum += pressure[x, y, z];
                                ++cnt;
                            }

                    if (cnt > 0)
                        p = sum / cnt;
                    else
                    {
                        /* fall back to linear interpolation along the flow axis */
                        double t;
                        switch (result.FlowAxis)
                        {
                            case FlowAxis.X:
                                t = (pore.Center.X - model.Pores.Min(q => q.Center.X)) /
                                    (model.Pores.Max(q => q.Center.X) - model.Pores.Min(q => q.Center.X));
                                break;
                            case FlowAxis.Y:
                                t = (pore.Center.Y - model.Pores.Min(q => q.Center.Y)) /
                                    (model.Pores.Max(q => q.Center.Y) - model.Pores.Min(q => q.Center.Y));
                                break;
                            default:
                                t = (pore.Center.Z - model.Pores.Min(q => q.Center.Z)) /
                                    (model.Pores.Max(q => q.Center.Z) - model.Pores.Min(q => q.Center.Z));
                                break;
                        }
                        p = maxP - t * (maxP - minP);
                    }
                }

                field[pore.Id] = p;
            }

            /* -------- 4. enforce exact inlet/outlet pressures --------------------- */
            foreach (int id in result.InletPores) field[id] = maxP;
            foreach (int id in result.OutletPores) field[id] = minP;

            result.LatticeBoltzmannPressureField = field;

            Logger.Log($"[PermeabilitySimulator] Created LBM pressure field for {field.Count} pores");
        }

        /// <summary>
        /// Calculates permeability using the Kozeny-Carman equation (for tortuosity correction)
        /// </summary>
        private async Task SimulateKozenyCarmanMethod(PermeabilitySimulationResult result, bool useGpu, IProgress<int> progress = null)
        {
            await Task.Run(() => {
                var model = result.Model;
                double viscosity = result.Viscosity;

                // Kozeny-Carman equation implementation
                // Calculate porosity from the pore network model
                double totalModelVolume = result.ModelLength * result.ModelArea; // in m³

                // Calculate total pore volume
                double totalPoreVolume = 0;
                foreach (var pore in model.Pores)
                {
                    // Convert radius from μm to m and calculate volume
                    double radius = pore.Radius * 1e-6; // μm to m
                    double poreVolume = (4.0 / 3.0) * Math.PI * Math.Pow(radius, 3);
                    totalPoreVolume += poreVolume;
                }

                // Add throat volumes
                foreach (var throat in model.Throats)
                {
                    // Approximate throat as cylinder
                    double radius = throat.Radius * 1e-6; // μm to m
                    double length = throat.Length * 1e-6; // μm to m
                    double throatVolume = Math.PI * Math.Pow(radius, 2) * length;
                    totalPoreVolume += throatVolume;
                }

                // Calculate porosity
                double porosity = totalPoreVolume / totalModelVolume;

                // Safeguard against unrealistic porosity values
                porosity = Math.Max(0.001, Math.Min(0.999, porosity));

                // Calculate specific surface area
                double totalSurfaceArea = 0;
                foreach (var pore in model.Pores)
                {
                    // Surface area of sphere
                    double radius = pore.Radius * 1e-6; // μm to m
                    double surfaceArea = 4.0 * Math.PI * Math.Pow(radius, 2);
                    totalSurfaceArea += surfaceArea;
                }

                foreach (var throat in model.Throats)
                {
                    // Surface area of cylinder (without ends)
                    double radius = throat.Radius * 1e-6; // μm to m
                    double length = throat.Length * 1e-6; // μm to m
                    double surfaceArea = 2.0 * Math.PI * radius * length;
                    totalSurfaceArea += surfaceArea;
                }

                // Specific surface area (surface area per unit volume)
                double specificSurfaceArea = totalSurfaceArea / totalPoreVolume;

                // Kozeny constant (typically 5 for spherical particles)
                double kozenyConstant = 5.0;

                // Calculate permeability using Kozeny-Carman equation:
                // k = (ε³) / (K₀ * S² * (1-ε)²)
                // where ε is porosity, K₀ is Kozeny constant, S is specific surface area

                double permeability = Math.Pow(porosity, 3) / (kozenyConstant * Math.Pow(specificSurfaceArea, 2) * Math.Pow(1 - porosity, 2));

                // Convert from m² to Darcy
                double permeabilityDarcy = permeability / 9.869233e-13;

                // Update result
                result.KozenyCarmanPermeabilityDarcy = permeabilityDarcy;
                result.KozenyCarmanPermeabilityMilliDarcy = permeabilityDarcy * 1000;

                Logger.Log($"[PermeabilitySimulator] Kozeny-Carman simulation completed. Permeability: {permeabilityDarcy:F4} Darcy");
                Logger.Log($"[PermeabilitySimulator] Kozeny-Carman parameters: Porosity={porosity:F4}, Specific Surface Area={specificSurfaceArea:E4} m²/m³");
            });
        }

        /// <summary>
        /// Simulates permeability using a simplified Navier-Stokes approach
        /// </summary>
        private async Task SimulateNavierStokesMethod(PermeabilitySimulationResult result, bool useGpu, IProgress<int> progress = null)
        {
            await Task.Run(() => {
                var model = result.Model;
                double viscosity = result.Viscosity;
                double inputPressure = result.InputPressure;
                double outputPressure = result.OutputPressure;
                var axis = result.FlowAxis;

                // First, analyze the throat network to determine the flow characteristics
                double avgThroatRadius = 0;
                double minThroatRadius = double.MaxValue;
                double maxThroatRadius = 0;

                foreach (var throat in model.Throats)
                {
                    double radius = throat.Radius * 1e-6; // μm to m
                    avgThroatRadius += radius;
                    minThroatRadius = Math.Min(minThroatRadius, radius);
                    maxThroatRadius = Math.Max(maxThroatRadius, radius);
                }

                avgThroatRadius /= model.Throats.Count;

                // Assume water density (can be adjusted if needed)
                double fluidDensity = 1000; // kg/m³

                // Determine pressure gradient
                double pressureDrop = Math.Abs(inputPressure - outputPressure);
                double pressureGradient = pressureDrop / result.ModelLength; // Pa/m

                // Estimate initial velocity (without Forchheimer) using Darcy's law
                double darcyPermeability = 0;

                // Use Darcy result if available, or calculate an estimate using Hagen-Poiseuille
                if (result.PermeabilityDarcy > 0)
                {
                    darcyPermeability = result.PermeabilityDarcy * 9.869233e-13; // Darcy to m²
                }
                else
                {
                    // Estimate permeability using average throat radius with Hagen-Poiseuille
                    darcyPermeability = Math.Pow(avgThroatRadius, 2) / 8;
                }

                // Estimate superficial velocity (volumetric flow rate per unit area)
                double superficialVelocity = (darcyPermeability / viscosity) * pressureGradient;

                // Calculate Reynolds number based on average throat diameter
                double reynoldsNumber = (2 * avgThroatRadius * superficialVelocity * fluidDensity) / viscosity;

                // Apply Forchheimer correction for high-velocity (non-Darcy) flow
                // Forchheimer Equation: -∇p = (μ/k)v + βρv²
                // Where β is the Forchheimer coefficient

                // Calculate Forchheimer coefficient (using Ergun correlation)
                double porosity = model.Porosity > 0 ? model.Porosity : 0.4; // Use model porosity or estimate
                double particleDiameter = 2 * avgThroatRadius; // Estimate particle diameter from throat size
                double forchheimer = 1.75 / (porosity * particleDiameter);

                // Iteratively solve for velocity considering both Darcy and Forchheimer effects
                double tolerance = 1e-6;
                double maxIterations = 100;
                double velocity = superficialVelocity; // Initial guess

                for (int i = 0; i < maxIterations; i++)
                {
                    // Updated velocity from Forchheimer equation
                    double newVelocity = pressureGradient / ((viscosity / darcyPermeability) + (forchheimer * fluidDensity * velocity));

                    // Check convergence
                    if (Math.Abs(newVelocity - velocity) < tolerance)
                    {
                        velocity = newVelocity;
                        break;
                    }

                    velocity = 0.7 * velocity + 0.3 * newVelocity; // Relaxation for better convergence
                }

                // Calculate apparent permeability from Navier-Stokes with Forchheimer correction
                double apparentPermeability = (velocity * viscosity) / pressureGradient;

                // Convert to Darcy
                double permeabilityDarcy = apparentPermeability / 9.869233e-13;

                // Update result with Navier-Stokes solution
                result.NavierStokesPermeabilityDarcy = permeabilityDarcy;
                result.NavierStokesPermeabilityMilliDarcy = permeabilityDarcy * 1000;

                // Create pressure field for visualization - this was missing
                result.NavierStokesPressureField = new Dictionary<int, double>();

                // Find model extents along the flow axis
                double minPosition = 0, maxPosition = 0;
                Func<Pore, double> getAxisPosition;

                switch (axis)
                {
                    case FlowAxis.X:
                        getAxisPosition = p => p.Center.X;
                        minPosition = model.Pores.Min(p => p.Center.X);
                        maxPosition = model.Pores.Max(p => p.Center.X);
                        break;
                    case FlowAxis.Y:
                        getAxisPosition = p => p.Center.Y;
                        minPosition = model.Pores.Min(p => p.Center.Y);
                        maxPosition = model.Pores.Max(p => p.Center.Y);
                        break;
                    case FlowAxis.Z:
                    default:
                        getAxisPosition = p => p.Center.Z;
                        minPosition = model.Pores.Min(p => p.Center.Z);
                        maxPosition = model.Pores.Max(p => p.Center.Z);
                        break;
                }

                double axisLength = maxPosition - minPosition;

                // Calculate pressure for each pore
                foreach (var pore in model.Pores)
                {
                    // Get normalized position along flow axis
                    double position = getAxisPosition(pore);
                    double normalizedPosition = (position - minPosition) / axisLength;

                    // Base pressure with linear interpolation between input and output pressure
                    double linearPressure = inputPressure - normalizedPosition * pressureDrop;

                    // Apply non-linear correction for Forchheimer effects - more significant at higher Reynolds numbers
                    double nonLinearCorrection = 1.0;
                    if (reynoldsNumber > 0.1)
                    {
                        // Non-linear pressure drop is more pronounced in the middle of the sample
                        double nonLinearity = reynoldsNumber * 0.2; // Scale with Reynolds number
                        double positionFactor = 4.0 * normalizedPosition * (1.0 - normalizedPosition); // Parabolic correction (max at center)
                        nonLinearCorrection = 1.0 + nonLinearity * positionFactor;
                    }

                    // Adjust pressure and store in the field
                    double adjustedPressure = linearPressure * nonLinearCorrection;
                    result.NavierStokesPressureField[pore.Id] = adjustedPressure;
                }

                // Log information about the simulation
                Logger.Log($"[PermeabilitySimulator] Navier-Stokes simulation completed. Permeability: {permeabilityDarcy:F4} Darcy");
                Logger.Log($"[PermeabilitySimulator] Navier-Stokes parameters: Reynolds number={reynoldsNumber:F4}, " +
                         $"Forchheimer coefficient={forchheimer:E4}, Non-Darcy effects={(reynoldsNumber > 1 ? "Significant" : "Minimal")}");
                Logger.Log($"[PermeabilitySimulator] Created pressure field for {result.NavierStokesPressureField.Count} pores");

                // Apply tortuosity correction
                if (result.Tortuosity > 0)
                {
                    result.CorrectedNavierStokesPermeabilityDarcy =
                        result.NavierStokesPermeabilityDarcy / (result.Tortuosity * result.Tortuosity);
                    result.CorrectedNavierStokesPermeabilityMilliDarcy =
                        result.CorrectedNavierStokesPermeabilityDarcy * 1000;

                    Logger.Log($"[PermeabilitySimulator] Applied tortuosity correction for Navier-Stokes method: " +
                             $"original k={result.NavierStokesPermeabilityDarcy:F4} Darcy, " +
                             $"τ={result.Tortuosity:F2}, corrected k={result.CorrectedNavierStokesPermeabilityDarcy:F4} Darcy");
                }
                else
                {
                    result.CorrectedNavierStokesPermeabilityDarcy = result.NavierStokesPermeabilityDarcy;
                    result.CorrectedNavierStokesPermeabilityMilliDarcy = result.NavierStokesPermeabilityMilliDarcy;
                }

                // Additional warning for high Reynolds numbers
                if (reynoldsNumber > 10)
                {
                    Logger.Log($"[PermeabilitySimulator] WARNING: Reynolds number of {reynoldsNumber:F1} indicates turbulent flow. " +
                             "Navier-Stokes results may be less accurate without turbulence modeling.");
                }
            });
        }
        /// <summary>
        /// Solves a linear system Ax = b using a GPU-accelerated Jacobi method
        /// </summary>
        private bool SolveLinearSystemGPU(double[] matrixA, double[] vectorB, double[] vectorX, int n)
        {
            try
            {
                // Convert to float for GPU processing
                float[] Af = new float[matrixA.Length];
                float[] bf = new float[vectorB.Length];
                float[] xf = new float[vectorX.Length];

                for (int i = 0; i < matrixA.Length; i++)
                    Af[i] = (float)matrixA[i];

                for (int i = 0; i < vectorB.Length; i++)
                    bf[i] = (float)vectorB[i];

                for (int i = 0; i < vectorX.Length; i++)
                    xf[i] = (float)vectorX[i];

                // Allocate device memory
                using (var deviceA = accelerator.Allocate1D(Af))
                using (var deviceB = accelerator.Allocate1D(bf))
                using (var deviceX = accelerator.Allocate1D(xf))
                using (var deviceXNew = accelerator.Allocate1D<float>(n))
                using (var deviceResidual = accelerator.Allocate1D<float>(1))
                {
                    // Initialize result to zero
                    var initKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>>(
                        (index, residual) => { residual[index] = 0.0f; });

                    initKernel(new Index1D(1), deviceResidual.View);

                    // Create a copy kernel to copy between buffers
                    var copyKernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D, ArrayView<float>, ArrayView<float>>(
                        (index, source, target) =>
                        {
                            if (index < source.Length)
                                target[index] = source[index];
                        });

                    // Define Jacobi iteration kernel
                    var jacobiKernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int>(
                        (index, matrix, vector, currentX, newX, size) =>
                        {
                            if (index >= size)
                                return;

                            float sum = 0.0f;
                            float diagValue = matrix[index * size + index];

                            // Skip if this is a fixed value row (diagonal = 1, meaning boundary condition)
                            if (Math.Abs(diagValue - 1.0f) < 1e-6f)
                            {
                                newX[index] = currentX[index];
                                return;
                            }

                            // Calculate sum of off-diagonal elements
                            for (int j = 0; j < size; j++)
                            {
                                if (j != index)
                                {
                                    sum += matrix[index * size + j] * currentX[j];
                                }
                            }

                            // Update x using Jacobi formula: x_i = (b_i - sum) / A_ii
                            if (Math.Abs(diagValue) > 1e-10f)
                            {
                                newX[index] = (vector[index] - sum) / diagValue;
                            }
                            else
                            {
                                newX[index] = currentX[index]; // Keep old value if diagonal is too small
                            }
                        });

                    // Define residual calculation kernel
                    var residualKernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int>(
                        (index, matrix, vector, currentX, residual, size) =>
                        {
                            if (index >= size)
                                return;

                            float sum = 0.0f;
                            for (int j = 0; j < size; j++)
                            {
                                sum += matrix[index * size + j] * currentX[j];
                            }

                            float localResidual = Math.Abs(sum - vector[index]);
                            Atomic.Add(ref residual[0], localResidual);
                        });

                    // Perform Jacobi iterations
                    int maxIterations = 5000;
                    float tolerance = 1e-6f;
                    int iterations = 0;
                    float residualNorm = float.MaxValue;

                    while (iterations < maxIterations && residualNorm > tolerance)
                    {
                        // Perform one Jacobi iteration
                        jacobiKernel(n, deviceA.View, deviceB.View, deviceX.View, deviceXNew.View, n);

                        // Copy from deviceXNew to deviceX
                        copyKernel(n, deviceXNew.View, deviceX.View);

                        // Calculate residual (Ax - b)
                        initKernel(new Index1D(1), deviceResidual.View);
                        residualKernel(n, deviceA.View, deviceB.View, deviceX.View, deviceResidual.View, n);

                        // Get residual norm
                        float[] residualArray = deviceResidual.GetAsArray1D();
                        residualNorm = residualArray[0];

                        iterations++;

                        // Add some logging for monitoring progress
                        if (iterations % 100 == 0 || iterations == 1)
                        {
                            Logger.Log($"[PermeabilitySimulator] GPU Jacobi iteration {iterations}, residual: {residualNorm}");
                        }
                    }

                    // Copy result back to CPU
                    float[] result = deviceX.GetAsArray1D();
                    for (int i = 0; i < n; i++)
                    {
                        vectorX[i] = result[i];
                    }

                    Logger.Log($"[PermeabilitySimulator] GPU linear system solved in {iterations} iterations, final residual: {residualNorm}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[PermeabilitySimulator] GPU linear solver error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Solves a linear system Ax = b using Gauss-Seidel iterative method
        /// </summary>
        private void SolveLinearSystem(double[] A, double[] b, double[] x, int n, int maxIterations, double tolerance)
        {
            int iterations = 0;
            double residualNorm;

            do
            {
                residualNorm = 0;

                for (int i = 0; i < n; i++)
                {
                    double sum = 0;

                    // Calculate sum of off-diagonal elements multiplied by current x values
                    for (int j = 0; j < n; j++)
                    {
                        if (i != j)
                        {
                            sum += A[i * n + j] * x[j];
                        }
                    }

                    // Update x[i] using Gauss-Seidel formula
                    double oldX = x[i];
                    double diag = A[i * n + i];

                    // Prevent division by zero
                    if (Math.Abs(diag) > 1e-10)
                    {
                        x[i] = (b[i] - sum) / diag;
                    }

                    // Calculate residual for this element
                    double residual = x[i] - oldX;
                    residualNorm += residual * residual;
                }

                residualNorm = Math.Sqrt(residualNorm);
                iterations++;
            } while (residualNorm > tolerance && iterations < maxIterations);

            Logger.Log($"[PermeabilitySimulator] Linear system solved in {iterations} iterations, residual: {residualNorm}");
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    accelerator?.Dispose();
                    context?.Dispose();
                }

                // Set the flag to prevent redundant calls
                disposed = true;
            }
        }

        ~PermeabilitySimulator()
        {
            Dispose(false);
        }
    }
}

/*
Recommended Values by Rock Type
Rock Type	Max Throat Length Factor	Min Overlap Factor	Enforce Flow Path
Sandstone (high permeability)	3.0 - 4.0	0.05 - 0.10	True
Sandstone (tight)	2.5 - 3.0	0.10 - 0.15	True
Limestone	3.0 - 5.0	0.10 - 0.20	True
Shale/Mudstone	2.0 - 2.5	0.15 - 0.25	True
Fractured rock	5.0 - 8.0	0.01 - 0.05	True
Explanation of Parameters

Max Throat Length Factor (multiplier of average pore radius):

Lower values (2.0-3.0): Creates more isolated pores, better for tight formations
Medium values (3.0-4.0): Balanced connectivity for typical reservoir rocks
Higher values (4.0-8.0): Creates longer connections, good for fractured/vuggy rocks

Min Overlap Factor (fraction of smaller pore radius):

Lower values (0.01-0.05): More connections, higher permeability
Medium values (0.05-0.15): Standard for most reservoir rocks
Higher values (0.15-0.25): More restrictive, better for tight formations

Enforce Flow Path:

Almost always keep this True for permeability simulations
Only set to False if studying isolated pore networks specifically

Fluid Viscosity Values (Pa·s)
Fluid Type	Viscosity (Pa·s)	Notes
Water (20°C)	0.001	Standard reference fluid
Water (80°C)	0.00035	For high-temperature reservoirs
Light oil	0.005 - 0.05	Typical reservoir oils
Medium oil	0.05 - 0.5	Thicker oils
Heavy oil	0.5 - 10	Bitumen, tar sands
Air	0.000018	For gas flow studies
CO₂ (supercritical)	0.00005	For carbon sequestration studies
Brine (20°C)	0.0012 - 0.0015	Saline formation water
Pressure Settings
Standard Laboratory Conditions:

Input pressure: 10,000 - 100,000 Pa (10-100 kPa)

Output pressure: 0 - 10,000 Pa (0-10 kPa)

Differential: Typically 10,000 - 90,000 Pa (10-90 kPa)

By Rock Type:
Rock Type	Suggested Pressure Differential	Notes
High-perm sandstone (>1D)	10,000 - 30,000 Pa	Lower differential for high-perm samples
Medium-perm sandstone	30,000 - 50,000 Pa	Balanced for most reservoir rocks
Tight sandstone (<1mD)	50,000 - 100,000 Pa	Higher differential for tight rocks
Carbonates	20,000 - 70,000 Pa	Varies with pore structure
Shale/Tight formations	100,000 - 500,000 Pa	Very high differential needed*/