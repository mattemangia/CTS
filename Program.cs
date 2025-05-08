using CTS;
using System;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            // Clear old logs
            string logPath = System.IO.Path.Combine(Application.StartupPath, "log.txt");
            if (System.IO.File.Exists(logPath))
                System.IO.File.Delete(logPath);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Log startup with system information
            Logger.Log("===== APPLICATION STARTED =====");
            Logger.LogSystemInfo();

            // Register application exit handler
            Application.ApplicationExit += (sender, e) =>
            {
                Logger.Log("===== APPLICATION ENDING =====");
                Logger.Shutdown();
            };

            // Create and run the main form
            MainForm mainForm = new MainForm(args);
            ControlForm controlForm = new ControlForm(mainForm);

            Application.Run(mainForm);
            mainForm.DockExternalControlForm(controlForm);
        }
        catch (Exception ex)
        {
            // Log any unhandled exceptions
            Logger.Log($"Unhandled exception in Main: {ex}", Microsoft.Extensions.Logging.LogLevel.Error);
            MessageBox.Show($"An unexpected error occurred: {ex.Message}", "Application Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            // Ensure the logger is shut down properly
            Logger.Shutdown();
        }
    }
}