using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using Krypton.Toolkit;

namespace CTS.NodeEditor
{
    /// <summary>
    /// Dialog to show dataset transfer progress and allow cancellation
    /// </summary>
    public class TransferProgressDialog : KryptonForm
    {
        private KryptonLabel _statusLabel;
        private KryptonProgressBar _progressBar;
        private KryptonButton _cancelButton;
        private KryptonLabel _detailsLabel;
        private KryptonLabel _timeElapsedLabel;
        private KryptonLabel _timeRemainingLabel;
        private KryptonLabel _transferRateLabel;

        private readonly DateTime _startTime;
        private System.Windows.Forms.Timer _updateTimer;
        private CancellationTokenSource _cancellationTokenSource;

        // Transfer state
        private int _chunksTransferred = 0;
        private int _totalChunks = 0;
        private long _bytesTransferred = 0;
        private string _currentStatus = "Initializing...";
        private float _progressPercentage = 0;

        // Public properties
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        public TransferProgressDialog(string title, int totalChunks)
        {
            this.Text = title;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Size = new Size(450, 300);
            this.ShowInTaskbar = false;

            _totalChunks = totalChunks;
            _startTime = DateTime.Now;
            _cancellationTokenSource = new CancellationTokenSource();

            InitializeComponents();

            // Start update timer
            _updateTimer = new System.Windows.Forms.Timer();
            _updateTimer.Interval = 500; // Update every 500ms
            _updateTimer.Tick += UpdateTimerTick;
            _updateTimer.Start();
        }

        private void InitializeComponents()
        {
            var panel = new KryptonPanel();
            panel.Dock = DockStyle.Fill;
            panel.StateCommon.Color1 = Color.FromArgb(240, 240, 240);

            // Status label
            _statusLabel = new KryptonLabel();
            _statusLabel.Text = "Initializing transfer...";
            _statusLabel.Location = new Point(20, 20);
            _statusLabel.AutoSize = true;
            _statusLabel.StateCommon.ShortText.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            panel.Controls.Add(_statusLabel);

            // Progress bar
            _progressBar = new KryptonProgressBar();
            _progressBar.Location = new Point(20, 50);
            _progressBar.Size = new Size(400, 25);
            _progressBar.Maximum = 100;
            _progressBar.Minimum = 0;
            _progressBar.Value = 0;
            panel.Controls.Add(_progressBar);

            // Details section
            _detailsLabel = new KryptonLabel();
            _detailsLabel.Text = $"Chunks: 0/{_totalChunks}";
            _detailsLabel.Location = new Point(20, 85);
            _detailsLabel.AutoSize = true;
            panel.Controls.Add(_detailsLabel);

            // Time elapsed
            _timeElapsedLabel = new KryptonLabel();
            _timeElapsedLabel.Text = "Time elapsed: 00:00:00";
            _timeElapsedLabel.Location = new Point(20, 115);
            _timeElapsedLabel.AutoSize = true;
            panel.Controls.Add(_timeElapsedLabel);

            // Time remaining
            _timeRemainingLabel = new KryptonLabel();
            _timeRemainingLabel.Text = "Time remaining: --:--:--";
            _timeRemainingLabel.Location = new Point(20, 145);
            _timeRemainingLabel.AutoSize = true;
            panel.Controls.Add(_timeRemainingLabel);

            // Transfer rate
            _transferRateLabel = new KryptonLabel();
            _transferRateLabel.Text = "Transfer rate: -- chunks/sec";
            _transferRateLabel.Location = new Point(20, 175);
            _transferRateLabel.AutoSize = true;
            panel.Controls.Add(_transferRateLabel);

            // Cancel button
            _cancelButton = new KryptonButton();
            _cancelButton.Text = "Cancel";
            _cancelButton.Location = new Point(185, 210);
            _cancelButton.Size = new Size(80, 30);
            _cancelButton.Click += (s, e) =>
            {
                _cancellationTokenSource.Cancel();
                _statusLabel.Text = "Cancelling...";
                _cancelButton.Enabled = false;
            };
            panel.Controls.Add(_cancelButton);

            this.Controls.Add(panel);
        }

        private void UpdateTimerTick(object sender, EventArgs e)
        {
            // Update elapsed time
            TimeSpan elapsed = DateTime.Now - _startTime;
            _timeElapsedLabel.Text = $"Time elapsed: {elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

            // Update transfer rate
            if (elapsed.TotalSeconds > 0)
            {
                double rate = _chunksTransferred / elapsed.TotalSeconds;
                _transferRateLabel.Text = $"Transfer rate: {rate:F2} chunks/sec";

                // Update remaining time if we have progress
                if (_progressPercentage > 0)
                {
                    double remaining = (100 - _progressPercentage) / _progressPercentage * elapsed.TotalSeconds;
                    TimeSpan remainingTime = TimeSpan.FromSeconds(remaining);
                    _timeRemainingLabel.Text = $"Time remaining: {remainingTime.Hours:00}:{remainingTime.Minutes:00}:{remainingTime.Seconds:00}";
                }
            }
        }

