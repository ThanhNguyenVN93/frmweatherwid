namespace frmweatherwid
{
    /// <summary>Dark "What's new" window: version headers + bullet list, in the current UI language.</summary>
    internal sealed class WhatsNewForm : Form
    {
        public WhatsNewForm(Version? updatedFrom)
        {
            var current = UpdateService.CurrentVersion.ToString(3);
            Text            = L.T("Có gì mới", "What's new") + $" — Weather Widget v{current}";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = MinimizeBox = false;
            StartPosition   = FormStartPosition.CenterScreen;
            TopMost         = true;
            ShowInTaskbar   = true;
            ClientSize      = new Size(460, 380);
            BackColor       = Color.FromArgb(27, 29, 35);
            ForeColor       = Color.Gainsboro;
            Font            = new Font("Segoe UI", 9.5f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Label
            {
                Bounds    = new Rectangle(16, 12, 428, 30),
                Font      = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = Color.FromArgb(77, 184, 255),
                Text      = updatedFrom is null
                    ? L.T("Có gì mới", "What's new")
                    : L.T($"Đã cập nhật lên v{current}", $"Updated to v{current}"),
            };

            // Scrollable column of labels: bold version header, then "•" items with a hanging indent.
            var body = new FlowLayoutPanel
            {
                Bounds        = new Rectangle(16, 48, 436, 280),
                FlowDirection = FlowDirection.TopDown,
                WrapContents  = false,
                AutoScroll    = true,
            };
            var bold = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            const int textWidth = 400;

            // After an update: what changed since the old version; from the menu: the whole history.
            var entries = Changelog.Since(updatedFrom).ToList();
            if (entries.Count == 0) entries = Changelog.Since(null).ToList();
            foreach (var e in entries)
            {
                body.Controls.Add(new Label
                {
                    AutoSize  = true,
                    Font      = bold,
                    ForeColor = Color.White,
                    Text      = $"v{e.Version.ToString(3)}",
                    Margin    = new Padding(0, body.Controls.Count == 0 ? 0 : 10, 0, 4),
                });
                foreach (var item in e.Items)
                {
                    var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(6, 0, 0, 3) };
                    row.Controls.Add(new Label { AutoSize = true, Text = "•", Margin = new Padding(0) }, 0, 0);
                    row.Controls.Add(new Label { AutoSize = true, Text = item, MaximumSize = new Size(textWidth - 20, 0), Margin = new Padding(2, 0, 0, 0) }, 1, 0);
                    body.Controls.Add(row);
                }
            }
            Disposed += (_, _) => bold.Dispose();

            var ok = new Button
            {
                Bounds    = new Rectangle(344, 338, 100, 30),
                Text      = "OK",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(25, 100, 190),
                ForeColor = Color.White,
                DialogResult = DialogResult.OK,
            };
            ok.FlatAppearance.BorderSize = 0;
            ok.Click += (_, _) => Close();

            Controls.AddRange([header, body, ok]);
            Visuals.ScaleDialog(this);
            AcceptButton = ok;
            CancelButton = ok;
        }
    }
}
