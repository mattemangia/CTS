//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Text;
using Excel = Microsoft.Office.Interop.Excel;
using System.Windows.Forms;
using Microsoft.Office.Interop.Excel;
using Point = System.Drawing.Point;

namespace CTS
{
    /// <summary>
    /// Manages all measurements in the application
    /// </summary>
    public class MeasurementManager
    {
        private List<Measurement> measurements;
        private int nextID;
        private Color[] availableColors;
        private int colorIndex;
        private MainForm mainForm;

        public event EventHandler MeasurementsChanged;

        public List<Measurement> Measurements => measurements;

        public MeasurementManager(MainForm mainForm)
        {
            this.mainForm = mainForm;
            measurements = new List<Measurement>();
            nextID = 1;
            colorIndex = 0;

            // Define available colors for measurements
            availableColors = new Color[]
            {
                Color.Red,
                Color.Blue,
                Color.Green,
                Color.Yellow,
                Color.Orange,
                Color.Purple,
                Color.Cyan,
                Color.Pink,
                Color.Brown,
                Color.Gray
            };
        }

        // Add a new measurement
        public Measurement AddMeasurement(ViewType viewType, Point start, Point end, int sliceIndex)
        {
            double distance = Measurement.CalculateDistance(start, end, viewType, mainForm.GetPixelSize());

            Color lineColor = availableColors[colorIndex % availableColors.Length];
            colorIndex++;

            var measurement = new Measurement(nextID++, $"Measurement {nextID}", viewType, start, end, sliceIndex, distance, lineColor);
            measurements.Add(measurement);

            OnMeasurementsChanged();
            return measurement;
        }

        // Remove a measurement by ID
        public bool RemoveMeasurement(int measurementID)
        {
            var measurement = measurements.FirstOrDefault(m => m.ID == measurementID);
            if (measurement != null)
            {
                measurements.Remove(measurement);
                OnMeasurementsChanged();
                return true;
            }
            return false;
        }

        // Get all measurements for a specific view and slice
        public List<Measurement> GetMeasurementsForView(ViewType viewType, int sliceIndex)
        {
            return measurements.Where(m => m.ViewType == viewType && m.SliceIndex == sliceIndex).ToList();
        }

        // Get measurement by ID
        public Measurement GetMeasurementByID(int id)
        {
            return measurements.FirstOrDefault(m => m.ID == id);
        }

        // Find measurement near a point
        public Measurement FindMeasurementNearPoint(Point point, ViewType viewType, int sliceIndex, float tolerance = 5.0f)
        {
            var measurementsInView = GetMeasurementsForView(viewType, sliceIndex);

            foreach (var measurement in measurementsInView)
            {
                if (measurement.IsNearLine(point, tolerance))
                {
                    return measurement;
                }
            }

            return null;
        }

        // Clear all measurements
        public void ClearAllMeasurements()
        {
            measurements.Clear();
            nextID = 1;
            colorIndex = 0;
            OnMeasurementsChanged();
        }

        // Export measurements to CSV
        public void ExportToCSV(string filePath)
        {
            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                // Write header
                writer.WriteLine("ID,Name,View,SliceIndex,StartX,StartY,EndX,EndY,Distance(m),Distance(µm),Distance(mm),Created");

                // Write measurements
                foreach (var measurement in measurements)
                {
                    writer.WriteLine($"{measurement.ID}," +
                                   $"\"{measurement.Name}\"," +
                                   $"{measurement.ViewType}," +
                                   $"{measurement.SliceIndex}," +
                                   $"{measurement.StartPoint.X}," +
                                   $"{measurement.StartPoint.Y}," +
                                   $"{measurement.EndPoint.X}," +
                                   $"{measurement.EndPoint.Y}," +
                                   $"{measurement.Distance:0.000000}," +
                                   $"{measurement.Distance * 1e6:0.00}," +
                                   $"{measurement.Distance * 1e3:0.00}," +
                                   $"\"{measurement.CreatedAt:yyyy-MM-dd HH:mm:ss}\"");
                }
            }
        }

