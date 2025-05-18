using System;
using System.Data;
using Terminal.Gui;
using ParallelComputingServer.Config;
using ParallelComputingServer.Services;

namespace ParallelComputingServer.UI
{
    public class StatusPanel : FrameView
    {
        private readonly ServerConfig _config;
        private readonly NetworkService _networkService;
        private readonly ComputeService _computeService;
        private readonly EndpointService _endpointService;
        private readonly TableView _tableView;
        private readonly DataTable _dataTable;

        public StatusPanel(
            ServerConfig config,
            NetworkService networkService,
            ComputeService computeService,
            EndpointService endpointService) : base("Status")
        {
            _config = config;
            _networkService = networkService;
            _computeService = computeService;
            _endpointService = endpointService;

            // Create the table view
            _tableView = new TableView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                FullRowSelect = true
            };

            // Create the data table
            _dataTable = new DataTable();
            _dataTable.Columns.Add("Property", typeof(string));
            _dataTable.Columns.Add("Value", typeof(string));

            // Populate the data table
            PopulateDataTable();

            // Set the table to the view
            _tableView.Table = _dataTable;

            // Add the table view to this frame
            Add(_tableView);

            // Register for service events
            _networkService.ClientsUpdated += (sender, args) => UpdateClientsCount();
            _endpointService.EndpointsUpdated += (sender, args) => UpdateEndpointsCount();

            // Create a timer to update the UI periodically
            Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(5), (_) =>
            {
                // Refresh the status display periodically
                UpdateClientsCount();
                UpdateEndpointsCount();
                return true; // Return true to keep the timer running
            });
        }

        private void PopulateDataTable()
        {
            _dataTable.Rows.Add("Server Status", "Running");
            _dataTable.Rows.Add("Server IP", NetworkService.GetLocalIPAddress());
            _dataTable.Rows.Add("Server Port", _config.ServerPort.ToString());
            _dataTable.Rows.Add("Beacon Port", _config.BeaconPort.ToString());
            _dataTable.Rows.Add("Endpoint Port", _config.EndpointPort.ToString());
            _dataTable.Rows.Add("Beacon Interval", $"{_config.BeaconIntervalMs} ms");
            _dataTable.Rows.Add("Connected Clients", "0");
            _dataTable.Rows.Add("Connected Endpoints", "0");
            _dataTable.Rows.Add("GPU Accelerator", _computeService.AcceleratorName);
        }

        private void UpdateClientsCount()
        {
            if (_dataTable.Rows.Count > 6)
            {
                _dataTable.Rows[6]["Value"] = _networkService.GetConnectedClients().Count.ToString();
                _tableView.SetNeedsDisplay();
            }
        }

        private void UpdateEndpointsCount()
        {
            if (_dataTable.Rows.Count > 7)
            {
                _dataTable.Rows[7]["Value"] = _endpointService.GetConnectedEndpoints().Count.ToString();
                _tableView.SetNeedsDisplay();
            }
        }
    }
}