using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace CTSegmenter
{
    /// <summary>
    /// Interface defining common properties and methods for stress simulations
    /// </summary>
    public interface IStressSimulation
    {
        /// <summary>
        /// Unique identifier for this simulation
        /// </summary>
        Guid SimulationId { get; }

        /// <summary>
        /// Name of the simulation
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Time when simulation was created
        /// </summary>
        DateTime CreationTime { get; }

        /// <summary>
        /// Simulation status
        /// </summary>
        SimulationStatus Status { get; }

        /// <summary>
        /// Material being simulated
        /// </summary>
        Material Material { get; }

        /// <summary>
        /// Input mesh triangles
        /// </summary>
        IReadOnlyList<Triangle> MeshTriangles { get; }

        /// <summary>
        /// Progress percentage (0-100)
        /// </summary>
        float Progress { get; }

        /// <summary>
        /// Event raised when progress changes
        /// </summary>
        event EventHandler<SimulationProgressEventArgs> ProgressChanged;

        /// <summary>
        /// Event raised when simulation is completed
        /// </summary>
        event EventHandler<SimulationCompletedEventArgs> SimulationCompleted;

        /// <summary>
        /// Check if simulation parameters are valid
        /// </summary>
        /// <returns>True if parameters are valid, false otherwise</returns>
        bool ValidateParameters();

        /// <summary>
        /// Initialize the simulation with the specified parameters
        /// </summary>
        /// <returns>True if initialization was successful, false otherwise</returns>
        bool Initialize();

        /// <summary>
        /// Run the simulation
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token to cancel simulation</param>
        /// <returns>Task representing the asynchronous operation</returns>
        Task<SimulationResult> RunAsync(System.Threading.CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancel the simulation
        /// </summary>
        void Cancel();

        /// <summary>
        /// Render simulation results to the specified graphics context
        /// </summary>
        /// <param name="g">Graphics context</param>
        /// <param name="width">Width of the rendering area</param>
        /// <param name="height">Height of the rendering area</param>
        /// <param name="renderMode">Rendering mode</param>
        void RenderResults(Graphics g, int width, int height, RenderMode renderMode = RenderMode.Stress);

        /// <summary>
        /// Export results to the specified file path
        /// </summary>
        /// <param name="filePath">Path to the output file</param>
        /// <param name="format">Export format</param>
        /// <returns>True if export was successful, false otherwise</returns>
        bool ExportResults(string filePath, ExportFormat format);
    }

    /// <summary>
    /// Simulation status
    /// </summary>
    public enum SimulationStatus
    {
        NotInitialized,
        Initializing,
        Ready,
        Running,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Rendering modes for simulation results
    /// </summary>
    public enum RenderMode
    {
        Wireframe,
        Solid,
        Stress,
        Strain,
        FailureProbability,
        Displacement
    }

    /// <summary>
    /// Export formats for simulation results
    /// </summary>
    public enum ExportFormat
    {
        CSV,
        JSON,
        VTK,
        OBJ,
        STL,
        PNG,
        PDF
    }

    /// <summary>
    /// Event arguments for simulation progress
    /// </summary>
    public class SimulationProgressEventArgs : EventArgs
    {
        public float Progress { get; }
        public string StatusMessage { get; }

        public SimulationProgressEventArgs(float progress, string statusMessage)
        {
            Progress = progress;
            StatusMessage = statusMessage;
        }
    }

    /// <summary>
    /// Event arguments for simulation completion
    /// </summary>
    public class SimulationCompletedEventArgs : EventArgs
    {
        public bool Success { get; }
        public string Message { get; }
        public SimulationResult Result { get; }
        public Exception Error { get; }

        public SimulationCompletedEventArgs(bool success, string message, SimulationResult result = null, Exception error = null)
        {
            Success = success;
            Message = message;
            Result = result;
            Error = error;
        }
    }

    /// <summary>
    /// Represents the result of a simulation
    /// </summary>
    public class SimulationResult
    {
        /// <summary>
        /// Unique identifier for this result
        /// </summary>
        public Guid ResultId { get; }

        /// <summary>
        /// Related simulation ID
        /// </summary>
        public Guid SimulationId { get; }

        /// <summary>
        /// Time when result was created
        /// </summary>
        public DateTime CreationTime { get; }

        /// <summary>
        /// Whether the simulation completed successfully
        /// </summary>
        public bool IsSuccessful { get; }

        /// <summary>
        /// Error message if simulation failed
        /// </summary>
        public string ErrorMessage { get; }

        /// <summary>
        /// Summary of the simulation result
        /// </summary>
        public string Summary { get; }

        /// <summary>
        /// Additional data for the simulation result
        /// </summary>
        public Dictionary<string, object> Data { get; }

        public SimulationResult(Guid simulationId, bool isSuccessful, string summary, string errorMessage = null)
        {
            ResultId = Guid.NewGuid();
            SimulationId = simulationId;
            CreationTime = DateTime.Now;
            IsSuccessful = isSuccessful;
            ErrorMessage = errorMessage;
            Summary = summary;
            Data = new Dictionary<string, object>();
        }
    }
}