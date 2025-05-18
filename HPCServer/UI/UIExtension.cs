using System;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;
using ParallelComputingServer.Config;
using ParallelComputingServer.Services;
using ParallelComputingServer.UI;

namespace ParallelComputingServer.UI
{
    public partial class UIManager
    {
        private DatasetTransferService _datasetTransferService;

        // Add dataset transfer service
        public void AddDatasetTransferService(DatasetTransferService datasetTransferService)
        {
            _datasetTransferService = datasetTransferService;
        }

        // Extend the BuildUI method to include transfer status
        private void AddTransferStatusToUI(Window mainWindow)
        {
            if (_datasetTransferService == null)
                return;

            // Add a menu option for transfers
            var menuItems = new StatusItem[] {
                 new StatusItem(Key.F1, "~F1~ Help", ShowHelp),
                new StatusItem(Key.F2, "~F2~ Settings", ShowSettings),
                new StatusItem(Key.F3, "~F3~ Clients", ShowClients),
                new StatusItem(Key.F4, "~F4~ Endpoints", ShowEndpoints),
                new StatusItem(Key.F5, "~F5~ About", ShowAbout),
                new StatusItem(Key.F6, "~F6~ Logs", ShowLogs),
                new StatusItem(Key.F7, "~F7~ Transfers", ShowTransfers),
                new StatusItem(Key.F10, "~F10~ Quit", () => Application.RequestStop())
            };

            // Update the status bar
            var statusBar = new StatusBar(menuItems);
            Application.Top.Add(statusBar);

            // Add a transfer status indicator to the main window
            var transferIndicator = new Label("Transfers: 0")
            {
                X = Pos.AnchorEnd(15),
                Y = 0,
            };
            mainWindow.Add(transferIndicator);

            // Create a timer to update the transfer count
            Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(3), (_) =>
            {
                if (_datasetTransferService != null)
                {
                    var transfers = _datasetTransferService.GetTransfersStatus();
                    transferIndicator.Text = $"Transfers: {transfers.Count}";
                    transferIndicator.SetNeedsDisplay();
                }
                return true;
            });

            // Also add a server status line 
            if (_networkService != null)
            {
                var serverStatusLine = new Label()
                {
                    X = 0,
                    Y = Pos.Top(mainWindow) + 1,
                    Width = Dim.Fill(),
                    Text = $"Server running on port {_config.ServerPort} | Beacon on port {_config.BeaconPort} | Endpoints on port {_config.EndpointPort}"
                };
                mainWindow.Add(serverStatusLine);
            }
        }

        // Add this method to show the transfers panel
        private void ShowTransfers()
        {
            if (_datasetTransferService == null)
            {
                MessageBox.ErrorQuery("Error", "Dataset transfer service not initialized.", "OK");
                return;
            }

            var transfersPanel = new TransfersPanel(_datasetTransferService);
            Application.Run(transfersPanel);
        }
    }
}