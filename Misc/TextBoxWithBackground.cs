//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Drawing;
using System.Windows.Forms;

namespace CTS
{
    /// <summary>
    /// Custom RichTextBox that supports a background image with adjustable opacity
    /// </summary>
    public class TextBoxWithBackground : RichTextBox
    {
        private Image _backgroundImage;
        private float _imageOpacity = 0.3f; // Default opacity

        public Image BackgroundImage
        {
            get { return _backgroundImage; }
            set
            {
                _backgroundImage = value;
                Invalidate(); // Force redraw when image changes
            }
        }

        public float ImageOpacity
        {
            get { return _imageOpacity; }
            set
            {
                // Ensure value is between 0.0 and 1.0
                _imageOpacity = Math.Max(0.0f, Math.Min(1.0f, value));
                Invalidate(); // Force redraw when opacity changes
            }
        }

        public TextBoxWithBackground()
        {
            // Set control styles for proper rendering
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.ResizeRedraw, true);

            // Make transparent to allow background image to show through
            this.BackColor = Color.Black;
        }

        protected override void WndProc(ref Message m)
        {
            // WM_PAINT message
            if (m.Msg == 0x000F)
            {
                // Handle painting ourselves
                using (Graphics graphics = Graphics.FromHwnd(this.Handle))
                {
                    // Draw the background
                    using (SolidBrush brush = new SolidBrush(this.BackColor))
                    {
                        graphics.FillRectangle(brush, this.ClientRectangle);
                    }

                    // Draw the background image if it exists
                    if (_backgroundImage != null)
                    {
                        // Calculate scaling to fit while maintaining aspect ratio
                        float scaleWidth = (float)ClientRectangle.Width / _backgroundImage.Width;
                        float scaleHeight = (float)ClientRectangle.Height / _backgroundImage.Height;
                        float scale = Math.Min(scaleWidth, scaleHeight);

                        // Calculate position to center
                        int imgWidth = (int)(_backgroundImage.Width * scale);
                        int imgHeight = (int)(_backgroundImage.Height * scale);
                        int x = (ClientRectangle.Width - imgWidth) / 2;
                        int y = (ClientRectangle.Height - imgHeight) / 2;

                        // Create a color matrix for opacity
                        System.Drawing.Imaging.ColorMatrix colorMatrix = new System.Drawing.Imaging.ColorMatrix();
                        colorMatrix.Matrix33 = _imageOpacity; // Set opacity

                        using (System.Drawing.Imaging.ImageAttributes attributes = new System.Drawing.Imaging.ImageAttributes())
                        {
                            attributes.SetColorMatrix(colorMatrix, System.Drawing.Imaging.ColorMatrixFlag.Default, System.Drawing.Imaging.ColorAdjustType.Bitmap);

                            // Draw the background image with opacity
                            graphics.DrawImage(_backgroundImage,
                                new Rectangle(x, y, imgWidth, imgHeight),
                                0, 0, _backgroundImage.Width, _backgroundImage.Height,
                                GraphicsUnit.Pixel, attributes);
                        }
                    }
                }
            }

            // Let the default handler process the message to draw text
            base.WndProc(ref m);
        }
    }
}