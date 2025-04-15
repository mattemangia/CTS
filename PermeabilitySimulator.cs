using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;

namespace CTSegmenter
{
    /// <summary>
    /// Simulates absolute permeability through a pore network model using Darcy's law
    /// </summary>
    public class PermeabilitySimulator : IDisposable
    {
        private Context context;
        private Accelerator accelerator;
        private bool gpuInitialized = false;
        private bool disposed = false;

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
        /// Simulates absolute permeability through the pore network model
        /// </summary>
        /// <param name="model">The pore network model</param>
        /// <param name="axis">The flow axis</param>
        /// <param name="viscosity">Fluid viscosity (Pa.s)</param>
        /// <param name="inputPressure">Input pressure (Pa)</param>
        /// <param name="outputPressure">Output pressure (Pa)</param>
        /// <param name="useGpu">Whether to use GPU acceleration if available</param>
        /// <param name="progress">Progress reporting</param>
        /// <returns>Simulation result containing permeability and pressure field</returns>
        public async Task<PermeabilitySimulationResult> SimulatePermeabilityAsync(
            PoreNetworkModel model,
            FlowAxis axis,
            double viscosity,
            double inputPressure,
            double outputPressure,
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
                OutputPressure = outputPressure
            };

            progress?.Report(10);

            // Use GPU or CPU based on availability and user preference
            if (useGpu && gpuInitialized)
            {
                Logger.Log("[PermeabilitySimulator] Using GPU for simulation");
                await SimulatePermeabilityGPU(result, progress);
            }
            else
            {
                Logger.Log("[PermeabilitySimulator] Using CPU for simulation");
                await SimulatePermeabilityCPU(result, progress);
            }

            progress?.Report(100);
            return result;
        }

