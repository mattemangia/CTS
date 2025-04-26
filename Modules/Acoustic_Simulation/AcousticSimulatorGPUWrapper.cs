using System;
using System.Threading;
using System.Threading.Tasks;
using CTSegmenter.Modules.Acoustic_Simulation;

namespace CTSegmenter
{
    /// <summary>
    /// Wrapper class for the GPU acoustic simulator that provides the same interface as the CPU version.
    /// </summary>
    public class AcousticSimulatorGPUWrapper : IDisposable
    {
        // Events that match the CPU simulator interface
        public event EventHandler<AcousticSimulationProgressEventArgs> ProgressUpdated;
        public event EventHandler<AcousticSimulationCompleteEventArgs> SimulationCompleted;

        // Simulation parameters
        private int width, height, depth;
        private float pixelSize;
        private byte[,,] volumeLabels;
        private float[,,] densityVolume;
        private byte selectedMaterialID;
        private string axis, waveType;
        private double energy, frequency;
        private int amplitude, timeSteps;
        private double youngsModulus, poissonRatio;
        private double lambda, mu; // Lame parameters
        private CancellationTokenSource cts;
        private bool receiverTouched = false;
        private int stepCount = 0, postTouchCount = 0, touchStep = 0;
        private int maxPostSteps;
        private bool simulationRunning = false;

        // Source/receiver positions
        private int tx, ty, tz; // Transmitter
        private int rx, ry, rz; // Receiver

        // The underlying GPU simulator
        private AcousticSimulatorGPU gpuSimulator;

        /// <summary>
        /// Constructor with the same signature as AcousticSimulator to ensure compatibility
        /// </summary>
        public AcousticSimulatorGPUWrapper(
            int width, int height, int depth, float pixelSize,
            byte[,,] volumeLabels, float[,,] densityVolume, byte selectedMaterialID,
            string axis, string waveType,
            double confiningPressure, double tensileStrength, double failureAngle, double cohesion,
            double energy, double frequency, int amplitude, int timeSteps,
            bool useElasticModel, bool usePlasticModel, bool useBrittleModel,
            double youngsModulus, double poissonRatio)
        {
            // Store parameters
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.pixelSize = pixelSize;
            this.volumeLabels = volumeLabels;
            this.densityVolume = densityVolume;
            this.selectedMaterialID = selectedMaterialID;
            this.axis = axis;
            this.waveType = waveType;
            this.energy = energy;
            this.frequency = frequency;
            this.amplitude = amplitude;
            this.timeSteps = timeSteps;
            this.youngsModulus = youngsModulus;
            this.poissonRatio = poissonRatio;
            this.maxPostSteps = timeSteps / 2; // Only continue for half the total steps after receiver touch

            // Compute Lame parameters from Young's modulus and Poisson's ratio
            double E = youngsModulus * 1e6; // Convert from MPa to Pa
            mu = E / (2 * (1 + poissonRatio));
            lambda = E * poissonRatio / ((1 + poissonRatio) * (1 - 2 * poissonRatio));

            // Set transducer positions based on the axis
            switch (axis)
            {
                case "X": tx = 0; ty = height / 2; tz = depth / 2; rx = width - 1; ry = height / 2; rz = depth / 2; break;
                case "Y": tx = width / 2; ty = 0; tz = depth / 2; rx = width / 2; ry = height - 1; rz = depth / 2; break;
                default: tx = width / 2; ty = height / 2; tz = 0; rx = width / 2; ry = height / 2; rz = depth - 1; break;
            }

            // Initialize the GPU simulator
            InitializeGPUSimulator();
        }

