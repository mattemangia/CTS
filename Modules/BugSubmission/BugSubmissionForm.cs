using Krypton.Toolkit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.BugSubmission
{
    /// <summary>
    /// Form for collecting and submitting bug reports from users
    /// </summary>
    public class BugSubmissionForm : KryptonForm
    {
        
        private const string BugReportEmail = "mattemangia@icloud.com";

        // UI Controls
        private KryptonLabel lblTitle;
        private KryptonLabel lblDescription;
        private KryptonTextBox txtDescription;
        private KryptonLabel lblSteps;
        private KryptonTextBox txtSteps;
        private KryptonLabel lblContact;
        private KryptonTextBox txtContact;
        private KryptonCheckBox chkIncludeLog;
        private KryptonCheckBox chkIncludeSystemInfo;
        private KryptonButton btnSubmit;
        private KryptonButton btnCancel;
        private KryptonPanel panelMain;
        private KryptonPanel panelScreenshot;
        private KryptonButton btnTakeScreenshot;
        private KryptonLabel lblScreenshot;
        private PictureBox pictureBoxScreenshot;
        private KryptonButton btnRemoveScreenshot;
        private Image screenshotImage = null;
        private KryptonComboBox cmbSubmissionMethod;
        private KryptonLabel lblSubmissionMethod;

        /// <summary>
        /// Initializes a new instance of the BugSubmissionForm
        /// </summary>
        public BugSubmissionForm()
        {
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }
            InitializeComponent();

            // Make the form dark themed
            ApplyDarkTheme(this);

            // Initialize to email submission by default
            cmbSubmissionMethod.SelectedIndex = 0;
        }

        /// <summary>
        /// Apply dark theme to all controls
        /// </summary>
        private void ApplyDarkTheme(Control control)
        {
            control.BackColor = Color.FromArgb(45, 45, 48);
            control.ForeColor = Color.Gainsboro;

            foreach (Control child in control.Controls)
            {
                ApplyDarkTheme(child);
            }
        }

        /// <summary>
        /// Initialize form components
        /// </summary>
        private void InitializeComponent()
        {
            this.panelMain = new Krypton.Toolkit.KryptonPanel();
            this.lblTitle = new Krypton.Toolkit.KryptonLabel();
            this.lblSubmissionMethod = new Krypton.Toolkit.KryptonLabel();
            this.cmbSubmissionMethod = new Krypton.Toolkit.KryptonComboBox();
            this.lblDescription = new Krypton.Toolkit.KryptonLabel();
            this.txtDescription = new Krypton.Toolkit.KryptonTextBox();
            this.lblSteps = new Krypton.Toolkit.KryptonLabel();
            this.txtSteps = new Krypton.Toolkit.KryptonTextBox();
            this.lblContact = new Krypton.Toolkit.KryptonLabel();
            this.txtContact = new Krypton.Toolkit.KryptonTextBox();
            this.panelScreenshot = new Krypton.Toolkit.KryptonPanel();
            this.lblScreenshot = new Krypton.Toolkit.KryptonLabel();
            this.btnTakeScreenshot = new Krypton.Toolkit.KryptonButton();
            this.btnRemoveScreenshot = new Krypton.Toolkit.KryptonButton();
            this.pictureBoxScreenshot = new System.Windows.Forms.PictureBox();
            this.chkIncludeLog = new Krypton.Toolkit.KryptonCheckBox();
            this.chkIncludeSystemInfo = new Krypton.Toolkit.KryptonCheckBox();
            this.btnSubmit = new Krypton.Toolkit.KryptonButton();
            this.btnCancel = new Krypton.Toolkit.KryptonButton();
            ((System.ComponentModel.ISupportInitialize)(this.panelMain)).BeginInit();
            this.panelMain.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.cmbSubmissionMethod)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.panelScreenshot)).BeginInit();
            this.panelScreenshot.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBoxScreenshot)).BeginInit();
            this.SuspendLayout();
            // 
            // panelMain
            // 
            this.panelMain.Controls.Add(this.lblTitle);
            this.panelMain.Controls.Add(this.lblSubmissionMethod);
            this.panelMain.Controls.Add(this.cmbSubmissionMethod);
            this.panelMain.Controls.Add(this.lblDescription);
            this.panelMain.Controls.Add(this.txtDescription);
            this.panelMain.Controls.Add(this.lblSteps);
            this.panelMain.Controls.Add(this.txtSteps);
            this.panelMain.Controls.Add(this.lblContact);
            this.panelMain.Controls.Add(this.txtContact);
            this.panelMain.Controls.Add(this.panelScreenshot);
            this.panelMain.Controls.Add(this.chkIncludeLog);
            this.panelMain.Controls.Add(this.chkIncludeSystemInfo);
            this.panelMain.Controls.Add(this.btnSubmit);
            this.panelMain.Controls.Add(this.btnCancel);
            this.panelMain.Dock = System.Windows.Forms.DockStyle.Fill;
            this.panelMain.Location = new System.Drawing.Point(0, 0);
            this.panelMain.Name = "panelMain";
            this.panelMain.Size = new System.Drawing.Size(586, 654);
            this.panelMain.TabIndex = 0;
            // 
            // lblTitle
            // 
            this.lblTitle.Location = new System.Drawing.Point(20, 20);
            this.lblTitle.Name = "lblTitle";
            this.lblTitle.Size = new System.Drawing.Size(88, 24);
            this.lblTitle.TabIndex = 0;
            this.lblTitle.Values.Text = "Bug Report";
            // 
            // lblSubmissionMethod
            // 
            this.lblSubmissionMethod.Location = new System.Drawing.Point(20, 60);
            this.lblSubmissionMethod.Name = "lblSubmissionMethod";
            this.lblSubmissionMethod.Size = new System.Drawing.Size(151, 24);
            this.lblSubmissionMethod.TabIndex = 1;
            this.lblSubmissionMethod.Values.Text = "Submission Method:";
            // 
            // cmbSubmissionMethod
            // 
            this.cmbSubmissionMethod.Items.AddRange(new object[] {
            "Email",
            "GitHub Issue"});
            this.cmbSubmissionMethod.Location = new System.Drawing.Point(177, 58);
            this.cmbSubmissionMethod.Name = "cmbSubmissionMethod";
            this.cmbSubmissionMethod.Size = new System.Drawing.Size(200, 26);
            this.cmbSubmissionMethod.TabIndex = 2;
            // 
            // lblDescription
            // 
            this.lblDescription.Location = new System.Drawing.Point(20, 90);
            this.lblDescription.Name = "lblDescription";
            this.lblDescription.Size = new System.Drawing.Size(123, 24);
            this.lblDescription.TabIndex = 3;
            this.lblDescription.Values.Text = "Bug Description:";
            // 
            // txtDescription
            // 
            this.txtDescription.AcceptsReturn = true;
            this.txtDescription.AcceptsTab = true;
            this.txtDescription.Location = new System.Drawing.Point(20, 115);
            this.txtDescription.Multiline = true;
            this.txtDescription.Name = "txtDescription";
            this.txtDescription.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtDescription.Size = new System.Drawing.Size(540, 104);
            this.txtDescription.TabIndex = 4;
            // 
            // lblSteps
            // 
            this.lblSteps.Location = new System.Drawing.Point(20, 225);
            this.lblSteps.Name = "lblSteps";
            this.lblSteps.Size = new System.Drawing.Size(149, 24);
            this.lblSteps.TabIndex = 5;
            this.lblSteps.Values.Text = "Steps to Reproduce:";
            // 
            // txtSteps
            // 
            this.txtSteps.AcceptsReturn = true;
            this.txtSteps.AcceptsTab = true;
            this.txtSteps.Location = new System.Drawing.Point(20, 250);
            this.txtSteps.Multiline = true;
            this.txtSteps.Name = "txtSteps";
            this.txtSteps.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtSteps.Size = new System.Drawing.Size(540, 104);
            this.txtSteps.TabIndex = 6;
            // 
            // lblContact
            // 
            this.lblContact.Location = new System.Drawing.Point(20, 360);
            this.lblContact.Name = "lblContact";
            this.lblContact.Size = new System.Drawing.Size(205, 24);
            this.lblContact.TabIndex = 7;
            this.lblContact.Values.Text = "Your Contact Info (optional):";
            // 
            // txtContact
            // 
            this.txtContact.Location = new System.Drawing.Point(20, 385);
            this.txtContact.Name = "txtContact";
            this.txtContact.Size = new System.Drawing.Size(540, 27);
            this.txtContact.TabIndex = 8;
            // 
            // panelScreenshot
            // 
            this.panelScreenshot.Controls.Add(this.lblScreenshot);
            this.panelScreenshot.Controls.Add(this.btnTakeScreenshot);
            this.panelScreenshot.Controls.Add(this.btnRemoveScreenshot);
            this.panelScreenshot.Controls.Add(this.pictureBoxScreenshot);
            this.panelScreenshot.Location = new System.Drawing.Point(20, 420);
            this.panelScreenshot.Name = "panelScreenshot";
            this.panelScreenshot.Size = new System.Drawing.Size(540, 150);
            this.panelScreenshot.StateCommon.Color1 = System.Drawing.Color.FromArgb(((int)(((byte)(35)))), ((int)(((byte)(35)))), ((int)(((byte)(38)))));
            this.panelScreenshot.TabIndex = 9;
            // 
            // lblScreenshot
            // 
            this.lblScreenshot.Location = new System.Drawing.Point(10, 10);
            this.lblScreenshot.Name = "lblScreenshot";
            this.lblScreenshot.Size = new System.Drawing.Size(90, 24);
            this.lblScreenshot.TabIndex = 0;
            this.lblScreenshot.Values.Text = "Screenshot:";
            // 
            // btnTakeScreenshot
            // 
            this.btnTakeScreenshot.Location = new System.Drawing.Point(10, 35);
            this.btnTakeScreenshot.Name = "btnTakeScreenshot";
            this.btnTakeScreenshot.Size = new System.Drawing.Size(120, 30);
            this.btnTakeScreenshot.TabIndex = 1;
            this.btnTakeScreenshot.Values.DropDownArrowColor = System.Drawing.Color.Empty;
            this.btnTakeScreenshot.Values.Text = "Take Screenshot";
            this.btnTakeScreenshot.Click += new System.EventHandler(this.BtnTakeScreenshot_Click);
            // 
            // btnRemoveScreenshot
            // 
            this.btnRemoveScreenshot.Enabled = false;
            this.btnRemoveScreenshot.Location = new System.Drawing.Point(140, 35);
            this.btnRemoveScreenshot.Name = "btnRemoveScreenshot";
            this.btnRemoveScreenshot.Size = new System.Drawing.Size(80, 30);
            this.btnRemoveScreenshot.TabIndex = 2;
            this.btnRemoveScreenshot.Values.DropDownArrowColor = System.Drawing.Color.Empty;
            this.btnRemoveScreenshot.Values.Text = "Remove";
            this.btnRemoveScreenshot.Click += new System.EventHandler(this.BtnRemoveScreenshot_Click);
            // 
            // pictureBoxScreenshot
            // 
            this.pictureBoxScreenshot.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(25)))), ((int)(((byte)(25)))), ((int)(((byte)(28)))));
            this.pictureBoxScreenshot.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.pictureBoxScreenshot.Location = new System.Drawing.Point(230, 10);
            this.pictureBoxScreenshot.Name = "pictureBoxScreenshot";
            this.pictureBoxScreenshot.Size = new System.Drawing.Size(300, 130);
            this.pictureBoxScreenshot.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.pictureBoxScreenshot.TabIndex = 3;
            this.pictureBoxScreenshot.TabStop = false;
            // 
            // chkIncludeLog
            // 
            this.chkIncludeLog.Checked = true;
            this.chkIncludeLog.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkIncludeLog.Location = new System.Drawing.Point(20, 580);
            this.chkIncludeLog.Name = "chkIncludeLog";
            this.chkIncludeLog.Size = new System.Drawing.Size(134, 24);
            this.chkIncludeLog.TabIndex = 10;
            this.chkIncludeLog.Values.Text = "Include Log File";
            // 
            // chkIncludeSystemInfo
            // 
            this.chkIncludeSystemInfo.Checked = true;
            this.chkIncludeSystemInfo.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkIncludeSystemInfo.Location = new System.Drawing.Point(250, 580);
            this.chkIncludeSystemInfo.Name = "chkIncludeSystemInfo";
            this.chkIncludeSystemInfo.Size = new System.Drawing.Size(215, 24);
            this.chkIncludeSystemInfo.TabIndex = 11;
            this.chkIncludeSystemInfo.Values.Text = "Include System Information";
            // 
            // btnSubmit
            // 
            this.btnSubmit.Location = new System.Drawing.Point(350, 610);
            this.btnSubmit.Name = "btnSubmit";
            this.btnSubmit.Size = new System.Drawing.Size(130, 35);
            this.btnSubmit.TabIndex = 12;
            this.btnSubmit.Values.DropDownArrowColor = System.Drawing.Color.Empty;
            this.btnSubmit.Values.Text = "Submit Bug Report";
            this.btnSubmit.Click += new System.EventHandler(this.BtnSubmit_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.Location = new System.Drawing.Point(490, 610);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(70, 35);
            this.btnCancel.TabIndex = 13;
            this.btnCancel.Values.DropDownArrowColor = System.Drawing.Color.Empty;
            this.btnCancel.Values.Text = "Cancel";
            this.btnCancel.Click += new System.EventHandler(this.BtnCancel_Click);
            // 
            // BugSubmissionForm
            // 
            this.ClientSize = new System.Drawing.Size(586, 654);
            this.Controls.Add(this.panelMain);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "BugSubmissionForm";
            this.PaletteMode = Krypton.Toolkit.PaletteMode.Office2010Black;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Submit Bug Report";
            ((System.ComponentModel.ISupportInitialize)(this.panelMain)).EndInit();
            this.panelMain.ResumeLayout(false);
            this.panelMain.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.cmbSubmissionMethod)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.panelScreenshot)).EndInit();
            this.panelScreenshot.ResumeLayout(false);
            this.panelScreenshot.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBoxScreenshot)).EndInit();
            this.ResumeLayout(false);

        }

        /// <summary>
        /// Handles change in submission method
        /// </summary>
        private void CmbSubmissionMethod_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Adjust the UI based on the selected submission method
            // For example, GitHub might need a repository URL or API token
            if (cmbSubmissionMethod.SelectedIndex == 1) // GitHub
            {
                // Show GitHub-specific options if needed
                MessageBox.Show("To use GitHub issue submission, make sure your application has a public GitHub repository.",
                    "GitHub Submission", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>
        /// Takes a screenshot to include with the bug report
        /// </summary>
        private void BtnTakeScreenshot_Click(object sender, EventArgs e)
        {
            try
            {
                // Minimize the form temporarily
                this.WindowState = FormWindowState.Minimized;

                // Wait a moment to allow the form to minimize
                System.Threading.Thread.Sleep(500);

                // Take a screenshot of the entire screen
                Rectangle bounds = Screen.GetBounds(Point.Empty);
                using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(Point.Empty, Point.Empty, bounds.Size);
                    }

                    // Save the screenshot
                    screenshotImage = new Bitmap(bitmap);
                    pictureBoxScreenshot.Image = screenshotImage;
                    btnRemoveScreenshot.Enabled = true;
                }

                // Restore the form
                this.WindowState = FormWindowState.Normal;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error taking screenshot: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error);
                MessageBox.Show($"Error taking screenshot: {ex.Message}", "Screenshot Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Removes the current screenshot
        /// </summary>
        private void BtnRemoveScreenshot_Click(object sender, EventArgs e)
        {
            pictureBoxScreenshot.Image = null;
            screenshotImage?.Dispose();
            screenshotImage = null;
            btnRemoveScreenshot.Enabled = false;
        }

        /// <summary>
        /// Handles the Submit button click
        /// </summary>
        private async void BtnSubmit_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtDescription.Text))
            {
                MessageBox.Show("Please provide a bug description.", "Missing Information",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Disable the button and show progress
            btnSubmit.Enabled = false;
            btnSubmit.Text = "Submitting...";
            Application.DoEvents();

            try
            {
                bool success = false;

                // Choose submission method based on dropdown selection
                if (cmbSubmissionMethod.SelectedIndex == 0) // Email
                {
                    success = await SubmitViaEmailAsync();
                }
                else if (cmbSubmissionMethod.SelectedIndex == 1) // GitHub Issue
                {
                    success = await SubmitViaGitHubAsync();
                }

                if (success)
                {
                    MessageBox.Show("Bug report submitted successfully. Thank you for your feedback!",
                        "Report Submitted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    this.Close();
                }
                else
                {
                    MessageBox.Show("Failed to submit bug report. Please try again or use a different submission method.",
                        "Submission Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error submitting bug report: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error);
                MessageBox.Show($"Error submitting bug report: {ex.Message}\n\nPlease try again or contact support directly.",
                    "Submission Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnSubmit.Text = "Submit Bug Report";
                btnSubmit.Enabled = true;
            }
        }

        /// <summary>
        /// Submits the bug report via email
        /// </summary>
        private async Task<bool> SubmitViaEmailAsync()
        {
            

            try
            {
                // Prepare the email content
                string subject = "CTS Bug Report: " + txtDescription.Text.Substring(0, Math.Min(txtDescription.Text.Length, 50));

                StringBuilder emailBody = new StringBuilder();
                emailBody.AppendLine("Bug Description:");
                emailBody.AppendLine(txtDescription.Text);
                emailBody.AppendLine();

                emailBody.AppendLine("Steps to Reproduce:");
                emailBody.AppendLine(txtSteps.Text);
                emailBody.AppendLine();

                if (!string.IsNullOrWhiteSpace(txtContact.Text))
                {
                    emailBody.AppendLine("Contact Information:");
                    emailBody.AppendLine(txtContact.Text);
                    emailBody.AppendLine();
                }

                if (chkIncludeSystemInfo.Checked)
                {
                    emailBody.AppendLine("System Information:");
                    emailBody.AppendLine(GetSystemInfo());
                    emailBody.AppendLine();
                }

                // Create a temporary folder for attachments
                string tempDir = Path.Combine(Path.GetTempPath(), "CTS_BugReport_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(tempDir);

                List<string> attachmentPaths = new List<string>();

                // Save log file if requested
                if (chkIncludeLog.Checked)
                {
                    string logCopyPath = Path.Combine(tempDir, "log.txt");
                    File.Copy(Logger.LogFilePath, logCopyPath);
                    attachmentPaths.Add(logCopyPath);
                }

                // Save screenshot if available
                if (screenshotImage != null)
                {
                    string screenshotPath = Path.Combine(tempDir, "screenshot.png");
                    screenshotImage.Save(screenshotPath);
                    attachmentPaths.Add(screenshotPath);
                }

                
                // Otherwise, open the default email client

                string mailtoUrl = $"mailto:{BugReportEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(emailBody.ToString())}";
                Process.Start(mailtoUrl);

                // Provide instructions for attaching files
                if (attachmentPaths.Count > 0)
                {
                    StringBuilder attachmentInstructions = new StringBuilder();
                    attachmentInstructions.AppendLine("Please attach the following files to your email:");
                    foreach (string path in attachmentPaths)
                    {
                        attachmentInstructions.AppendLine($"- {path}");
                    }

                    MessageBox.Show(attachmentInstructions.ToString(), "Attach Files",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);

                    // Open the folder containing the files
                    Process.Start("explorer.exe", tempDir);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error creating email: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error);
                throw;
            }
        }

        /// <summary>
        /// Submits the bug report via GitHub Issues API
        /// </summary>
        private async Task<bool> SubmitViaGitHubAsync()
        {
            string repoOwner = "mattemangia";
            string repoName = "CTS";
            string apiToken = "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";

            // Build issue data
            var issueData = new
            {
                title = "Bug Report: " + (txtDescription.Text.Length > 50
                    ? txtDescription.Text.Substring(0, 50)
                    : txtDescription.Text),
                body = GetFormattedGitHubIssue()
            };
            string jsonData = System.Text.Json.JsonSerializer.Serialize(issueData);

            // Log the payload
            Logger.Log($"[GitHub] Prepared issue JSON: {jsonData}", Microsoft.Extensions.Logging.LogLevel.Debug);

            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "CTS-Bug-Reporter");
                    client.DefaultRequestHeaders.Add("Authorization", $"token {apiToken}");
                    client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

                    var url = $"https://api.github.com/repos/{repoOwner}/{repoName}/issues";
                    var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

                    // Log the endpoint being called
                    Logger.Log($"[GitHub] POST {url}", Microsoft.Extensions.Logging.LogLevel.Debug);

                    var response = await client.PostAsync(url, content);

                    // Log status code and reason
                    Logger.Log(
                        $"[GitHub] Response status: {(int)response.StatusCode} {response.ReasonPhrase}",
                        Microsoft.Extensions.Logging.LogLevel.Information
                    );

                    var respBody = await response.Content.ReadAsStringAsync();

                    // Log the raw response body
                    Logger.Log($"[GitHub] Response body: {respBody}", Microsoft.Extensions.Logging.LogLevel.Debug);

                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                    else
                    {
                        // Log the error path
                        Logger.Log($"[GitHub] API error: {respBody}", Microsoft.Extensions.Logging.LogLevel.Error);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                // Log any unexpected exception
                Logger.Log($"[GitHub] Exception submitting issue: {ex}", Microsoft.Extensions.Logging.LogLevel.Error);
                throw;
            }
        }

        /// <summary>
        /// Formats the bug report into GitHub markdown format
        /// </summary>
        private string GetFormattedGitHubIssue()
        {
            StringBuilder issueBody = new StringBuilder();

            // Bug description
            issueBody.AppendLine("## Bug Description");
            issueBody.AppendLine(txtDescription.Text);
            issueBody.AppendLine();

            // Steps to reproduce
            issueBody.AppendLine("## Steps to Reproduce");
            issueBody.AppendLine(txtSteps.Text);
            issueBody.AppendLine();

            // Contact information (if provided)
            if (!string.IsNullOrWhiteSpace(txtContact.Text))
            {
                issueBody.AppendLine("## Reporter Contact");
                issueBody.AppendLine(txtContact.Text);
                issueBody.AppendLine();
            }

            // System information (if requested)
            if (chkIncludeSystemInfo.Checked)
            {
                issueBody.AppendLine("## System Information");
                issueBody.AppendLine("```");
                issueBody.AppendLine(GetSystemInfo());
                issueBody.AppendLine("```");
                issueBody.AppendLine();
            }

            // Add note about log file and screenshot
            issueBody.AppendLine("## Additional Information");

            if (chkIncludeLog.Checked)
            {
                issueBody.AppendLine("- Log file is available but cannot be attached directly to GitHub issues via API");
            }

            if (screenshotImage != null)
            {
                issueBody.AppendLine("- Screenshot is available but cannot be attached directly to GitHub issues via API");
            }

            return issueBody.ToString();
        }

        /// <summary>
        /// Gathers system information for the bug report
        /// </summary>
        private string GetSystemInfo()
        {
            StringBuilder info = new StringBuilder();
            try
            {
                info.AppendLine($"OS Version: {Environment.OSVersion}");
                info.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
                info.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
                info.AppendLine($"System Directory: {Environment.SystemDirectory}");
                info.AppendLine($"Processor Count: {Environment.ProcessorCount}");
                info.AppendLine($"CLR Version: {Environment.Version}");
                info.AppendLine($"Working Set: {Environment.WorkingSet / (1024 * 1024)} MB");

                // Add any application-specific information
                var process = Process.GetCurrentProcess();
                info.AppendLine($"Process Name: {process.ProcessName}");
                info.AppendLine($"Memory Usage: {process.WorkingSet64 / (1024 * 1024)} MB");
                info.AppendLine($"Start Time: {process.StartTime}");
                info.AppendLine($"Threads: {process.Threads.Count}");

                // Graphics info
                using (Graphics g = CreateGraphics())
                {
                    info.AppendLine($"Screen DPI: {g.DpiX}x{g.DpiY}");
                }

                foreach (Screen screen in Screen.AllScreens)
                {
                    info.AppendLine($"Screen: {screen.DeviceName}, " +
                        $"Resolution: {screen.Bounds.Width}x{screen.Bounds.Height}, " +
                        $"Primary: {screen.Primary}");
                }
            }
            catch (Exception ex)
            {
                info.AppendLine($"Error gathering system info: {ex.Message}");
            }

            return info.ToString();
        }

        /// <summary>
        /// Handles the Cancel button click
        /// </summary>
        private void BtnCancel_Click(object sender, EventArgs e)
        {
            // Clean up any resources
            if (screenshotImage != null)
            {
                screenshotImage.Dispose();
                screenshotImage = null;
            }

            this.Close();
        }

        /// <summary>
        /// Opens the bug submission form as a dialog
        /// </summary>
        public static void ShowBugReportDialog()
        {
            using (BugSubmissionForm form = new BugSubmissionForm())
            {
                form.ShowDialog();
            }
        }
    }
}