using System;
using System.ComponentModel;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace CTS.Modules.AutoUpdater
{
    /// <summary>
    /// Form to display update progress and information
    /// </summary>
    public partial class UpdateProgressForm : KryptonForm
    {
        private AutoUpdater updater;
        private UpdateInfo updateInfo;
        private bool isDownloading = false;

        /// <summary>
        /// Creates a new update progress form
        /// </summary>
        public UpdateProgressForm()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Initializes the component
        /// </summary>
        private void InitializeComponent()
        {
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }
            this.Text = "CTS Update";
            this.Size = new System.Drawing.Size(450, 300);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.PaletteMode = PaletteMode.Office2010Black;
            this.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
            this.ForeColor = System.Drawing.Color.Gainsboro;

            // Version label
            lblVersion = new KryptonLabel();
            lblVersion.Location = new System.Drawing.Point(20, 20);
            lblVersion.AutoSize = true;
            lblVersion.StateCommon.ShortText.Color1 = System.Drawing.Color.White;
            lblVersion.Text = "Checking for updates...";
            this.Controls.Add(lblVersion);

            // Release notes label
            lblNotes = new KryptonLabel();
            lblNotes.Location = new System.Drawing.Point(20, 50);
            lblNotes.AutoSize = true;
            lblNotes.StateCommon.ShortText.Color1 = System.Drawing.Color.White;
            lblNotes.Text = "Release Notes:";
            this.Controls.Add(lblNotes);

            // Release notes text box
            txtReleaseNotes = new KryptonRichTextBox();
            txtReleaseNotes.Location = new System.Drawing.Point(20, 75);
            txtReleaseNotes.Size = new System.Drawing.Size(400, 120);
            txtReleaseNotes.ReadOnly = true;
            txtReleaseNotes.StateCommon.Back.Color1 = System.Drawing.Color.FromArgb(30, 30, 30);
            txtReleaseNotes.StateCommon.Content.Color1 = System.Drawing.Color.White;
            this.Controls.Add(txtReleaseNotes);

            // Progress bar
            progressBar = new KryptonProgressBar();
            progressBar.Location = new System.Drawing.Point(20, 210);
            progressBar.Size = new System.Drawing.Size(400, 20);
            progressBar.StateCommon.Back.Color1 = System.Drawing.Color.FromArgb(30, 30, 30);
            progressBar.Visible = false;
            this.Controls.Add(progressBar);

            // Progress label
            lblProgress = new KryptonLabel();
            lblProgress.Location = new System.Drawing.Point(20, 235);
            lblProgress.AutoSize = true;
            lblProgress.StateCommon.ShortText.Color1 = System.Drawing.Color.White;
            lblProgress.Visible = false;
            this.Controls.Add(lblProgress);

            // Update button
            btnUpdate = new KryptonButton();
            btnUpdate.Location = new System.Drawing.Point(230, 210);
            btnUpdate.Size = new System.Drawing.Size(90, 30);
            btnUpdate.Text = "Update";
            btnUpdate.Enabled = false;
            btnUpdate.Click += btnUpdate_Click;
            this.Controls.Add(btnUpdate);

            // Close button
            btnClose = new KryptonButton();
            btnClose.Location = new System.Drawing.Point(330, 210);
            btnClose.Size = new System.Drawing.Size(90, 30);
            btnClose.Text = "Close";
            btnClose.Click += btnClose_Click;
            this.Controls.Add(btnClose);

            // Full package checkbox
            chkFullPackage = new KryptonCheckBox();
            chkFullPackage.Location = new System.Drawing.Point(20, 210);
            chkFullPackage.Text = "Download full package";
            chkFullPackage.CheckedChanged += chkFullPackage_CheckedChanged;
            chkFullPackage.StateCommon.ShortText.Color1 = System.Drawing.Color.White;
            this.Controls.Add(chkFullPackage);
        }

        /// <summary>
        /// Check for updates when the form loads
        /// </summary>
        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            updater = new AutoUpdater();
            updater.UpdateProgressChanged += Updater_UpdateProgressChanged;

            // Check for updates
            updateInfo = await updater.CheckForUpdateAsync();

            if (updateInfo != null)
            {
                // Update available
                lblVersion.Text = $"Current version: {updateInfo.CurrentVersion} - New version: {updateInfo.NewVersion}";
                txtReleaseNotes.Text = updateInfo.ReleaseNotes;
                btnUpdate.Enabled = true;

                // Show full package option only if URL is available
                chkFullPackage.Visible = !string.IsNullOrEmpty(updateInfo.FullPackageUrl);
            }
            else
            {
                // No update available
                lblVersion.Text = "You are running the latest version.";
                lblNotes.Visible = false;
                txtReleaseNotes.Visible = false;
                chkFullPackage.Visible = false;
            }
        }

        /// <summary>
        /// Handle update progress
        /// </summary>
        private void Updater_UpdateProgressChanged(object sender, UpdateProgressEventArgs e)
        {
            // Ensure UI updates happen on the UI thread
            if (InvokeRequired)
            {
                Invoke(new EventHandler<UpdateProgressEventArgs>(Updater_UpdateProgressChanged), sender, e);
                return;
            }

            progressBar.Value = e.ProgressPercentage;
            lblProgress.Text = $"Downloaded: {FormatFileSize(e.BytesReceived)} of {FormatFileSize(e.TotalBytesToReceive)} ({e.ProgressPercentage}%)";
        }

        /// <summary>
        /// Format file size for display
        /// </summary>
        private string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB" };
            int suffixIndex = 0;
            double size = bytes;

            while (size >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024;
                suffixIndex++;
            }

            return $"{size:0.##} {suffixes[suffixIndex]}";
        }

        /// <summary>
        /// Handle update button click
        /// </summary>
        private async void btnUpdate_Click(object sender, EventArgs e)
        {
            if (isDownloading)
                return;

            try
            {
                isDownloading = true;
                bool useFullPackage = chkFullPackage.Checked;

                // Update UI for downloading
                progressBar.Visible = true;
                lblProgress.Visible = true;
                btnUpdate.Enabled = false;
                btnClose.Enabled = false;
                chkFullPackage.Enabled = false;

                // Download the update
                string updaterPath = await updater.DownloadUpdateAsync(updateInfo, useFullPackage);

                // Show success message
                MessageBox.Show(
                    "Update downloaded successfully. The application will now close and the update will be installed.",
                    "Update Ready",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                // Install the update
                updater.InstallUpdate(updaterPath);
            }
            catch (Exception ex)
            {
                isDownloading = false;
                MessageBox.Show(
                    $"Error downloading update: {ex.Message}",
                    "Update Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                // Reset UI
                progressBar.Visible = false;
                lblProgress.Visible = false;
                btnUpdate.Enabled = true;
                btnClose.Enabled = true;
                chkFullPackage.Enabled = true;
            }
        }

        /// <summary>
        /// Handle close button click
        /// </summary>
        private void btnClose_Click(object sender, EventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Handle full package checkbox change
        /// </summary>
        private void chkFullPackage_CheckedChanged(object sender, EventArgs e)
        {
            // Add tooltip or information about what full package means
            if (chkFullPackage.Checked)
            {
                MessageBox.Show(
                    "The full package includes all dependencies including ONNX packages. " +
                    "Use this option if you're experiencing issues with the standard update.",
                    "Full Package Information",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        // UI controls
        private KryptonLabel lblVersion;
        private KryptonLabel lblNotes;
        private KryptonRichTextBox txtReleaseNotes;
        private KryptonProgressBar progressBar;
        private KryptonLabel lblProgress;
        private KryptonButton btnUpdate;
        private KryptonButton btnClose;
        private KryptonCheckBox chkFullPackage;
    }
}