        /// <summary>
        /// Initialize the GPU simulator with the volume data and material properties
        /// </summary>
        private void InitializeGPUSimulator()
        {
            // Create the GPU simulator
            gpuSimulator = new AcousticSimulatorGPU(width, height, depth, pixelSize);

            // Configure the auto-stop feature to monitor receiver position
            gpuSimulator.ConfigureAutoStop(rx, ry, rz, true, 0.01, 5, timeSteps / 10);
            gpuSimulator.SetTotalSteps(timeSteps);

            // Prepare material buffer (flatten 3D array to 1D)
            byte[] flatMaterials = new byte[width * height * depth];
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = x + y * width + z * width * height;
                        flatMaterials[index] = volumeLabels[x, y, z];
                    }
                }
            }

            // Send material data to GPU
            gpuSimulator.SetMaterials(flatMaterials);

            // Setup material properties for various materials
            float[] materialProps = new float[256]; // Properties for up to 256 different materials

            // Set properties for the selected material - use speed of sound (or other properties)
            // calculated from Young's modulus and Poisson's ratio
            float speedOfSound = (float)Math.Sqrt((lambda + 2 * mu) / GetAverageDensity());
            materialProps[selectedMaterialID] = speedOfSound;

            // Set other material properties as needed
            gpuSimulator.SetMaterialProperties(materialProps);
        }

        /// <summary>
        /// Calculate average density for the selected material
        /// </summary>
        private float GetAverageDensity()
        {
            float totalDensity = 0;
            int count = 0;

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            totalDensity += densityVolume[x, y, z];
                            count++;
                        }
                    }
                }
            }

            return count > 0 ? totalDensity / count : 1000.0f; // Default to water density if no voxels found
        }

        /// <summary>
        /// Start the simulation - matches the AcousticSimulator.StartSimulation method
        /// </summary>
        public void StartSimulation()
        {
            cts = new CancellationTokenSource();
            stepCount = 0;
            postTouchCount = 0;
            receiverTouched = false;
            touchStep = 0;
            simulationRunning = true;

            // Reset the GPU simulator
            gpuSimulator.Reset();

            // Run the simulation in a background task
            Task.Run(() => RunSimulation(cts.Token), cts.Token);
        }

        /// <summary>
        /// Cancel the simulation - matches the AcousticSimulator.CancelSimulation method
        /// </summary>
        public void CancelSimulation()
        {
            cts?.Cancel();
            simulationRunning = false;
        }

        /// <summary>
        /// Main simulation loop
        /// </summary>
        private void RunSimulation(CancellationToken token)
        {
            try
            {
                // Setup the source wavelet
                ApplySourceWavelet();

                // Estimate the number of iterations until the wave should reach the receiver
                double dist = Math.Sqrt(Math.Pow(tx - rx, 2) + Math.Pow(ty - ry, 2) + Math.Pow(tz - rz, 2)) * pixelSize;
                double vpEstimate = Math.Sqrt((lambda + 2 * mu) / GetAverageDensity());
                double expectedIterations = dist / (pixelSize * vpEstimate) * 1.5; // Add 50% margin

                // Main simulation loop - now uses the modified Step() function that returns false when auto-stop triggers
                bool continueSimulation = true;
                while (!token.IsCancellationRequested && continueSimulation && stepCount < timeSteps)
                {
                    // Step the simulation forward - now use return value to know when to stop
                    continueSimulation = gpuSimulator.Step();
                    gpuSimulator.ApplyAbsorbingBoundary();
                    stepCount++;

                    // Track first touch for velocity calculations
                    if (!receiverTouched && CheckReceiver())
                    {
                        receiverTouched = true;
                        touchStep = stepCount;
                    }

                    // Report progress every 5 steps
                    if (stepCount % 5 == 0)
                    {
                        // Calculate progress based on energy-based auto-stop
                        int progress;
                        if (!continueSimulation)
                        {
                            // If auto-stop triggered, show high progress
                            progress = 95;
                        }
                        else if (receiverTouched)
                        {
                            // If wave reached receiver but still running
                            progress = 50 + (int)(45.0 * Math.Min(1.0, stepCount / (double)timeSteps));
                        }
                        else
                        {
                            // Still waiting for wave to reach receiver
                            progress = (int)(49.0 * Math.Min(1.0, stepCount / expectedIterations));
                        }

                        progress = Math.Max(0, Math.Min(99, progress));

                        // Get wave fields for visualization (P-wave and S-wave)
                        var (vx, vy, vz) = GetWaveFieldSnapshot();
                        float[,,] pWaveField = ConvertToFloat(vx);
                        float[,,] sWaveField = ConvertToFloat(vy);

                        // Update status message based on simulation state
                        string statusMessage = !continueSimulation
                            ? "Simulation complete (energy-based auto-stop)"
                            : receiverTouched
                                ? $"Wave detected at receiver, continuing simulation ({stepCount}/{timeSteps} steps)"
                                : $"Propagating wave through material ({stepCount}/{timeSteps} steps)";

                        // Report progress to the UI
                        ProgressUpdated?.Invoke(this, new AcousticSimulationProgressEventArgs(
                            progress, stepCount, statusMessage, pWaveField, sWaveField));
                    }
                }

                // Final progress update showing 100%
                var finalSnapshot = GetWaveFieldSnapshot();
                ProgressUpdated?.Invoke(this, new AcousticSimulationProgressEventArgs(
                    100, stepCount, "Simulation complete, calculating results...",
                    ConvertToFloat(finalSnapshot.vx), ConvertToFloat(finalSnapshot.vy)));

                // Simulation completed - calculate results
                if (!token.IsCancellationRequested)
                {
                    CalculateResults();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GPU Simulation error: {ex.Message}");
            }
            finally
            {
                simulationRunning = false;
            }
        }

        /// <summary>
        /// Apply the source wavelet to excite the system
        /// </summary>
        private void ApplySourceWavelet()
        {
            // Calculate timestep (this should match what the AcousticSimulatorGPU uses internally)
            float speedOfSound = (float)Math.Sqrt((lambda + 2 * mu) / GetAverageDensity());
            float dt = 0.5f * pixelSize / speedOfSound; // CFL condition

            // Create a source wavelet based on frequency and amplitude
            int waveletLength = Math.Max(100, (int)(10.0 / (frequency * 1000.0 * dt)));
            double[] wavelet = new double[waveletLength];

            // Generate Ricker wavelet or similar pulse
            double centralFrequency = frequency * 1000.0; // Convert kHz to Hz
            for (int i = 0; i < waveletLength; i++)
            {
                double t = i * dt;
                double delay = 1.5 / centralFrequency;
                double arg = Math.PI * centralFrequency * (t - delay);
                double arg2 = arg * arg;
                wavelet[i] = amplitude * (1.0 - 2.0 * arg2) * Math.Exp(-arg2);
            }

            // Apply the source to the transmitter position
            gpuSimulator.ApplySource(wavelet, tx, ty, tz);
        }

        /// <summary>
        /// Check if the wave has reached the receiver position
        /// </summary>
        private bool CheckReceiver()
        {
            var (vx, vy, vz) = GetWaveFieldSnapshot();
            double threshold = 1e-6;
            return Math.Abs(vx[rx, ry, rz]) > threshold ||
                   Math.Abs(vy[rx, ry, rz]) > threshold ||
                   Math.Abs(vz[rx, ry, rz]) > threshold;
        }

        /// <summary>
        /// Calculate final results of the simulation
        /// </summary>
        private void CalculateResults()
        {
            // Calculate the distance between transmitter and receiver
            double dist = Math.Sqrt(
                Math.Pow(tx - rx, 2) + Math.Pow(ty - ry, 2) + Math.Pow(tz - rz, 2)) * pixelSize;

            // Calculate the velocity using the travel time
            float speedOfSound = (float)Math.Sqrt((lambda + 2 * mu) / GetAverageDensity());
            float dt = 0.5f * pixelSize / speedOfSound; // Should match what Step() uses

            // Use touchStep if receiver was touched, otherwise estimate based on material properties
            double travelTime;
            if (receiverTouched)
            {
                travelTime = touchStep * dt;
            }
            else
            {
                // Fallback if receiver was never touched (shouldn't happen with auto-stop)
                travelTime = dist / speedOfSound;
            }

            double vP = dist / travelTime;

            // Calculate S-wave velocity based on theory
            double vS = vP / Math.Sqrt(3.0); // Theoretical relation for Poisson solid
            double vpVsRatio = vP / vS;

            // Calculate S-wave travel time properly
            int pWaveTravelTime = touchStep > 0 ? touchStep : (int)(dist / (vP * dt));
            int sWaveTravelTime = touchStep > 0 ? (int)(touchStep * (vP / vS)) : (int)(dist / (vS * dt));

            // Notify of simulation completion
            SimulationCompleted?.Invoke(this,
                new AcousticSimulationCompleteEventArgs(
                    vP, vS, vpVsRatio, pWaveTravelTime, sWaveTravelTime, stepCount));
        }


        /// <summary>
        /// Get a snapshot of the current wave field - matches AcousticSimulator.GetWaveFieldSnapshot
        /// </summary>
        public (double[,,] vx, double[,,] vy, double[,,] vz) GetWaveFieldSnapshot()
        {
            // Get the 1D velocity arrays from the GPU
            double[] vxFlat = gpuSimulator.GetVelocityX();
            double[] vyFlat = gpuSimulator.GetVelocityY();
            double[] vzFlat = gpuSimulator.GetVelocityZ();

            // Convert from 1D to 3D arrays
            double[,,] vx = new double[width, height, depth];
            double[,,] vy = new double[width, height, depth];
            double[,,] vz = new double[width, height, depth];

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = x + y * width + z * width * height;
                        if (index < vxFlat.Length)
                        {
                            vx[x, y, z] = vxFlat[index];
                            vy[x, y, z] = vyFlat[index];
                            vz[x, y, z] = vzFlat[index];
                        }
                    }
                }
            }

            return (vx, vy, vz);
        }

        /// <summary>
        /// Convert a double array to a float array for visualization
        /// </summary>
        private float[,,] ConvertToFloat(double[,,] doubleArray)
        {
            float[,,] result = new float[width, height, depth];
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        result[x, y, z] = (float)doubleArray[x, y, z];
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Dispose of resources
        /// </summary>
        public void Dispose()
        {
            cts?.Dispose();
            gpuSimulator?.Dispose();
        }
    }
}
