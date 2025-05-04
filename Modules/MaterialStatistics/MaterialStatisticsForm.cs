using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

// Make sure to add reference to System.Windows.Forms.DataVisualization.dll in project

namespace CTS
{
    /// <summary>
    /// Form to display and calculate material statistics
    /// </summary>
    public partial class MaterialStatisticsForm : Form
    {
        private MainForm mainForm;
        private List<MaterialStatistics> statistics;
        private long totalVoxels;
        private ChartType currentChartType = ChartType.Pie;

        private enum ChartType
        {
            Pie,
            Bar
        }

        /// <summary>
        /// Creates a new MaterialStatisticsForm
        /// </summary>
        /// <param name="form">Reference to the MainForm</param>
        public MaterialStatisticsForm(MainForm form)
        {
            mainForm = form;
            InitializeComponent();
        }

        private void MaterialStatisticsForm_Load(object sender, EventArgs e)
        {
            // Initialize the form
            Text = "Material Statistics";
            Icon = mainForm.Icon;

            // Start calculating statistics
            CalculateStatisticsAsync();
        }

        private async void CalculateStatisticsAsync()
        {
            // Show progress indicator
            progressBar.Visible = true;
            lblStatus.Text = "Calculating material statistics...";
            btnExportTable.Enabled = false;
            btnExportChart.Enabled = false;

            // Set up progress reporting
            var progress = new Progress<int>(percent =>
            {
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Value = percent;
                lblStatus.Text = $"Calculating material statistics... {percent}%";
            });

            try
            {
                // Calculate statistics in a background thread
                statistics = await Task.Run(() => CalculateStatistics(progress));

                // Update the UI with the results
                UpdateDataGridView();
                UpdateChart();

                // Enable export buttons
                btnExportTable.Enabled = true;
                btnExportChart.Enabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error calculating statistics: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[MaterialStatisticsForm] Error calculating statistics: {ex.Message}");
            }
            finally
            {
                // Hide progress indicator
                progressBar.Visible = false;
                lblStatus.Text = "Statistics calculation complete";
            }
        }

        private List<MaterialStatistics> CalculateStatistics(IProgress<int> progress = null)
        {
            List<MaterialStatistics> results = new List<MaterialStatistics>();
            totalVoxels = 0;

            try
            {
                // Get volume dimensions
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();
                double pixelSize = mainForm.GetPixelSize();

                Logger.Log($"[MaterialStatisticsForm] Calculating statistics for volume: {width}x{height}x{depth}, pixel size: {pixelSize}m");

                // Create statistics objects for each material
                Dictionary<byte, MaterialStatistics> materialStats = new Dictionary<byte, MaterialStatistics>();
                foreach (Material material in mainForm.Materials)
                {
                    materialStats[material.ID] = new MaterialStatistics(material);
                }

                // Calculate total voxels in the volume
                totalVoxels = (long)width * height * depth;

                // Create counter for each material ID
                Dictionary<byte, long> voxelCounts = new Dictionary<byte, long>();
                foreach (byte id in materialStats.Keys)
                {
                    voxelCounts[id] = 0;
                }

                // Use parallel processing to count voxels for each material
                int completedSlices = 0;
                object lockObj = new object();

                Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, z =>
                {
                    Dictionary<byte, long> localCounts = new Dictionary<byte, long>();
                    foreach (byte id in materialStats.Keys)
                    {
                        localCounts[id] = 0;
                    }

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            byte materialId = mainForm.volumeLabels[x, y, z];
                            if (localCounts.ContainsKey(materialId))
                            {
                                localCounts[materialId]++;
                            }
                        }
                    }

                    // Merge local counts into global counts
                    lock (voxelCounts)
                    {
                        foreach (var kvp in localCounts)
                        {
                            voxelCounts[kvp.Key] += kvp.Value;
                        }
                    }

                    // Update progress
                    if (progress != null)
                    {
                        lock (lockObj)
                        {
                            completedSlices++;
                            int percent = (int)((completedSlices / (float)depth) * 95); // Go up to 95%
                            progress.Report(percent);
                        }
                    }
                });

