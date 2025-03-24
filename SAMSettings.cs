using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;

namespace CTSegmenter
{
    /// <summary>
    /// Holds settings for segmentation and fusion.
    /// </summary>
    public class SAMSettingsParams
    {
        public string FusionAlgorithm { get; set; }
        public int ImageInputSize { get; set; }
        public string ModelFolderPath { get; set; }
        public bool EnableMlp { get; set; }
        public bool EnableMultiMask { get; set; } = false;
        public bool UseSam2Models { get; set; } = true;
        // New property for CPU execution - default to true for stability
        public bool UseCpuExecutionProvider { get; set; } = true;

        // Existing path properties remain unchanged...
        public string ImageEncoderPath => Path.Combine(ModelFolderPath,
            UseSam2Models ? "sam2.1_large.encoder.onnx" : "image_encoder_hiera_t.onnx");
        public string PromptEncoderPath => Path.Combine(ModelFolderPath, "prompt_encoder_hiera_t.onnx");
        public string MaskDecoderPath => Path.Combine(ModelFolderPath,
            UseSam2Models ? "sam2.1_large.decoder.onnx" : "mask_decoder_hiera_t.onnx");
        public string MemoryAttentionPath => Path.Combine(ModelFolderPath, "memory_attention_hiera_t.onnx");
        public string MemoryEncoderPath => Path.Combine(ModelFolderPath, "memory_encoder_hiera_t.onnx");
        public string MlpPath => Path.Combine(ModelFolderPath, "mlp_hiera_t.onnx");

        public string EncoderPath => Path.Combine(ModelFolderPath, "sam2.1_large.encoder.onnx");
        public string DecoderPath => Path.Combine(ModelFolderPath, "sam2.1_large.decoder.onnx");

        public bool UseSelectiveHoleFilling { get; set; } = true;
    }


    public partial class SAMSettings : Form
    {
        private ComboBox comboBoxFusionAlgorithm;
        private NumericUpDown numericUpDownImageSize;
        private TextBox textBoxModelFolder;
        private Button buttonBrowse;
        private Button buttonOK;
        private Button buttonCancel;
        private Label labelFusion;
        private Label labelImageSize;
        private Label labelModelFolder;
        private CheckBox checkBoxRealTimeProcessing;
        private CheckBox checkBoxEnableMlp; // for MLP post–processing
        private CheckBox checkBoxUseSam2;
        private CheckBox checkBoxUseCpu; // New field for CPU execution


        // Declare radio buttons as class-level fields
        private RadioButton rbtnSelective;
        private RadioButton rbtnStandard;

        private SAMForm _parentForm;
        public SAMSettingsParams SettingsResult { get; private set; }
        private CheckBox checkBoxMultiCandidate;

        public SAMSettings(SAMForm parentForm)
        {
            _parentForm = parentForm;
            InitializeComponent();
            comboBoxFusionAlgorithm.SelectedIndex = 0;
            numericUpDownImageSize.Value = 1024;
            textBoxModelFolder.Text = Path.Combine(Application.StartupPath, "ONNX");
            checkBoxRealTimeProcessing.Checked = false;
            checkBoxEnableMlp.Checked = false; // default MLP off
            checkBoxUseCpu.Checked = true; // Default to using CPU for stability

            if (_parentForm != null && _parentForm.Icon != null)
                this.Icon = _parentForm.Icon;
        }


