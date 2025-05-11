using CTS;
using CTS.UI;
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

            // Initialize logger (don't log system info yet)
            Logger.Log("===== APPLICATION STARTED =====");
            Logger.Log("Starting dependencies checker...");

            // Show the dependencies checker first
            SplashScreen splash = new SplashScreen();

            // Show the dependencies checker modal
            DialogResult splashResult = splash.ShowDialog();

            // Ensure dependencies checker is closed before continuing
            splash.Dispose();

            // If dependencies check detected errors, show error message and exit
            if (splash.HasError)
            {
                Logger.Log($"Dependencies check detected error: {splash.ErrorMessage}", Microsoft.Extensions.Logging.LogLevel.Error);

                // Show error dialog to the user
                MessageBox.Show(splash.ErrorMessage, "Required Assemblies Missing",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);

                Logger.Log("===== APPLICATION TERMINATED DUE TO ASSEMBLY ERROR =====");
                Logger.Shutdown();
                return;
            }

            // Only proceed if dependencies check completed successfully
            if (splashResult == DialogResult.OK)
            {
                Logger.Log("Dependencies check completed successfully. Starting main application...");

                // Create the main form (which creates its own ControlForm)
                MainForm mainForm = new MainForm(args);

                // Register application exit handler
                Application.ApplicationExit += (sender, e) =>
                {
                    Logger.Log("===== APPLICATION ENDING =====");
                    Logger.Shutdown();
                };

                // Show the main form
                mainForm.Show();
                mainForm.BringToFront();

                // Now log system info and show any logger windows
                Logger.LogSystemInfo();

                // Run the main form
                Application.Run(mainForm);
            }
            else
            {
                Logger.Log("Dependencies check cancelled or failed to complete.", Microsoft.Extensions.Logging.LogLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            // Log any unhandled exceptions
            Logger.Log($"Unhandled exception in Main: {ex}", Microsoft.Extensions.Logging.LogLevel.Error);
            MessageBox.Show($"An unexpected error occurred: {ex.Message}\n\nPlease check the log file for more details.",
                "Application Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            // Ensure the logger is shut down properly
            Logger.Shutdown();
        }
    }
}