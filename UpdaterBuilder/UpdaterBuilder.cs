using System;
using System.IO;
using System.IO.Compression;
using System.Reflection.Emit;
using System.Windows.Forms;
using Label = System.Windows.Forms.Label;

namespace UpdaterBuilder
{
    /// <summary>
    /// Utility to build updater packages for GitHub releases
    /// </summary>
   static class Program
    {
        [STAThread]
    static void Main()
    {
        // Enable Windows visual styles & text rendering defaults
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Show the builder dialog
        UpdaterBuilder.ShowBuilderDialog();
    }
}
public class UpdaterBuilder
    {
        /// <summary>
        /// Creates an updater package that includes the specified files.
        /// </summary>
        /// <param name="updaterExePath">Path to the updater executable</param>
        /// <param name="filesDirectory">Directory containing files to include in the update</param>
        /// <param name="outputPath">Path where the final updater will be saved</param>
        /// <param name="isFullPackage">Whether this is a full package (true) or just an updater (false)</param>
        /// <returns>Path to the created updater package</returns>
        public static string BuildUpdaterPackage(string updaterExePath, string filesDirectory, string outputPath, bool isFullPackage)
        {
            try
            {
                // Create a temporary zip file with the update files
                string tempZipFile = Path.Combine(Path.GetTempPath(), "update_files.zip");
                if (File.Exists(tempZipFile))
                    File.Delete(tempZipFile);

                // Create the zip file
                ZipFile.CreateFromDirectory(filesDirectory, tempZipFile);

                // Read the updater executable and zip file
                byte[] updaterBytes = File.ReadAllBytes(updaterExePath);
                byte[] zipBytes = File.ReadAllBytes(tempZipFile);

                // Create the output file name with appropriate suffix
                string outputFileName = Path.GetFileName(outputPath);
                if (!outputFileName.Contains("updater") && !outputFileName.Contains("full"))
                {
                    string suffix = isFullPackage ? "_full" : "_updater";
                    outputFileName = Path.GetFileNameWithoutExtension(outputFileName) + suffix + Path.GetExtension(outputFileName);
                    outputPath = Path.Combine(Path.GetDirectoryName(outputPath), outputFileName);
                }

                // Combine the updater and zip into a single file
                using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(updaterBytes, 0, updaterBytes.Length);
                    fs.Write(zipBytes, 0, zipBytes.Length);
                }

                // Clean up the temporary zip file
                File.Delete(tempZipFile);

                return outputPath;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error building updater package: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Shows a dialog to build an updater package
        /// </summary>
        public static void ShowBuilderDialog()
        {
            using (Form form = new Form
            {
                Text = "Build Updater Package",
                Size = new System.Drawing.Size(500, 250),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false,
                MinimizeBox = false
            })
            {
                // Updater executable path
                Label lblUpdaterExe = new Label
                {
                    Text = "Updater Executable:",
                    Location = new System.Drawing.Point(20, 20),
                    AutoSize = true
                };
                form.Controls.Add(lblUpdaterExe);

                TextBox txtUpdaterExe = new TextBox
                {
                    Location = new System.Drawing.Point(150, 20),
                    Width = 250
                };
                form.Controls.Add(txtUpdaterExe);

                Button btnBrowseExe = new Button
                {
                    Text = "Browse",
                    Location = new System.Drawing.Point(410, 20),
                    Width = 60
                };
                btnBrowseExe.Click += (s, e) =>
                {
                    using (OpenFileDialog ofd = new OpenFileDialog
                    {
                        Filter = "Executable Files|*.exe",
                        Title = "Select Updater Executable"
                    })
                    {
                        if (ofd.ShowDialog() == DialogResult.OK)
                        {
                            txtUpdaterExe.Text = ofd.FileName;
                        }
                    }
                };
                form.Controls.Add(btnBrowseExe);

                // Files directory
                Label lblFilesDir = new Label
                {
                    Text = "Files Directory:",
                    Location = new System.Drawing.Point(20, 50),
                    AutoSize = true
                };
                form.Controls.Add(lblFilesDir);

                TextBox txtFilesDir = new TextBox
                {
                    Location = new System.Drawing.Point(150, 50),
                    Width = 250
                };
                form.Controls.Add(txtFilesDir);

                Button btnBrowseDir = new Button
                {
                    Text = "Browse",
                    Location = new System.Drawing.Point(410, 50),
                    Width = 60
                };
                btnBrowseDir.Click += (s, e) =>
                {
                    using (FolderBrowserDialog fbd = new FolderBrowserDialog
                    {
                        Description = "Select Directory with Update Files"
                    })
                    {
                        if (fbd.ShowDialog() == DialogResult.OK)
                        {
                            txtFilesDir.Text = fbd.SelectedPath;
                        }
                    }
                };
                form.Controls.Add(btnBrowseDir);

                // Output path
                Label lblOutputPath = new Label
                {
                    Text = "Output Path:",
                    Location = new System.Drawing.Point(20, 80),
                    AutoSize = true
                };
                form.Controls.Add(lblOutputPath);

                TextBox txtOutputPath = new TextBox
                {
                    Location = new System.Drawing.Point(150, 80),
                    Width = 250
                };
                form.Controls.Add(txtOutputPath);

                Button btnBrowseOutput = new Button
                {
                    Text = "Browse",
                    Location = new System.Drawing.Point(410, 80),
                    Width = 60
                };
                btnBrowseOutput.Click += (s, e) =>
                {
                    using (SaveFileDialog sfd = new SaveFileDialog
                    {
                        Filter = "Executable Files|*.exe",
                        Title = "Save Updater Package"
                    })
                    {
                        if (sfd.ShowDialog() == DialogResult.OK)
                        {
                            txtOutputPath.Text = sfd.FileName;
                        }
                    }
                };
                form.Controls.Add(btnBrowseOutput);

                // Full package checkbox
                CheckBox chkFullPackage = new CheckBox
                {
                    Text = "Full Package (include ONNX dependencies)",
                    Location = new System.Drawing.Point(150, 110),
                    AutoSize = true
                };
                form.Controls.Add(chkFullPackage);

                // Build button
                Button btnBuild = new Button
                {
                    Text = "Build Updater Package",
                    Location = new System.Drawing.Point(150, 140),
                    Width = 200,
                    Height = 30
                };
                btnBuild.Click += (s, e) =>
                {
                    try
                    {
                        // Validate inputs
                        if (string.IsNullOrEmpty(txtUpdaterExe.Text) || !File.Exists(txtUpdaterExe.Text))
                        {
                            MessageBox.Show("Please select a valid updater executable.", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        if (string.IsNullOrEmpty(txtFilesDir.Text) || !Directory.Exists(txtFilesDir.Text))
                        {
                            MessageBox.Show("Please select a valid directory with update files.", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        if (string.IsNullOrEmpty(txtOutputPath.Text))
                        {
                            MessageBox.Show("Please specify an output path.", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        // Build the updater package
                        string outputPath = BuildUpdaterPackage(
                            txtUpdaterExe.Text,
                            txtFilesDir.Text,
                            txtOutputPath.Text,
                            chkFullPackage.Checked);

                        MessageBox.Show($"Updater package created successfully:\n{outputPath}",
                            "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);

                        form.DialogResult = DialogResult.OK;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error building updater package: {ex.Message}",
                            "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                form.Controls.Add(btnBuild);

                // Show the form
                form.ShowDialog();
            }
        }
    }
}