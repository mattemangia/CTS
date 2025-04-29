using System;
using System.Windows.Forms;

namespace CTSegmenter
{
    public class ExportAnimationDialog : Form
    {
        private RadioButton radFrames;
        private RadioButton radVideo;
        private Label lblFps;
        private NumericUpDown nudFps;
        private Label lblQuality;
        private TrackBar trkQuality;
        private Label lblQualityValue;
        private Button btnOk;
        private Button btnCancel;
        private Label lblDuration;
        private NumericUpDown nudDuration;
        private Label lblDurationUnit;

        public bool ExportAsFrames { get; private set; }
        public int Fps { get; private set; }
        public int Quality { get; private set; }
        public int DurationSeconds { get; private set; }

        public ExportAnimationDialog()
        {
            InitializeComponents();
        }

        private void InitializeComponents()
        {
            this.Text = "Export Animation";
            this.Width = 350;
            this.Height = 300;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;

            // Export format options
            radFrames = new RadioButton
            {
                Text = "Export as frame images (PNG)",
                Location = new System.Drawing.Point(20, 20),
                Width = 250,
                Checked = true
            };

            radVideo = new RadioButton
            {
                Text = "Export as WMV video",
                Location = new System.Drawing.Point(20, 45),
                Width = 250
            };

            // FPS selection
            lblFps = new Label
            {
                Text = "Frames per second:",
                Location = new System.Drawing.Point(30, 80),
                Width = 120
            };

            nudFps = new NumericUpDown
            {
                Location = new System.Drawing.Point(160, 78),
                Width = 60,
                Minimum = 1,
                Maximum = 60,
                Value = 10
            };

            // Duration selection
            lblDuration = new Label
            {
                Text = "Duration (seconds):",
                Location = new System.Drawing.Point(30, 110),
                Width = 120
            };

            nudDuration = new NumericUpDown
            {
                Location = new System.Drawing.Point(160, 108),
                Width = 60,
                Minimum = 1,
                Maximum = 300,
                Value = 10
            };

            lblDurationUnit = new Label
            {
                Text = "seconds",
                Location = new System.Drawing.Point(225, 110),
                Width = 60
            };

            // Quality selection (for video)
            lblQuality = new Label
            {
                Text = "Video Quality:",
                Location = new System.Drawing.Point(30, 140),
                Width = 100
            };

            trkQuality = new TrackBar
            {
                Location = new System.Drawing.Point(30, 165),
                Width = 200,
                Minimum = 1,
                Maximum = 10,
                Value = 8,
                TickFrequency = 1
            };
            trkQuality.ValueChanged += (s, e) => lblQualityValue.Text = $"{trkQuality.Value}/10";

            lblQualityValue = new Label
            {
                Text = "8/10",
                Location = new System.Drawing.Point(240, 168),
                Width = 50
            };

            // OK and Cancel buttons
            btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new System.Drawing.Point(160, 220),
                Width = 75
            };
            btnOk.Click += (s, e) =>
            {
                ExportAsFrames = radFrames.Checked;
                Fps = (int)nudFps.Value;
                Quality = trkQuality.Value;
                DurationSeconds = (int)nudDuration.Value;
            };

            btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new System.Drawing.Point(245, 220),
                Width = 75
            };

            // Enable/disable controls based on selected export type
            radVideo.CheckedChanged += (s, e) =>
            {
                bool isVideoSelected = radVideo.Checked;
                lblQuality.Enabled = isVideoSelected;
                trkQuality.Enabled = isVideoSelected;
                lblQualityValue.Enabled = isVideoSelected;
            };

            // Add controls to form
            this.Controls.Add(radFrames);
            this.Controls.Add(radVideo);
            this.Controls.Add(lblFps);
            this.Controls.Add(nudFps);
            this.Controls.Add(lblDuration);
            this.Controls.Add(nudDuration);
            this.Controls.Add(lblDurationUnit);
            this.Controls.Add(lblQuality);
            this.Controls.Add(trkQuality);
            this.Controls.Add(lblQualityValue);
            this.Controls.Add(btnOk);
            this.Controls.Add(btnCancel);
        }
    }
}
