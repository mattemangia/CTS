using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ParallelComputingEndpoint
{
    /// <summary>
    /// Classe base per tutti gli handler di nodi
    /// </summary>
    public abstract class BaseNodeHandler : INodeHandler
    {
        public abstract Task<Dictionary<string, string>> ProcessAsync(
            Dictionary<string, string> inputData,
            Dictionary<string, byte[]> binaryData,
            EndpointComputeService computeService);

        // Helper method to log node processing
        protected void LogProcessing(string nodeType)
        {
            Console.WriteLine($"Processing {nodeType}...");
        }
    }
 


        /// <summary>
        /// Interfaccia per handler di nodi che supportano il tracciamento del progresso
        /// </summary>
        public interface IProgressTrackable
        {
            void SetProgressCallback(Action<int> progressCallback);
        }
    }
