using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace CTS.Modules.ArtificialIntelligence.GroundingDINO
{
    // Helper class to manage integrated SAM workflow
    public static class SAMPointsHelper
    {
        // Store the point types dictionary to ensure all points are marked as positive
        private static Dictionary<int, bool> pointTypes = new Dictionary<int, bool>();

        // Clear all existing point types
        public static void ClearPoints()
        {
            pointTypes.Clear();
        }

        // Register a point as positive
        public static void RegisterPositivePoint(int pointId)
        {
            pointTypes[pointId] = true;
        }

        // Send boxes to SAM with proper configuration
        public static void SendBoxesToSAM(MainForm mainForm, Material selectedMaterial,
                                         AnnotationManager annotationManager,
                                         Rectangle[] selectedBoxes,
                                         int currentSlice,
                                         string maskCreationMethod)
        {
            try
            {
                // Clear existing state
                ClearPoints();

                // Clear existing points in the annotation manager
                var allPoints = annotationManager.GetAllPoints();
                foreach (var point in allPoints.ToList())
                {
                    annotationManager.RemovePoint(point.ID);
                }

                // Add points based on the selected method and boxes
                foreach (var box in selectedBoxes)
                {
                    AddPointsForBox(annotationManager, box, currentSlice, selectedMaterial, maskCreationMethod);
                }

                // Now prepare SAM - use normal instance, with a special hook to inject our points
                SegmentAnythingCT samInterface = new SegmentAnythingCT(mainForm, selectedMaterial, annotationManager);

                // Use reflection to access and update the pointTypes dictionary in SAM
                Type samType = typeof(SegmentAnythingCT);
                FieldInfo pointTypesField = samType.GetField("pointTypes", BindingFlags.NonPublic | BindingFlags.Instance);

                if (pointTypesField != null)
                {
                    // Direct field replacement - set our point types to SAM's dictionary
                    pointTypesField.SetValue(samInterface, new Dictionary<int, bool>(pointTypes));
                    Logger.Log($"[SAMPointsHelper] Successfully injected {pointTypes.Count} positive points");
                }
                else
                {
                    Logger.Log("[SAMPointsHelper] Warning: Could not access pointTypes field");
                }

                // Get the form field from SegmentAnythingCT if it exists
                FieldInfo formField = samType.GetField("samForm", BindingFlags.NonPublic | BindingFlags.Instance);
                Form samForm = null;

                if (formField != null)
                {
                    samForm = formField.GetValue(samInterface) as Form;
                    if (samForm != null)
                    {
                        // Add Shown event to the form
                        samForm.Shown += (s, e) =>
                        {
                            Logger.Log("[SAMPointsHelper] SAM form shown, preparing auto-segmentation");
                            // Delay the segmentation to allow UI to fully initialize
                            Timer timer = new Timer();
                            timer.Interval = 800;
                            timer.Tick += (s2, e2) =>
                            {
                                timer.Stop();
                                timer.Dispose();
                                TriggerSegmentation(samInterface);
                            };
                            timer.Start();
                        };
                    }
                }

                // Show the SAM interface
                samInterface.Show();

                // Display guidance to the user
                MessageBox.Show(
                    "Detection boxes have been sent to SAM.\n\n" +
                    "Segmentation will begin automatically in a moment.\n" +
                    "You can adjust points if needed and apply the mask when ready.\n" +
                    "Use 'Apply to Volume' for 3D propagation.",
                    "SAM Integration",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                Logger.Log("[SAMPointsHelper] Successfully sent boxes to SAM with auto-segmentation");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SAMPointsHelper] Error in SendBoxesToSAM: {ex.Message}");
                MessageBox.Show($"Error sending boxes to SAM: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Add points for a specific box based on the selected method
        private static void AddPointsForBox(AnnotationManager annotationManager, Rectangle box,
                                           int slice, Material material, string method)
        {
            string pointLabel = "Positive_" + material.Name;

            switch (method)
            {
                case "Center Point":
                    // Add a single center point
                    float centerX = box.X + box.Width / 2f;
                    float centerY = box.Y + box.Height / 2f;
                    var point = annotationManager.AddPoint(centerX, centerY, slice, pointLabel);
                    RegisterPositivePoint(point.ID);
                    Logger.Log($"[SAMPointsHelper] Added positive center point at ({centerX}, {centerY}, {slice})");
                    break;

                case "Corner Points":
                    // Add each corner as a positive point
                    var topLeft = annotationManager.AddPoint(box.X, box.Y, slice, pointLabel);
                    RegisterPositivePoint(topLeft.ID);

                    var topRight = annotationManager.AddPoint(box.X + box.Width, box.Y, slice, pointLabel);
                    RegisterPositivePoint(topRight.ID);

                    var bottomLeft = annotationManager.AddPoint(box.X, box.Y + box.Height, slice, pointLabel);
                    RegisterPositivePoint(bottomLeft.ID);

                    var bottomRight = annotationManager.AddPoint(box.X + box.Width, box.Y + box.Height, slice, pointLabel);
                    RegisterPositivePoint(bottomRight.ID);

                    Logger.Log($"[SAMPointsHelper] Added 4 positive corner points for box at ({box.X}, {box.Y})");
                    break;

                case "Box Outline":
                    // Add points along the box perimeter
                    int outlineStep = Math.Max(5, Math.Min(box.Width, box.Height) / 8);

                    // Add points along top and bottom edges
                    for (int x = box.X; x <= box.X + box.Width; x += outlineStep)
                    {
                        var topPoint = annotationManager.AddPoint(x, box.Y, slice, pointLabel);
                        RegisterPositivePoint(topPoint.ID);

                        var bottomPoint = annotationManager.AddPoint(x, box.Y + box.Height, slice, pointLabel);
                        RegisterPositivePoint(bottomPoint.ID);
                    }

                    // Add points along left and right edges
                    for (int y = box.Y + outlineStep; y < box.Y + box.Height; y += outlineStep)
                    {
                        var leftPoint = annotationManager.AddPoint(box.X, y, slice, pointLabel);
                        RegisterPositivePoint(leftPoint.ID);

                        var rightPoint = annotationManager.AddPoint(box.X + box.Width, y, slice, pointLabel);
                        RegisterPositivePoint(rightPoint.ID);
                    }

                    Logger.Log($"[SAMPointsHelper] Added outline points for box at ({box.X}, {box.Y})");
                    break;

                case "Weighted Grid":
                    // Add a grid of points with more weight near the center
                    int gridSize = 5;
                    for (int i = 0; i < gridSize; i++)
                    {
                        for (int j = 0; j < gridSize; j++)
                        {
                            // Calculate position
                            float gridX = box.X + (box.Width * i) / (float)(gridSize - 1);
                            float gridY = box.Y + (box.Height * j) / (float)(gridSize - 1);

                            // Determine weight (center has highest weight)
                            int centerDistance = Math.Max(
                                Math.Abs(i - gridSize / 2),
                                Math.Abs(j - gridSize / 2)
                            );

                            int pointWeight = gridSize - centerDistance;

                            // Add point with weight
                            for (int w = 0; w < pointWeight; w++)
                            {
                                var gridPoint = annotationManager.AddPoint(gridX, gridY, slice, pointLabel);
                                RegisterPositivePoint(gridPoint.ID);
                            }
                        }
                    }

                    Logger.Log($"[SAMPointsHelper] Added weighted grid points for box at ({box.X}, {box.Y})");
                    break;

                case "Box Fill":
                    // Fill the box with evenly spaced points
                    int fillStep = Math.Max(4, Math.Min(box.Width, box.Height) / 10);

                    for (int fillX = box.X; fillX <= box.X + box.Width; fillX += fillStep)
                    {
                        for (int fillY = box.Y; fillY <= box.Y + box.Height; fillY += fillStep)
                        {
                            var fillPoint = annotationManager.AddPoint(fillX, fillY, slice, pointLabel);
                            RegisterPositivePoint(fillPoint.ID);
                        }
                    }

                    Logger.Log($"[SAMPointsHelper] Added box fill points for box at ({box.X}, {box.Y})");
                    break;

                default:
                    // Default to center point
                    float defaultX = box.X + box.Width / 2f;
                    float defaultY = box.Y + box.Height / 2f;
                    var defaultPoint = annotationManager.AddPoint(defaultX, defaultY, slice, pointLabel);
                    RegisterPositivePoint(defaultPoint.ID);
                    Logger.Log($"[SAMPointsHelper] Added default point at ({defaultX}, {defaultY}, {slice})");
                    break;
            }
        }

        // Try to trigger segmentation in the SAM interface
        private static void TriggerSegmentation(SegmentAnythingCT samInterface)
        {
            try
            {
                // Try to find and invoke the PerformSegmentation method
                MethodInfo segmentMethod = typeof(SegmentAnythingCT).GetMethod("PerformSegmentation",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (segmentMethod != null)
                {
                    Logger.Log("[SAMPointsHelper] Found PerformSegmentation method, invoking...");
                    segmentMethod.Invoke(samInterface, null);
                    return;
                }

                // If that fails, try to find and click the Apply button
                FieldInfo btnApplyField = typeof(SegmentAnythingCT).GetField("btnApply",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (btnApplyField != null)
                {
                    Button btnApply = btnApplyField.GetValue(samInterface) as Button;
                    if (btnApply != null)
                    {
                        Logger.Log("[SAMPointsHelper] Found Apply button, clicking...");
                        // Find the form
                        FieldInfo formField = typeof(SegmentAnythingCT).GetField("samForm",
                            BindingFlags.NonPublic | BindingFlags.Instance);

                        if (formField != null)
                        {
                            Form form = formField.GetValue(samInterface) as Form;
                            if (form != null)
                            {
                                form.Invoke(new Action(() =>
                                {
                                    btnApply.PerformClick();
                                }));
                                return;
                            }
                        }

                        // If we can't find the form, try clicking directly
                        btnApply.PerformClick();
                        return;
                    }
                }

                Logger.Log("[SAMPointsHelper] Could not find a way to trigger segmentation");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SAMPointsHelper] Error triggering segmentation: {ex.Message}");
            }
        }
    }
}