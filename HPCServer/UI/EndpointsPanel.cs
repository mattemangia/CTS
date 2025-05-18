using System;
using System.Collections.Generic;
using Terminal.Gui;
using ParallelComputingServer.Services;
using ParallelComputingServer.Models;

namespace ParallelComputingServer.UI
{
    public class EndpointsPanel : Dialog
    {
        private readonly EndpointService _endpointService;
        private readonly ListView _listView;
        private readonly Label _detailsLabel;
        private List<EndpointInfo> _endpoints;

        public EndpointsPanel(EndpointService endpointService)
            : base("Connected Endpoints", 80, 30) // Increased height and width
        {
            _endpointService = endpointService;

            // Create heading with fixed position
            Add(new Label(1, 1, "List of compute endpoints connected to the server:"));

            // Create list view for endpoints
            _listView = new ListView()
            {
                X = 1,
                Y = 3,
                Width = Dim.Fill() - 2,
                Height = 12 // Fixed height for list view
            };

            // Create details frame
            var detailsFrame = new FrameView("Endpoint Details")
            {
                X = 1,
                Y = 16, // Fixed position
                Width = Dim.Fill() - 2,
                Height = 8 // Fixed height for details
            };

            _detailsLabel = new Label("")
            {
                X = 1,
                Y = 0,
                Width = Dim.Fill() - 2,
                Height = 7 // Fixed height
            };

            detailsFrame.Add(_detailsLabel);

            // Set selected item handler
            _listView.SelectedItemChanged += OnEndpointSelected;

            // Create a button bar with fixed positions
            var buttonBar = new View()
            {
                X = 0,
                Y = 25, // Fixed position for buttons
                Width = Dim.Fill(),
                Height = 1
            };

            var refreshButton = new Button(20, 0, "Refresh");
            refreshButton.Clicked += () => UpdateEndpointList();
            buttonBar.Add(refreshButton);

            var closeButton = new Button(45, 0, "Close");
            closeButton.Clicked += () => Application.RequestStop();
            buttonBar.Add(closeButton);

            Add(_listView, detailsFrame, buttonBar);

            // Update the list
            UpdateEndpointList();

            // Register for updates
            _endpointService.EndpointsUpdated += (sender, args) => UpdateEndpointList();
        }

        private void UpdateEndpointList()
        {
            _endpoints = _endpointService.GetConnectedEndpoints();
            var endpointList = new List<string>();

            if (_endpoints.Count == 0)
            {
                endpointList.Add("No endpoints connected");
                _detailsLabel.Text = "";
            }
            else
            {
                endpointList.Add($"{"Name",-20} {"IP Address",-15} {"Status",-12} {"GPU",-5} {"CPU Load",-10}");
                foreach (var endpoint in _endpoints)
                {
                    endpointList.Add($"{endpoint.Name,-20} {endpoint.EndpointIP,-15} {endpoint.Status,-12} {(endpoint.GpuEnabled ? "Yes" : "No"),-5} {endpoint.CpuLoadPercent,10:F1}%");
                }

                // If there's a selection, refresh details
                if (_listView.SelectedItem > 0 && _listView.SelectedItem <= _endpoints.Count)
                {
                    ShowEndpointDetails(_endpoints[_listView.SelectedItem - 1]);
                }
                else if (_endpoints.Count > 0)
                {
                    // Select the first endpoint if none is selected
                    _listView.SelectedItem = 1;
                    ShowEndpointDetails(_endpoints[0]);
                }
            }

            _listView.SetSource(endpointList);
            _listView.SetNeedsDisplay();
        }

        private void OnEndpointSelected(ListViewItemEventArgs args)
        {
            if (_endpoints.Count == 0 || args.Item <= 0 || args.Item > _endpoints.Count)
            {
                _detailsLabel.Text = "";
                return;
            }

            ShowEndpointDetails(_endpoints[args.Item - 1]);
        }

        private void ShowEndpointDetails(EndpointInfo endpoint)
        {
            var details = new System.Text.StringBuilder();
            details.AppendLine($"Name: {endpoint.Name}");
            details.AppendLine($"IP Address: {endpoint.EndpointIP}");
            details.AppendLine($"Port: {endpoint.EndpointPort}");
            details.AppendLine($"Connected Since: {endpoint.ConnectedAt}");
            details.AppendLine($"Status: {endpoint.Status}");
            details.AppendLine($"GPU Enabled: {(endpoint.GpuEnabled ? "Yes" : "No")}");
            details.AppendLine($"CPU Load: {endpoint.CpuLoadPercent:F1}%");
            details.AppendLine($"Current Task: {endpoint.CurrentTask}");

            if (!string.IsNullOrEmpty(endpoint.HardwareInfo))
            {
                details.AppendLine($"Hardware Info: {endpoint.HardwareInfo}");
            }

            _detailsLabel.Text = details.ToString();
            _detailsLabel.SetNeedsDisplay();
        }
    }
}