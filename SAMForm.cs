using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTSegmenter
{
    public partial class SAMForm : Form
    {
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
        public SAMForm()
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
            Logger.Log("[SAM] Constructor start");
            InitializeComponent();
            Logger.Log("[SAM] Constructor end");
        }
    }
}
