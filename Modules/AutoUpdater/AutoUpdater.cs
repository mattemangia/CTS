//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.AutoUpdater
{
    /// <summary>
    /// Handles checking for application updates and downloading them from GitHub
    /// </summary>
    public class AutoUpdater
    {
        // GitHub repository information
        private const string GITHUB_USERNAME = "mattemangia";
        private const string GITHUB_REPO = "CTS";

        // GitHub API URLs for releases
        private const string GITHUB_API_RELEASES = "https://api.github.com/repos/{0}/{1}/releases/latest";

        // Temporary folder for downloads
        private readonly string tempFolder;

        // Current application version
        private readonly Version currentVersion;

        // Event to notify about update progress
        public event EventHandler<UpdateProgressEventArgs> UpdateProgressChanged;

        /// <summary>
        /// Creates a new instance of the AutoUpdater
        /// </summary>
        public AutoUpdater()
        {
            // Get the current application version from assembly
            currentVersion = Assembly.GetExecutingAssembly().GetName().Version;

            // Create temp folder for downloads
            tempFolder = Path.Combine(Path.GetTempPath(), "CTSUpdater");
            Directory.CreateDirectory(tempFolder);
        }

        /// <summary>
        /// Checks if there's a newer version available
        /// </summary>
        /// <returns>Update info if available, null otherwise</returns>
        public async Task<UpdateInfo> CheckForUpdateAsync()
        {
            try
            {
                // Configure HttpClient with appropriate headers for GitHub API
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "CTS-Updater");

                    // Get the latest release information from GitHub
                    string apiUrl = string.Format(GITHUB_API_RELEASES, GITHUB_USERNAME, GITHUB_REPO);
                    HttpResponseMessage response = await client.GetAsync(apiUrl);

                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();

                        // Parse GitHub release JSON
                        using (JsonDocument document = JsonDocument.Parse(json))
                        {
                            JsonElement root = document.RootElement;

                            // Extract version tag (remove 'v' prefix if present)
                            string versionTag = root.GetProperty("tag_name").GetString();
                            if (versionTag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                                versionTag = versionTag.Substring(1);

                            // Parse version
                            Version newVersion = Version.Parse(versionTag);

                            // If new version is greater than current
                            if (newVersion > currentVersion)
                            {
                                // Get release assets
                                JsonElement assets = root.GetProperty("assets");
                                string updaterAssetUrl = null;
                                string fullPackageUrl = null;

                                // Look for updater and full package URLs
                                foreach (JsonElement asset in assets.EnumerateArray())
                                {
                                    string name = asset.GetProperty("name").GetString();
                                    string url = asset.GetProperty("browser_download_url").GetString();

                                    if (name.IndexOf("updater", StringComparison.OrdinalIgnoreCase) >= 0)
                                        updaterAssetUrl = url;
                                    else if (name.IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0)
                                        fullPackageUrl = url;
                                }

                                // Return update info
                                return new UpdateInfo
                                {
                                    CurrentVersion = currentVersion.ToString(),
                                    NewVersion = newVersion.ToString(),
                                    ReleaseNotes = root.GetProperty("body").GetString(),
                                    UpdaterUrl = updaterAssetUrl,
                                    FullPackageUrl = fullPackageUrl
                                };
                            }
                        }
                    }
                }

                // No update available
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AutoUpdater] Error checking for updates: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Downloads the update file
        /// </summary>
        /// <param name="updateInfo">Update information</param>
        /// <param name="useFullPackage">Whether to download the full package or just the updater</param>
        /// <returns>Path to the downloaded file</returns>
        public async Task<string> DownloadUpdateAsync(UpdateInfo updateInfo, bool useFullPackage = false)
        {
            string url = useFullPackage ? updateInfo.FullPackageUrl : updateInfo.UpdaterUrl;
            if (string.IsNullOrEmpty(url))
                throw new InvalidOperationException("Update URL is not available.");

            string fileName = Path.GetFileName(url);
            string filePath = Path.Combine(tempFolder, fileName);

            try
            {
                using (WebClient webClient = new WebClient())
                {
                    // Report progress
                    webClient.DownloadProgressChanged += (s, e) =>
                    {
                        UpdateProgressChanged?.Invoke(this, new UpdateProgressEventArgs
                        {
                            ProgressPercentage = e.ProgressPercentage,
                            BytesReceived = e.BytesReceived,
                            TotalBytesToReceive = e.TotalBytesToReceive
                        });
                    };

                    // Download file
                    await webClient.DownloadFileTaskAsync(url, filePath);
                }

                return filePath;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AutoUpdater] Error downloading update: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Installs the update by running the downloaded updater
        /// </summary>
        /// <param name="updaterPath">Path to the updater executable</param>
        public void InstallUpdate(string updaterPath)
        {
            try
            {
                // Start the updater process
                Process.Start(new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = $"--app-path \"{Application.ExecutablePath}\"",
                    UseShellExecute = true
                });

                // Exit the current application to allow the updater to work
                Application.Exit();
            }
            catch (Exception ex)
            {
                Logger.Log($"[AutoUpdater] Error starting updater: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Contains information about an available update
    /// </summary>
    public class UpdateInfo
    {
        public string CurrentVersion { get; set; }
        public string NewVersion { get; set; }
        public string ReleaseNotes { get; set; }
        public string UpdaterUrl { get; set; }
        public string FullPackageUrl { get; set; }
    }

    /// <summary>
    /// Event arguments for update progress
    /// </summary>
    public class UpdateProgressEventArgs : EventArgs
    {
        public int ProgressPercentage { get; set; }
        public long BytesReceived { get; set; }
        public long TotalBytesToReceive { get; set; }
    }
}