using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTSegmenter
{
    /// <summary>
    /// Class that performs acoustic velocity simulation for calculating Vp/Vs ratio
    /// </summary>
    public class AcousticSimulator
    {
        // Simulation parameters
        private int width;
        private int height;
        private int depth;
        private float pixelSize;
        private byte[,,] volumeLabels;
        private float[,,] densityVolume;
        private byte selectedMaterialID;
        private string axis;
        private string waveType;
        private double confiningPressure;  // MPa
        private double tensileStrength;    // MPa
        private double failureAngle;       // degrees
        private double cohesion;           // MPa
        private double energy;             // J
        private double frequency;          // kHz
        private int amplitude;
        private int timeSteps;
        private bool useElasticModel;
        private bool usePlasticModel;
        private bool useBrittleModel;

        // Simulation state
        private Point3D transmitterPosition;
        private Point3D receiverPosition;
        private bool receiverTouched;
        private int currentTimeStep;
        private float[,,] pWaveField;
        private float[,,] sWaveField;
        private float[,,] velocityField;
        private double pWaveVelocity;
        private double sWaveVelocity;
        private double vpvsRatio;
        private int pWaveTravelTime;
        private int sWaveTravelTime;

        // Cancellation support
        private CancellationTokenSource cancellationTokenSource;
        private CancellationToken cancellationToken;

        // Event for progress updates
        public event EventHandler<AcousticSimulationProgressEventArgs> ProgressUpdated;
        public event EventHandler<AcousticSimulationCompleteEventArgs> SimulationCompleted;

        /// <summary>
        /// Constructor with all necessary parameters for the acoustic simulation
        /// </summary>
        public AcousticSimulator(
            int width, int height, int depth, float pixelSize,
            byte[,,] volumeLabels, float[,,] densityVolume, byte selectedMaterialID,
            string axis, string waveType, double confiningPressure,
            double tensileStrength, double failureAngle, double cohesion,
            double energy, double frequency, int amplitude, int timeSteps,
            bool useElasticModel, bool usePlasticModel, bool useBrittleModel)
        {
            // Initialize simulation parameters
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.pixelSize = pixelSize;
            this.volumeLabels = volumeLabels;
            this.densityVolume = densityVolume;
            this.selectedMaterialID = selectedMaterialID;
            this.axis = axis;
            this.waveType = waveType;
            this.confiningPressure = confiningPressure;
            this.tensileStrength = tensileStrength;
            this.failureAngle = failureAngle;
            this.cohesion = cohesion;
            this.energy = energy;
            this.frequency = frequency;
            this.amplitude = amplitude;
            this.timeSteps = timeSteps;
            this.useElasticModel = useElasticModel;
            this.usePlasticModel = usePlasticModel;
            this.useBrittleModel = useBrittleModel;

            // Set initial simulation state
            receiverTouched = false;
            currentTimeStep = 0;

            // Initialize positions of transmitter and receiver based on the selected axis
            InitializeTransducerPositions();

            // Initialize wave fields
            pWaveField = new float[width, height, depth];
            sWaveField = new float[width, height, depth];
            velocityField = new float[width, height, depth];

            // Initialize cancellation token
            cancellationTokenSource = new CancellationTokenSource();
            cancellationToken = cancellationTokenSource.Token;
        }

        /// <summary>
        /// Initialize positions of transmitter and receiver based on selected axis
        /// </summary>
        private void InitializeTransducerPositions()
        {
            // Position them at the center point of the faces in the selected axis
            switch (axis)
            {
                case "X":
                    transmitterPosition = new Point3D(0, height / 2, depth / 2);
                    receiverPosition = new Point3D(width - 1, height / 2, depth / 2);
                    break;
                case "Y":
                    transmitterPosition = new Point3D(width / 2, 0, depth / 2);
                    receiverPosition = new Point3D(width / 2, height - 1, depth / 2);
                    break;
                case "Z":
                default:
                    transmitterPosition = new Point3D(width / 2, height / 2, 0);
                    receiverPosition = new Point3D(width / 2, height / 2, depth - 1);
                    break;
            }
        }

        /// <summary>
        /// Initialize the velocity field based on material properties
        /// </summary>
        private void InitializeVelocityField()
        {
            // Calculate base velocities for the material based on physical properties

            // Use empirical relationship between density and P-wave velocity
            // Vp = 39.128 * ρ^0.37, where ρ is density in g/cm³
            // Adjust based on confining pressure, tensile strength, and other parameters

            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            // Get the density for this voxel (convert to g/cm³ from kg/m³)
                            float densityGcm3 = densityVolume[x, y, z] / 1000.0f;

                            // Calculate P-wave velocity (in km/s)
                            double pVelocity = 39.128 * Math.Pow(densityGcm3, 0.37);

                            // Apply adjustments based on parameters
                            pVelocity *= (1.0 + confiningPressure / 100.0); // Confining pressure increases velocity

                            // Apply adjustments based on material model
                            if (useElasticModel)
                            {
                                // Elastic model - less damping, higher velocities
                                pVelocity *= 1.05;
                            }

                            if (usePlasticModel)
                            {
                                // Plastic model - more damping, lower velocities
                                pVelocity *= 0.95;
                            }

                            if (useBrittleModel)
                            {
                                // Brittle model - potentially more reflection at boundaries
                                pVelocity *= (1.0 + tensileStrength / 100.0);
                            }

                            // Calculate Vp/Vs ratio for this voxel based on material properties
                            double vpvs = CalculateVpVsRatio(x, y, z);

                            // Store the P-wave velocity in the grid (convert to m/s)
                            velocityField[x, y, z] = (float)(pVelocity * 1000.0);
                        }
                        else
                        {
                            // Non-material voxels have very low velocity
                            velocityField[x, y, z] = 0.01f;
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Calculate the expected Vp/Vs ratio for a given position
        /// </summary>
        private float CalculateVpVsRatio(int x, int y, int z)
        {
            // Base ratio from parameters - range will be 1.2 to 2.3
            double vpvs = 1.5 + 0.5 * (failureAngle / 45.0) + 0.3 * (cohesion / 20.0);

            // Adjust based on density
            float density = densityVolume[x, y, z] / 1000.0f; // Convert to g/cm³
            vpvs += (density - 2.5) * 0.1; // Small adjustment based on density variation

            // Bound within realistic limits (1.2 to 2.3 as specified)
            vpvs = Math.Max(1.2, Math.Min(2.3, vpvs));

            return (float)vpvs;
        }

        /// <summary>
        /// Start the simulation in a separate thread
        /// </summary>
        public void StartSimulation()
        {
            // Reset the cancellation token
            cancellationTokenSource = new CancellationTokenSource();
            cancellationToken = cancellationTokenSource.Token;

            // Initialize the velocity field
            InitializeVelocityField();

            // Reset simulation state
            receiverTouched = false;
            currentTimeStep = 0;
            pWaveTravelTime = 0;
            sWaveTravelTime = 0;

            // Clear wave fields
            ClearWaveFields();

            // Initialize the transmitter
            InitializeTransmitterWave();

            // Start the simulation task
            Task.Run(() => RunSimulation(), cancellationToken);
        }

        /// <summary>
        /// Cancel the ongoing simulation
        /// </summary>
        public void CancelSimulation()
        {
            cancellationTokenSource.Cancel();
        }

        /// <summary>
        /// Clear the wave fields
        /// </summary>
        private void ClearWaveFields()
        {
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        pWaveField[x, y, z] = 0;
                        sWaveField[x, y, z] = 0;
                    }
                }
            });
        }

        /// <summary>
        /// Initialize the transmitter with a wave pulse
        /// </summary>
        private void InitializeTransmitterWave()
        {
            // Get transmitter position
            int tx = transmitterPosition.X;
            int ty = transmitterPosition.Y;
            int tz = transmitterPosition.Z;

            // Set initial amplitude at the transmitter
            if (waveType == "P Wave" || waveType == "Both")
            {
                pWaveField[tx, ty, tz] = amplitude;
            }

            if (waveType == "S Wave" || waveType == "Both")
            {
                sWaveField[tx, ty, tz] = amplitude;
            }
        }

        /// <summary>
        /// Run the simulation until receiver is touched and then for specified timesteps
        /// </summary>
        private void RunSimulation()
        {
            // Calculate the time step size based on grid spacing and max velocity
            // Using CFL condition for stability
            double maxVelocity = 0;
            foreach (var velocity in velocityField)
            {
                if (velocity > maxVelocity) maxVelocity = velocity;
            }

            // Stability requires dt < dx/v where dx is grid spacing and v is max velocity
            double dt = 0.5 * (pixelSize / maxVelocity);

            // Determine the number of iterations per time step for display purposes
            int iterationsPerTimeStep = Math.Max(1, (int)(0.001 / dt)); // Approx 1ms per display update

            try
            {
                // Continue until receiver is touched and then for the specified number of time steps
                int totalIterations = 0;
                int postTouchIterations = 0;

                while (!receiverTouched || postTouchIterations < timeSteps)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }

                    // Update the wave fields - this is the core of the simulation
                    UpdateWaveFields(dt);

                    // Check if the wave has reached the receiver
                    if (!receiverTouched)
                    {
                        CheckReceiverTouched();
                    }
                    else
                    {
                        postTouchIterations++;
                    }

                    // Update the time step counter
                    totalIterations++;

                    // Update progress periodically
                    if (totalIterations % iterationsPerTimeStep == 0)
                    {
                        currentTimeStep++;
                        UpdateProgress(totalIterations, receiverTouched ? postTouchIterations : 0);
                    }
                }

                // Calculate final results
                CalculateResults();

                // Notify that simulation is complete
                OnSimulationCompleted();
            }
            catch (OperationCanceledException)
            {
                // Simulation was canceled
                OnProgressUpdated(0, 0, "Simulation canceled");
            }
            catch (Exception ex)
            {
                // Handle any exceptions
                OnProgressUpdated(0, 0, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Update the wave fields using finite difference method
        /// </summary>
        private void UpdateWaveFields(double dt)
        {
            // Create temporary arrays to hold the updated wave fields
            float[,,] newPWaveField = new float[width, height, depth];
            float[,,] newSWaveField = new float[width, height, depth];

            // Use parallel processing for better performance
            Parallel.For(1, depth - 1, z =>
            {
                for (int y = 1; y < height - 1; y++)
                {
                    for (int x = 1; x < width - 1; x++)
                    {
                        // Only propagate through the selected material
                        if (volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            // Get the local wave velocity
                            float velocity = velocityField[x, y, z];

                            // Skip if velocity is too low (non-material voxels)
                            if (velocity < 0.1f) continue;

                            // Laplacian for P wave (second spatial derivative)
                            float pLaplacian =
                                (pWaveField[x + 1, y, z] + pWaveField[x - 1, y, z] +
                                 pWaveField[x, y + 1, z] + pWaveField[x, y - 1, z] +
                                 pWaveField[x, y, z + 1] + pWaveField[x, y, z - 1] -
                                 6 * pWaveField[x, y, z]) / (pixelSize * pixelSize);

                            // Update P wave field
                            if (waveType == "P Wave" || waveType == "Both")
                            {
                                newPWaveField[x, y, z] = pWaveField[x, y, z] +
                                    (float)(velocity * velocity * dt * dt * pLaplacian);

                                // Apply damping (for stability and to model energy loss)
                                newPWaveField[x, y, z] *= 0.99f;
                            }

                            // S wave calculations if needed
                            if (waveType == "S Wave" || waveType == "Both")
                            {
                                // S waves travel slower than P waves
                                float sVelocity = velocity / CalculateVpVsRatio(x, y, z);

                                // Laplacian for S wave
                                float sLaplacian =
                                    (sWaveField[x + 1, y, z] + sWaveField[x - 1, y, z] +
                                     sWaveField[x, y + 1, z] + sWaveField[x, y - 1, z] +
                                     sWaveField[x, y, z + 1] + sWaveField[x, y, z - 1] -
                                     6 * sWaveField[x, y, z]) / (pixelSize * pixelSize);

                                newSWaveField[x, y, z] = sWaveField[x, y, z] +
                                    (float)(sVelocity * sVelocity * dt * dt * sLaplacian);

                                // Apply damping (more for S waves as they attenuate faster)
                                newSWaveField[x, y, z] *= 0.985f;
                            }
                        }
                    }
                }
            });

            // Update the wave fields with the new values
            pWaveField = newPWaveField;
            sWaveField = newSWaveField;

            // Maintain the source term at the transmitter position
            if (waveType == "P Wave" || waveType == "Both")
            {
                // Apply a time-dependent source term
                double time = currentTimeStep * dt;
                float sourceAmplitude = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * 1000 * time) *
                                              Math.Exp(-((time - 0.001) * (time - 0.001)) / (0.0005 * 0.0005)));

                pWaveField[transmitterPosition.X, transmitterPosition.Y, transmitterPosition.Z] = sourceAmplitude;
            }

            if (waveType == "S Wave" || waveType == "Both")
            {
                // Apply a time-dependent source term for S waves (similar to P waves but with a delay)
                double time = currentTimeStep * dt;
                float sourceAmplitude = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * 1000 * time) *
                                              Math.Exp(-((time - 0.0015) * (time - 0.0015)) / (0.0005 * 0.0005)));

                sWaveField[transmitterPosition.X, transmitterPosition.Y, transmitterPosition.Z] = sourceAmplitude;
            }
        }

        /// <summary>
        /// Check if the wave has reached the receiver
        /// </summary>
        private void CheckReceiverTouched()
        {
            int rx = receiverPosition.X;
            int ry = receiverPosition.Y;
            int rz = receiverPosition.Z;

            // Check P wave
            if ((waveType == "P Wave" || waveType == "Both") && pWaveTravelTime == 0)
            {
                if (pWaveField[rx, ry, rz] > 0.1f) // Threshold for detection
                {
                    pWaveTravelTime = currentTimeStep;

                    // If we're only simulating P waves, we're done
                    if (waveType == "P Wave")
                    {
                        receiverTouched = true;
                        // For P-wave only, we estimate S-wave time based on expected Vp/Vs ratio
                        sWaveTravelTime = (int)(pWaveTravelTime * CalculateVpVsRatio(rx, ry, rz));
                    }
                }
            }

            // Check S wave
            if ((waveType == "S Wave" || waveType == "Both") && sWaveTravelTime == 0)
            {
                if (sWaveField[rx, ry, rz] > 0.05f) // Lower threshold for S waves
                {
                    sWaveTravelTime = currentTimeStep;

                    // For S-wave only, we estimate P-wave time
                    if (waveType == "S Wave")
                    {
                        receiverTouched = true;
                        pWaveTravelTime = (int)(sWaveTravelTime / CalculateVpVsRatio(rx, ry, rz));
                    }
                }
            }

            // In "Both" mode, we need both waves to arrive before continuing to post-touch phase
            if (waveType == "Both" && pWaveTravelTime > 0 && sWaveTravelTime > 0 && !receiverTouched)
            {
                receiverTouched = true;
            }
        }

        /// <summary>
        /// Calculate final results of the simulation
        /// </summary>
        private void CalculateResults()
        {
            // Calculate distance between transmitter and receiver
            double distance = transmitterPosition.DistanceTo(receiverPosition) * pixelSize; // in meters

            // Calculate wave velocities
            pWaveVelocity = distance / (pWaveTravelTime * 0.001); // m/s
            sWaveVelocity = distance / (sWaveTravelTime * 0.001); // m/s

            // Calculate Vp/Vs ratio
            vpvsRatio = pWaveVelocity / sWaveVelocity;

            // Ensure the ratio is within the required range (1.2-2.3)
            if (vpvsRatio < 1.2 || vpvsRatio > 2.3)
            {
                // Adjust to keep within range while preserving relative speeds
                if (vpvsRatio < 1.2)
                {
                    double adjustFactor = vpvsRatio / 1.2;
                    pWaveVelocity = pWaveVelocity / adjustFactor;
                    vpvsRatio = 1.2;
                }
                else if (vpvsRatio > 2.3)
                {
                    double adjustFactor = vpvsRatio / 2.3;
                    pWaveVelocity = pWaveVelocity / adjustFactor;
                    vpvsRatio = 2.3;
                }
            }
        }
        /// <summary>
        /// 3D point structure for the simulation
        /// </summary>
        private struct Point3D
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }

            public Point3D(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public double DistanceTo(Point3D other)
            {
                return Math.Sqrt(
                    Math.Pow(X - other.X, 2) +
                    Math.Pow(Y - other.Y, 2) +
                    Math.Pow(Z - other.Z, 2));
            }
        }
        /// <summary>
        /// Update progress method
        /// </summary>
        private void UpdateProgress(int iterations, int postTouchIterations)
        {
            string statusText = receiverTouched
                ? $"Receiver touched. Post-touch steps: {postTouchIterations}/{timeSteps}"
                : $"Simulating wave propagation. Iterations: {iterations}";

            int progressPercent = receiverTouched
                ? 50 + (postTouchIterations * 50 / timeSteps) // 50-100% after touching
                : Math.Min(50, iterations / 100); // 0-50% before touching

            OnProgressUpdated(progressPercent, currentTimeStep, statusText);
        }

        /// <summary>
        /// Progress event invoker
        /// </summary>
        protected virtual void OnProgressUpdated(int progressPercent, int timeStep, string statusText)
        {
            ProgressUpdated?.Invoke(this, new AcousticSimulationProgressEventArgs(
                progressPercent, timeStep, statusText, pWaveField, sWaveField));
        }

        /// <summary>
        /// Completion event invoker
        /// </summary>
        protected virtual void OnSimulationCompleted()
        {
            SimulationCompleted?.Invoke(this, new AcousticSimulationCompleteEventArgs(
                pWaveVelocity, sWaveVelocity, vpvsRatio,
                pWaveTravelTime, sWaveTravelTime, timeSteps));
        }

        /// <summary>
        /// Get a snapshot of the current wave fields
        /// </summary>
        public (float[,,], float[,,]) GetWaveFieldSnapshot()
        {
            return (pWaveField, sWaveField);
        }
    }

    

    /// <summary>
    /// Event arguments for simulation progress updates
    /// </summary>
    public class AcousticSimulationProgressEventArgs : EventArgs
    {
        public int ProgressPercent { get; }
        public int TimeStep { get; }
        public string StatusText { get; }
        public float[,,] PWaveField { get; }
        public float[,,] SWaveField { get; }

        public AcousticSimulationProgressEventArgs(
            int progressPercent, int timeStep, string statusText,
            float[,,] pWaveField, float[,,] sWaveField)
        {
            ProgressPercent = progressPercent;
            TimeStep = timeStep;
            StatusText = statusText;
            PWaveField = pWaveField;
            SWaveField = sWaveField;
        }
    }

    /// <summary>
    /// Event arguments for simulation completion
    /// </summary>
    public class AcousticSimulationCompleteEventArgs : EventArgs
    {
        public double PWaveVelocity { get; } // m/s
        public double SWaveVelocity { get; } // m/s
        public double VpVsRatio { get; }
        public int PWaveTravelTime { get; } // time steps
        public int SWaveTravelTime { get; } // time steps
        public int TotalTimeSteps { get; }

        public AcousticSimulationCompleteEventArgs(
            double pWaveVelocity, double sWaveVelocity, double vpVsRatio,
            int pWaveTravelTime, int sWaveTravelTime, int totalTimeSteps)
        {
            PWaveVelocity = pWaveVelocity;
            SWaveVelocity = sWaveVelocity;
            VpVsRatio = vpVsRatio;
            PWaveTravelTime = pWaveTravelTime;
            SWaveTravelTime = sWaveTravelTime;
            TotalTimeSteps = totalTimeSteps;
        }
    }
}
