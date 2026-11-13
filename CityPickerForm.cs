namespace frmweatherwid
{
    /// <summary>Small dialog: type a city name, pick one of the search results.</summary>
    internal sealed class CityPickerForm : Form
    {
        private readonly LocationService locationService;
        private readonly TextBox txtQuery;
        private readonly Button btnSearch, btnOk;
        private readonly ListBox lstResults;
        private readonly Label lblInfo;
        private CancellationTokenSource? searchCts;

        public CityResult? Selected => lstResults.SelectedItem as CityResult;

        public CityPickerForm(LocationService locationService)
        {
            this.locationService = locationService;

            Text            = L.T("Chọn thành phố", "Choose city");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = MinimizeBox = false;
            StartPosition   = FormStartPosition.CenterScreen;
            TopMost         = true; // opened from the tray — make sure it isn't hidden behind other windows
            ClientSize      = new Size(420, 330);
            Font            = new Font("Segoe UI", 9.5f);
            Padding         = new Padding(12);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* keep default */ }

            txtQuery   = new TextBox { Bounds = new Rectangle(12, 12, 300, 27), PlaceholderText = L.T("VD: Đà Nẵng, Hồ Chí Minh, Tokyo…", "e.g. Da Nang, Ho Chi Minh, Tokyo…") };
            btnSearch  = new Button  { Bounds = new Rectangle(318, 11, 90, 29), Text = L.T("Tìm", "Search") };
            lblInfo    = new Label   { Bounds = new Rectangle(12, 46, 396, 20), ForeColor = SystemColors.GrayText, Text = L.T("Nhập tên thành phố rồi bấm Tìm (Enter).", "Type a city name and press Search (Enter).") };
            lstResults = new ListBox { Bounds = new Rectangle(12, 70, 396, 210), IntegralHeight = false };
            btnOk      = new Button  { Bounds = new Rectangle(222, 290, 90, 29), Text = L.T("Chọn", "Select"), DialogResult = DialogResult.OK, Enabled = false };
            var btnCancel = new Button { Bounds = new Rectangle(318, 290, 90, 29), Text = L.T("Huỷ", "Cancel"), DialogResult = DialogResult.Cancel };

            Controls.AddRange([txtQuery, btnSearch, lblInfo, lstResults, btnOk, btnCancel]);
            AcceptButton = btnSearch;
            CancelButton = btnCancel;

            btnSearch.Click += async (_, _) => await SearchAsync();
            lstResults.SelectedIndexChanged += (_, _) =>
            {
                btnOk.Enabled = Selected is not null;
                AcceptButton  = Selected is not null ? btnOk : btnSearch;
            };
            lstResults.DoubleClick += (_, _) =>
            {
                if (Selected is null) return;
                DialogResult = DialogResult.OK;
            };
            txtQuery.TextChanged += (_, _) => AcceptButton = btnSearch; // typing again → Enter searches
        }

        private async Task SearchAsync()
        {
            var query = txtQuery.Text.Trim();
            if (query.Length < 2) return;

            searchCts?.Cancel();
            using var cts = searchCts = new CancellationTokenSource();

            btnSearch.Enabled = false;
            lblInfo.Text      = L.T("Đang tìm…", "Searching…");
            lstResults.Items.Clear();
            try
            {
                var results = await locationService.SearchCitiesAsync(query, cts.Token);
                if (cts.IsCancellationRequested || IsDisposed) return;

                lstResults.Items.AddRange([.. results]);
                lblInfo.Text = results.Count == 0
                    ? L.T("Không tìm thấy (hoặc không có mạng). Thử tên khác, có thể gõ không dấu.", "Nothing found (or offline). Try another name.")
                    : L.T("Chọn một kết quả rồi bấm Chọn (hoặc nháy đúp).", "Pick a result and press Select (or double-click).");
                if (results.Count > 0)
                {
                    lstResults.SelectedIndex = 0;
                    lstResults.Focus();
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (!IsDisposed) btnSearch.Enabled = true;
                if (ReferenceEquals(searchCts, cts)) searchCts = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) searchCts?.Cancel();
            base.Dispose(disposing);
        }
    }
}
