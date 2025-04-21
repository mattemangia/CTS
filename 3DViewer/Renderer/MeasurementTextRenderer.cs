// This would typically go in a separate file

using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
using SharpDX;
using RectangleF = System.Drawing.RectangleF;
using Color = System.Drawing.Color;

namespace CTSegmenter
{
    public class MeasurementTextRenderer : IDisposable
    {
        private Control parentControl;
        private BufferedGraphicsContext graphicsContext;
        private BufferedGraphics graphicsBuffer;
        private System.Drawing.Size bufferSize;
        private bool needsRedraw = true;
        private List<LabelData> labels = new List<LabelData>();

        public struct LabelData
        {
            public string Text;
            public System.Drawing.Point Position;
            public Color BackgroundColor;
            public Color TextColor;
        }

        public MeasurementTextRenderer(Control parent)
        {
            parentControl = parent;
            graphicsContext = BufferedGraphicsManager.Current;
            ResizeBuffer(parent.Width, parent.Height);

            // Hook into parent's Paint event
            parentControl.Paint += ParentControl_Paint;
            parentControl.Resize += ParentControl_Resize;
        }

        private void ParentControl_Paint(object sender, PaintEventArgs e)
        {
            if (needsRedraw)
            {
                RenderLabels();
                needsRedraw = false;
            }

            // Copy the buffer to the screen
            graphicsBuffer.Render(e.Graphics);
        }

        private void ParentControl_Resize(object sender, EventArgs e)
        {
            ResizeBuffer(parentControl.Width, parentControl.Height);
            needsRedraw = true;
            parentControl.Invalidate();
        }

        private void ResizeBuffer(int width, int height)
        {
            // Only resize if actually changed
            if (width <= 0 || height <= 0 ||
                (bufferSize.Width == width && bufferSize.Height == height))
                return;

            try
            {
                bufferSize = new System.Drawing.Size(width, height);

                // Dispose the old buffer if exists
                if (graphicsBuffer != null)
                {
                    graphicsBuffer.Dispose();
                    graphicsBuffer = null;
                }

                // Create a new buffer
                graphicsBuffer = graphicsContext.Allocate(
                    parentControl.CreateGraphics(),
                    new System.Drawing.Rectangle(0, 0, width, height));

                // Clear the buffer
                graphicsBuffer.Graphics.Clear(Color.Transparent);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error resizing buffer: {ex.Message}");
            }
        }

        public void ClearLabels()
        {
            labels.Clear();
            needsRedraw = true;
            parentControl.Invalidate();
        }

        public void AddLabel(string text, System.Drawing.Point position,
                            Color backgroundColor, Color textColor)
        {
            labels.Add(new LabelData
            {
                Text = text,
                Position = position,
                BackgroundColor = backgroundColor,
                TextColor = textColor
            });

            needsRedraw = true;
            parentControl.Invalidate();
        }

        private void RenderLabels()
        {
            if (graphicsBuffer == null || parentControl.Width <= 0 || parentControl.Height <= 0)
                return;

            try
            {
                // Clear the buffer
                graphicsBuffer.Graphics.Clear(Color.Transparent);

                // Set up for high quality text rendering
                graphicsBuffer.Graphics.TextRenderingHint =
                    System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                using (var font = new Font("Arial", 8))
                {
                    // Draw each label
                    foreach (var label in labels)
                    {
                        // Measure the text to position the background
                        SizeF textSize = graphicsBuffer.Graphics.MeasureString(label.Text, font);

                        // Create background rectangle
                        System.Drawing.RectangleF bgRect = new RectangleF(
                            label.Position.X - textSize.Width / 2,
                            label.Position.Y - textSize.Height / 2,
                            textSize.Width + 4, // Add a small padding
                            textSize.Height);

                        // Draw background
                        using (SolidBrush bgBrush = new SolidBrush(label.BackgroundColor))
                        {
                            graphicsBuffer.Graphics.FillRectangle(bgBrush, bgRect);
                        }

                        // Draw text
                        using (SolidBrush textBrush = new SolidBrush(label.TextColor))
                        {
                            graphicsBuffer.Graphics.DrawString(
                                label.Text,
                                font,
                                textBrush,
                                bgRect.X + 2, // Add a small padding
                                bgRect.Y);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error rendering labels: {ex.Message}");
            }
        }

        public void Invalidate()
        {
            needsRedraw = true;
            parentControl.Invalidate();
        }

        public void Dispose()
        {
            if (parentControl != null)
            {
                parentControl.Paint -= ParentControl_Paint;
                parentControl.Resize -= ParentControl_Resize;
            }

            if (graphicsBuffer != null)
            {
                graphicsBuffer.Dispose();
                graphicsBuffer = null;
            }

            if (graphicsContext != null)
            {
                graphicsContext = null;
            }
        }
    }
}