        /// <summary>
        /// CPU implementation of permeability simulation
        /// </summary>
        private async Task SimulatePermeabilityCPU(PermeabilitySimulationResult result, IProgress<int> progress = null)
        {
            await Task.Run(() =>
            {
                // Get model data
                var model = result.Model;
                var axis = result.FlowAxis;
                double viscosity = result.Viscosity;
                double inputPressure = result.InputPressure;
                double outputPressure = result.OutputPressure;

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

                // Sort pores by axis coordinate - use stable sort for consistency
                var sortedPores = model.Pores.OrderBy(getAxisCoordinate).ThenBy(p => p.Id).ToList();

                // Find inlet and outlet pores (10% from ends)
                int numInletPores = Math.Max(1, (int)(model.Pores.Count * 0.1));
                int numOutletPores = numInletPores;

                var inletPores = sortedPores.Take(numInletPores).ToList();
                var outletPores = sortedPores.Skip(sortedPores.Count - numOutletPores).ToList();

                progress?.Report(20);

                // Set up the conductivity matrix and right-hand side vector
                int numPores = model.Pores.Count;
                double[] conductivityMatrix = new double[numPores * numPores];
                double[] rhs = new double[numPores];
                bool[] isFixed = new bool[numPores];

                // Initialize the pressure field
                double[] pressures = new double[numPores];

                // Create dictionary for fast access to pore index
                Dictionary<int, int> poreIdToIndex = new Dictionary<int, int>();
                for (int i = 0; i < model.Pores.Count; i++)
                {
                    poreIdToIndex[model.Pores[i].Id] = i;
                }

                // Set pressure boundary conditions
                foreach (var pore in inletPores)
                {
                    int idx = poreIdToIndex[pore.Id];
                    pressures[idx] = inputPressure;
                    isFixed[idx] = true;
                }

                foreach (var pore in outletPores)
                {
                    int idx = poreIdToIndex[pore.Id];
                    pressures[idx] = outputPressure;
                    isFixed[idx] = true;
                }

                progress?.Report(30);

                // Calculate throat conductivities based on Hagen-Poiseuille equation (Q = πr⁴Δp / 8μL)
                double[] throatConductivities = new double[model.Throats.Count];
                for (int i = 0; i < model.Throats.Count; i++)
                {
                    var throat = model.Throats[i];

                    // Convert from µm to m for calculation
                    double radius = throat.Radius * 1e-6; // µm to m
                    double length = throat.Length * 1e-6; // µm to m

                    // Calculate hydraulic conductivity (π*r^4) / (8*μ*L)
                    double conductivity = Math.PI * Math.Pow(radius, 4) / (8 * viscosity * length);
                    throatConductivities[i] = conductivity;
                }

                progress?.Report(40);

                // Set up the linear system for internal pores (mass conservation)
                for (int i = 0; i < numPores; i++)
                {
                    if (isFixed[i])
                    {
                        // For fixed pressure pores, set diagonal to 1 and RHS to fixed pressure
                        conductivityMatrix[i * numPores + i] = 1.0;
                        rhs[i] = pressures[i];
                    }
                    else
                    {
                        // For internal pores, apply mass conservation: sum of flows = 0
                        // Find all throats connected to this pore
                        int poreId = model.Pores[i].Id;

                        // For each throat connected to this pore
                        foreach (var throat in model.Throats)
                        {
                            if (throat.PoreId1 == poreId || throat.PoreId2 == poreId)
                            {
                                // Get other pore
                                int otherPoreId = throat.PoreId1 == poreId ? throat.PoreId2 : throat.PoreId1;
                                int otherPoreIndex = poreIdToIndex[otherPoreId];

                                // Get throat index
                                int throatIndex = model.Throats.IndexOf(throat);
                                double conductivity = throatConductivities[throatIndex];

                                // Add to diagonal (self)
                                conductivityMatrix[i * numPores + i] += conductivity;

                                // Add to off-diagonal (connection to other pore)
                                conductivityMatrix[i * numPores + otherPoreIndex] -= conductivity;
                            }
                        }
                    }
                }

                progress?.Report(50);

                // Solve the linear system using a simple Gauss-Seidel iterative solver
                double tolerance = 1e-10;
                int maxIterations = 10000;
                SolveLinearSystem(conductivityMatrix, rhs, pressures, numPores, maxIterations, tolerance);

                progress?.Report(70);

                // Calculate flow rates through each throat
                double totalFlowRate = 0;

                // Store the pressure at each pore for visualization
                var pressureField = new Dictionary<int, double>();
                for (int i = 0; i < model.Pores.Count; i++)
                {
                    pressureField[model.Pores[i].Id] = pressures[i];
                }

                // Calculate the flow rate through each throat
                var throatFlowRates = new Dictionary<int, double>();
                foreach (var throat in model.Throats)
                {
                    int pore1Index = poreIdToIndex[throat.PoreId1];
                    int pore2Index = poreIdToIndex[throat.PoreId2];

                    double pressureDrop = pressures[pore1Index] - pressures[pore2Index];
                    int throatIndex = model.Throats.IndexOf(throat);
                    double conductivity = throatConductivities[throatIndex];

                    double flowRate = conductivity * pressureDrop;
                    throatFlowRates[throat.Id] = flowRate;

                    // Calculate contribution to total flow (only count flow across the boundary)
                    var pore1 = model.Pores.First(p => p.Id == throat.PoreId1);
                    var pore2 = model.Pores.First(p => p.Id == throat.PoreId2);

                    // Check if one pore is inlet and one is outlet
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

                progress?.Report(80);

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

                // Pressure drop
                double deltaP = Math.Abs(inputPressure - outputPressure);

                // Calculate permeability using Darcy's Law: k = (Q * μ * L) / (A * ΔP)
                double permeability = (totalFlowRate * viscosity * modelLength) / (area * deltaP);

                // Convert from m² to Darcy (1 Darcy = 9.869233e-13 m²)
                double permeabilityDarcy = permeability / 9.869233e-13;

                // Update result
                result.PermeabilityDarcy = permeabilityDarcy;
                result.PermeabilityMilliDarcy = permeabilityDarcy * 1000;
                result.PressureField = pressureField;
                result.TotalFlowRate = totalFlowRate;
                result.ModelLength = modelLength;
                result.ModelArea = area;
                result.ThroatFlowRates = throatFlowRates;
                result.InletPores = inletPores.Select(p => p.Id).ToList();
                result.OutletPores = outletPores.Select(p => p.Id).ToList();

                progress?.Report(90);

                Logger.Log($"[PermeabilitySimulator] CPU simulation completed. Permeability: {permeabilityDarcy:F4} Darcy");
            });
        }


        /// <summary>
        /// GPU implementation of permeability simulation
        /// </summary>
        private async Task SimulatePermeabilityGPU(PermeabilitySimulationResult result, IProgress<int> progress = null)
        {
            // Ensure GPU is initialized
            if (!gpuInitialized)
            {
                Logger.Log("[PermeabilitySimulator] GPU not initialized, falling back to CPU");
                await SimulatePermeabilityCPU(result, progress);
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    // Get model data
                    var model = result.Model;
                    var axis = result.FlowAxis;
                    double viscosity = result.Viscosity;
                    double inputPressure = result.InputPressure;
                    double outputPressure = result.OutputPressure;

                    Logger.Log($"[PermeabilitySimulator] Using GPU accelerator: {accelerator.Name}");
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

                    var inletPores = sortedPores.Take(numInletPores).ToList();
                    var outletPores = sortedPores.Skip(sortedPores.Count - numOutletPores).ToList();

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

                    // Use GPU to calculate throat conductivities
                    float[] throatConductivities = new float[numThroats];
                    float viscosityFloat = (float)viscosity;

                    // Allocate device memory
                    using (var deviceThroatRadii = accelerator.Allocate1D(throatRadii))
                    using (var deviceThroatLengths = accelerator.Allocate1D(throatLengths))
                    using (var deviceConductivities = accelerator.Allocate1D<float>(numThroats))
                    {
                        // Define kernel to calculate conductivities
                        var calculateConductivitiesKernel = accelerator.LoadAutoGroupedStreamKernel<
                            Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, float>(
                            (index, radii, lengths, conductivities, visc) =>
                            {
                                // Skip if out of range
                                if (index >= radii.Length)
                                    return;

                                // Convert from µm to m for calculation
                                float radius = radii[index] * 1e-6f; // µm to m
                                float length = lengths[index] * 1e-6f; // µm to m

                                // Calculate hydraulic conductivity (π*r^4) / (8*μ*L)
                                float conductivity = (float)(Math.PI * Math.Pow(radius, 4) / (8 * visc * length));
                                conductivities[index] = conductivity;
                            });

                        // Execute kernel
                        calculateConductivitiesKernel(numThroats, deviceThroatRadii.View, deviceThroatLengths.View,
                            deviceConductivities.View, viscosityFloat);

                        // Get results back to host
                        deviceConductivities.CopyToCPU(throatConductivities);
                    }

                    progress?.Report(40);

                    // For consistent results with CPU, we'll build the linear system on CPU
                    // but use the GPU-calculated conductivities
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

                    // Try to solve the linear system using GPU
                    bool usedGpuSolver = false;

                    try
                    {
                        // For very large matrices, GPU solver may run into memory limitations
                        if (numPores <= 20000) // Threshold based on GPU memory
                        {
                            usedGpuSolver = SolveLinearSystemGPU(conductivityMatrix, rhs, pressuresSolution, numPores);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[PermeabilitySimulator] GPU linear solver failed: {ex.Message}. Falling back to CPU solver.");
                        usedGpuSolver = false;
                    }

                    // If GPU solver didn't work, use CPU solver
                    if (!usedGpuSolver)
                    {
                        double tolerance = 1e-10;
                        int maxIterations = 10000;
                        SolveLinearSystem(conductivityMatrix, rhs, pressuresSolution, numPores, maxIterations, tolerance);
                    }

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

                    progress?.Report(80);

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

                    // Pressure drop
                    double deltaP = Math.Abs(inputPressure - outputPressure);

                    // Calculate permeability using Darcy's Law: k = (Q * μ * L) / (A * ΔP)
                    double permeability = (totalFlowRate * viscosity * modelLength) / (area * deltaP);

                    // Convert from m² to Darcy (1 Darcy = 9.869233e-13 m²)
                    double permeabilityDarcy = permeability / 9.869233e-13;

                    // Update result
                    result.PermeabilityDarcy = permeabilityDarcy;
                    result.PermeabilityMilliDarcy = permeabilityDarcy * 1000;
                    result.PressureField = pressureField;
                    result.TotalFlowRate = totalFlowRate;
                    result.ModelLength = modelLength;
                    result.ModelArea = area;
                    result.ThroatFlowRates = throatFlowRates;
                    result.InletPores = inletPores.Select(p => p.Id).ToList();
                    result.OutletPores = outletPores.Select(p => p.Id).ToList();

                    progress?.Report(90);

                    Logger.Log($"[PermeabilitySimulator] GPU simulation completed. Permeability: {permeabilityDarcy:F4} Darcy");
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[PermeabilitySimulator] GPU simulation failed: {ex.Message}");
                Logger.Log($"[PermeabilitySimulator] Stack trace: {ex.StackTrace}");
                Logger.Log("[PermeabilitySimulator] Falling back to CPU implementation");

                // Fall back to CPU implementation
                await SimulatePermeabilityCPU(result, progress);
            }
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

    /// <summary>
    /// Stores the results of a permeability simulation
    /// </summary>
    [Serializable]
    public class PermeabilitySimulationResult
    {
        public PoreNetworkModel Model { get; set; }
        public PermeabilitySimulator.FlowAxis FlowAxis { get; set; }
        public double Viscosity { get; set; }
        public double InputPressure { get; set; }
        public double OutputPressure { get; set; }
        public double PermeabilityDarcy { get; set; }
        public double PermeabilityMilliDarcy { get; set; }
        public Dictionary<int, double> PressureField { get; set; }
        public Dictionary<int, double> ThroatFlowRates { get; set; }
        public double TotalFlowRate { get; set; }
        public double ModelLength { get; set; }
        public double ModelArea { get; set; }
        public List<int> InletPores { get; set; }
        public List<int> OutletPores { get; set; }
    }
}