                // Calculate volume and percentage for each material
                foreach (var kvp in voxelCounts)
                {
                    byte materialId = kvp.Key;
                    long voxelCount = kvp.Value;

                    if (materialStats.TryGetValue(materialId, out MaterialStatistics stats))
                    {
                        stats.VoxelCount = voxelCount;
                        stats.CalculateVolumes(pixelSize);
                        stats.VolumePercentage = (double)voxelCount / totalVoxels * 100;

                        results.Add(stats);

                        Logger.Log($"[MaterialStatisticsForm] Material {stats.Material.Name} (ID: {materialId}): " +
                            $"{stats.VoxelCount} voxels, {stats.VolumePercentage:0.00}% of volume");
                    }
                }

                // Sort results by material ID (Exterior first, then others)
                results = results.OrderBy(s => s.Material.ID).ToList();

                // Report 100% done
                progress?.Report(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"[MaterialStatisticsForm] Error in statistics calculation: {ex.Message}");
                throw;
            }

            return results;
        }

        private void UpdateDataGridView()
        {
            // Create DataTable for grid
            DataTable dt = new DataTable();
            dt.Columns.Add("Material", typeof(string));
            dt.Columns.Add("Voxels", typeof(long));
            dt.Columns.Add("Volume (µm³)", typeof(string));
            dt.Columns.Add("Volume (mm³)", typeof(string));
            dt.Columns.Add("Volume (cm³)", typeof(string));
            dt.Columns.Add("Percentage", typeof(string));

            // Add rows for each material
            foreach (var stats in statistics)
            {
                dt.Rows.Add(
                    stats.Material.Name,
                    stats.VoxelCount,
                    stats.VolumeUm3.ToString("N2", CultureInfo.InvariantCulture),
                    stats.VolumeMm3.ToString("N6", CultureInfo.InvariantCulture),
                    stats.VolumeCm3.ToString("N9", CultureInfo.InvariantCulture),
                    stats.VolumePercentage.ToString("0.00") + "%"
                );
            }

            // Bind DataTable to grid
            dataGridView.DataSource = dt;

            // Format columns
            foreach (DataGridViewColumn col in dataGridView.Columns)
            {
                col.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
            }
        }

        private void UpdateChart()
        {
            // Clear existing series
            chart.Series.Clear();
            chart.Titles.Clear();

            // Create title
            chart.Titles.Add("Material Volume Distribution");

            // Reset hover highlighting
            chart.Annotations.Clear();

            // Create series based on current chart type
            Series series = new Series();

            if (currentChartType == ChartType.Pie)
            {
                // Configure pie chart
                series.ChartType = SeriesChartType.Pie;
                chart.ChartAreas[0].Area3DStyle.Enable3D = true;

                // Add data points for each material
                foreach (var stats in statistics)
                {
                    // Skip materials with negligible percentage
                    if (stats.VolumePercentage < 0.01)
                        continue;

                    DataPoint dp = new DataPoint();
                    dp.YValues = new double[] { stats.VolumePercentage };

                    // Include percentage in the label
                    dp.Label = $"{stats.Material.Name}: {stats.VolumePercentage:0.00}%";
                    dp.LegendText = stats.Material.Name;
                    dp.Color = stats.Material.Color;
                    dp.ToolTip = $"{stats.Material.Name}: {stats.VolumePercentage:0.00}% ({stats.VolumeMm3:0.000} mm³)";

                    // Add darker border to make transparent or light colors visible
                    dp.BorderWidth = 2;
                    dp.BorderColor = GetBorderColor(stats.Material.Color);

                    series.Points.Add(dp);
                }

                // Configure series to show borders
                series.BorderWidth = 1;
                series.BorderColor = Color.Black;

                // Configure label style
                series.Font = new Font("Arial", 9, FontStyle.Bold);
                series.LabelForeColor = Color.Black;
                series["PieLabelStyle"] = "Outside"; // Place labels outside the pie
                series["PieLineColor"] = "Black";    // Color of the line connecting slice to label
            }
            else // Bar chart
            {
                // Set the series name to blank to avoid "Series1" in legend
                series.Name = string.Empty;

                // Configure bar chart
                series.ChartType = SeriesChartType.Column;
                chart.ChartAreas[0].Area3DStyle.Enable3D = false;

                // Configure axes
                chart.ChartAreas[0].AxisX.Title = "Material";
                chart.ChartAreas[0].AxisY.Title = "Percentage";
                chart.ChartAreas[0].AxisY.LabelStyle.Format = "0.00%";

                // Fix Y-axis scale to appropriate range (0-100%)
                chart.ChartAreas[0].AxisY.Minimum = 0;
                chart.ChartAreas[0].AxisY.Maximum = 1.0; // 100% as decimal
                chart.ChartAreas[0].AxisY.Interval = 0.1; // 10% intervals

                // Make gridlines more transparent
                chart.ChartAreas[0].AxisX.MajorGrid.LineColor = Color.FromArgb(40, 0, 0, 0);
                chart.ChartAreas[0].AxisY.MajorGrid.LineColor = Color.FromArgb(40, 0, 0, 0);

                // Add data points for each material
                foreach (var stats in statistics)
                {
                    DataPoint dp = new DataPoint();
                    dp.AxisLabel = stats.Material.Name;
                    dp.YValues = new double[] { stats.VolumePercentage / 100 }; // Convert to decimal for percentage format
                    dp.Color = stats.Material.Color;
                    dp.Label = $"{stats.VolumePercentage:0.00}%"; // Add percentage label on top of each bar
                    dp.ToolTip = $"{stats.Material.Name}: {stats.VolumePercentage:0.00}% ({stats.VolumeMm3:0.000} mm³)";
                    dp.LegendText = stats.Material.Name; // Set legend text for each point

                    // Add darker border to make transparent or light colors visible
                    dp.BorderWidth = 2;
                    dp.BorderColor = GetBorderColor(stats.Material.Color);

                    series.Points.Add(dp);
                }

                // Configure series to show borders and labels
                series.BorderWidth = 1;
                series.BorderColor = Color.Black;
                series.IsValueShownAsLabel = true;
                series.Font = new Font("Arial", 8, FontStyle.Bold);
                series.LabelForeColor = Color.Black;

                // Disable legend for bar chart as we have clear labels
                chart.Legends[0].Enabled = false;
            }

            // Add the series to the chart
            chart.Series.Add(series);

            // Show legend for pie chart only
            if (currentChartType == ChartType.Pie)
            {
                chart.Legends[0].Enabled = true;
                chart.Legends[0].Docking = Docking.Bottom;
            }
        }

        /// <summary>
        /// Gets an appropriate border color for a given material color
        /// </summary>
        private Color GetBorderColor(Color color)
        {
            // For transparent or very light colors, use a dark gray border
            if (color.A < 128 || (color.GetBrightness() > 0.8))
            {
                return Color.FromArgb(64, 64, 64); // Dark gray
            }

            // For normal colors, darken the original color for the border
            return ControlPaint.Dark(color);
        }

        private void rbPieChart_CheckedChanged(object sender, EventArgs e)
        {
            if (rbPieChart.Checked)
            {
                currentChartType = ChartType.Pie;
                UpdateChart();
            }
        }

        private void rbBarChart_CheckedChanged(object sender, EventArgs e)
        {
            if (rbBarChart.Checked)
            {
                currentChartType = ChartType.Bar;
                UpdateChart();
            }
        }

        private void btnExportTable_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "CSV Files (*.csv)|*.csv|Excel Files (*.xlsx)|*.xlsx";
                saveDialog.Title = "Export Statistics Table";
                saveDialog.FileName = "MaterialStatistics";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string extension = Path.GetExtension(saveDialog.FileName).ToLower();

                        if (extension == ".csv")
                        {
                            ExportToCSV(saveDialog.FileName);
                        }
                        else if (extension == ".xlsx")
                        {
                            ExportToExcel(saveDialog.FileName);
                        }

                        MessageBox.Show($"Statistics exported to {saveDialog.FileName}", "Export Successful",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting statistics: {ex.Message}", "Export Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ExportToCSV(string fileName)
        {
            StringBuilder csv = new StringBuilder();

            // Add headers
            csv.AppendLine("Material,Voxels,Volume (µm³),Volume (mm³),Volume (cm³),Percentage");

            // Add data rows
            foreach (var stats in statistics)
            {
                csv.AppendLine($"{stats.Material.Name},{stats.VoxelCount},{stats.VolumeUm3.ToString(CultureInfo.InvariantCulture)}," +
                    $"{stats.VolumeMm3.ToString(CultureInfo.InvariantCulture)},{stats.VolumeCm3.ToString(CultureInfo.InvariantCulture)}," +
                    $"{stats.VolumePercentage.ToString(CultureInfo.InvariantCulture)}");
            }

            // Write to file
            File.WriteAllText(fileName, csv.ToString());
        }

        private void ExportToExcel(string fileName)
        {
            // Always create a CSV version as fallback
            string csvFileName = fileName.Replace(".xlsx", ".csv");
            ExportToCSV(csvFileName);

            try
            {
                // Try to use Excel COM automation through reflection (no direct reference)
                Type excelType = Type.GetTypeFromProgID("Excel.Application");
                if (excelType != null)
                {
                    // Create Excel objects through reflection to avoid dynamic
                    object excelApp = Activator.CreateInstance(excelType);

                    try
                    {
                        // Set Excel application to be invisible
                        excelType.InvokeMember("Visible",
                            System.Reflection.BindingFlags.SetProperty,
                            null, excelApp, new object[] { false });

                        // Get Workbooks collection
                        object workbooks = excelType.InvokeMember("Workbooks",
                            System.Reflection.BindingFlags.GetProperty,
                            null, excelApp, null);

                        // Get the Open method
                        Type workbooksType = workbooks.GetType();
                        object workbook = workbooksType.InvokeMember("Open",
                            System.Reflection.BindingFlags.InvokeMethod,
                            null, workbooks, new object[] { csvFileName });

                        // Save As xlsx (51 = xlOpenXMLWorkbook)
                        Type workbookType = workbook.GetType();
                        workbookType.InvokeMember("SaveAs",
                            System.Reflection.BindingFlags.InvokeMethod,
                            null, workbook, new object[] { fileName, 51 });

                        // Close workbook
                        workbookType.InvokeMember("Close",
                            System.Reflection.BindingFlags.InvokeMethod,
                            null, workbook, new object[] { });

                        // Quit Excel
                        excelType.InvokeMember("Quit",
                            System.Reflection.BindingFlags.InvokeMethod,
                            null, excelApp, null);

                        // Delete the CSV file
                        try
                        {
                            if (File.Exists(csvFileName))
                                File.Delete(csvFileName);
                        }
                        catch
                        {
                            // Ignore errors when deleting temporary file
                        }

                        // Success - we're done
                        return;
                    }
                    catch
                    {
                        // Clean up on error by attempting to close Excel
                        try
                        {
                            excelType.InvokeMember("Quit",
                                System.Reflection.BindingFlags.InvokeMethod,
                                null, excelApp, null);
                        }
                        catch { /* Ignore cleanup errors */ }

                        // Fall through to the message below
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[MaterialStatisticsForm] Excel export error: {ex.Message}");
            }

            // If we get here, we couldn't create an Excel file but the CSV exists
            MessageBox.Show($"Could not create Excel file. Data exported as CSV to {csvFileName}",
                "Excel Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void btnExportChart_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg|SVG Image (*.svg)|*.svg";
                saveDialog.Title = "Export Chart";
                saveDialog.FileName = "MaterialStatisticsChart";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string extension = Path.GetExtension(saveDialog.FileName).ToLower();

                        if (extension == ".png")
                        {
                            chart.SaveImage(saveDialog.FileName, ChartImageFormat.Png);
                        }
                        else if (extension == ".jpg")
                        {
                            chart.SaveImage(saveDialog.FileName, ChartImageFormat.Jpeg);
                        }
                        else if (extension == ".svg")
                        {
                            try
                            {
                                // Try to create a basic SVG file from the chart
                                ExportChartToSVG(saveDialog.FileName);
                            }
                            catch (Exception ex)
                            {
                                // Log the error
                                Logger.Log($"[MaterialStatisticsForm] SVG export error: {ex.Message}");

                                // Fall back to PNG if SVG export fails
                                string pngFileName = saveDialog.FileName.Replace(".svg", ".png");
                                chart.SaveImage(pngFileName, ChartImageFormat.Png);
                                MessageBox.Show($"SVG export failed. Image saved as PNG to {pngFileName}",
                                    "Format Conversion", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                        }

                        MessageBox.Show($"Chart exported to {saveDialog.FileName}", "Export Successful",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting chart: {ex.Message}", "Export Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ExportChartToSVG(string fileName)
        {
            // Create a basic SVG representation of the chart
            StringBuilder svg = new StringBuilder();

            // SVG header
            svg.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>");
            svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"800\" height=\"600\" viewBox=\"0 0 800 600\">");

            // Add title
            svg.AppendLine("<title>Material Statistics</title>");

            // If pie chart
            if (currentChartType == ChartType.Pie)
            {
                // Draw pie chart
                double totalAngle = 0;
                double centerX = 400;
                double centerY = 300;
                double radius = 200;

                // Add each pie slice
                foreach (var stats in statistics.Where(s => s.VolumePercentage >= 0.01))
                {
                    double percentage = stats.VolumePercentage / 100.0;
                    double angle = percentage * 2 * Math.PI;
                    double startAngle = totalAngle;
                    double endAngle = totalAngle + angle;

                    // Calculate arc points
                    double x1 = centerX + radius * Math.Cos(startAngle);
                    double y1 = centerY + radius * Math.Sin(startAngle);
                    double x2 = centerX + radius * Math.Cos(endAngle);
                    double y2 = centerY + radius * Math.Sin(endAngle);

                    // Determine if the arc is large (> 180 degrees)
                    int largeArcFlag = (angle > Math.PI) ? 1 : 0;

                    // Create SVG path for the pie slice
                    string path = $"M {centerX},{centerY} L {x1},{y1} A {radius},{radius} 0 {largeArcFlag},1 {x2},{y2} Z";

                    // Convert Color to HTML hex
                    string colorHex = $"#{stats.Material.Color.R:X2}{stats.Material.Color.G:X2}{stats.Material.Color.B:X2}";

                    // Determine opacity based on alpha
                    double opacity = stats.Material.Color.A / 255.0;
                    if (opacity < 0.3)
                        opacity = 0.3; // Minimum opacity for visibility

                    // Add the path with material color and name as title
                    svg.AppendLine($"<path d=\"{path}\" fill=\"{colorHex}\" stroke=\"#333333\" stroke-width=\"2\" fill-opacity=\"{opacity:0.0}\">");
                    svg.AppendLine($"<title>{stats.Material.Name}: {stats.VolumePercentage:0.00}%</title>");
                    svg.AppendLine("</path>");

                    // Calculate label position - outside the pie
                    double midAngle = startAngle + (angle / 2);
                    double labelRadius = radius * 1.2;
                    double labelX = centerX + labelRadius * Math.Cos(midAngle);
                    double labelY = centerY + labelRadius * Math.Sin(midAngle);

                    // Add label with percentage
                    svg.AppendLine($"<text x=\"{labelX}\" y=\"{labelY}\" text-anchor=\"{(midAngle > Math.PI / 2 && midAngle < 3 * Math.PI / 2 ? "end" : "start")}\" " +
                        $"font-family=\"Arial\" font-size=\"12\" font-weight=\"bold\">{stats.Material.Name}: {stats.VolumePercentage:0.00}%</text>");

                    // Add a line from slice to label
                    double edgeX = centerX + radius * 1.05 * Math.Cos(midAngle);
                    double edgeY = centerY + radius * 1.05 * Math.Sin(midAngle);
                    svg.AppendLine($"<line x1=\"{edgeX}\" y1=\"{edgeY}\" x2=\"{labelX - (midAngle > Math.PI / 2 && midAngle < 3 * Math.PI / 2 ? 5 : -5)}\" y2=\"{labelY}\" " +
                        $"stroke=\"#666666\" stroke-width=\"1\"/>");

                    totalAngle = endAngle;
                }
            }
            else // Bar chart
            {
                int barCount = statistics.Count;
                double barWidth = 600.0 / barCount;
                double maxHeight = 400.0;
                double xStart = 100.0;
                double yBaseline = 500.0;

                // Draw each bar
                for (int i = 0; i < barCount; i++)
                {
                    var stats = statistics[i];
                    double barHeight = (stats.VolumePercentage / 100.0) * maxHeight;
                    double x = xStart + i * barWidth;

                    // Convert Color to HTML hex
                    string colorHex = $"#{stats.Material.Color.R:X2}{stats.Material.Color.G:X2}{stats.Material.Color.B:X2}";

                    // Determine opacity based on alpha
                    double opacity = stats.Material.Color.A / 255.0;
                    if (opacity < 0.3)
                        opacity = 0.3; // Minimum opacity for visibility

                    // Draw the bar with stroke
                    svg.AppendLine($"<rect x=\"{x}\" y=\"{yBaseline - barHeight}\" width=\"{barWidth * 0.8}\" " +
                        $"height=\"{barHeight}\" fill=\"{colorHex}\" fill-opacity=\"{opacity:0.0}\" " +
                        $"stroke=\"#333333\" stroke-width=\"2\">");
                    svg.AppendLine($"<title>{stats.Material.Name}: {stats.VolumePercentage:0.00}%</title>");
                    svg.AppendLine("</rect>");

                    // Add percentage label above the bar
                    svg.AppendLine($"<text x=\"{x + barWidth * 0.4}\" y=\"{yBaseline - barHeight - 5}\" " +
                        $"text-anchor=\"middle\" font-size=\"11\" font-weight=\"bold\">{stats.VolumePercentage:0.00}%</text>");

                    // Add material name below the bar
                    svg.AppendLine($"<text x=\"{x + barWidth * 0.4}\" y=\"{yBaseline + 20}\" " +
                        $"text-anchor=\"middle\" font-size=\"12\">{stats.Material.Name}</text>");
                }

                // Add Y-axis - mark 0% to 100% in 10% increments
                svg.AppendLine($"<line x1=\"{xStart}\" y1=\"{yBaseline}\" x2=\"{xStart}\" y2=\"100\" " +
                    $"stroke=\"#000000\" stroke-width=\"1\"/>");

                // Add X-axis
                svg.AppendLine($"<line x1=\"{xStart}\" y1=\"{yBaseline}\" x2=\"700\" y2=\"{yBaseline}\" " +
                    $"stroke=\"#000000\" stroke-width=\"1\"/>");

                // Add percentage labels on Y-axis - 0% to 100% in 10% increments
                for (int i = 0; i <= 10; i++)
                {
                    double percentage = i * 10;
                    double y = yBaseline - (percentage / 100.0) * maxHeight;

                    svg.AppendLine($"<line x1=\"{xStart - 5}\" y1=\"{y}\" x2=\"{xStart}\" y2=\"{y}\" " +
                        $"stroke=\"#000000\" stroke-width=\"1\"/>");

                    svg.AppendLine($"<text x=\"{xStart - 10}\" y=\"{y + 5}\" " +
                        $"text-anchor=\"end\" font-size=\"12\">{percentage}%</text>");
                }
            }

            // Add legend
            double legendX = 650;
            double legendY = 100;
            double legendSpacing = 25;

            svg.AppendLine("<rect x=\"630\" y=\"80\" width=\"150\" height=\"" +
                (legendSpacing * statistics.Count + 40) + "\" fill=\"#FFFFFF\" stroke=\"#000000\" stroke-width=\"1\"/>");

            svg.AppendLine("<text x=\"705\" y=\"100\" text-anchor=\"middle\" font-size=\"14\" font-weight=\"bold\">Legend</text>");

            for (int i = 0; i < statistics.Count; i++)
            {
                var stats = statistics[i];
                double y = legendY + 20 + i * legendSpacing;

                // Convert Color to HTML hex
                string colorHex = $"#{stats.Material.Color.R:X2}{stats.Material.Color.G:X2}{stats.Material.Color.B:X2}";

                // Determine opacity based on alpha
                double opacity = stats.Material.Color.A / 255.0;
                if (opacity < 0.3)
                    opacity = 0.3; // Minimum opacity for visibility

                // Color square with strong border
                svg.AppendLine($"<rect x=\"{legendX - 15}\" y=\"{y - 10}\" width=\"15\" height=\"15\" " +
                    $"fill=\"{colorHex}\" fill-opacity=\"{opacity:0.0}\" stroke=\"#333333\" stroke-width=\"1\"/>");

                // Material name with percentage
                svg.AppendLine($"<text x=\"{legendX + 5}\" y=\"{y}\" font-size=\"12\">" +
                    $"{stats.Material.Name} ({stats.VolumePercentage:0.00}%)</text>");
            }

            // Close SVG
            svg.AppendLine("</svg>");

            // Write to file
            File.WriteAllText(fileName, svg.ToString());
        }

        #region Designer Generated Code

        private void InitializeComponent()
        {
            System.Windows.Forms.DataVisualization.Charting.ChartArea chartArea = new System.Windows.Forms.DataVisualization.Charting.ChartArea();
            System.Windows.Forms.DataVisualization.Charting.Legend legend = new System.Windows.Forms.DataVisualization.Charting.Legend();
            this.splitContainer = new System.Windows.Forms.SplitContainer();
            this.dataGridView = new System.Windows.Forms.DataGridView();
            this.panel1 = new System.Windows.Forms.Panel();
            this.progressBar = new System.Windows.Forms.ProgressBar();
            this.lblStatus = new System.Windows.Forms.Label();
            this.btnExportTable = new System.Windows.Forms.Button();
            this.panel2 = new System.Windows.Forms.Panel();
            this.btnExportChart = new System.Windows.Forms.Button();
            this.rbBarChart = new System.Windows.Forms.RadioButton();
            this.rbPieChart = new System.Windows.Forms.RadioButton();
            this.chart = new System.Windows.Forms.DataVisualization.Charting.Chart();

            // splitContainer
            this.splitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer.Location = new System.Drawing.Point(0, 0);
            this.splitContainer.Name = "splitContainer";
            this.splitContainer.Orientation = System.Windows.Forms.Orientation.Horizontal;

            // splitContainer.Panel1
            this.splitContainer.Panel1.Controls.Add(this.dataGridView);
            this.splitContainer.Panel1.Controls.Add(this.panel1);

            // splitContainer.Panel2
            this.splitContainer.Panel2.Controls.Add(this.chart);
            this.splitContainer.Panel2.Controls.Add(this.panel2);
            this.splitContainer.Size = new System.Drawing.Size(800, 600);
            this.splitContainer.SplitterDistance = 300;
            this.splitContainer.TabIndex = 0;

            // dataGridView
            this.dataGridView.AllowUserToAddRows = false;
            this.dataGridView.AllowUserToDeleteRows = false;
            this.dataGridView.AllowUserToResizeRows = false;
            this.dataGridView.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dataGridView.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dataGridView.Location = new System.Drawing.Point(0, 0);
            this.dataGridView.Name = "dataGridView";
            this.dataGridView.ReadOnly = true;
            this.dataGridView.RowHeadersVisible = false;
            this.dataGridView.Size = new System.Drawing.Size(800, 270);
            this.dataGridView.TabIndex = 0;

            // panel1
            this.panel1.Controls.Add(this.progressBar);
            this.panel1.Controls.Add(this.lblStatus);
            this.panel1.Controls.Add(this.btnExportTable);
            this.panel1.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.panel1.Location = new System.Drawing.Point(0, 270);
            this.panel1.Name = "panel1";
            this.panel1.Size = new System.Drawing.Size(800, 30);
            this.panel1.TabIndex = 1;

            // progressBar
            this.progressBar.Anchor = (System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right);
            this.progressBar.Location = new System.Drawing.Point(200, 5);
            this.progressBar.MarqueeAnimationSpeed = 50;
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new System.Drawing.Size(400, 20);
            this.progressBar.Style = System.Windows.Forms.ProgressBarStyle.Marquee;
            this.progressBar.TabIndex = 2;
            this.progressBar.Visible = false;

            // lblStatus
            this.lblStatus.AutoSize = true;
            this.lblStatus.Location = new System.Drawing.Point(12, 8);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(79, 13);
            this.lblStatus.TabIndex = 1;
            this.lblStatus.Text = "Ready";

            // btnExportTable
            this.btnExportTable.Anchor = (System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right);
            this.btnExportTable.Location = new System.Drawing.Point(688, 3);
            this.btnExportTable.Name = "btnExportTable";
            this.btnExportTable.Size = new System.Drawing.Size(100, 23);
            this.btnExportTable.TabIndex = 0;
            this.btnExportTable.Text = "Export Table";
            this.btnExportTable.UseVisualStyleBackColor = true;
            this.btnExportTable.Click += new System.EventHandler(this.btnExportTable_Click);

            // panel2
            this.panel2.Controls.Add(this.btnExportChart);
            this.panel2.Controls.Add(this.rbBarChart);
            this.panel2.Controls.Add(this.rbPieChart);
            this.panel2.Dock = System.Windows.Forms.DockStyle.Top;
            this.panel2.Location = new System.Drawing.Point(0, 0);
            this.panel2.Name = "panel2";
            this.panel2.Size = new System.Drawing.Size(800, 30);
            this.panel2.TabIndex = 1;

            // btnExportChart
            this.btnExportChart.Anchor = (System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right);
            this.btnExportChart.Location = new System.Drawing.Point(688, 3);
            this.btnExportChart.Name = "btnExportChart";
            this.btnExportChart.Size = new System.Drawing.Size(100, 23);
            this.btnExportChart.TabIndex = 2;
            this.btnExportChart.Text = "Export Chart";
            this.btnExportChart.UseVisualStyleBackColor = true;
            this.btnExportChart.Click += new System.EventHandler(this.btnExportChart_Click);

            // rbBarChart
            this.rbBarChart.AutoSize = true;
            this.rbBarChart.Location = new System.Drawing.Point(112, 7);
            this.rbBarChart.Name = "rbBarChart";
            this.rbBarChart.Size = new System.Drawing.Size(74, 17);
            this.rbBarChart.TabIndex = 1;
            this.rbBarChart.Text = "Bar Chart";
            this.rbBarChart.UseVisualStyleBackColor = true;
            this.rbBarChart.CheckedChanged += new System.EventHandler(this.rbBarChart_CheckedChanged);

            // rbPieChart
            this.rbPieChart.AutoSize = true;
            this.rbPieChart.Checked = true;
            this.rbPieChart.Location = new System.Drawing.Point(12, 7);
            this.rbPieChart.Name = "rbPieChart";
            this.rbPieChart.Size = new System.Drawing.Size(74, 17);
            this.rbPieChart.TabIndex = 0;
            this.rbPieChart.TabStop = true;
            this.rbPieChart.Text = "Pie Chart";
            this.rbPieChart.UseVisualStyleBackColor = true;
            this.rbPieChart.CheckedChanged += new System.EventHandler(this.rbPieChart_CheckedChanged);

            // chart
            this.chart.Anchor = (((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right);
            chartArea.Name = "ChartArea1";
            this.chart.ChartAreas.Add(chartArea);
            legend.Name = "Legend1";
            this.chart.Legends.Add(legend);
            this.chart.Location = new System.Drawing.Point(0, 30);
            this.chart.Name = "chart";
            this.chart.Size = new System.Drawing.Size(800, 266);
            this.chart.TabIndex = 0;
            this.chart.Text = "chart";

            // MaterialStatisticsForm
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 600);
            this.Controls.Add(this.splitContainer);
            this.Name = "MaterialStatisticsForm";
            this.Text = "Material Statistics";
            this.Load += new System.EventHandler(this.MaterialStatisticsForm_Load);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer)).EndInit();
            this.splitContainer.Panel1.ResumeLayout(false);
            this.splitContainer.Panel2.ResumeLayout(false);
            this.splitContainer.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView)).EndInit();
            this.panel1.ResumeLayout(false);
            this.panel1.PerformLayout();
            this.panel2.ResumeLayout(false);
            this.panel2.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.chart)).EndInit();
            this.ResumeLayout(false);
        }

        private System.Windows.Forms.SplitContainer splitContainer;
        private System.Windows.Forms.DataGridView dataGridView;
        private System.Windows.Forms.Panel panel1;
        private System.Windows.Forms.Button btnExportTable;
        private System.Windows.Forms.DataVisualization.Charting.Chart chart;
        private System.Windows.Forms.Panel panel2;
        private System.Windows.Forms.RadioButton rbBarChart;
        private System.Windows.Forms.RadioButton rbPieChart;
        private System.Windows.Forms.Button btnExportChart;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.ProgressBar progressBar;

        #endregion Designer Generated Code
    }
}