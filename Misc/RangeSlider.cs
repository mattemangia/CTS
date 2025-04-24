using System;
using System.Drawing;
using System.Windows.Forms;

namespace CTSegmenter
{
    /// <summary>
    /// A custom slider control that allows selecting a range between minimum and maximum values
    /// with two draggable handles.
    /// </summary>
    public class RangeSlider : Control
    {
        // Properties for range values
        private int minimum = 0;

        private int maximum = 255;
        private int rangeMinimum = 0;
        private int rangeMaximum = 255;

        // UI settings
        private int trackHeight = 6;

        private int handleSize = 16;
        private Color trackColor = Color.FromArgb(60, 60, 60);
        private Color rangeColor = Color.FromArgb(0, 120, 215);
        private Color handleColor = Color.FromArgb(200, 200, 200);
        private Color handleBorderColor = Color.FromArgb(100, 100, 100);

        // Dragging state
        private bool draggingMinHandle = false;

        private bool draggingMaxHandle = false;
        private Rectangle minHandleRect;
        private Rectangle maxHandleRect;

        // Event for range changes
        public event EventHandler RangeChanged;

        #region Properties

        public int Minimum
        {
            get => minimum;
            set
            {
                if (minimum != value)
                {
                    minimum = value;
                    if (rangeMinimum < minimum)
                        RangeMinimum = minimum;
                    Invalidate();
                }
            }
        }

        public int Maximum
        {
            get => maximum;
            set
            {
                if (maximum != value)
                {
                    maximum = value;
                    if (rangeMaximum > maximum)
                        RangeMaximum = maximum;
                    Invalidate();
                }
            }
        }

        public int RangeMinimum
        {
            get => rangeMinimum;
            set
            {
                value = Math.Max(minimum, Math.Min(value, rangeMaximum));
                if (rangeMinimum != value)
                {
                    rangeMinimum = value;
                    OnRangeChanged(EventArgs.Empty);
                    Invalidate();
                }
            }
        }

        public int RangeMaximum
        {
            get => rangeMaximum;
            set
            {
                value = Math.Min(maximum, Math.Max(value, rangeMinimum));
                if (rangeMaximum != value)
                {
                    rangeMaximum = value;
                    OnRangeChanged(EventArgs.Empty);
                    Invalidate();
                }
            }
        }

        public Color TrackColor
        {
            get => trackColor;
            set
            {
                trackColor = value;
                Invalidate();
            }
        }

        public Color RangeColor
        {
            get => rangeColor;
            set
            {
                rangeColor = value;
                Invalidate();
            }
        }

        public Color HandleColor
        {
            get => handleColor;
            set
            {
                handleColor = value;
                Invalidate();
            }
        }

        #endregion Properties

        public RangeSlider()
        {
            // Enable double buffering for smooth rendering
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            // Set default size
            Height = 40;
            Width = 260;

            // Set dark mode colors for CTSegmenter's black theme
            BackColor = Color.FromArgb(45, 45, 48);
            ForeColor = Color.Gainsboro;
        }

        // Raise the RangeChanged event
        protected virtual void OnRangeChanged(EventArgs e)
        {
            RangeChanged?.Invoke(this, e);
        }

        // Convert a value to its position on the control
        private int ValueToPosition(int value)
        {
            if (maximum == minimum)
                return 0;

            return (int)((value - minimum) * (Width - handleSize) / (double)(maximum - minimum));
        }

