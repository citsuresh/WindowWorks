using System;
using System.Drawing;
using System.Windows.Forms;

namespace WindowWorks.App
{
    // Redesigned settings dialog inspired by PowerToys: left navigation for feature groups, right pane for settings.
    public class SettingsForm : Form
    {
        private ListBox navList;
        private Panel contentPanel;
        private Button btnSave;
        private Button btnCancel;

        // General controls
        private CheckBox chkEnableHighlight;
        private CheckBox chkEnableConfirmations;

        // Highlight/HUD controls
        private TextBox txtBorderColor;
        private NumericUpDown nudBorderThickness;
        private NumericUpDown nudCornerRadius;
        private NumericUpDown nudHighlightMs;
        private NumericUpDown nudHudMs;

        public Models.AppSettings ResultSettings { get; private set; }

        public SettingsForm(Models.AppSettings current)
        {
            ArgumentNullException.ThrowIfNull(current);

            ResultSettings = new Models.AppSettings()
            {
                HighlightBorderColor = current.HighlightBorderColor,
                HighlightBorderThickness = current.HighlightBorderThickness,
                HighlightCornerRadius = current.HighlightCornerRadius,
                HighlightDurationMs = current.HighlightDurationMs,
                HudDurationMs = current.HudDurationMs,
                HudBackgroundColor = current.HudBackgroundColor,
                HudFontSize = current.HudFontSize,
                HudCornerRadius = current.HudCornerRadius,
                // UseSystemColors removed - keep explicit HighlightBorderColor only
                EnableHighlight = current.EnableHighlight,
                EnableConfirmations = current.EnableConfirmations
            };

            InitializeComponent();
            BuildGeneralPage();
            BuildHighlightPage();

            // select first item by default
            if (navList.Items.Count > 0) navList.SelectedIndex = 0;
        }

        private void InitializeComponent()
        {
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 480);
            MaximizeBox = false;
            MinimizeBox = false;

            navList = new ListBox() { Left = 12, Top = 12, Width = 200, Height = 400 }; // navigation
            navList.BorderStyle = BorderStyle.None;
            navList.ItemHeight = 24;
            navList.SelectedIndexChanged += NavList_SelectedIndexChanged;

            contentPanel = new Panel() { Left = 224, Top = 12, Width = 520, Height = 400, AutoScroll = true, BorderStyle = BorderStyle.None };

