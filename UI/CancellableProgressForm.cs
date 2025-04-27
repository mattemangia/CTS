using System;
using System.Drawing;
using System.Windows.Forms;

namespace CTSegmenter
{
    /// <summary>
    /// Progress form with cancellation support
    /// </summary>
    public class CancellableProgressForm : Form
    {
        private ProgressBar progressBar;
        private Label label;
        private Button cancelButton;
        private string messageText;

        /// <summary>
        /// Event triggered when the cancel button is pressed
        /// </summary>
        public event EventHandler CancelPressed;

        public CancellableProgressForm(string text = "Loading dataset...")
        {
            // Store the provided text.
            this.messageText = text;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Size = new Size(400, 150); // Increased height for cancel button
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.WindowsDefaultLocation;
            this.ControlBox = false;
            this.Text = "Processing...";

            label = new Label
            {
                Text = this.messageText,
                Location = new Point(20, 20),
                AutoSize = true
            };

            progressBar = new ProgressBar
            {
                Location = new Point(20, 50),
                Size = new Size(360, 25),
                Style = ProgressBarStyle.Continuous,
                Maximum = 100  // Default maximum for percentage-based updates.
            };

            cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(160, 85),
                Size = new Size(80, 25),
                UseVisualStyleBackColor = true
            };

            cancelButton.Click += (s, e) =>
            {
                cancelButton.Enabled = false;
                cancelButton.Text = "Cancelling...";
                CancelPressed?.Invoke(this, EventArgs.Empty);
            };

            this.Controls.Add(label);
            this.Controls.Add(progressBar);
            this.Controls.Add(cancelButton);
        }

        /// <summary>
        /// Updates the progress using a percentage (0 to 100).
        /// </summary>
        /// <param name="percent">The current progress percentage.</param>
        public void UpdateProgress(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => UpdateProgress(percent)));
                return;
            }
            progressBar.Maximum = 100;
            progressBar.Value = percent;
            label.Text = $"{messageText} ({percent}%)";
            Application.DoEvents();
        }

        /// <summary>
        /// Updates the progress with current and total values.
        /// </summary>
        /// <param name="current">The current progress value.</param>
        /// <param name="total">The total progress value.</param>
        /// <param name="message">Optional message to display.</param>
        public void UpdateProgress(int current, int total, string message = null)
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => UpdateProgress(current, total, message)));
                return;
            }

            if (message != null)
                label.Text = message;

            progressBar.Maximum = total;
            progressBar.Value = Math.Min(current, total);
            Application.DoEvents();
        }

        /// <summary>
        /// Updates the message text without changing the progress value
        /// </summary>
        public void UpdateMessage(string message)
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => UpdateMessage(message)));
                return;
            }

            label.Text = message;
            Application.DoEvents();
        }
    }
}