        // Convert a position to its corresponding value
        private int PositionToValue(int position)
        {
            if (Width - handleSize <= 0)
                return minimum;

            int value = minimum + (int)((position * (maximum - minimum)) / (double)(Width - handleSize));
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        // Update the handle rectangles based on current values
        private void UpdateHandleRectangles()
        {
            int minPos = ValueToPosition(rangeMinimum);
            int maxPos = ValueToPosition(rangeMaximum);

            minHandleRect = new Rectangle(
                minPos,
                (Height - handleSize) / 2,
                handleSize,
                handleSize);

            maxHandleRect = new Rectangle(
                maxPos,
                (Height - handleSize) / 2,
                handleSize,
                handleSize);
        }

        // Paint the control
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Update handle positions
            UpdateHandleRectangles();

            // Draw the track
            int trackY = (Height - trackHeight) / 2;
            Rectangle trackRect = new Rectangle(handleSize / 2, trackY, Width - handleSize, trackHeight);
            using (Brush trackBrush = new SolidBrush(trackColor))
            {
                g.FillRectangle(trackBrush, trackRect);
            }

            // Draw the selected range
            int rangeX = minHandleRect.X + handleSize / 2;
            int rangeWidth = maxHandleRect.X - minHandleRect.X;
            Rectangle rangeRect = new Rectangle(rangeX, trackY, rangeWidth, trackHeight);
            using (Brush rangeBrush = new SolidBrush(rangeColor))
            {
                g.FillRectangle(rangeBrush, rangeRect);
            }

            // Draw the min/max values below the track
            using (Font valueFont = new Font(Font.FontFamily, 7f))
            using (Brush textBrush = new SolidBrush(ForeColor))
            {
                string minText = rangeMinimum.ToString();
                string maxText = rangeMaximum.ToString();

                g.DrawString(minText, valueFont, textBrush,
                    minHandleRect.X, trackY + trackHeight + 4);

                g.DrawString(maxText, valueFont, textBrush,
                    maxHandleRect.X + handleSize - g.MeasureString(maxText, valueFont).Width,
                    trackY + trackHeight + 4);
            }

            // Draw the handles
            using (Brush handleBrush = new SolidBrush(handleColor))
            using (Pen handleBorder = new Pen(handleBorderColor))
            {
                g.FillEllipse(handleBrush, minHandleRect);
                g.DrawEllipse(handleBorder, minHandleRect);

                g.FillEllipse(handleBrush, maxHandleRect);
                g.DrawEllipse(handleBorder, maxHandleRect);
            }
        }

        // Handle mouse down to start dragging
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (minHandleRect.Contains(e.Location))
            {
                draggingMinHandle = true;
                Capture = true;
            }
            else if (maxHandleRect.Contains(e.Location))
            {
                draggingMaxHandle = true;
                Capture = true;
            }
            else
            {
                // Check if clicked on the track to jump to that position
                int trackY = (Height - trackHeight) / 2;
                Rectangle trackRect = new Rectangle(handleSize / 2, trackY, Width - handleSize, trackHeight);

                if (trackRect.Contains(e.Location))
                {
                    // Determine which handle to move based on click position relative to handles
                    int clickX = e.X;
                    int midPoint = (minHandleRect.X + maxHandleRect.X) / 2;

                    if (clickX < midPoint)
                    {
                        RangeMinimum = PositionToValue(clickX - handleSize / 2);
                        draggingMinHandle = true;
                    }
                    else
                    {
                        RangeMaximum = PositionToValue(clickX - handleSize / 2);
                        draggingMaxHandle = true;
                    }

                    Capture = true;
                }
            }
        }

        // Handle mouse move to update values when dragging
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (draggingMinHandle)
            {
                int newPos = Math.Max(0, Math.Min(e.X, maxHandleRect.X - handleSize / 2));
                RangeMinimum = PositionToValue(newPos);
            }
            else if (draggingMaxHandle)
            {
                int newPos = Math.Max(minHandleRect.X + handleSize / 2, Math.Min(e.X, Width - handleSize / 2));
                RangeMaximum = PositionToValue(newPos - handleSize / 2);
            }
        }

        // Handle mouse up to end dragging
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            draggingMinHandle = false;
            draggingMaxHandle = false;
            Capture = false;
        }

        // Update when the control is resized
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        // Make sure the handles have enough space
        protected override Size DefaultSize => new Size(260, 40);
    }
}