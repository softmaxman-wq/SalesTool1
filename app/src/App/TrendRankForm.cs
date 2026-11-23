using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using SmallKeyword;

namespace SalesTool
{
    public class TrendRankForm : Form
    {
        readonly SearchAd ad;
        readonly Trend trend;
        readonly WebView2 coupangView;
        readonly HashSet<string> selectedWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> coupangTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ComboBox cbCategory;
        ComboBox cbSort;
        TextBox txtFilter;
        DataGridView grid;
        Label lblStatus;
        Label lblCount;
        Button btnApply;
        Button btnRefresh;
        Button btnCoupangSuggest;

        public List<string> ResultTags { get; private set; } = new List<string>();

        public TrendRankForm(string initialCategory, IEnumerable<string> currentTags, SearchAd ad, Trend trend, WebView2 coupangView = null)
        {
            this.ad = ad;
            this.trend = trend;
            this.coupangView = coupangView;

            if (currentTags != null)
            {
                foreach (var t in currentTags)
                {
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        string s = t.Trim();
                        selectedWords.Add(s);
                        coupangTags.Add(s);
                    }
                }
            }

            Text = "네이버 검색어 트렌드 카테고리별 1~100위 랭킹 & 쿠팡 태그 선택기";
            Size = new Size(960, 680);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("맑은 고딕", 9.5f);
            Icon = Icon;

            BuildUi(initialCategory);
            LoadCategoryData();
        }

        void BuildUi(string initialCat)
        {
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(10), BackColor = Color.FromArgb(248, 249, 250) };

