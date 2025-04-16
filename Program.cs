using CTSegmenter;
using System;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string logPath = System.IO.Path.Combine(Application.StartupPath, "log.txt");
        if (System.IO.File.Exists(logPath))
            System.IO.File.Delete(logPath); // Clear old logs

        Logger.Log("Application started.");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        MainForm mainForm = new MainForm(args);
        ControlForm controlForm = new ControlForm(mainForm);
        controlForm.Show();
        Application.Run(mainForm);

        Logger.Log("Application ended.");
    }
}
