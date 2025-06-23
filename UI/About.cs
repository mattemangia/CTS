using System;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace CTS
{
    public class About : KryptonForm
    {
        private KryptonPanel mainPanel;
        private PictureBox logoBox;
        private KryptonLabel titleLabel;
        private KryptonLabel authorLabel;
        private KryptonLabel affiliationLabel;
        private KryptonLabel departmentLabel;
        private KryptonLabel emailLabel;
        private KryptonLabel licenseLabel;
        private KryptonLinkLabel emailLinkLabel;
        private KryptonButton okButton;

        public About()
        {
            InitializeComponents();
            ApplyDarkTheme();
        }

        private void InitializeComponents()
        {
            // Form settings
            this.Text = "About CTS";
            this.Size = new Size(500, 700);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;

            // Main panel
            mainPanel = new KryptonPanel();
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.BackColor=Color.Black;

            // Logo
            logoBox = new PictureBox();
            logoBox.SizeMode = PictureBoxSizeMode.Zoom;
            logoBox.Size = new Size(300, 250);
            logoBox.Location = new Point((this.ClientSize.Width - logoBox.Width) / 2, 20);

            try
            {
                // Try to load the logo from resources
                logoBox.Image = Properties.Resources.logo;
            }
            catch (Exception ex)
            {
                // If logo can't be loaded, log the error
                Logger.Log($"[AboutForm] Could not load logo: {ex.Message}");
                // Use a placeholder text instead
                KryptonLabel placeholderLabel = new KryptonLabel();
                placeholderLabel.Text = "CTS";
                placeholderLabel.StateCommon.ShortText.Font = new Font("Segoe UI", 24, FontStyle.Bold);
                placeholderLabel.StateCommon.ShortText.TextH = Krypton.Toolkit.PaletteRelativeAlign.Center;
                placeholderLabel.Location = new Point((this.ClientSize.Width - 100) / 2, 70);
                placeholderLabel.Size = new Size(100, 40);
                mainPanel.Controls.Add(placeholderLabel);
            }

            // Title
            titleLabel = new KryptonLabel();
            titleLabel.Text = "CT Simulation Environment";
            titleLabel.StateCommon.ShortText.Font = new Font("Segoe UI", 16, FontStyle.Bold);
            titleLabel.StateCommon.ShortText.TextH = Krypton.Toolkit.PaletteRelativeAlign.Center;
            titleLabel.Location = new Point(0, logoBox.Bottom + 20);
            titleLabel.Size = new Size(this.ClientSize.Width, 30);

            // Author
            authorLabel = new KryptonLabel();
            authorLabel.Text = "Matteo Mangiagalli";
            authorLabel.StateCommon.ShortText.Font = new Font("Segoe UI", 12, FontStyle.Regular);
            authorLabel.StateCommon.ShortText.TextH = Krypton.Toolkit.PaletteRelativeAlign.Center;
            authorLabel.Location = new Point(0, titleLabel.Bottom + 20);
            authorLabel.Size = new Size(this.ClientSize.Width, 25);

            // Affiliation
            affiliationLabel = new KryptonLabel();
            affiliationLabel.Text = "Carbonate Sedimentology Research Group";
            affiliationLabel.StateCommon.ShortText.Font = new Font("Segoe UI", 11, FontStyle.Regular);
            affiliationLabel.StateCommon.ShortText.TextH = Krypton.Toolkit.PaletteRelativeAlign.Center;
            affiliationLabel.Location = new Point(0, authorLabel.Bottom + 5);
            affiliationLabel.Size = new Size(this.ClientSize.Width, 25);

            // Department
            departmentLabel = new KryptonLabel();
            departmentLabel.Text = "University Of Fribourg (Switzerland) - Departement des Geosciences";
            departmentLabel.StateCommon.ShortText.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            departmentLabel.StateCommon.ShortText.TextH = Krypton.Toolkit.PaletteRelativeAlign.Center;
            departmentLabel.Location = new Point(0, affiliationLabel.Bottom + 5);
            departmentLabel.Size = new Size(this.ClientSize.Width, 22);

            // Email
            emailLabel = new KryptonLabel();
            emailLabel.Text = "Contact:";
            emailLabel.StateCommon.ShortText.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            emailLabel.StateCommon.ShortText.TextH = Krypton.Toolkit.PaletteRelativeAlign.Center;
            emailLabel.Location = new Point((this.ClientSize.Width - 250) / 2, departmentLabel.Bottom + 15);
            emailLabel.Size = new Size(65, 22);

            // Email link
            emailLinkLabel = new KryptonLinkLabel();
            emailLinkLabel.Text = "mattemangia@icloud.com";
            emailLinkLabel.StateCommon.ShortText.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            emailLinkLabel.Location = new Point(emailLabel.Right + 5, departmentLabel.Bottom + 15);
            emailLinkLabel.Size = new Size(200, 22);
            emailLinkLabel.LinkClicked += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start("mailto:mattemangia@icloud.com");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[AboutForm] Could not open email client: {ex.Message}");
                    MessageBox.Show("Could not open email client.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            // License
            licenseLabel = new KryptonLabel();
            licenseLabel.Text = "License: Apache 2.0, 2025";
            licenseLabel.StateCommon.ShortText.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            licenseLabel.StateCommon.ShortText.TextH = Krypton.Toolkit.PaletteRelativeAlign.Center;
            licenseLabel.Location = new Point(0, emailLabel.Bottom + 20);
            licenseLabel.Size = new Size(this.ClientSize.Width, 22);

            // OK button
            okButton = new KryptonButton();
            okButton.Text = "OK";
            okButton.Size = new Size(100, 30);
            okButton.Location = new Point((this.ClientSize.Width - 100) / 2, licenseLabel.Bottom + 10);
            okButton.Click += (s, e) => this.Close();

            // Add controls to panel
            mainPanel.Controls.Add(logoBox);
            mainPanel.Controls.Add(titleLabel);
            mainPanel.Controls.Add(authorLabel);
            mainPanel.Controls.Add(affiliationLabel);
            mainPanel.Controls.Add(departmentLabel);
            mainPanel.Controls.Add(emailLabel);
            mainPanel.Controls.Add(emailLinkLabel);
            mainPanel.Controls.Add(licenseLabel);
            mainPanel.Controls.Add(okButton);

            this.Controls.Add(mainPanel);
        }

        private void ApplyDarkTheme()
        {
            // Set form to use Office2010 Black palette mode
            this.PaletteMode = PaletteMode.Office2010Black;

            // Apply dark colors to panels and controls
            this.BackColor = Color.Black;
            mainPanel.StateCommon.Color1 = Color.Black;
            mainPanel.StateCommon.Color2 = Color.Black;

            // Set text colors
            titleLabel.StateCommon.ShortText.Color1 = Color.White;
            authorLabel.StateCommon.ShortText.Color1 = Color.White;
            affiliationLabel.StateCommon.ShortText.Color1 = Color.White;
            departmentLabel.StateCommon.ShortText.Color1 = Color.White;
            emailLabel.StateCommon.ShortText.Color1 = Color.White;
            licenseLabel.StateCommon.ShortText.Color1 = Color.White;

            // Style the email link
            emailLinkLabel.StateCommon.ShortText.Color1 = Color.LightBlue;

            // Style the OK button
            okButton.StateCommon.Back.Color1 = Color.FromArgb(60, 60, 63);
            okButton.StateCommon.Back.Color2 = Color.FromArgb(70, 70, 73);
            okButton.StateCommon.Content.ShortText.Color1 = Color.White;
            okButton.StateCommon.Border.Color1 = Color.FromArgb(90, 90, 93);
        }
    }
}