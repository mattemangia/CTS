using CTS.NodeEditor;
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

namespace CTS.Modules.NodeEditor.Nodes
{
    public class MaterialStatisticsNode : BaseNode
    {
        private List<MaterialStatistics> statistics;
        private long totalVoxels;
        private DateTime lastCalculationTime;
        private bool statisticsValid = false;
        private bool calculationInProgress = false;

        // UI Controls
        private Button calculateButton;
        private Button showStatisticsButton;
        private Button exportCSVButton;
        private Label statusLabel;
        private Panel previewPanel; // Store reference to preview panel

        public MaterialStatisticsNode(Point position) : base(position)
        {
            Color = Color.FromArgb(160, 120, 200); // Purple theme for analysis nodes
        }

        protected override void SetupPins()
        {
            AddInputPin("Labels", Color.LightCoral);
            AddOutputPin("Statistics", Color.LightGreen); // Output of statistics data
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            // Title
            var titleLabel = new Label
            {
                Text = "Material Statistics",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            // Status label
            statusLabel = new Label
            {
                Text = "Ready",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.LightGray
            };

            // Add a preview of statistics if available
            var statsPreviewLabel = new Label
            {
                Text = "Statistics Preview:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            // Create the preview panel and store a reference to it
            previewPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 120,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(50, 50, 53)
            };

            // Calculate button
            calculateButton = new Button
            {
                Text = "Calculate Statistics",
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(100, 180, 100), // Green for action
                ForeColor = Color.White
            };
            calculateButton.Click += (s, e) => Execute();

            // Show statistics button
            showStatisticsButton = new Button
            {
                Text = "Show Full Statistics",
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(100, 100, 180), // Blue for view
                ForeColor = Color.White,
                Enabled = false // Disabled until statistics are calculated
            };
            showStatisticsButton.Click += (s, e) => ShowStatistics();

            // Export CSV button
            exportCSVButton = new Button
            {
                Text = "Export as CSV",
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(180, 100, 100), // Red for export
                ForeColor = Color.White,
                Enabled = false // Disabled until statistics are calculated
            };
            exportCSVButton.Click += (s, e) => ExportStatistics();

            // Add controls to panel (in reverse order because of DockStyle.Top)
            panel.Controls.Add(exportCSVButton);
            panel.Controls.Add(showStatisticsButton);
            panel.Controls.Add(calculateButton);
            panel.Controls.Add(previewPanel);
            panel.Controls.Add(statsPreviewLabel);
            panel.Controls.Add(statusLabel);
            panel.Controls.Add(titleLabel);

            // Return the panel
            return panel;
        }

        public override async void Execute()
        {
            try
            {
                if (calculationInProgress)
                {
                    MessageBox.Show("Calculation already in progress. Please wait.",
                        "Calculation in Progress", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Get MainForm reference to access the dataset
                var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
                if (mainForm == null || mainForm.volumeLabels == null)
                {
                    MessageBox.Show("No label dataset is currently loaded to analyze.",
                        "Analysis Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Disable Calculate button and update status
                calculationInProgress = true;
                calculateButton.Enabled = false;
                statusLabel.Text = "Calculating statistics...";
                statusLabel.ForeColor = Color.Yellow;

                // Show progress dialog
                using (var progress = new ProgressFormWithProgress("Calculating material statistics..."))
                {
                    progress.Show();

                    try
                    {
                        // Calculate statistics in a background thread
                        statistics = await Task.Run(() => CalculateStatistics(mainForm, progress));
                        lastCalculationTime = DateTime.Now;
                        statisticsValid = true;

                        // Update the UI to reflect that statistics are available
                        showStatisticsButton.Enabled = true;
                        exportCSVButton.Enabled = true;
                        statusLabel.Text = $"Statistics calculated at {lastCalculationTime.ToShortTimeString()}";
                        statusLabel.ForeColor = Color.LightGreen;

                        // Update the preview panel with a summary
                        UpdatePreviewPanel();

                        MessageBox.Show("Material statistics calculated successfully!",
                            "Calculation Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to calculate statistics: {ex.Message}",
                            "Calculation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        statusLabel.Text = "Error calculating statistics";
                        statusLabel.ForeColor = Color.OrangeRed;
                    }
                    finally
                    {
                        progress.Close();
                        calculateButton.Enabled = true;
                        calculationInProgress = false;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error in Material Statistics node: {ex.Message}",
                    "Node Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                calculationInProgress = false;
                calculateButton.Enabled = true;
            }
        }

        private void UpdatePreviewPanel()
        {
            // Now we can access the previewPanel directly
            if (previewPanel == null || statistics == null || statistics.Count == 0)
                return;

            previewPanel.Controls.Clear();

            // Add a label with summary information
            var summaryLabel = new Label
            {
                Text = $"Total materials: {statistics.Count}\n" +
                       $"Total voxels: {totalVoxels:N0}\n" +
                       $"Largest material: {statistics.OrderByDescending(s => s.VoxelCount).First().Material.Name} " +
                       $"({statistics.OrderByDescending(s => s.VoxelCount).First().VolumePercentage:0.00}%)",
                Dock = DockStyle.Top,
                Height = 60,
                ForeColor = Color.White,
                Font = new Font("Arial", 9)
            };
            previewPanel.Controls.Add(summaryLabel);

            // Create a mini pie chart (very simple version)
            var miniChart = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(40, 40, 43)
            };

            miniChart.Paint += (s, e) => {
                var g = e.Graphics;
                var rect = new Rectangle(10, 5, miniChart.Width - 20, miniChart.Height - 10);
                float startAngle = 0;

                foreach (var stat in statistics)
                {
                    var sweepAngle = (float)(stat.VolumePercentage / 100 * 360);
                    using (var brush = new SolidBrush(stat.Material.Color))
                    {
                        g.FillPie(brush, rect, startAngle, sweepAngle);
                        g.DrawPie(Pens.White, rect, startAngle, sweepAngle);
                    }
                    startAngle += sweepAngle;
                }
            };

            previewPanel.Controls.Add(miniChart);
        }

        // The rest of the code remains the same...

        private List<MaterialStatistics> CalculateStatistics(MainForm mainForm, IProgress<int> progress = null)
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

                // Create statistics objects for each material
                Dictionary<byte, MaterialStatistics> materialStats = new Dictionary<byte, MaterialStatistics>();
                foreach (Material material in mainForm.Materials)
                {
                    materialStats[material.ID] = new MaterialStatistics(material);
                }

                // Calculate total voxels
                totalVoxels = (long)width * height * depth;

                // Create counter for each material ID
                Dictionary<byte, long> voxelCounts = new Dictionary<byte, long>();
                foreach (byte id in materialStats.Keys)
                {
                    voxelCounts[id] = 0;
                }

                // Use parallel processing to count voxels
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

                    // Merge local counts
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
                            int percent = (int)((completedSlices / (float)depth) * 95);
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
                    }
                }

                // Sort results by material ID (Exterior first, then others)
                results = results.OrderBy(s => s.Material.ID).ToList();

                // Report 100% done
                progress?.Report(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"[MaterialStatisticsNode] Error in statistics calculation: {ex.Message}");
                throw;
            }

            return results;
        }

        private void ShowStatistics()
        {
            if (!statisticsValid || statistics == null)
            {
                MessageBox.Show("No valid statistics available. Please calculate statistics first.",
                    "No Data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Get the main form to be the parent of our statistics form
                var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();

                // Create and show the statistics form with our data
                var statsForm = new MaterialStatisticsDialog(statistics, totalVoxels);
                statsForm.Show(mainForm);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error showing statistics: {ex.Message}",
                    "Display Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExportStatistics()
        {
            if (!statisticsValid || statistics == null)
            {
                MessageBox.Show("No valid statistics available. Please calculate statistics first.",
                    "No Data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "CSV Files (*.csv)|*.csv";
                saveDialog.Title = "Export Statistics";
                saveDialog.FileName = "MaterialStatistics";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        ExportToCSV(saveDialog.FileName);
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
            csv.AppendLine("Material,ID,Voxels,Volume (µm³),Volume (mm³),Volume (cm³),Percentage");

            // Add data rows
            foreach (var stats in statistics)
            {
                csv.AppendLine($"{stats.Material.Name},{stats.Material.ID},{stats.VoxelCount}," +
                    $"{stats.VolumeUm3.ToString(CultureInfo.InvariantCulture)}," +
                    $"{stats.VolumeMm3.ToString(CultureInfo.InvariantCulture)}," +
                    $"{stats.VolumeCm3.ToString(CultureInfo.InvariantCulture)}," +
                    $"{stats.VolumePercentage.ToString(CultureInfo.InvariantCulture)}");
            }

            // Write to file
            File.WriteAllText(fileName, csv.ToString());
        }
    }

    /// <summary>
    /// A simplified version of MaterialStatisticsForm to display statistics from a node
    /// </summary>
    public class MaterialStatisticsDialog : Form
    {
        private List<MaterialStatistics> statistics;
        private long totalVoxels;
        private Chart chart;
        private DataGridView dataGridView;
        private RadioButton rbPieChart;
        private RadioButton rbBarChart;
        private Button btnExport;

        public MaterialStatisticsDialog(List<MaterialStatistics> stats, long totalVoxels)
        {
            this.statistics = stats;
            this.totalVoxels = totalVoxels;
            InitializeComponents();
            PopulateData();
        }

        private void InitializeComponents()
        {
            this.Text = "Material Statistics";
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterParent;

            // Split container for grid and chart
            var splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 250
            };

            // Data grid for top panel
            dataGridView = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells
            };
            splitContainer.Panel1.Controls.Add(dataGridView);

            // Control panel under grid
            var topControlPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 35
            };

            btnExport = new Button
            {
                Text = "Export CSV",
                Dock = DockStyle.Right,
                Width = 100
            };
            btnExport.Click += (s, e) => ExportCSV();

            topControlPanel.Controls.Add(btnExport);
            splitContainer.Panel1.Controls.Add(topControlPanel);

            // Chart for bottom panel
            chart = new Chart
            {
                Dock = DockStyle.Fill
            };

            // Add chart area and legend
            var chartArea = new ChartArea("MainChartArea");
            chart.ChartAreas.Add(chartArea);
            chart.Legends.Add(new Legend("MainLegend"));

            splitContainer.Panel2.Controls.Add(chart);

            // Control panel for chart type
            var chartControlPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 35
            };

            rbPieChart = new RadioButton
            {
                Text = "Pie Chart",
                Checked = true,
                Location = new Point(10, 10)
            };
            rbPieChart.CheckedChanged += (s, e) => {
                if (rbPieChart.Checked)
                    UpdateChart(true);
            };

            rbBarChart = new RadioButton
            {
                Text = "Bar Chart",
                Location = new Point(100, 10)
            };
            rbBarChart.CheckedChanged += (s, e) => {
                if (rbBarChart.Checked)
                    UpdateChart(false);
            };

            chartControlPanel.Controls.Add(rbPieChart);
            chartControlPanel.Controls.Add(rbBarChart);
            splitContainer.Panel2.Controls.Add(chartControlPanel);

            this.Controls.Add(splitContainer);
        }

        private void PopulateData()
        {
            // Create DataTable for grid
            DataTable dt = new DataTable();
            dt.Columns.Add("Material", typeof(string));
            dt.Columns.Add("ID", typeof(byte));
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
                    stats.Material.ID,
                    stats.VoxelCount,
                    stats.VolumeUm3.ToString("N2", CultureInfo.InvariantCulture),
                    stats.VolumeMm3.ToString("N6", CultureInfo.InvariantCulture),
                    stats.VolumeCm3.ToString("N9", CultureInfo.InvariantCulture),
                    stats.VolumePercentage.ToString("0.00") + "%"
                );
            }

            // Bind DataTable to grid
            dataGridView.DataSource = dt;

            // Update chart
            UpdateChart(true); // Start with pie chart
        }

        private void UpdateChart(bool isPieChart)
        {
            // Clear existing series
            chart.Series.Clear();
            chart.Titles.Clear();

            // Add title
            chart.Titles.Add("Material Volume Distribution");

            // Create new series
            Series series = new Series();

            if (isPieChart)
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
                    dp.Label = $"{stats.Material.Name}: {stats.VolumePercentage:0.00}%";
                    dp.LegendText = stats.Material.Name;
                    dp.Color = stats.Material.Color;
                    dp.ToolTip = $"{stats.Material.Name}: {stats.VolumePercentage:0.00}% ({stats.VolumeMm3:0.000} mm³)";

                    series.Points.Add(dp);
                }

                // Configure label style
                series["PieLabelStyle"] = "Outside";
                chart.Legends[0].Enabled = true;
            }
            else // Bar chart
            {
                // Configure bar chart
                series.ChartType = SeriesChartType.Column;
                chart.ChartAreas[0].Area3DStyle.Enable3D = false;

                // Configure axes
                chart.ChartAreas[0].AxisX.Title = "Material";
                chart.ChartAreas[0].AxisY.Title = "Percentage";
                chart.ChartAreas[0].AxisY.LabelStyle.Format = "0.00%";

                // Add data points for each material
                foreach (var stats in statistics)
                {
                    DataPoint dp = new DataPoint();
                    dp.AxisLabel = stats.Material.Name;
                    dp.YValues = new double[] { stats.VolumePercentage / 100 };
                    dp.Color = stats.Material.Color;
                    dp.Label = $"{stats.VolumePercentage:0.00}%";
                    dp.ToolTip = $"{stats.Material.Name}: {stats.VolumePercentage:0.00}% ({stats.VolumeMm3:0.000} mm³)";

                    series.Points.Add(dp);
                }

                series.IsValueShownAsLabel = true;
                chart.Legends[0].Enabled = false;
            }

            // Add the series to the chart
            chart.Series.Add(series);
        }

        private void ExportCSV()
        {
            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "CSV Files (*.csv)|*.csv";
                saveDialog.Title = "Export Statistics";
                saveDialog.FileName = "MaterialStatistics";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        StringBuilder csv = new StringBuilder();

                        // Add headers
                        csv.AppendLine("Material,ID,Voxels,Volume (µm³),Volume (mm³),Volume (cm³),Percentage");

                        // Add data rows
                        foreach (var stats in statistics)
                        {
                            csv.AppendLine($"{stats.Material.Name},{stats.Material.ID},{stats.VoxelCount}," +
                                $"{stats.VolumeUm3.ToString(CultureInfo.InvariantCulture)}," +
                                $"{stats.VolumeMm3.ToString(CultureInfo.InvariantCulture)}," +
                                $"{stats.VolumeCm3.ToString(CultureInfo.InvariantCulture)}," +
                                $"{stats.VolumePercentage.ToString(CultureInfo.InvariantCulture)}");
                        }

                        // Write to file
                        File.WriteAllText(saveDialog.FileName, csv.ToString());

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
    }
}