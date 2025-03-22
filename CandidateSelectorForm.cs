using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CTSegmenter
{
    public class CandidateSelectorForm : Form
    {
        private TabControl tabControl;
        private Button btnOK;
        private Button btnCancel;
        private Button btnPreview;
        private Button btnSaveComposite;
        private SAMForm _parentForm; // Reference to parent form for previews

        // Dictionary: key = direction ("XY", "XZ", "YZ")
        // Value: Dictionary with key = material name, value = selected candidate index (int)
        public Dictionary<string, Dictionary<string, int>> Selections { get; private set; }

        // The full candidate images provided by SAM:
        // Dictionary: key = direction; value = dictionary (material name → List of candidate Bitmaps)
        private Dictionary<string, Dictionary<string, List<Bitmap>>> _allCandidates;

        // Store the mask bitmaps for later retrieval
        public Dictionary<string, Dictionary<string, Bitmap>> SelectedMasks { get; private set; }

        // Array of distinct colors for materials
        private static readonly Color[] MaterialColors = new Color[] {
            Color.Red, Color.Green, Color.Blue, Color.Yellow, Color.Magenta,
            Color.Cyan, Color.Orange, Color.Purple, Color.Lime, Color.Teal,
            Color.Pink, Color.Brown, Color.Navy, Color.Maroon, Color.Olive
        };

        public CandidateSelectorForm(Dictionary<string, Dictionary<string, List<Bitmap>>> allCandidates, SAMForm parentForm = null)
        {
            _allCandidates = allCandidates;
            _parentForm = parentForm;
            Selections = new Dictionary<string, Dictionary<string, int>>();
            SelectedMasks = new Dictionary<string, Dictionary<string, Bitmap>>();
            InitializeComponent();
            PopulateTabs();
        }

        private void InitializeComponent()
        {
            this.Text = "Select Candidate Masks";
            this.Size = new Size(1000, 700); // Larger size for better viewing
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.Sizable; // Allow user to resize
            this.MinimumSize = new Size(800, 600);

            tabControl = new TabControl();
            tabControl.Dock = DockStyle.Fill; // Fill the form
            tabControl.Padding = new Point(10, 10);
            this.Controls.Add(tabControl);

            // Create button panel for all buttons
            Panel buttonPanel = new Panel();
            buttonPanel.Dock = DockStyle.Bottom;
            buttonPanel.Height = 50;
            buttonPanel.Padding = new Padding(10);

            // Preview button
            btnPreview = new Button();
            btnPreview.Text = "Preview in Main View";
            btnPreview.Width = 150;
            btnPreview.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnPreview.Location = new Point(15, 15);
            btnPreview.Click += BtnPreview_Click;

            // Save Composite button
            btnSaveComposite = new Button();
            btnSaveComposite.Text = "Save Composite Image";
            btnSaveComposite.Width = 150;
            btnSaveComposite.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnSaveComposite.Location = new Point(175, 15);
            btnSaveComposite.Click += BtnSaveComposite_Click;

            // OK button
            btnOK = new Button();
            btnOK.Text = "OK";
            btnOK.DialogResult = DialogResult.OK;
            btnOK.Width = 80;
            btnOK.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnOK.Location = new Point(buttonPanel.Width - 180, 15);
            btnOK.Click += BtnOK_Click;

            // Cancel button
            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Width = 80;
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnCancel.Location = new Point(buttonPanel.Width - 90, 15);

            buttonPanel.Controls.Add(btnPreview);
            buttonPanel.Controls.Add(btnSaveComposite);
            buttonPanel.Controls.Add(btnOK);
            buttonPanel.Controls.Add(btnCancel);
            this.Controls.Add(buttonPanel);
        }

        private void PopulateTabs()
        {
            // For each direction, create a tab page
            foreach (string direction in new string[] { "XY", "XZ", "YZ" })
            {
                // Create tab and selections dict
                TabPage tab = new TabPage(direction);
                Selections[direction] = new Dictionary<string, int>();
                SelectedMasks[direction] = new Dictionary<string, Bitmap>();

                // Skip if we don't have candidates for this direction
                if (!_allCandidates.ContainsKey(direction))
                {
                    tabControl.TabPages.Add(tab);
                    continue;
                }

                // Get all materials for this direction
                var materials = _allCandidates[direction].Keys.ToList();

                // Create a panel for each material to contain its candidates
                TableLayoutPanel mainPanel = new TableLayoutPanel();
                mainPanel.Dock = DockStyle.Fill;
                mainPanel.AutoScroll = true;
                mainPanel.CellBorderStyle = TableLayoutPanelCellBorderStyle.Single;

                // Set up columns - one for each material
                mainPanel.ColumnCount = materials.Count;
                float colWidth = 100.0f / materials.Count;

                for (int i = 0; i < materials.Count; i++)
                {
                    mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, colWidth));

                    // Create a panel for this material
                    Panel materialPanel = new Panel();
                    materialPanel.Dock = DockStyle.Fill;
                    materialPanel.AutoScroll = true;

                    string materialName = materials[i];
                    var candidates = _allCandidates[direction][materialName];

                    // Add header label for material
                    Label headerLabel = new Label();
                    headerLabel.Text = materialName;
                    headerLabel.Dock = DockStyle.Top;
                    headerLabel.Height = 30;
                    headerLabel.Font = new Font(headerLabel.Font, FontStyle.Bold);
                    headerLabel.TextAlign = ContentAlignment.MiddleCenter;
                    headerLabel.BackColor = Color.LightGray;
                    materialPanel.Controls.Add(headerLabel);

                    // Create a new panel solely to contain radio buttons - this ensures proper grouping
                    Panel radioContainer = new Panel();
                    radioContainer.Dock = DockStyle.Fill;
                    radioContainer.AutoScroll = true;

                    // Add each candidate
                    for (int j = 0; j < candidates.Count; j++)
                    {
                        // Create a candidate row panel
                        Panel candidatePanel = new Panel();
                        candidatePanel.Width = materialPanel.Width - 30;
                        candidatePanel.Height = 200;
                        candidatePanel.Left = 10;
                        candidatePanel.Top = j * 205; // Position each below the previous one

                        // Create picture box for mask
                        PictureBox pb = new PictureBox();
                        pb.Width = candidatePanel.Width - 20;
                        pb.Height = 170;
                        pb.Left = 10;
                        pb.Top = 5;
                        pb.SizeMode = PictureBoxSizeMode.Zoom;
                        pb.Image = candidates[j];
                        pb.Tag = j;
                        pb.BorderStyle = BorderStyle.FixedSingle;

                        // Create radio button - these will be grouped by the radioContainer parent
                        RadioButton rb = new RadioButton();
                        rb.Text = $"Candidate {j + 1}";
                        rb.Left = 10;
                        rb.Top = 175;
                        rb.Width = candidatePanel.Width - 20;
                        rb.Height = 25;
                        rb.Tag = new Tuple<string, int>(materialName, j);
                        rb.Checked = (j == 0); // Default to first

                        if (rb.Checked)
                        {
                            // Initialize selection
                            Selections[direction][materialName] = 0;
                            SelectedMasks[direction][materialName] = new Bitmap(candidates[0]);
                        }

                        // Set up events  
                        int candidateIndex = j; // Capture for use in lambda
                        pb.Click += (s, e) => {
                            // Find the radio button in this panel and check it
                            foreach (Control c in candidatePanel.Controls)
                            {
                                if (c is RadioButton)
                                {
                                    ((RadioButton)c).Checked = true;
                                    break;
                                }
                            }
                        };

                        rb.CheckedChanged += (s, e) => {
                            if (((RadioButton)s).Checked)
                            {
                                var tag = (Tuple<string, int>)((RadioButton)s).Tag;
                                string mat = tag.Item1;
                                int idx = tag.Item2;
                                Selections[direction][mat] = idx;

                                // Store selected bitmap
                                if (_allCandidates[direction][mat].Count > idx)
                                {
                                    SelectedMasks[direction][mat] = new Bitmap(_allCandidates[direction][mat][idx]);
                                }
                            }
                        };

                        candidatePanel.Controls.Add(pb);
                        candidatePanel.Controls.Add(rb);
                        radioContainer.Controls.Add(candidatePanel);
                    }

                    materialPanel.Controls.Add(radioContainer);
                    mainPanel.Controls.Add(materialPanel, i, 0);
                }

                tab.Controls.Add(mainPanel);
                tabControl.TabPages.Add(tab);
            }
        }





        private void BtnPreview_Click(object sender, EventArgs e)
        {
            try
            {
                // Get current tab to determine direction
                string direction = tabControl.SelectedTab.Text; // "XY", "XZ", or "YZ"

                // Only proceed if parent form available and we have masks to preview
                if (_parentForm != null && SelectedMasks.ContainsKey(direction) && SelectedMasks[direction].Count > 0)
                {
                    // Pass selected masks to parent form for preview
                    _parentForm.PreviewSelectedMasks(direction, SelectedMasks[direction]);

                    MessageBox.Show($"Previewing masks in {direction} view. Click Apply in SAM Form to commit changes.",
                        "Preview Active", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("No masks available to preview, or preview not available in this context.",
                        "Preview Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error previewing masks: {ex.Message}",
                    "Preview Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnSaveComposite_Click(object sender, EventArgs e)
        {
            try
            {
                // Get current tab to determine which view's composite to save
                string direction = tabControl.SelectedTab.Text; // "XY", "XZ", or "YZ"

                // Show save dialog
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "PNG Image|*.png";
                    saveDialog.Title = $"Save {direction} Composite Image";
                    saveDialog.FileName = $"SAM_{direction}_Composite.png";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        // Create and save the composite image
                        using (Bitmap composite = CreateCompositeImage(direction))
                        {
                            composite.Save(saveDialog.FileName, ImageFormat.Png);
                            MessageBox.Show($"Composite image saved to {saveDialog.FileName}",
                                "Save Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving composite image: {ex.Message}",
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private Bitmap CreateCompositeImage(string direction)
        {
            if (!_allCandidates.ContainsKey(direction) || _allCandidates[direction].Count == 0)
                throw new InvalidOperationException($"No candidates available for {direction} direction");

            // Get materials and determine dimensions
            var materials = _allCandidates[direction].Keys.ToList();
            int numMaterials = materials.Count;

            // Determine max number of candidates
            int maxCandidates = 0;
            int sampleWidth = 0;
            int sampleHeight = 0;

            foreach (var material in materials)
            {
                var candidates = _allCandidates[direction][material];
                maxCandidates = Math.Max(maxCandidates, candidates.Count);

                if (candidates.Count > 0)
                {
                    sampleWidth = candidates[0].Width;
                    sampleHeight = candidates[0].Height;
                }
            }

            if (maxCandidates == 0 || sampleWidth == 0 || sampleHeight == 0)
                throw new InvalidOperationException("No valid candidates found");

            // Calculate composite image dimensions
            const int padding = 10;
            const int headerHeight = 50;
            const int rowHeaderWidth = 120;

            int cellWidth = sampleWidth;
            int cellHeight = sampleHeight;
            int imageWidth = rowHeaderWidth + (cellWidth + padding) * numMaterials + padding;
            int imageHeight = headerHeight + (cellHeight + padding) * maxCandidates + padding;

            // Create the composite image
            Bitmap composite = new Bitmap(imageWidth, imageHeight);
            using (Graphics g = Graphics.FromImage(composite))
            {
                // Fill background
                g.Clear(Color.Black);

                // Draw header labels (material names)
                using (Font headerFont = new Font("Arial", 12, FontStyle.Bold))
                using (Brush textBrush = new SolidBrush(Color.White))
                {
                    // Draw corner cell
                    Rectangle cornerRect = new Rectangle(0, 0, rowHeaderWidth, headerHeight);
                    g.DrawString("Candidate", headerFont, textBrush, cornerRect,
                        new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });

                    // Draw column headers (material names)
                    for (int i = 0; i < numMaterials; i++)
                    {
                        Rectangle headerRect = new Rectangle(
                            rowHeaderWidth + i * (cellWidth + padding),
                            0,
                            cellWidth,
                            headerHeight);

                        g.DrawString(materials[i], headerFont, textBrush, headerRect,
                            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                    }
                }

                // Draw each cell with colored mask
                for (int candidateIdx = 0; candidateIdx < maxCandidates; candidateIdx++)
                {
                    // Draw row header (candidate number)
                    using (Font rowFont = new Font("Arial", 10))
                    using (Brush textBrush = new SolidBrush(Color.White))
                    {
                        Rectangle rowHeaderRect = new Rectangle(
                            0,
                            headerHeight + candidateIdx * (cellHeight + padding),
                            rowHeaderWidth,
                            cellHeight);

                        g.DrawString($"Candidate {candidateIdx + 1}", rowFont, textBrush, rowHeaderRect,
                            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                    }

                    // Draw each material's mask for this candidate
                    for (int matIdx = 0; matIdx < numMaterials; matIdx++)
                    {
                        string materialName = materials[matIdx];
                        var candidates = _allCandidates[direction][materialName];

                        if (candidateIdx < candidates.Count)
                        {
                            // Get color for this material
                            Color materialColor = MaterialColors[matIdx % MaterialColors.Length];

                            // Create colored version of the mask
                            Bitmap origMask = candidates[candidateIdx];
                            Bitmap coloredMask = new Bitmap(origMask.Width, origMask.Height);

                            // Apply the color
                            for (int y = 0; y < origMask.Height; y++)
                            {
                                for (int x = 0; x < origMask.Width; x++)
                                {
                                    Color pixelColor = origMask.GetPixel(x, y);
                                    if (pixelColor.R > 128) // If mask is active here
                                    {
                                        coloredMask.SetPixel(x, y, materialColor);
                                    }
                                    else
                                    {
                                        coloredMask.SetPixel(x, y, Color.Black);
                                    }
                                }
                            }

                            // Draw to the composite
                            int cellX = rowHeaderWidth + matIdx * (cellWidth + padding);
                            int cellY = headerHeight + candidateIdx * (cellHeight + padding);
                            g.DrawImage(coloredMask, cellX, cellY, cellWidth, cellHeight);

                            // Draw border around selected candidate
                            if (Selections[direction].ContainsKey(materialName) &&
                                Selections[direction][materialName] == candidateIdx)
                            {
                                using (Pen selectedPen = new Pen(Color.White, 3))
                                {
                                    g.DrawRectangle(selectedPen,
                                        cellX, cellY, cellWidth, cellHeight);
                                }
                            }

                            // Clean up
                            coloredMask.Dispose();
                        }
                    }
                }
            }

            return composite;
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            // Make sure we have selections for all materials
            foreach (string direction in new string[] { "XY", "XZ", "YZ" })
            {
                if (_allCandidates.ContainsKey(direction))
                {
                    foreach (var material in _allCandidates[direction].Keys)
                    {
                        if (!Selections[direction].ContainsKey(material))
                        {
                            // Default to candidate 0 if none selected
                            Selections[direction][material] = 0;

                            // Store the bitmap too
                            if (_allCandidates[direction][material].Count > 0)
                            {
                                SelectedMasks[direction][material] =
                                    new Bitmap(_allCandidates[direction][material][0]);
                            }
                        }
                    }
                }
            }

            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
