// SAMSettings.cs
using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;

namespace CTSegmenter
{
    /// <summary>
    /// Holds settings for segmentation and fusion.
    /// We remove the threshold trackbar concept, using SAM2.1 typical default 0.5 for mask binarization.
    /// </summary>
    public class SAMSettingsParams
    {
        public string FusionAlgorithm { get; set; }
        public int ImageInputSize { get; set; }
        public string ModelFolderPath { get; set; }
        public bool EnableMlp { get; set; }
        public bool EnableMultiMask { get; set; } = false;
        public bool UseSam2Models { get; set; } = true;
        public bool UseCpuExecutionProvider { get; set; } = true; // default to CPU for stability
        public bool UseSelectiveHoleFilling { get; set; } = false;

        public string ImageEncoderPath => Path.Combine(ModelFolderPath,
            UseSam2Models ? "sam2.1_large.encoder.onnx" : "image_encoder_hiera_t.onnx");
        public string PromptEncoderPath => Path.Combine(ModelFolderPath, "prompt_encoder_hiera_t.onnx");
        public string MaskDecoderPath => Path.Combine(ModelFolderPath,
            UseSam2Models ? "sam2.1_large.decoder.onnx" : "mask_decoder_hiera_t.onnx");
        public string MemoryAttentionPath => Path.Combine(ModelFolderPath, "memory_attention_hiera_t.onnx");
        public string MemoryEncoderPath => Path.Combine(ModelFolderPath, "memory_encoder_hiera_t.onnx");
        public string MlpPath => Path.Combine(ModelFolderPath, "mlp_hiera_t.onnx");
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
        private CheckBox checkBoxEnableMlp;
        private CheckBox checkBoxUseSam2;
        private CheckBox checkBoxUseCpu;
        private CheckBox checkBoxMultiMask;
        private CheckBox checkBoxSelective;

        private SAMForm _parentForm;

        public SAMSettingsParams SettingsResult { get; private set; }

        public SAMSettings(SAMForm parentForm)
        {
            _parentForm = parentForm;
            InitializeComponent();
            comboBoxFusionAlgorithm.SelectedIndex = 0;
            numericUpDownImageSize.Value = 1024;
            textBoxModelFolder.Text = Path.Combine(Application.StartupPath, "ONNX");
            checkBoxEnableMlp.Checked = false;
            checkBoxUseCpu.Checked = true;
            checkBoxUseSam2.Checked = true;
            checkBoxMultiMask.Checked = false;
            checkBoxSelective.Checked = false;

            if (_parentForm != null && _parentForm.Icon != null)
                this.Icon = _parentForm.Icon;
        }

        private void InitializeComponent()
        {
            this.Text = "SAM Settings";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ClientSize = new Size(450, 350);

            labelFusion = new Label() { Text = "Fusion Algorithm:", Left = 20, Top = 20, AutoSize = true };
            comboBoxFusionAlgorithm = new ComboBox() { Left = 150, Top = 15, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            comboBoxFusionAlgorithm.Items.Add("Majority Voting Fusion");
            comboBoxFusionAlgorithm.Items.Add("Weighted Averaging Fusion");
            comboBoxFusionAlgorithm.Items.Add("Probability Map Fusion");
            comboBoxFusionAlgorithm.Items.Add("CRF Fusion");
            comboBoxFusionAlgorithm.SelectedIndex = 0;

            labelImageSize = new Label() { Text = "Image Input Size:", Left = 20, Top = 60, AutoSize = true };
            numericUpDownImageSize = new NumericUpDown() { Left = 150, Top = 55, Width = 100 };
            numericUpDownImageSize.Minimum = 256;
            numericUpDownImageSize.Maximum = 4096;
            numericUpDownImageSize.Value = 1024;

            labelModelFolder = new Label() { Text = "Model Folder Path:", Left = 20, Top = 100, AutoSize = true };
            textBoxModelFolder = new TextBox() { Left = 150, Top = 95, Width = 200 };
            buttonBrowse = new Button() { Text = "Browse...", Left = 360, Top = 93, Width = 70 };
            buttonBrowse.Click += ButtonBrowse_Click;

            checkBoxEnableMlp = new CheckBox() { Text = "Enable MLP", Left = 20, Top = 140, AutoSize = true };
            checkBoxUseCpu = new CheckBox() { Text = "Use CPU Execution Provider", Left = 20, Top = 170, AutoSize = true };
            checkBoxUseSam2 = new CheckBox() { Text = "Use SAM2.1 Models", Left = 20, Top = 200, AutoSize = true };
            checkBoxMultiMask = new CheckBox() { Text = "Enable Multi-Mask", Left = 20, Top = 230, AutoSize = true };
            checkBoxSelective = new CheckBox() { Text = "Selective Hole Filling", Left = 20, Top = 260, AutoSize = true };

            buttonOK = new Button() { Text = "OK", Left = 220, Width = 80, Top = 290, DialogResult = DialogResult.OK };
            buttonOK.Click += ButtonOK_Click;
            buttonCancel = new Button() { Text = "Cancel", Left = 320, Width = 80, Top = 290, DialogResult = DialogResult.Cancel };

            this.Controls.Add(labelFusion);
            this.Controls.Add(comboBoxFusionAlgorithm);
            this.Controls.Add(labelImageSize);
            this.Controls.Add(numericUpDownImageSize);
            this.Controls.Add(labelModelFolder);
            this.Controls.Add(textBoxModelFolder);
            this.Controls.Add(buttonBrowse);
            this.Controls.Add(checkBoxEnableMlp);
            this.Controls.Add(checkBoxUseCpu);
            this.Controls.Add(checkBoxUseSam2);
            this.Controls.Add(checkBoxMultiMask);
            this.Controls.Add(checkBoxSelective);
            this.Controls.Add(buttonOK);
            this.Controls.Add(buttonCancel);
        }

        private void ButtonBrowse_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    textBoxModelFolder.Text = fbd.SelectedPath;
                }
            }
        }

        private void ButtonOK_Click(object sender, EventArgs e)
        {
            SettingsResult = new SAMSettingsParams
            {
                FusionAlgorithm = comboBoxFusionAlgorithm.SelectedItem.ToString(),
                ImageInputSize = (int)numericUpDownImageSize.Value,
                ModelFolderPath = textBoxModelFolder.Text,
                EnableMlp = checkBoxEnableMlp.Checked,
                UseCpuExecutionProvider = checkBoxUseCpu.Checked,
                UseSam2Models = checkBoxUseSam2.Checked,
                EnableMultiMask = checkBoxMultiMask.Checked,
                UseSelectiveHoleFilling = checkBoxSelective.Checked
            };
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
