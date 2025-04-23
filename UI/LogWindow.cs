using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CTSegmenter
{
    public class LogWindow : Form
    {
        private readonly TextBox textBox;

        public LogWindow()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "favicon.ico");
                if (File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
                else
                {
                    // Optionally log that the icon file wasn't found.
                }
            }
            catch (Exception ex)
            {
                // Optionally log or handle the exception.
            }
            Text = "Log Window";
            Width = 600;
            Height = 400;

            textBox = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                BackColor = Color.Black,
                ForeColor = Color.LightCyan,
                Font = new Font("Consolas", 10,FontStyle.Bold),
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