        private void InitializeComponent()
        {
            this.Text = "SAM Settings";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ClientSize = new Size(470, 410); // Increased height to accommodate all controls
            this.TopMost = true;

            labelFusion = new Label() { Text = "Fusion Algorithm:", Left = 20, Top = 20, AutoSize = true };
            labelImageSize = new Label() { Text = "Image Input Size:", Left = 20, Top = 60, AutoSize = true };
            labelModelFolder = new Label() { Text = "Model Folder Path:", Left = 20, Top = 100, AutoSize = true };

            comboBoxFusionAlgorithm = new ComboBox() { Left = 150, Top = 15, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            comboBoxFusionAlgorithm.Items.Add("Majority Voting Fusion");
            comboBoxFusionAlgorithm.Items.Add("Weighted Averaging Fusion");
            comboBoxFusionAlgorithm.Items.Add("Probability Map Fusion");
            comboBoxFusionAlgorithm.Items.Add("CRF Fusion");

            numericUpDownImageSize = new NumericUpDown() { Left = 150, Top = 55, Width = 100 };
            numericUpDownImageSize.Minimum = 256;
            numericUpDownImageSize.Maximum = 4096;
            numericUpDownImageSize.Increment = 1;

            textBoxModelFolder = new TextBox() { Left = 150, Top = 95, Width = 200 };
            buttonBrowse = new Button() { Text = "Browse...", Left = 360, Top = 93, Width = 70 };
            buttonBrowse.Click += ButtonBrowse_Click;

            checkBoxRealTimeProcessing = new CheckBox() { Text = "Real Time Processing", Left = 20, Top = 140, AutoSize = true };
            checkBoxEnableMlp = new CheckBox() { Text = "Enable MLP Post-Processing", Left = 20, Top = 170, AutoSize = true };

            // Add multi-candidate checkbox with proper spacing
            checkBoxMultiCandidate = new CheckBox
            {
                Text = "Multi-Candidate Masks",
                Left = 20,
                Top = 200,
                AutoSize = true
            };

            // Add CPU execution checkbox
            checkBoxUseCpu = new CheckBox
            {
                Text = "Use CPU Execution Provider (more stable, slower)",
                Left = 20,
                Top = 230,
                AutoSize = true,
                Checked = true // Default to true for stability
            };

            // Properly position radio buttons after the checkboxes
            rbtnSelective = new RadioButton()
            {
                Text = "Selective Hole Filling",
                Left = 20,
                Top = 260, // Moved down to avoid overlap
                AutoSize = true,
                Checked = true // default option
            };

            rbtnStandard = new RadioButton()
            {
                Text = "Standard Hole Filling",
                Left = 20,
                Top = 290, // Moved down to avoid overlap
                AutoSize = true
            };

            checkBoxUseSam2 = new CheckBox()
            {
                Text = "Use SAM 2.1 Models",
                Left = 20,
                Top = 320, // Adjust position as needed
                AutoSize = true,
                Checked = true // Default to SAM 2.1
            };

            buttonOK = new Button() { Text = "OK", Left = 150, Top = 360, Width = 80 }; // Moved down
            buttonOK.Click += ButtonOK_Click;
            buttonCancel = new Button() { Text = "Cancel", Left = 250, Top = 360, Width = 80 }; // Moved down
            buttonCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };

            this.Controls.Add(labelFusion);
            this.Controls.Add(comboBoxFusionAlgorithm);
            this.Controls.Add(labelImageSize);
            this.Controls.Add(numericUpDownImageSize);
            this.Controls.Add(labelModelFolder);
            this.Controls.Add(textBoxModelFolder);
            this.Controls.Add(buttonBrowse);
            this.Controls.Add(checkBoxRealTimeProcessing);
            this.Controls.Add(checkBoxEnableMlp);
            this.Controls.Add(checkBoxMultiCandidate);
            this.Controls.Add(checkBoxUseCpu); // Add the new checkbox
            this.Controls.Add(rbtnSelective);
            this.Controls.Add(rbtnStandard);
            this.Controls.Add(checkBoxUseSam2);
            this.Controls.Add(buttonOK);
            this.Controls.Add(buttonCancel);
        }



        private void ButtonBrowse_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.SelectedPath = textBoxModelFolder.Text;
                if (fbd.ShowDialog() == DialogResult.OK)
                    textBoxModelFolder.Text = fbd.SelectedPath;
            }
        }

        private void ButtonOK_Click(object sender, EventArgs e)
        {
            SAMSettingsParams settings = new SAMSettingsParams
            {
                FusionAlgorithm = comboBoxFusionAlgorithm.SelectedItem.ToString(),
                ImageInputSize = (int)numericUpDownImageSize.Value,
                ModelFolderPath = textBoxModelFolder.Text,
                EnableMlp = checkBoxEnableMlp.Checked,
                UseSelectiveHoleFilling = rbtnSelective.Checked,
                EnableMultiMask = checkBoxMultiCandidate.Checked,
                UseSam2Models = checkBoxUseSam2.Checked,
                UseCpuExecutionProvider = checkBoxUseCpu.Checked // Save CPU execution setting
            };

            _parentForm.SetRealTimeProcessing(checkBoxRealTimeProcessing.Checked);
            _parentForm?.UpdateSettings(settings);
            this.SettingsResult = settings;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }

}
