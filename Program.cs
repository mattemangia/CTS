using CTS;
using System;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string logPath = System.IO.Path.Combine(Application.StartupPath, "log.txt");
        if (System.IO.File.Exists(logPath))
            System.IO.File.Delete(logPath); // Clear old logs

        Logger.Log("Application started.");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        MainForm mainForm = new MainForm(args);
        ControlForm controlForm = new ControlForm(mainForm);

        Application.Run(mainForm);
        mainForm.DockExternalControlForm(controlForm);
        Logger.Log("Application ended.");
    }
}