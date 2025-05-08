using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CTS
{
    public class LogWindow : Form
    {
        private readonly RichTextBox logTextBox;
        private readonly object _lockObj = new object();

        public LogWindow()
        {
            Text = "Log Window";
            Width = 800;
            Height = 600;
            BackColor = Color.Black;

            try
            {
                Icon = Properties.Resources.favicon;
            }
            catch { }

            // Create the main layout panel
            TableLayoutPanel mainPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                BackColor = Color.Black
            };

            // Set row percentages - small logo area at top, large text area below
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 20F)); // Logo area - 20%
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 80F)); // Text area - 80%

            // Create logo panel for top area
            Panel logoPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            // Create the text box for log messages
            logTextBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                ForeColor = Color.LightCyan,
                Font = new Font("Consolas", 10, FontStyle.Bold),
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BorderStyle = BorderStyle.None
            };

            // Try to set the logo
            try
            {
                PictureBox logoBox = new PictureBox
                {
                    Image = Properties.Resources.logo,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Dock = DockStyle.Fill,
                    BackColor = Color.Transparent
                };
                logoPanel.Controls.Add(logoBox);
            }
            catch
            {
                // Add a simple label if logo fails to load
                Label fallbackLabel = new Label
                {
                    Text = "App Logo",
                    ForeColor = Color.LightCyan,
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill,
                    Font = new Font("Arial", 16, FontStyle.Bold)
                };
                logoPanel.Controls.Add(fallbackLabel);
            }

            // Add the panels to the main layout
            mainPanel.Controls.Add(logoPanel, 0, 0);
            mainPanel.Controls.Add(logTextBox, 0, 1);

            // Add the main panel to the form
            Controls.Add(mainPanel);
        }

        // Append a log message - thread-safe
        public void AppendLog(string logMessage)
        {
            if (logTextBox.IsDisposed) return;

            try
            {
                if (logTextBox.InvokeRequired)
                {
                    logTextBox.BeginInvoke(new Action(() => SafeAppendText(logMessage)));
                }
                else
                {
                    SafeAppendText(logMessage);
                }
            }
            catch
            {
                // Suppress threading errors
            }
        }

        // Helper method to safely append text
        private void SafeAppendText(string text)
        {
            try
            {
                lock (_lockObj)
                {
                    const int maxLength = 1000000; // ~1MB of text
                    if (logTextBox.TextLength > maxLength)
                    {
                        logTextBox.Text = logTextBox.Text.Substring(logTextBox.TextLength - maxLength / 2);
                    }

                    logTextBox.AppendText(text);
                    logTextBox.SelectionStart = logTextBox.TextLength;
                    logTextBox.ScrollToCaret();
                }
            }
            catch
            {
                // Suppress errors
            }
        }

        // Instead of closing, hide the window.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!Logger.ShuttingDown)
            {
                // Prevent closing if the application is still running.
                e.Cancel = true;
                this.Hide();
            }
            else
            {
                base.OnFormClosing(e);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (logTextBox != null && !logTextBox.IsDisposed)
                {
                    logTextBox.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(LogWindow));
            this.SuspendLayout();
            // 
            // LogWindow
            // 
            this.ClientSize = new System.Drawing.Size(800, 600);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("favicon.Icon")));
            this.Name = "LogWindow";
            this.Load += new System.EventHandler(this.LogWindow_Load);
            this.ResumeLayout(false);
        }

        private void LogWindow_Load(object sender, EventArgs e)
        {
            // LogWindow initialization code if needed
        }
    }
}