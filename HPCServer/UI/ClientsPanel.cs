using System;
using System.Collections.Generic;
using Terminal.Gui;
using ParallelComputingServer.Services;
using ParallelComputingServer.Models;

namespace ParallelComputingServer.UI
{
    public class ClientsPanel : Dialog
    {
        private readonly NetworkService _networkService;
        private readonly ListView _listView;

        public ClientsPanel(NetworkService networkService)
            : base("Connected Clients", 70, 25) // Increased height
        {
            _networkService = networkService;

            // Create a heading with fixed position
            Add(new Label(1, 1, "List of clients connected to the server:"));

            _listView = new ListView()
            {
                X = 1,
                Y = 3,
                Width = Dim.Fill() - 2,
                Height = 15 // Fixed height
            };

            UpdateClientList();

            // Register for updates
            _networkService.ClientsUpdated += (sender, args) => UpdateClientList();

            // Create a button bar with fixed positions
            var buttonBar = new View()
            {
                X = 0,
                Y = 20, // Fixed position for buttons
                Width = Dim.Fill(),
                Height = 1
            };
            Add(buttonBar);

            var refreshButton = new Button(15, 0, "Refresh");
            refreshButton.Clicked += () => UpdateClientList();
            buttonBar.Add(refreshButton);

            var closeButton = new Button(40, 0, "Close");
            closeButton.Clicked += () => Application.RequestStop();
            buttonBar.Add(closeButton);

            Add(_listView);
        }

        private void UpdateClientList()
        {
            var clients = _networkService.GetConnectedClients();
            var clientList = new List<string>();

            if (clients.Count == 0)
            {
                clientList.Add("No clients connected");
            }
            else
            {
                clientList.Add($"{"IP Address",-15} {"Port",-8} {"Connected Since",-20} {"Status",-10}");
                foreach (var client in clients)
                {
                    clientList.Add($"{client.ClientIP,-15} {client.ClientPort,-8} {client.ConnectedAt,-20} {client.Status,-10}");
                }
            }

            _listView.SetSource(clientList);
            _listView.SetNeedsDisplay();
        }
    }
}