using System;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui;
using ParallelComputingServer.Services;

namespace ParallelComputingServer.UI
{
    /// <summary>
    /// A panel to display the status of active dataset transfers
    /// </summary>
    public class TransfersPanel : Dialog
    {
        private readonly DatasetTransferService _transferService;
        private ListView _transfersListView;
        private List<TransferStatusInfo> _transfers = new List<TransferStatusInfo>();
        private Label _detailsLabel;
        private ProgressBar _progressBar;
        private Button _cleanupButton;

        public TransfersPanel(DatasetTransferService transferService)
            : base("Dataset Transfers", 80, 25)
        {
            _transferService = transferService;

            InitializeComponent();
            RefreshTransfersAsync();

            // Set up a timer to refresh transfers
            Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(2), (_) =>
            {
                RefreshTransfersAsync();
                return true;
            });
        }

        private void InitializeComponent()
        {
            // Create list view for transfers
            _transfersListView = new ListView
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(1),
                Height = Dim.Percent(60),
                AllowsMarking = false,
                CanFocus = true
            };
            Add(_transfersListView);

            // Add selection handler
            _transfersListView.SelectedItemChanged += OnTransferSelected;

            // Add details section
            var detailsFrame = new FrameView("Details")
            {
                X = 1,
                Y = Pos.Bottom(_transfersListView) + 1,
                Width = Dim.Fill(1),
                Height = Dim.Fill(3)
            };
            Add(detailsFrame);

            _detailsLabel = new Label("")
            {
                X = 1,
                Y = 0,
                Width = Dim.Fill(1),
                Height = Dim.Fill(2)
            };
            detailsFrame.Add(_detailsLabel);

            _progressBar = new ProgressBar
            {
                X = 1,
                Y = Pos.Bottom(_detailsLabel),
                Width = Dim.Fill(1),
                Height = 1,
                Fraction = 0
            };
            detailsFrame.Add(_progressBar);

            // Add buttons
            _cleanupButton = new Button("Cleanup Selected")
            {
                X = 1,
                Y = Pos.AnchorEnd(1),
                Enabled = false
            };
            _cleanupButton.Clicked += OnCleanupClicked;
            Add(_cleanupButton);

            var refreshButton = new Button("Refresh")
            {
                X = Pos.Right(_cleanupButton) + 2,
                Y = Pos.AnchorEnd(1)
            };
            refreshButton.Clicked += OnRefreshClicked;
            Add(refreshButton);

            var closeButton = new Button("Close")
            {
                X = Pos.AnchorEnd(10),
                Y = Pos.AnchorEnd(1)
            };
            closeButton.Clicked += () => Application.RequestStop();
            Add(closeButton);
        }

        private async void RefreshTransfersAsync()
        {
            try
            {
                // Get transfer statuses
                var transfersDict = _transferService.GetTransfersStatus();

                // Convert to list for display
                _transfers = transfersDict.Values.ToList();

                // Update the list view
                _transfersListView.SetSource(_transfers.Select(t => $"{t.TransferId.Substring(0, 8)}... | {t.Status} | {t.ProgressPercentage:F1}% | {t.ReceivedChunks}/{t.TotalChunks}").ToList());

                // Update selected item details if any
                if (_transfersListView.SelectedItem >= 0 && _transfersListView.SelectedItem < _transfers.Count)
                {
                    UpdateDetailsPanel(_transfers[_transfersListView.SelectedItem]);
                }
                else if (_transfers.Count > 0)
                {
                    // Select the first item if nothing is selected
                    _transfersListView.SelectedItem = 0;
                    UpdateDetailsPanel(_transfers[0]);
                }
                else
                {
                    // Clear details if no transfers
                    _detailsLabel.Text = "No active transfers";
                    _progressBar.Fraction = 0;
                    _cleanupButton.Enabled = false;
                }

                SetNeedsDisplay();
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error refreshing transfers: {ex.Message}");
            }
        }

        private void OnTransferSelected(ListViewItemEventArgs e)
        {
            if (e.Item >= 0 && e.Item < _transfers.Count)
            {
                var transfer = _transfers[e.Item];
                UpdateDetailsPanel(transfer);

                // Enable or disable cleanup button based on transfer status
                _cleanupButton.Enabled = transfer.Status == "Completed" || transfer.Status == "Failed";
            }
            else
            {
                _detailsLabel.Text = "No transfer selected";
                _progressBar.Fraction = 0;
                _cleanupButton.Enabled = false;
            }

            SetNeedsDisplay();
        }

        private void UpdateDetailsPanel(TransferStatusInfo transfer)
        {
            // Format details text
            _detailsLabel.Text = $"Transfer ID: {transfer.TransferId}\n" +
                                 $"Dataset ID: {transfer.DatasetId}\n" +
                                 $"Status: {transfer.Status}\n" +
                                 $"Progress: {transfer.ProgressPercentage:F1}% ({transfer.ReceivedChunks}/{transfer.TotalChunks} chunks)\n" +
                                 $"Started: {transfer.StartTime}\n" +
                                 $"Last Update: {transfer.LastUpdateTime}";

            // Update progress bar
            _progressBar.Fraction = transfer.ProgressPercentage / 100f;

            // Update button state
            _cleanupButton.Enabled = transfer.Status == "Completed" || transfer.Status == "Failed";
        }

        private void OnCleanupClicked()
        {
            if (_transfersListView.SelectedItem >= 0 && _transfersListView.SelectedItem < _transfers.Count)
            {
                var transfer = _transfers[_transfersListView.SelectedItem];

                // Confirm cleanup
                var result = MessageBox.Query("Confirm Cleanup",
                    $"Are you sure you want to clean up transfer {transfer.TransferId.Substring(0, 8)}...?", "Yes", "No");

                if (result == 0) // Yes
                {
                    try
                    {
                        // Cleanup the transfer
                        _transferService.CleanupTransfer(transfer.TransferId);

                        // Refresh the list
                        RefreshTransfersAsync();

                        TuiLogger.Log($"Transfer {transfer.TransferId} cleaned up successfully");
                    }
                    catch (Exception ex)
                    {
                        TuiLogger.Log($"Error cleaning up transfer: {ex.Message}");
                        MessageBox.ErrorQuery("Error", $"Error cleaning up transfer: {ex.Message}", "OK");
                    }
                }
            }
        }

        private void OnRefreshClicked()
        {
            RefreshTransfersAsync();
        }
    }
}