            btnSave = new Button() { Text = "Save", Width = 100, Left = ClientSize.Width - 220, Top = ClientSize.Height - 50, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnCancel = new Button() { Text = "Cancel", Width = 100, Left = ClientSize.Width - 110, Top = ClientSize.Height - 50, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnSave.Click += BtnSave_Click;
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(navList);
            Controls.Add(contentPanel);
            Controls.Add(btnSave);
            Controls.Add(btnCancel);

            // Populate navigation with groups (General + features)
            navList.Items.Add("General");
            navList.Items.Add("Highlight & HUD");
            navList.Items.Add("Presets");
            navList.Items.Add("Advanced");
        }

        private void NavList_SelectedIndexChanged(object? sender, EventArgs e)
        {
            ShowPage(navList.SelectedItem as string);
        }

        private void ShowPage(string? page)
        {
            contentPanel.Controls.Clear();
            if (string.IsNullOrEmpty(page)) return;
            switch (page)
            {
                case "General":
                    RenderGeneralPage();
                    break;
                case "Highlight & HUD":
                    RenderHighlightPage();
                    break;
                case "Presets":
                    RenderPresetsPage();
                    break;
                case "Advanced":
                    RenderAdvancedPage();
                    break;
                default:
                    break;
            }
        }

        #region Page builders
        private void BuildGeneralPage()
        {
            // controls instantiated here and reused when rendering
            chkEnableHighlight = new CheckBox() { Text = "Enable Highlight", Left = 8, Top = 8, Width = 300 };
            chkEnableConfirmations = new CheckBox() { Text = "Enable HUD confirmations", Left = 8, Top = 40, Width = 300 };

            chkEnableHighlight.Checked = ResultSettings.EnableHighlight;
            chkEnableConfirmations.Checked = ResultSettings.EnableConfirmations;
        }

        private void BuildHighlightPage()
        {
            txtBorderColor = new TextBox() { Left = 8, Top = 8, Width = 300 };
            nudBorderThickness = new NumericUpDown() { Left = 8, Top = 48, Width = 80, Minimum = 1, Maximum = 50 };
            nudCornerRadius = new NumericUpDown() { Left = 100, Top = 48, Width = 80, Minimum = 0, Maximum = 50 };
            nudHighlightMs = new NumericUpDown() { Left = 8, Top = 88, Width = 100, Minimum = 100, Maximum = 10000, Increment = 100 };
            nudHudMs = new NumericUpDown() { Left = 120, Top = 88, Width = 100, Minimum = 100, Maximum = 10000, Increment = 100 };

            // populate from ResultSettings
            txtBorderColor.Text = ResultSettings.HighlightBorderColor;
            nudBorderThickness.Value = Math.Max(1, ResultSettings.HighlightBorderThickness);
            nudCornerRadius.Value = Math.Max(0, ResultSettings.HighlightCornerRadius);
            nudHighlightMs.Value = Math.Max(100, ResultSettings.HighlightDurationMs);
            nudHudMs.Value = Math.Max(100, ResultSettings.HudDurationMs);
            // UseSystemColors removed; no corresponding control
        }

        private void RenderGeneralPage()
        {
            contentPanel.Controls.Add(new Label() { Text = "General settings", Left = 8, Top = 8, Font = new Font(Font.FontFamily, 12, FontStyle.Bold) });
            contentPanel.Controls.Add(chkEnableHighlight);
            contentPanel.Controls.Add(chkEnableConfirmations);
        }

        private void RenderHighlightPage()
        {
            contentPanel.Controls.Add(new Label() { Text = "Highlight & HUD", Left = 8, Top = 8, Font = new Font(Font.FontFamily, 12, FontStyle.Bold) });
            contentPanel.Controls.Add(new Label() { Text = "Border color (hex)", Left = 8, Top = 32 });
            contentPanel.Controls.Add(txtBorderColor);
            contentPanel.Controls.Add(new Label() { Text = "Border thickness (px)", Left = 8, Top = 72 });
            contentPanel.Controls.Add(nudBorderThickness);
            contentPanel.Controls.Add(new Label() { Text = "Corner radius (px)", Left = 100, Top = 72 });
            contentPanel.Controls.Add(nudCornerRadius);
            contentPanel.Controls.Add(new Label() { Text = "Highlight duration (ms)", Left = 8, Top = 112 });
            contentPanel.Controls.Add(nudHighlightMs);
            contentPanel.Controls.Add(new Label() { Text = "HUD duration (ms)", Left = 120, Top = 112 });
            contentPanel.Controls.Add(nudHudMs);
            // UseSystemColors removed; nothing to add
        }

        private void RenderPresetsPage()
        {
            contentPanel.Controls.Add(new Label() { Text = "Presets (configured separately)", Left = 8, Top = 8 });
            // Placeholder: could list presets and enable/disable
        }

        private void RenderAdvancedPage()
        {
            contentPanel.Controls.Add(new Label() { Text = "Advanced settings", Left = 8, Top = 8 });
            // Placeholder for advanced options
        }
        #endregion

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            // Apply general settings
            ResultSettings.EnableHighlight = chkEnableHighlight.Checked;
            ResultSettings.EnableConfirmations = chkEnableConfirmations.Checked;

            // Apply highlight settings
            var colorText = txtBorderColor.Text?.Trim();
            if (string.IsNullOrWhiteSpace(colorText))
            {
                MessageBox.Show(this, "Please enter a border color (e.g. #FF00FF00).", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ResultSettings.HighlightBorderColor = colorText;
            ResultSettings.HighlightBorderThickness = (int)nudBorderThickness.Value;
            ResultSettings.HighlightCornerRadius = (int)nudCornerRadius.Value;
            ResultSettings.HighlightDurationMs = (int)nudHighlightMs.Value;
            ResultSettings.HudDurationMs = (int)nudHudMs.Value;
            // UseSystemColors removed; no action

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