        // Export measurements to Excel
        public void ExportToExcel(string filePath)
        {
            Excel.Application excelApp = null;
            Excel.Workbook workbook = null;
            Excel.Worksheet worksheet = null;

            try
            {
                // Create Excel application
                excelApp = new Excel.Application();
                workbook = excelApp.Workbooks.Add();
                worksheet = workbook.Worksheets[1];
                worksheet.Name = "Measurements";

                // Set headers
                worksheet.Cells[1, 1] = "ID";
                worksheet.Cells[1, 2] = "Name";
                worksheet.Cells[1, 3] = "View";
                worksheet.Cells[1, 4] = "Slice Index";
                worksheet.Cells[1, 5] = "Start X";
                worksheet.Cells[1, 6] = "Start Y";
                worksheet.Cells[1, 7] = "End X";
                worksheet.Cells[1, 8] = "End Y";
                worksheet.Cells[1, 9] = "Distance (m)";
                worksheet.Cells[1, 10] = "Distance (µm)";
                worksheet.Cells[1, 11] = "Distance (mm)";
                worksheet.Cells[1, 12] = "Created";

                // Style headers
                Excel.Range headerRange = worksheet.Range["A1:L1"];
                headerRange.Font.Bold = true;
                headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);

                // Add measurements
                int row = 2;
                foreach (var measurement in measurements)
                {
                    worksheet.Cells[row, 1] = measurement.ID;
                    worksheet.Cells[row, 2] = measurement.Name;
                    worksheet.Cells[row, 3] = measurement.ViewType.ToString();
                    worksheet.Cells[row, 4] = measurement.SliceIndex;
                    worksheet.Cells[row, 5] = measurement.StartPoint.X;
                    worksheet.Cells[row, 6] = measurement.StartPoint.Y;
                    worksheet.Cells[row, 7] = measurement.EndPoint.X;
                    worksheet.Cells[row, 8] = measurement.EndPoint.Y;
                    worksheet.Cells[row, 9] = measurement.Distance;
                    worksheet.Cells[row, 10] = measurement.Distance * 1e6;
                    worksheet.Cells[row, 11] = measurement.Distance * 1e3;
                    worksheet.Cells[row, 12] = measurement.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
                    row++;
                }

                // AutoFit columns
                worksheet.Columns.AutoFit();

                // Save the file
                workbook.SaveAs(filePath);

                // Close
                workbook.Close();
                excelApp.Quit();
            }
            catch (Exception ex)
            {
                // Show error message
                MessageBox.Show($"Error exporting to Excel: {ex.Message}", "Export Error",
                               MessageBoxButtons.OK, MessageBoxIcon.Error);

                // Cleanup
                if (workbook != null)
                {
                    workbook.Close(false);
                }
                if (excelApp != null)
                {
                    excelApp.Quit();
                }
            }
            finally
            {
                // Release COM objects
                if (worksheet != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(worksheet);
                if (workbook != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(workbook);
                if (excelApp != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(excelApp);

                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        // Import measurements from CSV
        public void ImportFromCSV(string filePath)
        {
            try
            {
                using (var reader = new StreamReader(filePath))
                {
                    string line = reader.ReadLine(); // Skip header

                    while ((line = reader.ReadLine()) != null)
                    {
                        var parts = ParseCSVLine(line);
                        if (parts.Length >= 12)
                        {
                            int id = int.Parse(parts[0]);
                            string name = parts[1].Trim('"');
                            ViewType viewType = (ViewType)Enum.Parse(typeof(ViewType), parts[2]);
                            int sliceIndex = int.Parse(parts[3]);
                            Point start = new Point(int.Parse(parts[4]), int.Parse(parts[5]));
                            Point end = new Point(int.Parse(parts[6]), int.Parse(parts[7]));
                            double distance = double.Parse(parts[8]);
                            DateTime created = DateTime.ParseExact(parts[11].Trim('"'), "yyyy-MM-dd HH:mm:ss", null);

                            // Create color based on ID
                            Color color = availableColors[id % availableColors.Length];

                            var measurement = new Measurement(id, name, viewType, start, end, sliceIndex, distance, color);
                            measurement.CreatedAt = created;
                            measurements.Add(measurement);

                            // Update nextID if needed
                            if (id >= nextID)
                                nextID = id + 1;
                        }
                    }
                }

                OnMeasurementsChanged();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing measurements: {ex.Message}", "Import Error",
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Helper method to parse CSV line with quoted fields
        private string[] ParseCSVLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var currentField = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(currentField.ToString());
                    currentField.Clear();
                }
                else
                {
                    currentField.Append(c);
                }
            }

            result.Add(currentField.ToString());
            return result.ToArray();
        }

        protected virtual void OnMeasurementsChanged()
        {
            MeasurementsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}