            var lblCat = new Label { Text = "카테고리:", AutoSize = true, Location = new Point(12, 16) };
            cbCategory = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(85, 12), Width = 140 };
            cbCategory.Items.AddRange(CategoryTrends.Categories.Cast<object>().ToArray());

            int idx = cbCategory.Items.IndexOf(initialCat ?? "");
            cbCategory.SelectedIndex = idx >= 0 ? idx : 0;
            cbCategory.SelectedIndexChanged += (s, e) => LoadCategoryData();

            var lblSort = new Label { Text = "정렬:", AutoSize = true, Location = new Point(235, 16) };
            cbSort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(275, 12), Width = 165 };
            cbSort.Items.AddRange(new object[] { "인기순 (월간검색량▼)", "급상승순 (최근증가배수▲)", "위험키워드 우선 (약/브랜드/인물)", "쿠팡 태그/추천 우선" });
            cbSort.SelectedIndex = 0;
            cbSort.SelectedIndexChanged += (s, e) => RenderGrid();

            btnRefresh = new Button { Text = "⚡ 네이버 트렌드 최신 갱신", Location = new Point(450, 10), Size = new Size(175, 28), BackColor = Color.White };
            btnRefresh.Click += async (s, e) => await RefreshFromApi();

            btnCoupangSuggest = new Button { Text = "⚡ 쿠팡 자동완성 키워드 조회", Location = new Point(635, 10), Size = new Size(185, 28), BackColor = Color.FromArgb(235, 247, 255), Font = new Font("맑은 고딕", 9f, FontStyle.Bold) };
            btnCoupangSuggest.Click += async (s, e) => await QueryCoupangSuggestAsync();

            var lblSearch = new Label { Text = "키워드 검색:", AutoSize = true, Location = new Point(12, 49) };
            txtFilter = new TextBox { Location = new Point(85, 46), Width = 200 };
            txtFilter.TextChanged += (s, e) => RenderGrid();

            lblStatus = new Label { Location = new Point(300, 49), AutoSize = true, ForeColor = Color.DimGray, Text = "데이터 준비 완료" };

            topPanel.Controls.AddRange(new Control[] { lblCat, cbCategory, lblSort, cbSort, btnRefresh, btnCoupangSuggest, lblSearch, txtFilter, lblStatus });
            Controls.Add(topPanel);

            // Grid
            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowTemplate = { Height = 28 }
            };

            var chkCol = new DataGridViewCheckBoxColumn { Name = "ColCheck", HeaderText = "선택", Width = 50 };
            var rankCol = new DataGridViewTextBoxColumn { Name = "ColRank", HeaderText = "순위", Width = 55, ReadOnly = true, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter } };
            var wordCol = new DataGridViewTextBoxColumn { Name = "ColWord", HeaderText = "키워드명", Width = 230, ReadOnly = true };
            var volCol = new DataGridViewTextBoxColumn { Name = "ColVol", HeaderText = "월간검색량", Width = 110, ReadOnly = true, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight, Format = "#,0" } };
            var growthCol = new DataGridViewTextBoxColumn { Name = "ColGrowth", HeaderText = "최근 증가배수", Width = 110, ReadOnly = true, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter } };
            var riskCol = new DataGridViewTextBoxColumn { Name = "ColRisk", HeaderText = "분류(위험도)", Width = 120, ReadOnly = true, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter } };

            grid.Columns.AddRange(chkCol, rankCol, wordCol, volCol, growthCol, riskCol);
            grid.CellContentClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == 0)
                {
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                    string w = Convert.ToString(grid.Rows[e.RowIndex].Cells["ColWord"].Value);
                    bool isChecked = Convert.ToBoolean(grid.Rows[e.RowIndex].Cells["ColCheck"].Value);
                    if (isChecked) selectedWords.Add(w);
                    else selectedWords.Remove(w);
                    UpdateCountLabel();
                }
            };
            Controls.Add(grid);

            // Bottom Panel
            var btmPanel = new Panel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(10), BackColor = Color.FromArgb(248, 249, 250) };

            lblCount = new Label { Location = new Point(12, 20), AutoSize = true, Font = new Font("맑은 고딕", 10f, FontStyle.Bold), Text = "선택된 키워드: 0개" };

            var btnTop20 = new Button { Text = "상위 20개 담기", Location = new Point(230, 14), Size = new Size(110, 32) };
            btnTop20.Click += (s, e) => PickTopN(20, false, false);

            var btnRising20 = new Button { Text = "급상승 20개", Location = new Point(345, 14), Size = new Size(100, 32) };
            btnRising20.Click += (s, e) => PickTopN(20, true, false);

            var btnRisk20 = new Button { Text = "위험(약/브랜드) 20개", Location = new Point(450, 14), Size = new Size(140, 32), ForeColor = Color.Firebrick };
            btnRisk20.Click += (s, e) => PickTopN(20, false, true);

            var btnClear = new Button { Text = "선택 해제", Location = new Point(595, 14), Size = new Size(80, 32) };
            btnClear.Click += (s, e) => { selectedWords.Clear(); RenderGrid(); };

            btnApply = new Button { Text = "✔ 쿠팡 검색어에 적용 (최대 20개)", Location = new Point(685, 12), Size = new Size(170, 36), BackColor = Color.FromArgb(13, 110, 253), ForeColor = Color.White, Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold) };
            btnApply.Click += (s, e) =>
            {
                ResultTags = selectedWords.Take(20).ToList();
                DialogResult = DialogResult.OK;
                Close();
            };

            btmPanel.Controls.AddRange(new Control[] { lblCount, btnTop20, btnRising20, btnRisk20, btnClear, btnApply });
            Controls.Add(btmPanel);

            grid.BringToFront();
        }

        CategoryTrendData currentData;

        void LoadCategoryData()
        {
            string cat = cbCategory.SelectedItem?.ToString() ?? CategoryTrends.Categories[0];
            currentData = CategoryTrends.Get(cat);
            lblStatus.Text = $"{cat} (업데이트: {currentData.Updated})";
            RenderGrid();
        }

        void RenderGrid()
        {
            if (currentData == null) return;
            grid.SuspendLayout();
            try
            {
                grid.Rows.Clear();

                // 1) Ensure currentTags (from Coupang) are present in item list for display
                var itemsMap = new Dictionary<string, CategoryTrendItem>(StringComparer.OrdinalIgnoreCase);
                if (currentData.Items != null)
                {
                    foreach (var it in currentData.Items)
                    {
                        if (!itemsMap.ContainsKey(it.Word)) itemsMap[it.Word] = it;
                    }
                }
                foreach (var tag in coupangTags)
                {
                    if (!itemsMap.ContainsKey(tag))
                    {
                        itemsMap[tag] = new CategoryTrendItem
                        {
                            Rank = 0,
                            Word = tag,
                            Volume = 0,
                            Growth = 1.0,
                            RiskType = "쿠팡 태그"
                        };
                    }
                }

                var items = itemsMap.Values.AsEnumerable();

                string q = txtFilter.Text.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(q))
                    items = items.Where(x => x.Word.ToLowerInvariant().Contains(q) || x.RiskType.ToLowerInvariant().Contains(q));

                int mode = cbSort.SelectedIndex;
                if (mode == 0) // 인기순
                    items = items.OrderByDescending(x => x.Volume);
                else if (mode == 1) // 급상승순
                    items = items.OrderByDescending(x => x.Growth).ThenByDescending(x => x.Volume);
                else if (mode == 2) // 위험키워드 우선
                    items = items.OrderByDescending(x => x.RiskType != "일반" && !x.RiskType.StartsWith("쿠팡")).ThenByDescending(x => x.Volume);
                else if (mode == 3) // 쿠팡 태그/추천 우선
                    items = items.OrderByDescending(x => x.RiskType.StartsWith("쿠팡") ? 1 : 0).ThenByDescending(x => x.Volume);

                int displayRank = 1;
                foreach (var it in items.Take(120))
                {
                    bool isSel = selectedWords.Contains(it.Word);
                    string growthText = it.Growth >= 1.2 ? $"▲ {it.Growth:0.0}배" : $"{it.Growth:0.0}배";
                    string rankText = it.RiskType == "쿠팡 태그" ? "태그" : (displayRank++).ToString();

                    int rowIdx = grid.Rows.Add(isSel, rankText, it.Word, it.Volume, growthText, it.RiskType);
                    var row = grid.Rows[rowIdx];

                    if (it.Growth >= 1.5) row.Cells["ColGrowth"].Style.ForeColor = Color.Red;
                    if (it.RiskType == "의약품")
                    {
                        row.Cells["ColRisk"].Style.ForeColor = Color.Firebrick;
                        row.Cells["ColRisk"].Style.Font = new Font(grid.Font, FontStyle.Bold);
                    }
                    else if (it.RiskType == "질병·치료") row.Cells["ColRisk"].Style.ForeColor = Color.DarkOrange;
                    else if (it.RiskType == "다른 브랜드") row.Cells["ColRisk"].Style.ForeColor = Color.DarkMagenta;
                    else if (it.RiskType == "인물") row.Cells["ColRisk"].Style.ForeColor = Color.DarkSlateBlue;
                    else if (it.RiskType == "쿠팡 태그")
                    {
                        row.Cells["ColRisk"].Style.ForeColor = Color.FromArgb(13, 110, 253);
                        row.Cells["ColRisk"].Style.Font = new Font(grid.Font, FontStyle.Bold);
                        row.DefaultCellStyle.BackColor = Color.FromArgb(245, 250, 255);
                    }
                    else if (it.RiskType == "쿠팡 추천")
                    {
                        row.Cells["ColRisk"].Style.ForeColor = Color.FromArgb(10, 140, 90);
                        row.Cells["ColRisk"].Style.Font = new Font(grid.Font, FontStyle.Bold);
                    }
                }
            }
            finally
            {
                grid.ResumeLayout();
            }

            UpdateCountLabel();
        }

        void UpdateCountLabel()
        {
            lblCount.Text = $"선택된 키워드: {selectedWords.Count}개 (쿠팡 최대 20개 적용)";
            lblCount.ForeColor = selectedWords.Count > 20 ? Color.Firebrick : Color.Black;
        }

        void PickTopN(int n, bool risingOnly, bool riskOnly)
        {
            if (currentData == null) return;
            var list = currentData.Items.AsEnumerable();

            if (risingOnly) list = list.OrderByDescending(x => x.Growth).ThenByDescending(x => x.Volume);
            else if (riskOnly) list = list.Where(x => x.RiskType != "일반" && !x.RiskType.StartsWith("쿠팡")).OrderByDescending(x => x.Volume);
            else list = list.OrderByDescending(x => x.Volume);

            selectedWords.Clear();
            foreach (var it in list.Take(n))
            {
                selectedWords.Add(it.Word);
            }
            RenderGrid();
        }

        async Task RefreshFromApi()
        {
            string cat = cbCategory.SelectedItem?.ToString() ?? CategoryTrends.Categories[0];
            btnRefresh.Enabled = false;
            lblStatus.Text = "네이버 API로 최신 트렌드 데이터 수집 중...";
            try
            {
                currentData = await CategoryTrends.RefreshAsync(cat, ad, trend, msg =>
                {
                    if (InvokeRequired) Invoke(new Action(() => lblStatus.Text = msg));
                    else lblStatus.Text = msg;
                });
                RenderGrid();
                MessageBox.Show($"'{cat}' 카테고리 최신 트렌드 키워드 1~100위 갱신이 완료되었습니다.", "갱신 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("트렌드 갱신 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                btnRefresh.Enabled = true;
                lblStatus.Text = $"{cat} (업데이트: {currentData.Updated})";
            }
        }

        async Task QueryCoupangSuggestAsync()
        {
            string cat = cbCategory.SelectedItem?.ToString() ?? CategoryTrends.Categories[0];
            string querySeed = txtFilter.Text.Trim();
            if (string.IsNullOrEmpty(querySeed))
            {
                var seeds = CategoryTrends.CategorySeeds.ContainsKey(cat) ? CategoryTrends.CategorySeeds[cat] : new[] { cat };
                querySeed = seeds[0];
            }

            btnCoupangSuggest.Enabled = false;
            lblStatus.Text = $"쿠팡 자동완성 키워드 조회 중... ({querySeed})";
            try
            {
                var suggestEngine = new CoupangSuggest(coupangView);
                var words = await suggestEngine.Suggest(querySeed);
                int added = 0;
                if (words != null && words.Count > 0)
                {
                    foreach (var w in words)
                    {
                        if (currentData.Items != null && !currentData.Items.Any(x => string.Equals(x.Word, w, StringComparison.OrdinalIgnoreCase)))
                        {
                            currentData.Items.Insert(0, new CategoryTrendItem
                            {
                                Rank = 0,
                                Word = w,
                                Volume = 15000,
                                Growth = 1.4,
                                RiskType = "쿠팡 추천"
                            });
                            added++;
                        }
                        coupangTags.Add(w);
                        selectedWords.Add(w);
                    }
                    RenderGrid();
                    lblStatus.Text = $"쿠팡 자동완성 {words.Count}개 조회 완료 ({querySeed})";
                    MessageBox.Show($"'{querySeed}'에 대한 쿠팡 자동완성 키워드 {words.Count}개를 성공적으로 조회했습니다.", "쿠팡 자동완성 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    lblStatus.Text = $"쿠팡 자동완성 결과 없음 ({querySeed})";
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "쿠팡 조회 오류: " + ex.Message;
                MessageBox.Show("쿠팡 자동완성 조회 중 오류가 발생했습니다: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                btnCoupangSuggest.Enabled = true;
            }
        }
    }
}