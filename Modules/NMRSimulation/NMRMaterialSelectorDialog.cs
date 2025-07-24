//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace CTS.Modules.Simulation.NMR
{
    /// <summary>
    /// Dialog for selecting which materials represent pore/fluid space for NMR simulation
    /// </summary>
    public class NMRMaterialSelectorDialog : KryptonForm
    {
        public List<byte> SelectedMaterialIDs { get; private set; }

        private CheckedListBox clbMaterials;
        private KryptonButton btnSelectAll;
        private KryptonButton btnDeselectAll;
        private KryptonButton btnOK;
        private KryptonButton btnCancel;
        private Label lblInstructions;

        public NMRMaterialSelectorDialog(IEnumerable<Material> materials, List<byte> preselectedIDs = null)
        {
            try
            {
                this.Icon = CTS.Properties.Resources.favicon;
            }
            catch { }

            SelectedMaterialIDs = new List<byte>();
            InitializeComponent();
            LoadMaterials(materials, preselectedIDs);
        }

        private void InitializeComponent()
        {
            this.Text = "Select Pore/Fluid Materials for NMR";
            this.Size = new Size(400, 500);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.Black;

            // Main panel
            var mainPanel = new KryptonPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };
            mainPanel.StateCommon.Color1 = Color.Black;
            mainPanel.StateCommon.Color2 = Color.Black;

            // Instructions label
            lblInstructions = new Label
            {
                Text = "Select the materials that represent pore space or fluids.\n" +
                      "Only these materials will be included in the NMR simulation.\n" +
                      "Solid rock materials should NOT be selected.",
                Dock = DockStyle.Top,
                Height = 60,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9),
                Padding = new Padding(5)
            };

            // Materials list
            var listPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 5, 0, 5),
                BackColor = Color.Black
            };

            clbMaterials = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10),
                BorderStyle = BorderStyle.FixedSingle,
                SelectionMode = SelectionMode.One
            };

            // Selection buttons panel
            var selectionPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 35,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Black
            };

            btnSelectAll = new KryptonButton
            {
                Text = "Select All",
                Width = 90,
                Height = 30
            };
            btnSelectAll.Click += (s, e) => SetAllChecked(true);

            btnDeselectAll = new KryptonButton
            {
                Text = "Deselect All",
                Width = 90,
                Height = 30
            };
            btnDeselectAll.Click += (s, e) => SetAllChecked(false);

            selectionPanel.Controls.Add(btnSelectAll);
            selectionPanel.Controls.Add(btnDeselectAll);

            listPanel.Controls.Add(clbMaterials);
            listPanel.Controls.Add(selectionPanel);

            // Bottom buttons panel
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(5),
                BackColor = Color.Black
            };

            btnCancel = new KryptonButton
            {
                Text = "Cancel",
                Width = 80,
                Height = 30,
                DialogResult = DialogResult.Cancel
            };

            btnOK = new KryptonButton
            {
                Text = "OK",
                Width = 80,
                Height = 30,
                DialogResult = DialogResult.OK
            };
            btnOK.Click += BtnOK_Click;

            buttonPanel.Controls.Add(btnCancel);
            buttonPanel.Controls.Add(btnOK);

            // Add all to main panel
            mainPanel.Controls.Add(listPanel);
            mainPanel.Controls.Add(lblInstructions);
            mainPanel.Controls.Add(buttonPanel);

            this.Controls.Add(mainPanel);
        }

        private void LoadMaterials(IEnumerable<Material> materials, List<byte> preselectedIDs)
        {
            clbMaterials.Items.Clear();

            foreach (var material in materials)
            {
                // Skip exterior material (ID 0)
                if (material.ID == 0)
                    continue;

                // Add material with suggestion based on name
                string displayName = material.Name;

                // Add hints for common material names
                if (IsPoreMaterial(material.Name))
                {
                    displayName += " [Pore/Fluid]";
                }
                else if (IsSolidMaterial(material.Name))
                {
                    displayName += " [Solid]";
                }

                int index = clbMaterials.Items.Add(new MaterialItem(material, displayName));

                // Check if it should be preselected
                bool shouldCheck = false;

                if (preselectedIDs != null && preselectedIDs.Contains(material.ID))
                {
                    shouldCheck = true;
                }
                else if (preselectedIDs == null && IsPoreMaterial(material.Name))
                {
                    // Auto-select likely pore materials if no preselection provided
                    shouldCheck = true;
                }

                clbMaterials.SetItemChecked(index, shouldCheck);
            }
        }

        private bool IsPoreMaterial(string name)
        {
            string lowerName = name.ToLower();
            string[] poreKeywords = { "pore", "void", "water", "oil", "gas", "fluid", "brine", "fracture" };
            return poreKeywords.Any(keyword => lowerName.Contains(keyword));
        }

        private bool IsSolidMaterial(string name)
        {
            string lowerName = name.ToLower();
            string[] solidKeywords = { "rock", "solid", "grain", "matrix", "mineral", "quartz", "calcite", "clay" };
            return solidKeywords.Any(keyword => lowerName.Contains(keyword));
        }

        private void SetAllChecked(bool isChecked)
        {
            for (int i = 0; i < clbMaterials.Items.Count; i++)
            {
                clbMaterials.SetItemChecked(i, isChecked);
            }
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            SelectedMaterialIDs.Clear();

            foreach (var item in clbMaterials.CheckedItems)
            {
                if (item is MaterialItem materialItem)
                {
                    SelectedMaterialIDs.Add(materialItem.Material.ID);
                }
            }

            if (SelectedMaterialIDs.Count == 0)
            {
                var result = MessageBox.Show(
                    "No materials selected. The NMR simulation requires at least one pore/fluid material.\n\n" +
                    "Do you want to continue without any selection?",
                    "No Materials Selected",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result == DialogResult.No)
                {
                    DialogResult = DialogResult.None;
                    return;
                }
            }

            this.Close();
        }

        // Helper class to store material with display name
        private class MaterialItem
        {
            public Material Material { get; }
            public string DisplayName { get; }

            public MaterialItem(Material material, string displayName)
            {
                Material = material;
                DisplayName = displayName;
            }

            public override string ToString()
            {
                return DisplayName;
            }
        }
    }
}