        /// <summary>
        /// Updates the progress display
        /// </summary>
        public void UpdateProgress(string status, float percentage, int chunks, long bytes)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => UpdateProgress(status, percentage, chunks, bytes)));
                return;
            }

            _currentStatus = status;
            _progressPercentage = percentage;
            _chunksTransferred = chunks;
            _bytesTransferred = bytes;

            // Update UI elements
            _statusLabel.Text = status;
            _progressBar.Value = Math.Min(100, (int)percentage);
            _detailsLabel.Text = $"Chunks: {chunks}/{_totalChunks} ({FormatBytes(bytes)})";

            // Update the dialog title too
            this.Text = $"Transfer - {percentage:F1}% Complete";
        }

        /// <summary>
        /// Completes the transfer and closes the dialog
        /// </summary>
        public void CompleteTransfer(string finalStatus = "Transfer completed")
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => CompleteTransfer(finalStatus)));
                return;
            }

            // Stop the timer
            _updateTimer.Stop();

            // Update final status
            _statusLabel.Text = finalStatus;
            _progressBar.Value = 100;

            // Show final stats
            TimeSpan elapsed = DateTime.Now - _startTime;
            _timeElapsedLabel.Text = $"Total time: {elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            _timeRemainingLabel.Text = "Time remaining: 00:00:00";

            if (elapsed.TotalSeconds > 0)
            {
                double rate = _chunksTransferred / elapsed.TotalSeconds;
                _transferRateLabel.Text = $"Average rate: {rate:F2} chunks/sec";
            }

            // Change cancel button to close
            this.Close();
        }

        /// <summary>
        /// Shows error and optionally closes the dialog
        /// </summary>
        public void ShowError(string errorMessage, bool closeDialog = false)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => ShowError(errorMessage, closeDialog)));
                return;
            }

            // Stop the timer
            _updateTimer.Stop();

            // Show error
            _statusLabel.Text = "Error";
            _statusLabel.StateCommon.ShortText.Color1 = Color.Red;

            var errorLabel = new KryptonLabel();
            errorLabel.Text = errorMessage;
            errorLabel.Location = new Point(20, 240);
            errorLabel.Size = new Size(360, 40);
            errorLabel.StateCommon.ShortText.Color1 = Color.Red;
            errorLabel.StateCommon.ShortText.TextH = Krypton.Toolkit.PaletteRelativeAlign.Center;
            this.Controls.Add(errorLabel);

            // Change cancel button to close
           /* _cancelButton.Text = "Close";
            _cancelButton.Click -= _cancelButton.Click;
            _cancelButton.Click += (s, e) => this.DialogResult = DialogResult.Cancel;
            _cancelButton.Enabled = true;*/

            if (closeDialog)
            {
                // Auto-close with delay
                Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    this.Invoke(new Action(() => this.DialogResult = DialogResult.Cancel));
                });
            }
        }

        /// <summary>
        /// Formats bytes to a human-readable string
        /// </summary>
        private string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;

            while (size >= 1024 && order < suffixes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {suffixes[order]}";
        }

        /// <summary>
        /// Displays a transfer progress dialog and executes a task with progress reporting
        /// </summary>
        public static async Task<DialogResult> ExecuteWithProgressAsync(
            string title,
            int totalChunks,
            Func<IProgress<(string, float, int, long)>, CancellationToken, Task> task,
            Form owner = null)
        {
            using (var dialog = new TransferProgressDialog(title, totalChunks))
            {
                // Show dialog but don't block
                dialog.Show(owner);

                try
                {
                    // Create progress reporter
                    var progress = new Progress<(string, float, int, long)>(update =>
                    {
                        var (status, percentage, chunks, bytes) = update;
                        dialog.UpdateProgress(status, percentage, chunks, bytes);
                    });

                    // Execute the task
                    await task(progress, dialog.CancellationToken);

                    // Complete successfully
                    dialog.CompleteTransfer();

                    // Wait for user to close dialog
                    while (dialog.DialogResult != DialogResult.OK && dialog.DialogResult != DialogResult.Cancel)
                    {
                        await Task.Delay(100);
                        Application.DoEvents();
                    }

                    return dialog.DialogResult;
                }
                catch (OperationCanceledException)
                {
                    // Cancelled by user
                    dialog.CompleteTransfer("Transfer cancelled");
                    return DialogResult.Cancel;
                }
                catch (Exception ex)
                {
                    // Show error
                    dialog.ShowError(ex.Message);

                    // Wait for user to close dialog
                    while (dialog.DialogResult != DialogResult.OK && dialog.DialogResult != DialogResult.Cancel)
                    {
                        await Task.Delay(100);
                        Application.DoEvents();
                    }

                    return DialogResult.Cancel;
                }
            }
        }
    }
}