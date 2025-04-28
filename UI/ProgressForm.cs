using System;
using System.Drawing;
using System.Windows.Forms;

namespace CTSegmenter
{
    public partial class ProgressForm : Form, IProgress<int>
    {
        private ProgressBar progressBar;
        private Label label;
        private string messageText;

        public ProgressForm(string text = "Loading dataset...")
        {
            // Store the provided text.
            this.messageText = text;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Size = new Size(400, 120);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(350, 400);
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
                Size = new Size(360, 30),
                Style = ProgressBarStyle.Continuous,
                Maximum = 100  // Default maximum for percentage-based updates.
            };

            this.Controls.Add(label);
            this.Controls.Add(progressBar);
        }

        /// <summary>
        /// Implementation of IProgress<int> interface
        /// </summary>
        /// <param name="value">The progress value to report</param>
        public void Report(int value)
        {
            UpdateProgress(value);
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
            if (message != null)
                label.Text = message;

            progressBar.Maximum = total;
            progressBar.Value = Math.Min(current, total);
            Application.DoEvents();
        }

        /// <summary>
        /// Safely updates the progress, ensuring thread safety.
        /// </summary>
        /// <param name="current">The current progress value.</param>
        /// <param name="total">The total progress value.</param>
        /// <param name="message">Optional message to display.</param>
        public void SafeUpdateProgress(int current, int total, string message = null)
        {
            if (this.IsDisposed) return;

            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() =>
                    SafeUpdateProgress(current, total, message)));
                return;
            }

            try
            {
                if (message != null)
                    label.Text = message;
                progressBar.Maximum = total;
                progressBar.Value = Math.Min(current, total);
            }
            catch (ObjectDisposedException)
            {
                // Ignore if form is closed.
            }
        }
    }
    public class ProgressFormAdapter : IProgress<int>
    {
        private readonly ProgressForm _progressForm;

        public ProgressFormAdapter(ProgressForm progressForm)
        {
            _progressForm = progressForm ?? throw new ArgumentNullException(nameof(progressForm));
        }

        public void Report(int value)
        {
            _progressForm.UpdateProgress(value);
        }

        // Allow direct access to the wrapped ProgressForm
        public ProgressForm ProgressForm => _progressForm;
    }
}