using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CTS
{
    public class LogWindow : Form
    {
        private readonly TextBox textBox;

        public LogWindow()
        {
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }
            Text = "Log Window";
            Width = 800;
            Height = 600;

            textBox = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                BackColor = Color.Black,
                ForeColor = Color.LightCyan,
                Font = new Font("Consolas", 10, FontStyle.Bold),
            };
            Controls.Add(textBox);
        }

        // Append a log message
        public void AppendLog(string logMessage)
        {
            textBox.AppendText(logMessage);
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

        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(LogWindow));
            this.SuspendLayout();
            // 
            // LogWindow
            // 
            this.ClientSize = new System.Drawing.Size(282, 253);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("favicon.Icon")));
            this.Name = "LogWindow";
            this.Load += new System.EventHandler(this.LogWindow_Load);
            this.ResumeLayout(false);


        }

        private void LogWindow_Load(object sender, EventArgs e)
        {

        }

    }
}
