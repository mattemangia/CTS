using System;
using System.Data;
using Terminal.Gui;

namespace ParallelComputingEndpoint
{
    public class StatusPanel : FrameView
    {
        private readonly EndpointConfig _config;
        private readonly EndpointNetworkService _networkService;
        private readonly EndpointComputeService _computeService;
        private readonly TableView _tableView;
        private readonly DataTable _dataTable;
        private readonly Label _cpuLoadLabel;
        private readonly ProgressBar _cpuLoadBar;

        public StatusPanel(
            EndpointConfig config,
            EndpointNetworkService networkService,
            EndpointComputeService computeService) : base("Status")
        {
            _config = config;
            _networkService = networkService;
            _computeService = computeService;

            // Create labels for hardware info
            var hardwareLabel = new Label("Hardware:")
            {
                X = 0,
                Y = 0
            };
            Add(hardwareLabel);

            var hardwareInfoLabel = new Label(_computeService.HardwareInfo)
            {
                X = 10,
                Y = 0,
                Width = Dim.Fill() - 10
            };
            Add(hardwareInfoLabel);

            // Create CPU load indicator
            var cpuLoadTextLabel = new Label("CPU Load:")
            {
                X = 0,
                Y = 2
            };
            Add(cpuLoadTextLabel);

            _cpuLoadLabel = new Label("0.0%")
            {
                X = 10,
                Y = 2,
                Width = 10
            };
            Add(_cpuLoadLabel);

            _cpuLoadBar = new ProgressBar()
            {
                X = 20,
                Y = 2,
                Width = Dim.Fill() - 20,
                Height = 1,
                Fraction = 0
            };
            Add(_cpuLoadBar);

            // Create the table view for general info
            _tableView = new TableView
            {
                X = 0,
                Y = 4,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 4,
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

            // Subscribe to CPU load updates
            _computeService.CpuLoadUpdated += OnCpuLoadUpdated;

            // Create a timer to update the UI periodically
            Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(2), (_) =>
            {
                UpdateDisplay();
                return true; // Return true to keep the timer running
            });
        }

        private void PopulateDataTable()
        {
            _dataTable.Rows.Add("Status", _networkService.IsConnected ? "Connected" : "Disconnected");
            _dataTable.Rows.Add("Endpoint Name", _config.EndpointName);
            _dataTable.Rows.Add("Server IP", _config.ServerIP);
            _dataTable.Rows.Add("Server Port", _config.ServerPort.ToString());
            _dataTable.Rows.Add("Auto Connect", _config.AutoConnect ? "Enabled" : "Disabled");
            _dataTable.Rows.Add("GPU Accelerator", _computeService.GpuAvailable ? "Available" : "Not Available");
            _dataTable.Rows.Add("Accelerator Name", _computeService.AcceleratorName);
        }

        public void UpdateDisplay()
        {
            if (_dataTable.Rows.Count > 0)
            {
                _dataTable.Rows[0]["Value"] = _networkService.IsConnected ? "Connected" : "Disconnected";
                _dataTable.Rows[1]["Value"] = _config.EndpointName;
                _dataTable.Rows[2]["Value"] = _config.ServerIP;
                _dataTable.Rows[3]["Value"] = _config.ServerPort.ToString();
                _dataTable.Rows[4]["Value"] = _config.AutoConnect ? "Enabled" : "Disabled";
                _tableView.SetNeedsDisplay();
            }
        }

        private void OnCpuLoadUpdated(object sender, double cpuLoad)
        {
            Application.MainLoop.Invoke(() =>
            {
                _cpuLoadLabel.Text = $"{cpuLoad:F1}%";
                _cpuLoadBar.Fraction = (float)(cpuLoad / 100.0);

                _cpuLoadLabel.SetNeedsDisplay();
                _cpuLoadBar.SetNeedsDisplay();
            });
        }
    }
}