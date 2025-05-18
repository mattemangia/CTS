using System;
using System.Text.Json;
using System.Threading.Tasks;
using ParallelComputingServer.Models;
using ParallelComputingServer.UI;

namespace ParallelComputingServer.Services
{
    /// <summary>
    /// Extension for NetworkService to handle dataset transfer commands.
    /// Add this code to the appropriate part of your NetworkService where message processing occurs.
    /// </summary>
    public static class NetworkServiceExtension
    {
        /// <summary>
        /// Handles dataset transfer commands within the ProcessClientMessageAsync method
        /// </summary>
        public static async Task<string> HandleDatasetTransferMessage(
            JsonElement commandObj,
            DatasetTransferCommands transferCommands,
            DatasetTransferService transferService)
        {
            try
            {
                // Check if this is a dataset transfer command
                if (commandObj.TryGetProperty("Type", out JsonElement typeElement) &&
                    typeElement.GetString() == "DATASET_TRANSFER")
                {
                    TuiLogger.Log("Received dataset transfer command");
                    return await transferCommands.ProcessDatasetCommandAsync(commandObj);
                }

                return null; // Not a dataset transfer command
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error handling dataset transfer message: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error handling dataset transfer message: {ex.Message}"
                });
            }
        }
    }

    /// <summary>
    /// Example integration into the NetworkService.ProcessClientMessageAsync method
    /// Include this code in your existing NetworkService class
    /// </summary>
    public partial class NetworkService
    {
        // These field declarations are removed to avoid duplication with NetworkService.cs
        // The fields are already declared in the main NetworkService.cs file

        // This method can be called during service initialization
        public void InitializeDatasetTransfer(DatasetTransferService datasetTransferService)
        {
            _datasetTransferService = datasetTransferService;
            _datasetTransferCommands = new DatasetTransferCommands(datasetTransferService);

            TuiLogger.Log("Dataset transfer command handling initialized");
        }

        // Example modification to ProcessClientMessageAsync
        private async Task<string> ProcessClientMessageAsyncWithDatasetTransfer(string message, ClientInfo clientInfo)
        {
            try
            {
                // Parse the JSON message
                var commandObj = JsonSerializer.Deserialize<JsonElement>(message);

                // Check if this is a dataset transfer command
                if (commandObj.TryGetProperty("Type", out JsonElement typeElement) &&
                    typeElement.GetString() == "DATASET_TRANSFER")
                {
                    // Handle dataset transfer message
                    return await NetworkServiceExtension.HandleDatasetTransferMessage(
                        commandObj, _datasetTransferCommands, _datasetTransferService);
                }

                // Otherwise, handle other command types as before
                if (commandObj.TryGetProperty("Command", out JsonElement commandElement))
                {
                    string command = commandElement.GetString();

                    switch (command)
                    {
                        case "PING":
                            return "{\"Status\":\"OK\",\"Message\":\"Pong\"}";

                        // Handle other command types...

                        default:
                            return "{\"Status\":\"Error\",\"Message\":\"Unknown command\"}";
                    }
                }

                return "{\"Status\":\"Error\",\"Message\":\"Invalid command format\"}";
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error processing message: {ex.Message}");
                return $"{{\"Status\":\"Error\",\"Message\":\"{ex.Message}\"}}";
            }
        }
    }
}