using System.Windows.Forms;

namespace CTSegmenter
{
    // ------------------------------------------------------------------------
    // Prompt helper
    // ------------------------------------------------------------------------
    public static class Prompt
    {
        public static string ShowDialog(string text, string caption)
        {
            using (Form prompt = new Form()
            {
                Width = 400,
                Height = 150,
                Text = caption,
                StartPosition = FormStartPosition.CenterParent
            })
            {
                Label textLabel = new Label() { Left = 10, Top = 20, Text = text, AutoSize = true };
                TextBox textBox = new TextBox() { Left = 10, Top = 50, Width = 360 };
                Button confirmation = new Button() { Text = "OK", Left = 300, Width = 70, Top = 80, DialogResult = DialogResult.OK };
                confirmation.Click += (sender, e) => prompt.Close();
                prompt.TopMost = true;
                prompt.FormBorderStyle = FormBorderStyle.FixedSingle;
                prompt.Controls.Add(textLabel);
                prompt.Controls.Add(textBox);
                prompt.Controls.Add(confirmation);
                prompt.AcceptButton = confirmation;
                return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : "";
            }
        }
    }
}