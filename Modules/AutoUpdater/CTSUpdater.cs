using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Windows.Forms;

namespace CTSUpdater
{
    public class Program
    {
        private static string applicationPath;
        private static string updateDir;
        private static Form updateForm;

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                // Parse command line arguments
                ParseArguments(args);

                // Ensure the application path was provided
                if (string.IsNullOrEmpty(applicationPath))
                {
                    MessageBox.Show("Application path not specified. Please run this updater from the main application.",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Check if the application is in Program Files
                string appDir = Path.GetDirectoryName(applicationPath);
                bool isInProgramFiles = appDir.Contains("Program Files") || appDir.Contains("ProgramFiles");

                // Check if we need admin rights
                if (isInProgramFiles && !IsRunningAsAdmin())
                {
                    // Restart as administrator
                    RestartAsAdmin(args);
                    return;
                }

                // Create update form
                ShowUpdateForm();

                // Extract update files
                string updaterExe = Process.GetCurrentProcess().MainModule.FileName;
                string updaterDir = Path.GetDirectoryName(updaterExe);
                updateDir = Path.Combine(updaterDir, "update_files");

                UpdateStatus("Extracting update files...");
                ExtractUpdateFiles(updaterExe, updateDir);

                // Kill the application process if it's running
                UpdateStatus("Stopping application...");
                KillApplication();

                // Copy updated files
                UpdateStatus("Installing update...");
                CopyUpdatedFiles(updateDir, appDir);

                // Cleanup
                UpdateStatus("Finalizing update...");
                CleanupUpdateFiles();

                // Start the application
                UpdateStatus("Starting application...");
                Process.Start(applicationPath);

                // Close updater
                UpdateStatus("Update complete!");
                Thread.Sleep(2000);
                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during update: {ex.Message}", "Update Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Parse command line arguments
        /// </summary>
        private static void ParseArguments(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--app-path" && i + 1 < args.Length)
                {
                    applicationPath = args[i + 1];
                    i++;
                }
            }
        }

        /// <summary>
        /// Show update progress form
        /// </summary>
        private static void ShowUpdateForm()
        {
            updateForm = new Form
            {
                Text = "CTS Updater",
                Size = new Size(400, 150),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };

            Label statusLabel = new Label
            {
                Location = new Point(20, 20),
                Size = new Size(360, 20),
                Text = "Preparing update...",
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            ProgressBar progressBar = new ProgressBar
            {
                Location = new Point(20, 50),
                Size = new Size(360, 20),
                Style = ProgressBarStyle.Marquee
            };

            updateForm.Controls.Add(statusLabel);
            updateForm.Controls.Add(progressBar);

            // Show the form in a non-blocking way
            new Thread(() =>
            {
                Application.Run(updateForm);
            })
            { IsBackground = true }.Start();
        }

        /// <summary>
        /// Update the status label on the form
        /// </summary>
        private static void UpdateStatus(string status)
        {
            if (updateForm == null || updateForm.IsDisposed)
                return;

            updateForm.Invoke(new Action(() =>
            {
                foreach (Control control in updateForm.Controls)
                {
                    if (control is Label label)
                    {
                        label.Text = status;
                        break;
                    }
                }
            }));

            // Small delay to make the UI responsive
            Thread.Sleep(500);
        }

        /// <summary>
        /// Extract update files from the embedded zip
        /// </summary>
        private static void ExtractUpdateFiles(string updaterPath, string extractPath)
        {
            try
            {
                // Create extraction directory if it doesn't exist
                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);
                Directory.CreateDirectory(extractPath);

                // The updater exe has the update zip appended to it
                // We need to extract it first
                using (FileStream fs = new FileStream(updaterPath, FileMode.Open, FileAccess.Read))
                {
                    // Find the zip marker at the end of the file
                    byte[] marker = new byte[] { 0x50, 0x4B, 0x05, 0x06 }; // End of central directory record
                    byte[] buffer = new byte[4];

                    // Start from 22 bytes from the end (minimum size of end of central directory record)
                    // and search backwards for the marker
                    long position = fs.Length - 22;
                    while (position > 0)
                    {
                        fs.Position = position;
                        fs.Read(buffer, 0, 4);

                        if (BuffersEqual(buffer, marker))
                        {
                            // Found the marker, extract the zip
                            long zipStart = position - 0x10000; // Approximate start of zip
                            if (zipStart < 0) zipStart = 0;

                            // Find the real start of the zip (PK header)
                            fs.Position = zipStart;
                            byte[] pkHeader = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
                            byte[] searchBuffer = new byte[4];

                            while (fs.Position < fs.Length - 4)
                            {
                                fs.Read(searchBuffer, 0, 4);
                                fs.Position -= 3; // Overlap for searching

                                if (BuffersEqual(searchBuffer, pkHeader))
                                {
                                    // Found the start of the zip file
                                    long zipFilePosition = fs.Position - 1;
                                    long zipFileLength = fs.Length - zipFilePosition;

                                    // Extract the zip file
                                    byte[] zipData = new byte[zipFileLength];
                                    fs.Position = zipFilePosition;
                                    fs.Read(zipData, 0, (int)zipFileLength);

                                    // Save to a temporary file
                                    string tempZipFile = Path.Combine(Path.GetTempPath(), "update.zip");
                                    File.WriteAllBytes(tempZipFile, zipData);

                                    // Extract the zip to the update directory
                                    ZipFile.ExtractToDirectory(tempZipFile, extractPath);

                                    // Delete the temporary zip file
                                    File.Delete(tempZipFile);
                                    return;
                                }
                            }
                        }

                        position--;
                    }

                    throw new Exception("Could not find update data in the updater file.");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error extracting update files: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Check if two byte buffers are equal
        /// </summary>
        private static bool BuffersEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Kill the application process if it's running
        /// </summary>
        private static void KillApplication()
        {
            try
            {
                string processName = Path.GetFileNameWithoutExtension(applicationPath);
                Process[] processes = Process.GetProcessesByName(processName);

                foreach (Process process in processes)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                        }
                    }
                    catch
                    {
                        // Ignore errors killing the process
                    }
                }

                // Additional wait to ensure the process is fully terminated
                Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error stopping application: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Copy updated files to the application directory
        /// </summary>
        private static void CopyUpdatedFiles(string sourceDir, string targetDir)
        {
            try
            {
                // Get all files in the source directory
                foreach (string file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
                {
                    // Get the relative path
                    string relativePath = file.Substring(sourceDir.Length + 1);
                    string targetPath = Path.Combine(targetDir, relativePath);

                    // Create the target directory if it doesn't exist
                    string targetFileDir = Path.GetDirectoryName(targetPath);
                    if (!Directory.Exists(targetFileDir))
                        Directory.CreateDirectory(targetFileDir);

                    // Copy the file, overwriting if it exists
                    File.Copy(file, targetPath, true);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error copying updated files: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Clean up temporary update files
        /// </summary>
        private static void CleanupUpdateFiles()
        {
            try
            {
                if (Directory.Exists(updateDir))
                    Directory.Delete(updateDir, true);

                // Schedule the updater itself for deletion after it exits
                string updaterExe = Process.GetCurrentProcess().MainModule.FileName;
                string batchFile = Path.Combine(Path.GetTempPath(), "cleanup.bat");

                // Create a batch file to delete the updater executable after a short delay
                string batch =
                    "@echo off\r\n" +
                    "ping 127.0.0.1 -n 2 > nul\r\n" + // Wait for 1 second
                    $"del \"{updaterExe}\"\r\n" +
                    "del \"%~f0\"\r\n"; // Delete this batch file

                File.WriteAllText(batchFile, batch);

                // Start the batch file
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = batchFile,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = true
                };
                Process.Start(startInfo);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        /// <summary>
        /// Check if the application is running with admin privileges
        /// </summary>
        private static bool IsRunningAsAdmin()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restart the application with admin privileges
        /// </summary>
        private static void RestartAsAdmin(string[] args)
        {
            try
            {
                // Get the current executable path
                string exePath = Process.GetCurrentProcess().MainModule.FileName;

                // Create a ProcessStartInfo with the "runas" verb to request admin rights
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    WorkingDirectory = Environment.CurrentDirectory,
                    FileName = exePath,
                    Verb = "runas" // This requests elevation
                };

                // Add arguments
                if (args.Length > 0)
                {
                    startInfo.Arguments = string.Join(" ", args);
                }

                // Start the process with elevated privileges
                Process.Start(startInfo);

                // Exit the current process
                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to restart with administrative privileges. The update might fail. Error: {ex.Message}",
                    "Elevation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}