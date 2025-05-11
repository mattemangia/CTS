using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CTS.Modules.Simulation.NMR
{
    /// <summary>
    /// Represents a single relaxation component in the NMR simulation
    /// </summary>
    public struct RelaxationComponent
    {
        public double RelaxationTime; // T1 or T2 in ms
        public double Amplitude;      // Initial magnetization amplitude
        public double Tortuosity;     // Tortuosity factor for the component

        public RelaxationComponent(double relaxationTime, double amplitude, double tortuosity = 1.0)
        {
            RelaxationTime = relaxationTime;
            Amplitude = amplitude;
            Tortuosity = tortuosity;
        }
    }

    /// <summary>
    /// Represents NMR properties for a specific material
    /// </summary>
    public class MaterialNMRProperties
    {
        public string MaterialName { get; set; }
        public double RelaxationTime { get; set; } // T2 relaxation time in ms
        public double Density { get; set; } // Hydrogen density
        public double Tortuosity { get; set; } // Tortuosity factor
        public double RelaxationStrength { get; set; } // Relaxation strength factor
        public double PorosityEffect { get; set; } // How porosity affects relaxation

        public MaterialNMRProperties()
        {
            RelaxationTime = 100.0; // Default T2 = 100ms
            Density = 1.0;
            Tortuosity = 1.0;
            RelaxationStrength = 1.0;
            PorosityEffect = 1.0;
        }

        public MaterialNMRProperties Copy()
        {
            return new MaterialNMRProperties
            {
                MaterialName = MaterialName,
                RelaxationTime = RelaxationTime,
                Density = Density,
                Tortuosity = Tortuosity,
                RelaxationStrength = RelaxationStrength,
                PorosityEffect = PorosityEffect
            };
        }
    }

    /// <summary>
    /// Represents the result of an NMR simulation
    /// </summary>
    public class NMRSimulationResult
    {
        public double[] TimePoints { get; set; }
        public double[] Magnetization { get; set; }
        public double[] T2Distribution { get; set; }
        public double[] T2Values { get; set; }
        public double[] ComponentAmplitudes { get; set; }
        public List<RelaxationComponent> FittedComponents { get; set; }
        public double TotalPorosity { get; set; }
        public double AverageTortuosity { get; set; }
        public double AverageT2 { get; set; }
        public double SimulationTime { get; set; }
        public int ThreadsUsed { get; set; }
        public bool UsedGPU { get; set; }

        public NMRSimulationResult()
        {
            FittedComponents = new List<RelaxationComponent>();
        }
    }

    /// <summary>
    /// Core NMR simulation engine with both CPU and GPU support
    /// </summary>
    public class NMRSimulation
    {
        private readonly MainForm _mainForm;
        private readonly double _pixelSize;

        // Simulation parameters
        public int MaxThreads { get; set; } = Environment.ProcessorCount;
        public bool UseGPU { get; set; } = false;
        public double MaxTime { get; set; } = 1000.0; // ms
        public int TimePoints { get; set; } = 1000;
        public int T2Components { get; set; } = 32;
        public double MinT2 { get; set; } = 0.1; // ms
        public double MaxT2 { get; set; } = 5000.0; // ms

        // Material properties
        private Dictionary<byte, MaterialNMRProperties> _materialProperties;

        // GPU compute instance
        private NMRGPUDirectCompute _gpuCompute;

        public NMRSimulation(MainForm mainForm)
        {
            _mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));
            _pixelSize = mainForm.GetPixelSize();

            _materialProperties = new Dictionary<byte, MaterialNMRProperties>();
            InitializeDefaultMaterialProperties();

            // Initialize GPU compute if available
            if (NMRGPUDirectCompute.IsGPUAvailable())
            {
                _gpuCompute = new NMRGPUDirectCompute();
                UseGPU = true;
            }
        }

        private void InitializeDefaultMaterialProperties()
        {
            // Default properties for exterior material (set density to 0 to exclude from computation)
            _materialProperties[0] = new MaterialNMRProperties
            {
                MaterialName = "Exterior",
                RelaxationTime = 0.1,
                Density = 0.0,  // Zero density ensures it's excluded from computation
                Tortuosity = 1.0,
                RelaxationStrength = 0.0,
                PorosityEffect = 0.0
            };

            // Add properties for existing materials
            foreach (var material in _mainForm.Materials)
            {
                // Skip exterior material
                if (material.ID == 0)
                    continue;

                if (!_materialProperties.ContainsKey(material.ID))
                {
                    _materialProperties[material.ID] = new MaterialNMRProperties
                    {
                        MaterialName = material.Name,
                        RelaxationTime = GetDefaultT2ForMaterial(material.Name),
                        Density = GetDefaultDensityForMaterial(material.Name),
                        Tortuosity = GetDefaultTortuosityForMaterial(material.Name),
                        RelaxationStrength = 1.0,
                        PorosityEffect = 1.0
                    };
                }
            }
        }

        private double GetDefaultT2ForMaterial(string materialName)
        {
            // Typical T2 values for different materials
            if (materialName.ToLower().Contains("water") || materialName.ToLower().Contains("fluid"))
                return 1000.0;
            if (materialName.ToLower().Contains("oil"))
                return 500.0;
            if (materialName.ToLower().Contains("gas"))
                return 50.0;
            if (materialName.ToLower().Contains("pore") || materialName.ToLower().Contains("void"))
                return 300.0;

            // Default for solid materials
            return 10.0;
        }

        private double GetDefaultDensityForMaterial(string materialName)
        {
            // Hydrogen density (relative to water)
            if (materialName.ToLower().Contains("water"))
                return 1.0;
            if (materialName.ToLower().Contains("oil"))
                return 0.9;
            if (materialName.ToLower().Contains("gas"))
                return 0.1;
            if (materialName.ToLower().Contains("pore"))
                return 0.8; // Assuming partial water saturation

            // Default for solid materials (bound hydrogen)
            return 0.1;
        }

        private double GetDefaultTortuosityForMaterial(string materialName)
        {
            // Tortuosity values
            if (materialName.ToLower().Contains("pore") || materialName.ToLower().Contains("void"))
                return 2.0; // Typical for porous media
            if (materialName.ToLower().Contains("fracture"))
                return 1.5;

            // Default
            return 1.0;
        }

        public MaterialNMRProperties GetMaterialProperties(byte materialID)
        {
            if (_materialProperties.ContainsKey(materialID))
                return _materialProperties[materialID].Copy();

            return new MaterialNMRProperties();
        }

        public void SetMaterialProperties(byte materialID, MaterialNMRProperties properties)
        {
            _materialProperties[materialID] = properties.Copy();
        }

        
        private double[] GenerateTimePoints()
        {
            var points = new double[TimePoints];
            double logStep = (Math.Log10(MaxTime) - Math.Log10(0.1)) / (TimePoints - 1);

            for (int i = 0; i < TimePoints; i++)
            {
                points[i] = Math.Pow(10, Math.Log10(0.1) + i * logStep);
            }

            return points;
        }

        
        public async Task<NMRSimulationResult> RunSimulationAsync(NMRCalibration calibration = null, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.Now;

            // Create result structure
            var result = new NMRSimulationResult
            {
                TimePoints = GenerateTimePoints(),
                ThreadsUsed = MaxThreads,
                UsedGPU = UseGPU && _gpuCompute != null
            };

            cancellationToken.ThrowIfCancellationRequested();

            // Analyze volume and extract pore structure
            var poreStructure = AnalyzePoreStructure(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // Generate relaxation components based on material properties and pore structure
            var components = GenerateRelaxationComponents(poreStructure);

            cancellationToken.ThrowIfCancellationRequested();

            // Apply calibration if available
            if (calibration != null && calibration.IsCalibrated)
            {
                ApplyCalibration(components, calibration);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Perform simulation
            if (UseGPU && _gpuCompute != null)
            {
                result.Magnetization = await RunGPUSimulationAsync(components, result.TimePoints, cancellationToken);
            }
            else
            {
                result.Magnetization = await Task.Run(() => RunCPUSimulation(components, result.TimePoints, cancellationToken), cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Calculate T2 distribution
            CalculateT2Distribution(result, components);

            cancellationToken.ThrowIfCancellationRequested();

            // Calculate statistics
            CalculateStatistics(result, components, poreStructure);

            result.SimulationTime = (DateTime.Now - startTime).TotalMilliseconds;

            return result;
        }

        // Updated supporting method with cancellation
        private Dictionary<byte, PoreStructureInfo> AnalyzePoreStructure(CancellationToken cancellationToken)
        {
            var structure = new Dictionary<byte, PoreStructureInfo>();

            // Analyze volume for pore structure
            int width = _mainForm.GetWidth();
            int height = _mainForm.GetHeight();
            int depth = _mainForm.GetDepth();

            // Count voxels for each material
            var materialCounts = new ConcurrentDictionary<byte, long>();
            var poreConnectivity = new ConcurrentDictionary<byte, double>();
            var avgPoreSize = new ConcurrentDictionary<byte, double>();

            // Parallel analysis of volume
            Parallel.For(0, depth, new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxThreads,
                CancellationToken = cancellationToken
            }, z =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var localCounts = new Dictionary<byte, long>();
                var localConnectivity = new Dictionary<byte, double>();
                var localPoreSize = new Dictionary<byte, double>();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        byte label = _mainForm.volumeLabels[x, y, z];

                        // Skip exterior material (ID 0)
                        if (label == 0)
                            continue;

                        // Count voxels
                        if (!localCounts.ContainsKey(label))
                            localCounts[label] = 0;
                        localCounts[label]++;

                        // Analyze connectivity for pore materials
                        if (_materialProperties[label].Density > 0.3) // Assume pore-like materials
                        {
                            double connectivity = AnalyzeLocalConnectivity(x, y, z, label);
                            if (!localConnectivity.ContainsKey(label))
                                localConnectivity[label] = 0;
                            localConnectivity[label] += connectivity;

                            double poreSize = EstimateLocalPoreSize(x, y, z, label);
                            if (!localPoreSize.ContainsKey(label))
                                localPoreSize[label] = 0;
                            localPoreSize[label] += poreSize;
                        }
                    }
                }

                // Merge results
                foreach (var kvp in localCounts)
                {
                    materialCounts.AddOrUpdate(kvp.Key, kvp.Value, (k, v) => v + kvp.Value);
                }

                foreach (var kvp in localConnectivity)
                {
                    poreConnectivity.AddOrUpdate(kvp.Key, kvp.Value, (k, v) => v + kvp.Value);
                }

                foreach (var kvp in localPoreSize)
                {
                    avgPoreSize.AddOrUpdate(kvp.Key, kvp.Value, (k, v) => v + kvp.Value);
                }
            });

            // Create structure info for each material (excluding exterior)
            foreach (var material in _mainForm.Materials)
            {
                if (material.ID == 0 || !materialCounts.ContainsKey(material.ID))
                    continue;

                structure[material.ID] = new PoreStructureInfo
                {
                    MaterialID = material.ID,
                    VoxelCount = materialCounts[material.ID],
                    Porosity = (double)materialCounts[material.ID] / (width * height * depth),
                    AveragePoreSize = avgPoreSize.ContainsKey(material.ID) ?
                        avgPoreSize[material.ID] / materialCounts[material.ID] : 0,
                    Connectivity = poreConnectivity.ContainsKey(material.ID) ?
                        poreConnectivity[material.ID] / materialCounts[material.ID] : 0
                };
            }

            return structure;
        }
        private double AnalyzeLocalConnectivity(int x, int y, int z, byte label)
        {
            int connections = 0;
            int neighbors = 0;

            // Check 6-connectivity
            for (int d = 0; d < 6; d++)
            {
                int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                int ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                int nz = z + (d == 4 ? 1 : d == 5 ? -1 : 0);

                if (nx >= 0 && nx < _mainForm.GetWidth() &&
                    ny >= 0 && ny < _mainForm.GetHeight() &&
                    nz >= 0 && nz < _mainForm.GetDepth())
                {
                    if (_mainForm.volumeLabels[nx, ny, nz] == label)
                        connections++;
                    neighbors++;
                }
            }

            return neighbors > 0 ? (double)connections / neighbors : 0;
        }

        private double EstimateLocalPoreSize(int x, int y, int z, byte label)
        {
            // Simple estimate based on distance to nearest non-pore voxel
            double minDistance = double.MaxValue;

            for (int dz = -5; dz <= 5; dz++)
            {
                for (int dy = -5; dy <= 5; dy++)
                {
                    for (int dx = -5; dx <= 5; dx++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;

                        int nx = x + dx;
                        int ny = y + dy;
                        int nz = z + dz;

                        if (nx >= 0 && nx < _mainForm.GetWidth() &&
                            ny >= 0 && ny < _mainForm.GetHeight() &&
                            nz >= 0 && nz < _mainForm.GetDepth())
                        {
                            if (_mainForm.volumeLabels[nx, ny, nz] != label)
                            {
                                double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz) * _pixelSize;
                                minDistance = Math.Min(minDistance, distance);
                            }
                        }
                    }
                }
            }

            return minDistance != double.MaxValue ? minDistance : 0;
        }

        private List<RelaxationComponent> GenerateRelaxationComponents(Dictionary<byte, PoreStructureInfo> poreStructure)
        {
            var components = new List<RelaxationComponent>();

            // Generate T2 values logarithmically distributed
            var t2Values = new List<double>();
            double logStep = (Math.Log10(MaxT2) - Math.Log10(MinT2)) / (T2Components - 1);

            for (int i = 0; i < T2Components; i++)
            {
                double t2 = Math.Pow(10, Math.Log10(MinT2) + i * logStep);
                t2Values.Add(t2);
            }

            // Create relaxation components for each material and T2 value (excluding exterior)
            foreach (var material in _mainForm.Materials)
            {
                // Skip exterior material (ID 0)
                if (material.ID == 0)
                    continue;

                var properties = _materialProperties[material.ID];

                if (properties.Density > 0) // Only for materials with hydrogen
                {
                    var structure = poreStructure.ContainsKey(material.ID) ?
                        poreStructure[material.ID] : new PoreStructureInfo();

                    foreach (double t2 in t2Values)
                    {
                        // Calculate amplitude based on material density and pore structure
                        double amplitude = CalculateComponentAmplitude(properties, structure, t2);

                        if (amplitude > 0)
                        {
                            // Calculate effective tortuosity
                            double effectiveTortuosity = CalculateEffectiveTortuosity(
                                properties.Tortuosity, structure.Connectivity, structure.AveragePoreSize);

                            // Apply tortuosity effect to T2
                            double effectiveT2 = t2 / effectiveTortuosity;

                            components.Add(new RelaxationComponent(effectiveT2, amplitude, effectiveTortuosity));
                        }
                    }
                }
            }

            return components;
        }
        private double CalculateComponentAmplitude(MaterialNMRProperties properties,
            PoreStructureInfo structure, double t2)
        {
            // Base amplitude from hydrogen density
            double baseAmplitude = properties.Density * structure.Porosity;

            // T2 distribution based on material relaxation characteristics
            double t2Distribution = CalculateT2Distribution(t2, properties.RelaxationTime,
                properties.RelaxationStrength);

            // Apply pore size effects
            double poreSizeEffect = CalculatePoreSizeEffect(structure.AveragePoreSize, t2);

            return baseAmplitude * t2Distribution * poreSizeEffect * properties.PorosityEffect;
        }

        private double CalculateT2Distribution(double t2, double meanT2, double relaxationStrength)
        {
            // Log-normal distribution for T2 values
            double sigma = 0.5 / relaxationStrength; // Width of distribution
            double logT2 = Math.Log(t2);
            double logMeanT2 = Math.Log(meanT2);

            double exponent = -Math.Pow(logT2 - logMeanT2, 2) / (2 * sigma * sigma);
            return Math.Exp(exponent) / (t2 * sigma * Math.Sqrt(2 * Math.PI));
        }

        private double CalculatePoreSizeEffect(double averagePoreSize, double t2)
        {
            // Smaller pores -> shorter T2 times
            if (averagePoreSize > 0)
            {
                double criticalSize = 10e-6; // 10 μm
                double sizeRatio = criticalSize / averagePoreSize;

                // Exponential relationship between pore size and preferred T2
                return Math.Exp(-sizeRatio / t2);
            }

            return 1.0;
        }

        private double CalculateEffectiveTortuosity(double baseTortuosity, double connectivity,
            double averagePoreSize)
        {
            // Tortuosity increases with less connectivity and smaller pores
            double connectivityEffect = 1.0 + (1.0 - connectivity);
            double sizeEffect = averagePoreSize > 0 ? 1.0 / Math.Sqrt(averagePoreSize / 10e-6) : 1.0;

            return baseTortuosity * connectivityEffect * sizeEffect;
        }

        private void ApplyCalibration(List<RelaxationComponent> components, NMRCalibration calibration)
        {
            // Apply calibration transformation to all components
            for (int i = 0; i < components.Count; i++)
            {
                var component = components[i];

                // Apply T2 calibration
                component.RelaxationTime = calibration.TransformT2(component.RelaxationTime);

                // Apply amplitude calibration
                component.Amplitude = calibration.TransformAmplitude(component.Amplitude);

                // Update the component in the list
                components[i] = component;
            }
        }
        private double[] RunCPUSimulation(List<RelaxationComponent> components, double[] timePoints, CancellationToken cancellationToken)
        {
            var magnetization = new double[timePoints.Length];

            // Parallel calculation of magnetization decay
            Parallel.For(0, timePoints.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxThreads,
                CancellationToken = cancellationToken
            }, i =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                double t = timePoints[i];
                double mag = 0;

                // Sum contributions from all components
                for (int j = 0; j < components.Count; j++)
                {
                    var component = components[j];
                    mag += component.Amplitude * Math.Exp(-t / component.RelaxationTime);
                }

                magnetization[i] = mag;
            });

            return magnetization;
        }


        private async Task<double[]> RunGPUSimulationAsync(List<RelaxationComponent> components, double[] timePoints, CancellationToken cancellationToken)
        {
            // Prepare data for GPU
            var t2Values = components.Select(c => (float)c.RelaxationTime).ToArray();
            var amplitudes = components.Select(c => (float)c.Amplitude).ToArray();
            var times = timePoints.Select(t => (float)t).ToArray();

            // Run GPU simulation
            var result = await _gpuCompute.ComputeDecayAsync(t2Values, amplitudes, times, cancellationToken);

            // Convert back to double
            return result.Select(f => (double)f).ToArray();
        }

        private void CalculateT2Distribution(NMRSimulationResult result, List<RelaxationComponent> components)
        {
            // Create T2 histogram
            result.T2Values = new double[T2Components];
            result.T2Distribution = new double[T2Components];

            double logStep = (Math.Log10(MaxT2) - Math.Log10(MinT2)) / (T2Components - 1);

            for (int i = 0; i < T2Components; i++)
            {
                result.T2Values[i] = Math.Pow(10, Math.Log10(MinT2) + i * logStep);
                result.T2Distribution[i] = 0;
            }

            // Bin components into histogram
            foreach (var component in components)
            {
                int bin = FindT2Bin(component.RelaxationTime, result.T2Values);
                if (bin >= 0 && bin < T2Components)
                {
                    result.T2Distribution[bin] += component.Amplitude;
                }
            }

            // Normalize distribution
            double total = result.T2Distribution.Sum();
            if (total > 0)
            {
                for (int i = 0; i < T2Components; i++)
                {
                    result.T2Distribution[i] /= total;
                }
            }

            // Store fitted components
            result.FittedComponents = components;

            // Store component amplitudes
            result.ComponentAmplitudes = components.Select(c => c.Amplitude).ToArray();
        }

        private int FindT2Bin(double t2, double[] t2Values)
        {
            // Binary search for closest T2 bin
            int left = 0;
            int right = t2Values.Length - 1;

            while (left <= right)
            {
                int mid = (left + right) / 2;

                if (t2Values[mid] < t2)
                    left = mid + 1;
                else if (t2Values[mid] > t2)
                    right = mid - 1;
                else
                    return mid;
            }

            // Return closest bin
            if (left >= t2Values.Length)
                return t2Values.Length - 1;
            if (right < 0)
                return 0;

            double distLeft = Math.Abs(t2 - t2Values[left]);
            double distRight = Math.Abs(t2 - t2Values[right]);

            return distLeft < distRight ? left : right;
        }

        private void CalculateStatistics(NMRSimulationResult result, List<RelaxationComponent> components,
            Dictionary<byte, PoreStructureInfo> poreStructure)
        {
            // Total porosity
            result.TotalPorosity = poreStructure.Values.Sum(p => p.Porosity);

            // Average tortuosity (weighted by amplitude)
            double totalAmplitude = components.Sum(c => c.Amplitude);
            if (totalAmplitude > 0)
            {
                result.AverageTortuosity = components.Sum(c => c.Amplitude * c.Tortuosity) / totalAmplitude;
            }

            // Average T2 (weighted by amplitude)
            if (totalAmplitude > 0)
            {
                result.AverageT2 = components.Sum(c => c.Amplitude * c.RelaxationTime) / totalAmplitude;
            }
        }

        public void Dispose()
        {
            _gpuCompute?.Dispose();
        }
    }

    /// <summary>
    /// Stores pore structure information for a material
    /// </summary>
    public class PoreStructureInfo
    {
        public byte MaterialID { get; set; }
        public long VoxelCount { get; set; }
        public double Porosity { get; set; }
        public double AveragePoreSize { get; set; }
        public double Connectivity { get; set; }
    }
}