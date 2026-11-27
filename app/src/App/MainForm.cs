using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SmallKeyword;

namespace SalesTool
{
    public class MainForm : Form
    {
        static readonly string[] NameKeys = { "n1_1", "n1_2", "c1", "n2_1", "n2_2", "c2", "n3_1", "n3_2", "c3" };

        AppSettings set;
        Learned learned;
        List<Job> jobs;
        Pipeline pipeline;
        MsgoodWeb msgood;
        bool pumping, paused;
        Job current;

        TextBox codesBox;
        Label todayLabel, statusLabel;
        ComboBox orderBox;
        ListView list;
        TabControl tabs;
        TabPage reviewTab, msgoodTab, settingsTab, learnTab, stockTab;
        TextBox stockLog;
        readonly System.Windows.Forms.Timer stockTimer = new System.Windows.Forms.Timer { Interval = 6 * 60 * 60 * 1000 };
        bool stockBusy, orderBusy;
        readonly MobileServer mobile = new MobileServer();
        readonly List<string> recentLog = new List<string>();
        void Phone(string m) { AppEvents.Add(m); TelegramNotify.Send(m); }   // 휴대폰 앱 알림 (+ 텔레그램)
        // 자동 발주 시각: msgood 발주 마감(1차 09:30, 2차 11:00, 3차 14:00) 5분 전에 모아서 발주, 송장은 19:00 한 번에
        static readonly (TimeSpan At, string Name, bool Track)[] OrderSlots = {
            (new TimeSpan(9, 25, 0), "1차 발주", false), (new TimeSpan(10, 55, 0), "2차 발주", false),
            (new TimeSpan(13, 55, 0), "3차 발주", false), (new TimeSpan(19, 0, 0), "송장 입력", true) };
        public const string OrderScheduleText = "평일 1차 09:25 · 2차 10:55 · 3차 13:55 발주 · 송장 19:00 · 적립금 부족 알림: 적립(09:15·10:30·11:30·13:30) 10분 전";
        readonly System.Windows.Forms.Timer orderTimer = new System.Windows.Forms.Timer { Interval = 30 * 1000 };
        readonly HashSet<string> slotsDone = new HashSet<string>();   // "날짜 시각" (오늘 이미 한 차수)
        // msgood 적립금 적립 시각(1차 09:15 · 2차 10:30 · 3차 11:30 · 4차 13:30). 10분 전에 대기 주문을 미리보기로 올려 보고
        // 적립금이 모자라면 알림 (발주는 하지 않음)
        static readonly (TimeSpan At, string Name)[] CreditSlots = {
            (new TimeSpan(9, 15, 0), "1차 적립"), (new TimeSpan(10, 30, 0), "2차 적립"), (new TimeSpan(11, 30, 0), "3차 적립"), (new TimeSpan(13, 30, 0), "4차 적립") };

        // 다음 발주 차수 이름과 시각 (알림 문구용)
        static bool Weekend(DateTime d) { return d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday; }   // msgood 은 주말 발주 안 받음

        static string NextOrderSlot()
        {
            var now = DateTime.Now;
            if (!Weekend(now)) foreach (var s in OrderSlots.Where(x => !x.Track)) if (now.TimeOfDay < s.At) return s.Name + " " + now.Date.Add(s.At).ToString("HH:mm");
            var d = now.Date.AddDays(1);
            while (Weekend(d)) d = d.AddDays(1);
            return (d == now.Date.AddDays(1) ? "내일" : d.ToString("M/d") + "(" + "일월화수목금토"[(int)d.DayOfWeek] + ")") + " 1차 발주 09:25";
        }
        SplitContainer leftSplit;   // 왼쪽: 위 = 대기열, 아래 = 작업 화면(네이버 쇼핑을 읽는 브라우저)
        bool workBig;
        WebView2 msgoodView, naverView;
        DateTime lastCaptchaAlert = DateTime.MinValue;
        Panel review;
        Button continueButton;
        SplitContainer split;

        public MainForm()
        {
            Text = "판매도구";
            Font = new Font("맑은 고딕", 9.5f);
            ClientSize = new Size(1400, 900);
            MinimumSize = new Size(1100, 700);
            StartPosition = FormStartPosition.CenterScreen;

            set = Store.LoadSettings();
            learned = Store.Load<Learned>(Store.LearnPath);
            jobs = Store.LoadJobs();
            // 예전 규칙으로 만든 초안(아직 등록 전)은 프로그램이 알아서 새 규칙으로 다시 만듦
            foreach (var j in jobs.Where(j => j.Status == "검토 대기" && j.RulesVersion < Pipeline.RulesVersion))
            { j.Status = "대기"; j.Pending = null; j.Step = "새 규칙으로 다시 만듦"; Store.SaveJob(j); }

            // ── 위: 상품번호 넣기
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 0), WrapContents = false };
            codesBox = new TextBox { Width = 300, Margin = new Padding(0, 3, 6, 0) };
            codesBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddCodes(); } };
            orderBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(0, 3, 6, 0) };
            orderBox.Items.AddRange(Orders.All);
            orderBox.SelectedItem = set.OrderMode;
            orderBox.SelectedIndexChanged += (s, e) => { set.OrderMode = (string)orderBox.SelectedItem; Store.Save(Store.SettingsPath, set); };
            todayLabel = new Label { AutoSize = true, Margin = new Padding(8, 7, 12, 0), ForeColor = Color.DimGray };
            continueButton = Btn("계속", async () => { paused = false; continueButton.Visible = false; await QueuePriceIfLoggedIn(); Pump(); });
            continueButton.Visible = false;
            var naverLogin = Btn("네이버 로그인", async () =>
            {
                if (naverView.CoreWebView2 == null) return;
                // 이미 로그인돼 있으면 다시 로그인 화면을 열지 않음 (네이버 로그인 쿠키 NID_AUT 가 있으면 로그인 상태)
                var cookies = await naverView.CoreWebView2.CookieManager.GetCookiesAsync("https://nid.naver.com");
                if (cookies.Any(c => c.Name == "NID_AUT" && !string.IsNullOrEmpty(c.Value)))
                {
                    UpdateNaverLoginButton(true);
                    Status("네이버에 이미 로그인돼 있습니다.");
                    MessageBox.Show(this, "네이버에 이미 로그인돼 있습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                WorkBig(true);
                naverView.CoreWebView2.Navigate("https://nid.naver.com/nidlogin.login?url=" + Uri.EscapeDataString("https://shopping.naver.com/"));
                Status("왼쪽 아래 작업 화면에서 네이버에 로그인하세요. 로그인 상태는 저장됩니다. 다 되면 [작게]를 누르세요.");
            });
            naverLoginBtn = naverLogin;
            naverLogin.Font = new Font(naverLogin.Font, FontStyle.Bold);
            // 로그인 상태를 버튼에 표시 (1분마다 확인)
            var naverLoginTimer = new System.Windows.Forms.Timer { Interval = 60000 };
            naverLoginTimer.Tick += async (s, e) => await RefreshNaverLoginButton();
            naverLoginTimer.Start();
            var msgoodLogin = Btn("msgood4u 로그인", () =>
            {
                if (msgoodView.CoreWebView2 == null) return;
                tabs.SelectedTab = msgoodTab;
                var cred = MsgoodCred.Load("1") ?? MsgoodCred.Load("2") ?? MsgoodCred.Load("3");
                if (cred != null && cred.HasMember) { var t = msgood.AutoLogin(Status, cred).ContinueWith(_ => BeginInvoke(new Action(async () => await QueuePriceIfLoggedIn()))); return; }
                msgoodView.CoreWebView2.Navigate(MsgoodWeb.Home);
                Status("[msgood4u] 탭에서 로그인하세요. [설정]에 계정별 아이디·비밀번호를 저장해 두면 다음부터 자동으로 로그인합니다.");
            });
            top.Controls.AddRange(new Control[] { Lbl("msgood4u 상품번호"), codesBox, Btn("대기열에 추가", AddCodes), todayLabel, Lbl("키워드 순서"), orderBox, naverLogin, msgoodLogin, continueButton });

            statusLabel = new Label { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(8, 5, 0, 0), BackColor = Color.FromArgb(245, 245, 245),
                Text = "상품번호를 넣고 Enter (여러 개는 띄어쓰기나 쉼표로). 하나씩 순서대로 초안을 만들고, 검토 대기가 되면 확인 후 [등록]을 누르세요." };

            // ── 왼쪽: 대기열
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            list.Columns.Add("상품번호", 70); list.Columns.Add("상태", 140); list.Columns.Add("상품명", 260);
            list.SelectedIndexChanged += (s, e) => { if (refreshingList || list.SelectedItems.Count == 0) return; var sj = (Job)list.SelectedItems[0].Tag; if (sj != current || review.Controls.Count == 0) ShowJob(sj); };
            var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(4) };
            leftButtons.Controls.AddRange(new Control[] {
                Btn("다시 만들기", () => { var j = Selected(); if (j != null && j.Status != "진행 중") { ResetJob(j); Pump(); } }),
                Btn("목록에서 지우기", RemoveSelected),
                Btn("폴더 열기", () => { var j = Selected(); string d = j != null ? Store.JobDir(j.Code) : Store.JobsDir; Directory.CreateDirectory(d); Process.Start("explorer.exe", "\"" + d + "\""); }) });
            // 작업 화면: 프로그램이 네이버 쇼핑·쿠팡을 읽는 브라우저 (가격비교 화면은 보여야 읽히므로 늘 보이는 곳에 둠)
            naverView = new WebView2 { Dock = DockStyle.Fill };
            var workHead = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(4, 3, 4, 0), WrapContents = false, BackColor = Color.FromArgb(240, 244, 250) };
            Button bigButton = null;
            bigButton = Btn("크게", () => { WorkBig(!workBig); bigButton.Text = workBig ? "작게" : "크게"; });
            workHead.Controls.Add(new Label { AutoSize = true, Margin = new Padding(2, 6, 6, 0), Text = "작업 화면 (보안 확인이 나오면 여기서 풀어 주세요)", ForeColor = Color.FromArgb(30, 60, 120) });
            workHead.Controls.Add(bigButton);
            leftSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            leftSplit.Panel1.Controls.Add(list); leftSplit.Panel1.Controls.Add(leftButtons);
            leftSplit.Panel2.Controls.Add(naverView); leftSplit.Panel2.Controls.Add(workHead);
            var left = new Panel { Dock = DockStyle.Fill };
            left.Controls.Add(leftSplit);

            // ── 오른쪽: 검토 / 브라우저 / 설정 / 학습
            tabs = new TabControl { Dock = DockStyle.Fill };
            reviewTab = new TabPage("검토");
            review = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10) };
            reviewTab.Controls.Add(review);
            msgoodTab = new TabPage("msgood4u");
            msgoodView = new WebView2 { Dock = DockStyle.Fill };
            msgoodTab.Controls.Add(msgoodView);
            settingsTab = new TabPage("설정");
            learnTab = new TabPage("학습 내역");
            stockTab = new TabPage("품절·판매");
            tabs.TabPages.AddRange(new[] { reviewTab, msgoodTab, settingsTab, learnTab, stockTab });
            tabs.SelectedIndexChanged += (s, e) => { if (tabs.SelectedTab == learnTab) BuildLearnTab(); };

            split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(tabs);
            Controls.Add(split);
            Controls.Add(statusLabel);
            Controls.Add(top);

            BuildSettingsTab();
            BuildStockTab();
            stockTimer.Tick += async (s, e) => { if (set.AutoStock) await StockRun(true); };
            stockTimer.Start();
            // 쿠팡 광고 키워드: 1시간마다 새 광고 보고서가 있는지 보고 있으면 목록을 만듦
            var adTimer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
            adTimer.Tick += async (s, e) => { if (set.AutoAdKw) { try { await Task.Run(() => AdKeywords.RunIfNew(set, StockLog)); } catch (Exception ex) { StockLog("광고 키워드 목록 오류: " + ex.Message); } } };
            adTimer.Start();
            // 자동 발주: 정해진 시각(1·2·3차 마감 5분 전)에만 모아서 발주, 송장은 19시에 한 번에
            // 30초마다 시각을 보고, 그 시각부터 20분 안이면 실행 (PC가 조금 늦게 켜져도). 같은 차수는 하루 한 번
            orderTimer.Tick += async (s, e) =>
            {
                if (!set.AutoOrder) return;
                var now = DateTime.Now;
                if (Weekend(now)) return;   // 주말: 발주·송장 모두 쉼 (월요일 1차에 모아서)
                foreach (var slot in OrderSlots)
                {
                    string key = now.ToString("yyyy-MM-dd") + " " + slot.At;
                    if (slotsDone.Contains(key) || now.TimeOfDay < slot.At || now.TimeOfDay >= slot.At + TimeSpan.FromMinutes(20)) continue;
                    slotsDone.Add(key);
                    StockLog("── " + slot.Name + " (" + now.Date.Add(slot.At).ToString("HH:mm") + ") ──");
                    if (slot.Track) await TrackRun(); else await OrderRun(true);
                }
                foreach (var cs in CreditSlots)
                {
                    var at = cs.At - TimeSpan.FromMinutes(10);
                    string key = now.ToString("yyyy-MM-dd") + " credit " + cs.At;
                    if (slotsDone.Contains(key) || now.TimeOfDay < at || now.TimeOfDay >= cs.At) continue;
                    slotsDone.Add(key);
                    await CreditCheck(cs.Name + " " + now.Date.Add(cs.At).ToString("HH:mm"));
                }
            };
            orderTimer.Start();
            if (string.IsNullOrEmpty(set.MobileKey)) { set.MobileKey = NewKey(); Store.Save(Store.SettingsPath, set); }
            if (set.MobileOn) mobile.Start(set.MobilePort, () => set.MobileKey, OnMobile, StockLog);
            msgood = new MsgoodWeb(msgoodView);
            pipeline = new Pipeline(msgood, naverView, () => set, () => learned, ShowTab);

            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsImage())
                {
                    var cur = Selected();
                    if (cur != null)
                    {
                        if (AiImageGen.PasteFromClipboard(cur, learned, Status))
                        {
                            ShowJob(cur);
                            Status($"✔ [{cur.Code}] 클립보드 이미지로 1~6통 및 네이버 대표이미지 합성 완료!");
                            e.Handled = true;
                        }
                    }
                }
            };

            Shown += async (s, e) => await Startup();
        }

        async Task Startup()
        {
            split.SplitterDistance = 440;
            WorkBig(false);
            RefreshList();
            ShowJob(null);
            try
            {
                // 네이버 로그인을 다른 도구(상품명키워드·소형키워드선택기)와 같이 쓰는 브라우저 저장소
                var env = await CoreWebView2Environment.CreateAsync(null, SmallKeyword.PriceCompare.ProfileDir);
                await msgoodView.EnsureCoreWebView2Async(env);
                msgood.HookDialogs(this);
                // 직접 로그인하든 자동 로그인하든, 페이지가 열릴 때마다 로그인 쿠키를 오래 남게 저장 → 다음에 켜도 로그인된 채로
                msgoodView.CoreWebView2.NavigationCompleted += async (s, e) => await msgood.PersistCookies();
                var keep = new System.Windows.Forms.Timer { Interval = 10 * 60 * 1000 };
                keep.Tick += async (s, e) => await msgood.KeepAlive();
                keep.Start();
                await naverView.EnsureCoreWebView2Async(env);
                await msgood.Navigate(MsgoodWeb.Home);
                naverView.CoreWebView2.Navigate("https://shopping.naver.com/");
                if (!await msgood.IsLoggedIn() && !await msgood.AutoLogin(Status))
                    Status("msgood4u 에 로그인돼 있지 않아 도매가·가격 없이 초안을 만듭니다. 나중에 [msgood4u 로그인] 후 [가격 다시 계산] (또는 [계속])을 누르세요.");
                else await QueuePriceIfLoggedIn();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "프로그램 안 브라우저(WebView2)를 열지 못했습니다.\n" + ex.Message + "\n\nMicrosoft Edge WebView2 런타임을 설치해 주세요.", Text);
                return;
            }
            // 프로그램 시작 시 무조건 팝업으로 방해하지 않고, 대기열(Pump)이 완전히 끝났을 때 또는 주기적으로 유휴 시 최신 버전 자동 확인 및 무중단 재실행
            var autoUpdateTimer = new System.Windows.Forms.Timer { Interval = 10 * 60 * 1000 };
            autoUpdateTimer.Tick += async (s, e) => await CheckUpdateWhenIdleAsync();
            autoUpdateTimer.Start();

            Pump();
        }

        // 새 버전이 있으면 묻지 않고 자동 업데이트. 초안을 만드는 도중에는 하지 않고 다음 상품으로 넘어가기 전에 적용
        // (대기 중인 상품은 다시 켠 뒤 이어서 만듦). 발주·적립금 확인·송장 시간 앞뒤에는 하지 않음
        UpdateInfo pendingUpdate;
        bool updating;

        static readonly int[] busyMinutes = { 9 * 60 + 5, 9 * 60 + 15, 9 * 60 + 25, 10 * 60 + 30, 10 * 60 + 55, 11 * 60 + 30, 13 * 60 + 30, 13 * 60 + 55, 19 * 60 };
        static bool NearScheduledWork()
        {
            int now = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            return busyMinutes.Any(m => now >= m - 10 && now <= m + 25);
        }

        async Task CheckUpdateWhenIdleAsync()
        {
            try
            {
                var s = set;
                if (!s.AutoUpdateCheck || string.IsNullOrWhiteSpace(s.GithubRepo) || updating) return;
                var info = await AutoUpdater.CheckLatestAsync(s.GithubRepo);
                if (info == null || string.IsNullOrEmpty(info.ZipUrl) || AutoUpdater.CompareVersion(info.Version, AutoUpdater.CurrentVersion) <= 0) return;
                pendingUpdate = info;
                // 지금 만드는 상품이 없으면 바로, 있으면 그 상품이 끝난 뒤 Pump 에서 적용
                if (!jobs.Any(j => j.Status == "진행 중")) await ApplyPendingUpdate();
            }
            catch { }
        }

        async Task<bool> ApplyPendingUpdate()
        {
            var info = pendingUpdate;
            if (info == null || updating || NearScheduledWork()) return false;
            if (!msgood.Gate.Wait(0)) return false;   // msgood 작업(발주·송장 읽기) 중이면 다음에
            msgood.Gate.Release();
            updating = true;
            Status($"새 버전(v{info.Version})으로 자동 업데이트합니다. 잠시 후 다시 켜지고, 대기 중인 상품은 이어서 만듭니다.");
            Phone($"판매도구를 v{info.Version}으로 자동 업데이트합니다. 잠시 후 다시 켜집니다.");
            foreach (var j in jobs) Store.SaveJob(j);
            await Task.Delay(1500);
            await AutoUpdater.PerformUpdateAsync(info.ZipUrl, true, Status);
            updating = false;   // 실패했을 때만 여기로 옴
            return false;
        }

        // ── 대기열
        void AddCodes()
        {
            var codes = Regex.Split(codesBox.Text, @"[\s,]+").Select(c => c.Trim()).Where(c => Regex.IsMatch(c, @"^\d+(-\d+)?$")).Distinct().ToList();
            if (codes.Count == 0) { codesBox.Focus(); return; }
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            int added = 0, skipped = 0;
            foreach (var c in codes)
            {
                if (TodayCount() >= set.DailyLimit) { skipped++; continue; }
                var old = jobs.FirstOrDefault(j => j.Code == c);
                if (old != null && old.Status != "오류") { skipped++; continue; }
                if (old != null) jobs.Remove(old);
                var job = new Job { Code = c, Added = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), OrderMode = set.OrderMode };
                jobs.Add(job); Store.SaveJob(job); added++;
            }
            codesBox.Clear();
            RefreshList();
            Status(added + "개를 대기열에 넣었습니다." + (skipped > 0 ? " (" + skipped + "개는 이미 있거나 오늘 " + set.DailyLimit + "개를 넘어서 뺐습니다)" : ""));
            Pump();
        }

        int TodayCount() { string t = DateTime.Now.ToString("yyyy-MM-dd"); return jobs.Count(j => (j.Added ?? "").StartsWith(t)); }

        async void Pump()
        {
            if (pumping || paused) return;
            pumping = true;
            try
            {
                while (true)
                {
                    // 새 버전이 나와 있으면 다음 상품으로 넘어가기 전에 자동 업데이트 (대기 상품은 다시 켠 뒤 이어서)
                    if (pendingUpdate != null) await ApplyPendingUpdate();
                    var job = jobs.FirstOrDefault(j => j.Status == "대기");
                    if (job == null) break;
                    job.Status = "진행 중"; job.Error = null; job.Results.Clear();
                    if (job.OrderMode == null) job.OrderMode = set.OrderMode;
                    RefreshList();
                    try
                    {
                        Action<string> step = m =>
                        {
                            job.Step = m; Status("[" + job.Code + "] " + m); UpdateRow(job);
                            // 보안 확인이 뜨면 휴대폰으로 알림 (10분에 한 번만): 앱 [홈]에서 화면을 보고 바로 풀 수 있음
                            if ((m ?? "").Contains("보안 확인") && (DateTime.Now - lastCaptchaAlert).TotalMinutes >= 10)
                            { lastCaptchaAlert = DateTime.Now; Phone("네이버 보안 확인이 나왔습니다. 앱 [홈]에서 문제를 보고 답을 넣으면 이어서 진행합니다."); }
                        };
                        string what = job.Pending;
                        if (what == "keywords") await pipeline.Refind(job, step);
                        else if (what == "tags") { await pipeline.MakeTags(job, step); job.DraftTags = new List<string>(job.CoupangTags); }
                        else if (what == "price") await pipeline.RefreshPrice(job, step);
                        else await pipeline.Draft(job, step);
                        job.Status = "검토 대기"; job.Step = null; job.Pending = null;
                        if (set.NotifyDraft)
                            Phone("[" + job.Code + "] " + (what == null ? "초안 완료" : what == "price" ? "가격 다시 계산 완료" : "다시 만들기 완료") + ": " + (job.Names.ContainsKey("n1_1") ? job.Names["n1_1"] : "") + (job.Prices[1] > 0 ? " / 1개 " + Won(job.Prices[1]) : " / 가격 없음") + "\n앱 [초안]에서 확인·등록");
                        Status("[" + job.Code + "] " + (what == "keywords" ? "'" + job.Seed + "' 로 키워드·상품명·쿠팡 검색어를 다시 만들었습니다." : what == "tags" ? "쿠팡 검색어 " + job.CoupangTags.Count + "개를 다시 찾았습니다." : "초안을 만들었습니다.") + " 검토 후 [등록]을 누르세요.");
                    }
                    catch (SmallKeyword.NaverBlocked ex)
                    {
                        job.Status = "대기"; job.Step = null;
                        paused = true; continueButton.Visible = true;
                        Status(ex.Message);
                        Store.SaveJob(job); RefreshList();
                        Phone("네이버 보안 확인이 나와서 멈췄습니다. PC 작업 화면에서 풀고 앱 [홈]의 '계속'을 눌러 주세요.");
                        MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                    }
                    catch (LoginNeeded ex)
                    {
                        job.Status = "대기"; job.Step = null;
                        paused = true; continueButton.Visible = true;
                        tabs.SelectedTab = msgoodTab;
                        Status(ex.Message);
                        Store.SaveJob(job); RefreshList();
                        break;
                    }
                    catch (Exception ex)
                    {
                        job.Status = job.Pending != null && job.Keywords.Count > 0 ? "검토 대기" : "오류"; job.Error = ex.Message; job.Step = null; job.Pending = null;
                        Status("[" + job.Code + "] 오류: " + ex.Message);
                        Phone("[" + job.Code + "] 오류: " + ex.Message);
                    }
                    Store.SaveJob(job);
                    RefreshList();
                    if (current == null || current == job) SelectJob(job);
                }
            }
            finally
            {
                pumping = false;
                // 모든 대기열 처리가 끝났거나 대기 중인 상품이 없을 때 조용히 최신 버전 자동 점검
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    await CheckUpdateWhenIdleAsync();
                });
            }
        }

        void ResetJob(Job j)
        {
            j.Status = "대기"; j.Error = null; j.Keywords.Clear(); j.Names.Clear(); j.DraftNames.Clear(); j.Results.Clear();
            j.NaverCat = j.CoupangCat = j.NaverCatName = j.CoupangCatName = null;
            j.CoupangTags.Clear(); j.DraftTags.Clear(); if (j.RiskTags != null) j.RiskTags.Clear();
            Store.SaveJob(j); RefreshList();
        }

        void RemoveSelected()
        {
            var j = Selected();
            if (j == null || j.Status == "진행 중") return;
            if (MessageBox.Show(this, "[" + j.Code + "] 를 목록에서 지울까요? (만든 이미지 폴더도 지웁니다)", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            jobs.Remove(j);
            try { Directory.Delete(Store.JobDir(j.Code), true); } catch { }
            if (current == j) ShowJob(null);
            RefreshList();
        }

        Job Selected() { return list.SelectedItems.Count > 0 ? (Job)list.SelectedItems[0].Tag : null; }

        void SelectJob(Job job)
        {
            refreshingList = true;   // 목록 선택이 바뀌며 한 번, 아래에서 또 한 번 그리지 않게
            try { foreach (ListViewItem it in list.Items) if (it.Tag == job) { it.Selected = true; it.EnsureVisible(); } }
            finally { refreshingList = false; }
            ShowJob(job);
        }

        bool refreshingList;
        void RefreshList()
        {
            var sel = Selected();
            refreshingList = true;
            try { RefreshListCore(sel); } finally { refreshingList = false; }
            todayLabel.Text = "오늘 " + TodayCount() + " / " + set.DailyLimit;
        }
        void RefreshListCore(Job sel)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var j in jobs)
            {
                var it = new ListViewItem(new[] { j.Code, StatusText(j), j.Title }) { Tag = j };
                it.ForeColor = j.Status == "오류" ? Color.Firebrick : j.Status == "등록 완료" ? Color.SeaGreen : j.Status == "검토 대기" ? Color.RoyalBlue : Color.Black;
                list.Items.Add(it);
                if (j == sel) it.Selected = true;
            }
            list.EndUpdate();
        }

        void UpdateRow(Job j)
        {
            foreach (ListViewItem it in list.Items) if (it.Tag == j) { it.SubItems[1].Text = StatusText(j); it.SubItems[2].Text = j.Title; }
        }

        static string StatusText(Job j)
        {
            if (j.Status == "진행 중" && j.Step != null) { var m = Regex.Match(j.Step, @"^\d/6"); return "진행 중 " + (m.Success ? m.Value : ""); }
            if (j.Status == "대기" && j.Pending == "keywords") return "대기 (키워드)";
            if (j.Status == "대기" && j.Pending == "tags") return "대기 (검색어)";
            if (j.Status == "대기" && j.Pending == "price") return "대기 (가격)";
            if (j.Status == "검토 대기" && j.NeedPrice) return "검토 대기 (가격 없음)";
            return j.Status;
        }

        void Status(string m) { if (InvokeRequired) BeginInvoke(new Action(() => statusLabel.Text = m)); else statusLabel.Text = m; }

        // 작업 화면은 늘 왼쪽 아래에 보이므로 오른쪽 탭은 바꾸지 않음 (검토 중인 화면을 그대로 볼 수 있게)
        void ShowTab(string which) { }

        void WorkBig(bool big)
        {
            workBig = big;
            int h = leftSplit.Height;
            if (h <= 0) return;
            leftSplit.SplitterDistance = Math.Max(120, big ? 160 : h - 330);
        }

        // ── 검토 화면
        DataGridView kwGrid;
        Dictionary<string, TextBox> nameBoxes = new Dictionary<string, TextBox>();
        TextBox[] priceBoxes = new TextBox[7];
        TextBox seedBox, naverCatBox, coupangCatBox, volumeBox, tagsBox, tagsBox2, tagsBox3;
        CheckBox healthBox;
        Label resultLabel;

        void ShowJob(Job job)
        {
            if (current != null && current != job && current.Keywords.Count > 0 && current.Status != "대기" && current.Status != "진행 중") ReadBack(current);
            current = job;
            review.SuspendLayout();
            ClearDispose(review);
            nameBoxes.Clear();
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top };
            flow.SuspendLayout();
            var showSw = Stopwatch.StartNew();
            Action done = () =>
            {
                flow.ResumeLayout(false); review.Controls.Add(flow); review.ResumeLayout();
                // 화면 그리는 데 걸린 시간 기록 (멈춤 점검용)
                try
                {
                    string lp = Path.Combine(Store.Dir, "ui_timing.log");
                    if (File.Exists(lp) && new FileInfo(lp).Length > 200000) File.Delete(lp);
                    File.AppendAllText(lp, DateTime.Now.ToString("MM-dd HH:mm:ss") + " " + (job == null ? "-" : job.Code + " " + job.Status) + " " + showSw.ElapsedMilliseconds + "ms" + Environment.NewLine);
                }
                catch { }
            };
            if (job == null)
            {
                flow.Controls.Add(new Label { AutoSize = true, Text = "왼쪽 목록에서 상품을 고르세요.", ForeColor = Color.DimGray, Margin = new Padding(4, 10, 0, 0) });
                done(); return;
            }

            var head = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Font = BoldFont, Margin = new Padding(3, 3, 3, 2),
                Text = "[" + job.Code + "] " + job.Title + "   (" + job.Status + ")" };
            flow.Controls.Add(head);
            var info = new List<string>();
            if (job.Brand != null) info.Add("브랜드 " + job.Brand);
            if (job.Category != null) info.Add("msgood 분류: " + job.Category);
            if (job.Wholesale > 0) info.Add("판매가 " + Won(job.SalePrice) + " · 도매가 " + Won(job.Wholesale) + " · 배송비 " + Won(job.Shipping));
            if (!string.IsNullOrEmpty(job.Notice)) info.Add("조건: " + job.Notice);
            flow.Controls.Add(Note(string.Join("   |   ", info)));
            if (job.NaverCatName != null || job.CoupangCatName != null)
                flow.Controls.Add(new Label { AutoSize = true, Font = BoldFont, ForeColor = Color.FromArgb(30, 60, 120), Margin = new Padding(3, 2, 3, 2),
                    Text = "등록 카테고리 → 네이버: " + (job.NaverCatName ?? "-") + "   |   쿠팡: " + (job.CoupangCatName ?? "-") });
            string n11;
            if (job.Names.TryGetValue("n1_1", out n11) && job.NameWords != null && job.NameWords.Count > 0)
            {
                var left = Naming.Leftover(n11, job.NameWords, 2);
                if (left.Count > 0) flow.Controls.Add(Note("네이버 브랜드·제조사 칸 (#1 스토어 1개 기준): " + left[0] + (left.Count > 1 ? " / " + left[1] : "") + "  (상품명에 못 넣은 키워드, 스토어마다 그 상품명 기준으로 넣음)"));
            }
            if (job.Status == "오류") flow.Controls.Add(Note("오류: " + job.Error + "  → 왼쪽 [다시 만들기]", Color.Firebrick));
            if (job.Status == "대기" || job.Status == "진행 중") { flow.Controls.Add(Note(job.Step ?? "차례를 기다리는 중입니다.", Color.RoyalBlue)); done(); return; }
            if (job.Keywords.Count == 0) { done(); return; }

            // 시작 단어 + 키워드
            seedBox = new TextBox { Width = 160, Text = job.Seed };
            seedBox.Leave += (s2, e2) => { if (current == job) ReadBack(job); };
            flow.Controls.Add(Section("시작 단어 · 키워드"));
            flow.Controls.Add(Row(Lbl("시작 단어"), seedBox, Btn("키워드 다시 찾기", async () => await RefindKeywords(job)), Note(job.SeedReason ?? "")));
            kwGrid = new DataGridView { Width = 900, Height = 250, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White };
            kwGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "사용", FillWeight = 40 });
            foreach (var h in new[] { "키워드", "월간검색량", "상품총수량", "배율", "1페이지", "최근 증가", "비고" }) kwGrid.Columns.Add(h, h);
            for (int c = 1; c < kwGrid.Columns.Count; c++) kwGrid.Columns[c].ReadOnly = true;
            var shown = Naming.Ordered(job.Keywords.Skip(1).Where(k => k.Excluded == null), job.OrderMode ?? set.OrderMode, set.MaxRatio, learned).ToList();
            shown.AddRange(job.Keywords.Skip(1).Where(k => !shown.Contains(k)).OrderBy(k => k.Excluded == null ? 0 : 1).ThenByDescending(k => k.Volume));
            foreach (var k in shown)
            {
                int i = kwGrid.Rows.Add(k.Use && k.Excluded == null, k.Word, k.Volume >= 0 ? k.Volume.ToString("#,0") : "-", k.Total >= 0 ? k.Total.ToString("#,0") : "-",
                    k.Ratio < double.MaxValue ? k.Ratio.ToString("0.00") : "-", k.InPage,
                    k.Growth < 0 ? "-" : (k.Growth >= 9.99 ? "새로 뜸" : k.Growth.ToString("0.0") + "배") + (k.Rising ? " ▲" : ""), k.Excluded != null ? "제외: " + k.Excluded : k.Typo ? "시작 단어 오타 (꼭 넣음)" : !k.Mentioned ? "상세 페이지에 없음" + (k.OtherCategory ? " · 다른 카테고리" : "") : k.OtherCategory && !k.Core ? "다른 카테고리 (" + (int)Math.Round(k.CatShare * 100) + "%)" : k.TagReason != null ? "쿠팡 검색어 (" + k.TagReason + ")" : Naming.NameBlockReason(k) != null ? "상품명 X: " + Naming.NameBlockReason(k) : k.Small(set.MaxRatio) ? "소형키워드" + (k.FromDetail ? " · 상세 " + k.DetailCount + "번" : "") : k.FromDetail ? "상세 페이지 " + k.DetailCount + "번" : "");
                kwGrid.Rows[i].Tag = k;
                if (k.Excluded != null || k.OtherCategory) kwGrid.Rows[i].DefaultCellStyle.ForeColor = Color.Gray;
                else if (k.TagReason != null) kwGrid.Rows[i].DefaultCellStyle.ForeColor = Color.DarkOrange;
                else if (k.Small(set.MaxRatio)) kwGrid.Rows[i].DefaultCellStyle.ForeColor = Color.SeaGreen;
                if (k.Rising && k.Excluded == null) { kwGrid.Rows[i].Cells[6].Style.ForeColor = Color.Crimson; kwGrid.Rows[i].Cells[6].Style.Font = BoldFont; }
            }
            kwGrid.CurrentCellDirtyStateChanged += (s, e) => { if (kwGrid.IsCurrentCellDirty) kwGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            kwGrid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
                var k = (KwRow)kwGrid.Rows[e.RowIndex].Tag;
                k.Use = (bool)kwGrid.Rows[e.RowIndex].Cells[0].Value;
                if (k.Use) k.Excluded = null;
            };
            // 오른쪽 클릭: 브랜드로 기억 (상품명 X, 검색량이 많으면 쿠팡 검색어 후보) / 상품명에서 빼기
            var menu = new ContextMenuStrip();
            Action<string> mark = how =>
            {
                ReadBack(job, false);
                foreach (DataGridViewRow row in kwGrid.SelectedRows)
                {
                    var k = (KwRow)row.Tag; string key = k.Word.ToLowerInvariant();
                    if (how == "brand") { learned.Brands[key] = 1; learned.Excluded.Remove(key); k.Excluded = "브랜드(학습)"; }
                    else if (how == "use")
                    {
                        // 브랜드가 아님 / 빼지 말 것: 다음부터 상품명에 넣음
                        learned.Brands.Remove(key); learned.Excluded.Remove(key);
                        int p; learned.Preferred.TryGetValue(key, out p); learned.Preferred[key] = p + 1;
                        k.Excluded = null; k.Use = true; if (k.TagReason != "직접") k.TagReason = null;
                    }
                    else { int n; learned.Excluded.TryGetValue(key, out n); learned.Excluded[key] = n + 1; k.Excluded = "전에 뺀 단어"; }
                    learned.History.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " [" + job.Code + "] " + (how == "brand" ? "브랜드로 기억: " : how == "use" ? "상품명에 넣을 단어(브랜드 아님): " : "뺄 단어: ") + k.Word);
                }
                Store.Save(Store.LearnPath, learned);
                pipeline.MakeNames(job); Store.SaveJob(job); ShowJob(job);
                Status("기억했습니다. 상품명을 다시 만들었습니다. 쿠팡 검색어는 [쿠팡 검색어 다시 찾기]로 다시 만들 수 있습니다.");
            };
            menu.Items.Add("브랜드로 기억 (상품명 X, 쿠팡 검색어 후보)", null, (s2, e2) => mark("brand"));
            menu.Items.Add("상품명에서 빼기 (다음부터도)", null, (s2, e2) => mark("exclude"));
            menu.Items.Add("상품명에 넣기 (브랜드 아님, 다음부터도)", null, (s2, e2) => mark("use"));
            kwGrid.ContextMenuStrip = menu;
            kwGrid.CellMouseDown += (s2, e2) =>
            {
                if (e2.Button == MouseButtons.Right && e2.RowIndex >= 0 && !kwGrid.Rows[e2.RowIndex].Selected) { kwGrid.ClearSelection(); kwGrid.Rows[e2.RowIndex].Selected = true; }
            };
            flow.Controls.Add(kwGrid);
            flow.Controls.Add(Row(Btn("체크한 키워드로 상품명 다시 만들기", () => { ReadBack(job, false); pipeline.MakeNames(job); Store.SaveJob(job); ShowJob(job); }),
                Btn("쿠팡 검색어 다시 찾기", () => Queue(job, "tags")),
                Note("초록 = 소형키워드 (상품총수량 < " + set.MaxRatio + " × 검색량) · 빨강 ▲ = 최근 급상승 (먼저 넣음) · 순서: " + (job.OrderMode ?? set.OrderMode) + " · 단어를 오른쪽 클릭하면 브랜드로 기억 / 빼기")));

            // 상품명 (사업자별 차별화)
            flow.Controls.Add(Section("상품명 (사업자별 차별화)"));
            // 사업자 3개 모두 표시 (휴대폰앱과 같게). '사용 안 함' 사업자도 상품명은 보이고 고칠 수 있음 (등록만 안 함)
            foreach (var plan in set.Accounts)
            {
                string bInfo = string.IsNullOrWhiteSpace(plan.BusinessId) ? "" : " - " + plan.BusinessId;
                string sInfo = string.IsNullOrWhiteSpace(plan.NameStyle) ? "기본" : plan.NameStyle;
                flow.Controls.Add(new Label { Text = "■ #" + plan.Id + " 사업자" + bInfo + " (스타일: " + sInfo + ")" + (plan.Use ? "" : "  — 설정에서 '사용 안 함' (자동 등록 X, 상품명만 보기·수정)"), AutoSize = true, Font = Fnt(9.5f, FontStyle.Bold), ForeColor = plan.Use ? Color.FromArgb(40, 70, 140) : Color.FromArgb(110, 110, 110), Margin = new Padding(4, 6, 0, 2) });
                foreach (var key in NameKeys.Where(k => k.Substring(1, 1) == plan.Id))
                {
                    bool has = job.Names.ContainsKey(key) && !string.IsNullOrWhiteSpace(job.Names[key]);
                    if (key.StartsWith("n") && !has && !plan.Counts(plan.NaverCounts).Contains(int.Parse(key.Substring(3)))) continue;
                    if (key.StartsWith("c") && !has && plan.Counts(plan.CoupangCounts).Count == 0) continue;
                    string v; job.Names.TryGetValue(key, out v);
                    int max = Naming.MaxLen(key);
                    bool two = key.StartsWith("c");
                    var tb = new TextBox { Width = 700, Text = v ?? "", Multiline = two, WordWrap = two, Height = two ? 44 : 23, AcceptsReturn = false };
                    var cnt = new Label { AutoSize = true, Margin = new Padding(6, 6, 0, 0) };
                    EventHandler upd = (s, e) => { cnt.Text = tb.TextLength + "/" + max; cnt.ForeColor = tb.TextLength > max ? Color.Firebrick : Color.DimGray; };
                    tb.TextChanged += upd; upd(null, null);
                    nameBoxes[key] = tb;
                    string extra = key.StartsWith("c") ? " (" + string.Join("·", plan.Counts(plan.CoupangCounts)) + "개)" : "";
                    flow.Controls.Add(Row(Lbl(Naming.NameLabel(key) + extra, 130), tb, cnt));
                }
            }

            // 쿠팡 검색어: 3개 샵별 맞춤형 자동 풀링 (검색량 30 초과 필수)
            tagsBox = new TextBox { Width = 700, Height = 60, Multiline = true, WordWrap = true, AcceptsReturn = false, Text = string.Join(", ", job.CoupangTags1 != null && job.CoupangTags1.Count > 0 ? job.CoupangTags1 : job.CoupangTags) };
            var tagCount = new Label { AutoSize = true, Margin = new Padding(6, 6, 0, 0), ForeColor = Color.DimGray };
            EventHandler tagUpd = (s2, e2) => tagCount.Text = Tags(tagsBox.Text).Count + "/20";
            tagsBox.TextChanged += tagUpd; tagUpd(null, null);

            tagsBox2 = new TextBox { Width = 700, Height = 48, Multiline = true, WordWrap = true, AcceptsReturn = false, Text = string.Join(", ", job.CoupangTags2 ?? new List<string>()) };
            var tagCount2 = new Label { AutoSize = true, Margin = new Padding(6, 6, 0, 0), ForeColor = Color.DimGray };
            EventHandler tagUpd2 = (s2, e2) => tagCount2.Text = Tags(tagsBox2.Text).Count + "/20";
            tagsBox2.TextChanged += tagUpd2; tagUpd2(null, null);

            tagsBox3 = new TextBox { Width = 700, Height = 48, Multiline = true, WordWrap = true, AcceptsReturn = false, Text = string.Join(", ", job.CoupangTags3 ?? new List<string>()) };
            var tagCount3 = new Label { AutoSize = true, Margin = new Padding(6, 6, 0, 0), ForeColor = Color.DimGray };
            EventHandler tagUpd3 = (s2, e2) => tagCount3.Text = Tags(tagsBox3.Text).Count + "/20";
            tagsBox3.TextChanged += tagUpd3; tagUpd3(null, null);

            var btnTrendRank = Btn("📊 카테고리 트렌드 키워드 순위표 (1~100위)", () =>
            {
                string matchCat = CategoryTrends.MatchCategory(job.Seed, job.Name, job.NaverCatName);
                var searchAdKeys = SearchAdKeys.Load(Store.Dir);
                var searchAdObj = searchAdKeys.Ready ? new SearchAd(searchAdKeys) : null;
                var trendKeys = TrendKeys.Load(Store.Dir);
                var trendObj = trendKeys.Ready ? new Trend(trendKeys) : null;

                using (var dlg = new TrendRankForm(matchCat, Tags(tagsBox.Text), searchAdObj, trendObj, naverView))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.ResultTags != null)
                    {
                        tagsBox.Text = string.Join(", ", dlg.ResultTags);
                    }
                }
            });

            var btnShop1Fill = Btn("⚡ 질병·치료 20개 재주입", () =>
            {
                if (job.CoupangTags1 != null && job.CoupangTags1.Count > 0)
                {
                    tagsBox.Text = string.Join(", ", job.CoupangTags1);
                    Status("쿠팡 #1에 질병·치료 위험키워드 20개를 자동 주입했습니다.");
                }
            });

            var btnShop2Fill = Btn("⚡ 유명인·브랜드 20개 재주입", () =>
            {
                if (job.CoupangTags2 != null && job.CoupangTags2.Count > 0)
                {
                    tagsBox2.Text = string.Join(", ", job.CoupangTags2);
                    Status("쿠팡 #2에 유명인·의사·약사·브랜드·상품명 키워드 20개를 자동 주입했습니다.");
                }
            });

            var btnShop3Fill = Btn("⚡ 급상승 20개 재주입", () =>
            {
                if (job.CoupangTags3 != null && job.CoupangTags3.Count > 0)
                {
                    tagsBox3.Text = string.Join(", ", job.CoupangTags3);
                    Status("쿠팡 #3에 급상승 키워드 20개를 자동 주입했습니다.");
                }
            });

            flow.Controls.Add(Row(Lbl("쿠팡 #1 (질병·치료)", 130), tagsBox, tagCount, btnShop1Fill));
            flow.Controls.Add(Row(Lbl("쿠팡 #2 (유명인·브랜드·상품명)", 170), tagsBox2, tagCount2, btnShop2Fill));
            flow.Controls.Add(Row(Lbl("쿠팡 #3 (급상승 순)", 130), tagsBox3, tagCount3, btnShop3Fill));
            flow.Controls.Add(Row(Lbl("", 130), btnTrendRank));
            flow.Controls.Add(Note("쿠팡 3개 샵 전자동 풀링: #1 질병·치료, #2 유명인·의사·약사·브랜드·상품명(대상군 제외), #3 급상승 키워드가 월검색수 > 30 조건 및 상품명 안전단어 제외 규칙으로 20개씩 100% 자동 채워집니다."));

            // 가격
            flow.Controls.Add(Section("가격 (가격설정기 식 · 하한선 " + (job.FloorReason ?? "없음") + ")"));
            if (job.NeedPrice)
                flow.Controls.Add(Row(Note("msgood4u 에 로그인하지 않은 채 만들어서 도매가·가격이 없습니다. 로그인한 뒤 누르세요 →", Color.Firebrick),
                                      Btn("가격 다시 계산", () => Queue(job, "price"))));
            var pr = new List<Control>();
            for (int n = 1; n <= 6; n++)
            {
                priceBoxes[n] = new TextBox { Width = 80, Text = job.Prices[n].ToString("#,0"), TextAlign = HorizontalAlignment.Right };
                if (job.Prices[n] != job.FormulaPrices[n]) priceBoxes[n].ForeColor = Color.Firebrick;
                pr.Add(Lbl(n + "개", 34)); pr.Add(priceBoxes[n]);
            }
            flow.Controls.Add(Row(pr.ToArray()));
            if (Enumerable.Range(1, 6).Any(n => job.Prices[n] != job.FormulaPrices[n])) flow.Controls.Add(Note("빨간 숫자 = 식 가격(" + string.Join(", ", Enumerable.Range(1, 6).Select(n => Won(job.FormulaPrices[n]))) + ")이 하한선보다 낮아 하한선 × 개수로 올림"));

            // 이미지
            flow.Controls.Add(Section("대표이미지 (1~6통) · 추가이미지 — 누르면 크게 봅니다"));

            var imgToolbar = Row(
                Btn("🎨 AI 대표이미지 생성 (무료)", async () =>
                {
                    try
                    {
                        Status("AI 엔진으로 3D 대표이미지 생성 중 (약 3~5초)...");
                        string topicKo;
                        string prompt = AiImageGen.BuildPrompt(job, out topicKo);
                        var bmp = await AiImageGen.GenerateFreeAsync(prompt, Status);
                        if (bmp != null)
                        {
                            AiImageGen.ApplyImageToJob(job, bmp, learned, Status);
                            ShowJob(job);
                            Status($"✔ [{topicKo}] 3D 대표이미지 및 1~6통 배지 합성이 완료되었습니다!");
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "AI 이미지 생성 오류: " + ex.Message, "오류");
                    }
                }),
                Btn("⚡ 어사이드(Aside) 챗GPT 생성", () =>
                {
                    AiImageGen.OpenAsideWithPrompt(job, Status);
                    MessageBox.Show(this, "어사이드(Aside) 브라우저로 챗GPT가 열렸으며, 고화질 팩샷 프롬프트가 복사되었습니다!\n\n1. 챗GPT 대화창 또는 어사이드 에이전트에 'Ctrl + V'로 붙여넣고 엔터를 누르세요.\n2. 생성된 이미지를 우클릭하여 '이미지 복사' 하신 뒤,\n3. 판매도구에서 [📋 클립보드 사진 즉시 붙여넣기]를 누르시면 1~6통 및 네이버 대표이미지로 1초 만에 자동 합성됩니다!", "어사이드 챗GPT 연동");
                }),
                Btn("📋 클립보드 사진 즉시 붙여넣기", () =>
                {
                    if (AiImageGen.PasteFromClipboard(job, learned, Status))
                    {
                        ShowJob(job);
                        Status($"✔ [{job.Code}] 클립보드 이미지로 대표이미지(1~6통 및 네이버) 합성이 완료되었습니다!");
                    }
                }),
                Btn("📦 어사이드 일괄 파일 내보내기", () =>
                {
                    string jsonFile = AiImageGen.ExportAsideBatchTasks(jobs, Status);
                    if (!string.IsNullOrEmpty(jsonFile))
                    {
                        Process.Start("explorer.exe", $"/select,\"{jsonFile}\"");
                        MessageBox.Show(this, $"어사이드(Aside) 브라우저 에이전트에 전달할 일괄 작업 파일이 생성되었습니다!\n\n위치: {jsonFile}\n\nAside 브라우저의 에이전트에게\n'aside_batch_tasks.json 파일을 읽어서 각 상품마다 ChatGPT에서 prompt 내용으로 이미지를 생성하고 save_path에 저장해줘'\n라고 지시하시면 됩니다.", "어사이드 일괄 작업 생성 완료");
                    }
                }),
                Btn("📁 이미지 파일로 교체", () =>
                {
                    using (var ofd = new OpenFileDialog { Filter = "이미지 파일 (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp", Title = "대표이미지로 쓸 사진 선택" })
                    {
                        if (ofd.ShowDialog(this) == DialogResult.OK)
                        {
                            try
                            {
                                using (var fs = new FileStream(ofd.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                using (var raw = Image.FromStream(fs))
                                {
                                    var bmp = new Bitmap(raw);
                                    AiImageGen.ApplyImageToJob(job, bmp, learned, Status);
                                    ShowJob(job);
                                    Status("✔ 선택하신 이미지로 대표이미지 합성을 완료했습니다!");
                                }
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(this, "이미지 파일 처리 오류: " + ex.Message, "오류");
                            }
                        }
                    }
                }),
                Btn("🔄 원래 사진으로 복구", () =>
                {
                    try
                    {
                        Status("공급사 원래 상품 사진으로 복구 중...");
                        string url = string.IsNullOrEmpty(job.MainImageUrl) ? RepImages.Msgood.MainImageUrl(job.Url, out _) : job.MainImageUrl;
                        using (var bmp = RepImages.Msgood.DownloadImage(url))
                        {
                            AiImageGen.ApplyImageToJob(job, bmp, learned, Status);
                            ShowJob(job);
                            Status("✔ 원래 상품 사진으로 복구 완료했습니다.");
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "원래 사진 복구 실패: " + ex.Message, "오류");
                    }
                })
            );
            flow.Controls.Add(imgToolbar);
            flow.Controls.Add(Row(Enumerable.Range(1, 6).Select(n => (Control)Thumb(Pipeline.ImagePath(job, "rep", n))).ToArray()));
            flow.Controls.Add(Row(Enumerable.Range(1, 4).Select(n => (Control)Thumb(Pipeline.ImagePath(job, "extra", n))).ToArray()));
            flow.Controls.Add(Note("네이버 스토어 1개·2개 대표이미지 (상표 그대로, 글자판 없음)"));
            flow.Controls.Add(Row(Enumerable.Range(1, 2).Select(n => (Control)Thumb(Pipeline.NaverImagePath(job, n))).ToArray()));

            // 카테고리·고시
            flow.Controls.Add(Section("카테고리 · 정보"));
            naverCatBox = new TextBox { Width = 90, Text = job.NaverCat ?? "" };
            naverCatBox.Leave += (s2, e2) => { if (current == job) ReadBack(job); };
            coupangCatBox = new TextBox { Width = 90, Text = job.CoupangCat ?? "" };
            volumeBox = new TextBox { Width = 80, Text = job.Volume ?? "" };
            healthBox = new CheckBox { Text = "건강기능식품", Checked = job.Health, AutoSize = true, Margin = new Padding(12, 5, 0, 0) };
            flow.Controls.Add(Row(Lbl("네이버", 50), naverCatBox, Note(job.NaverCatName ?? ""), Lbl("쿠팡", 40), coupangCatBox, Note(job.CoupangCatName ?? "")));
            var healthNote = Note(job.HealthNote ?? "");
            if ((job.HealthNote ?? "").StartsWith("⚠")) healthNote.ForeColor = Color.Crimson;
            flow.Controls.Add(Row(Lbl("1개 용량", 60), volumeBox, healthBox, healthNote));

            // 등록
            var regButton = new Button { Text = job.Status == "등록 완료" ? "다시 등록" : "등록", Width = 150, Height = 38, Font = Fnt(11f, FontStyle.Bold), Margin = new Padding(3, 14, 3, 3) };
            regButton.Click += async (s, e) => await RegisterJob(job, regButton, false);

            var retryButton = new Button { Text = "실패한 계정만 재시도", Width = 180, Height = 38, Font = Fnt(11f, FontStyle.Bold), BackColor = Color.FromArgb(255, 235, 235), ForeColor = Color.DarkRed, Margin = new Padding(3, 14, 3, 3) };
            retryButton.Visible = job.Status == "일부 실패" || (job.Results.Any(r => r.StartsWith("✖")) && job.Results.Any(r => r.StartsWith("✔")));
            retryButton.Click += async (s, e) => await RegisterJob(job, retryButton, true);

            var mode = "네이버 전시중" + " · " + (set.CoupangApprove ? "쿠팡 승인요청까지" : "쿠팡 임시저장") + "  ([설정] 탭에서 바꿈)";
            flow.Controls.Add(Row(regButton, retryButton, Note(mode)));

            // 등록 결과 분리 표시 (성공 / 실패)
            var okList = job.Results.Where(r => r.StartsWith("✔")).ToList();
            var failList = job.Results.Where(r => r.StartsWith("✖")).ToList();
            if (okList.Count > 0)
            {
                var okLbl = new Label { Text = "등록 성공 (" + okList.Count + "건):\n" + string.Join("\n", okList), AutoSize = true, ForeColor = Color.DarkGreen, Font = Fnt(9.5f, FontStyle.Regular), Margin = new Padding(4, 6, 0, 2) };
                flow.Controls.Add(okLbl);
            }
            if (failList.Count > 0)
            {
                var failLbl = new Label { Text = "등록 실패 (" + failList.Count + "건):\n" + string.Join("\n", failList), AutoSize = true, ForeColor = Color.Firebrick, Font = Fnt(9.5f, FontStyle.Regular), Margin = new Padding(4, 4, 0, 2) };
                flow.Controls.Add(failLbl);
            }
            resultLabel = Note(job.Results.Count == 0 ? "" : "총 " + job.Results.Count + "건 (성공 " + okList.Count + "건, 실패 " + failList.Count + "건)");
            flow.Controls.Add(resultLabel);
            done();
            tabs.SelectedTab = reviewTab;
        }

        // 검토 화면에서 고친 값을 작업에 반영
        void ReadBack(Job job, bool names = true)
        {
            if (job != current || seedBox == null || seedBox.IsDisposed) return;
            // 시작 단어나 네이버 카테고리를 바꾸면 키워드를 알아서 다시 찾음 (대기열)
            string newSeed = seedBox.Text.Trim().Length > 0 ? seedBox.Text.Trim() : job.Seed;
            string newCat = naverCatBox.Text.Trim().Length > 0 ? naverCatBox.Text.Trim() : null;
            bool changed = job.Keywords.Count > 0 && (newSeed != job.Seed || newCat != job.NaverCat);
            job.Seed = newSeed;
            if (names) foreach (var kv in nameBoxes) job.Names[kv.Key] = Regex.Replace(kv.Value.Text.Trim(), @"\s+", " ");
            for (int n = 1; n <= 6; n++) { long p; if (priceBoxes[n] != null && long.TryParse(priceBoxes[n].Text.Replace(",", ""), out p) && p > 0) job.Prices[n] = p; }
            job.NaverCat = naverCatBox.Text.Trim().Length > 0 ? naverCatBox.Text.Trim() : null;
            job.CoupangCat = coupangCatBox.Text.Trim().Length > 0 ? coupangCatBox.Text.Trim() : null;
            job.Volume = volumeBox.Text.Trim();
            if (changed && job.Status != "대기" && job.Status != "진행 중")
            {
                if (newCat != null) job.NaverCatName = null;
                job.OrderMode = set.OrderMode;
                job.Status = "대기"; job.Pending = "keywords"; job.Step = "시작 단어·카테고리가 바뀌어 키워드 다시 찾기 대기";
                Status("[" + job.Code + "] 시작 단어·카테고리가 바뀌어 키워드·상품명·쿠팡 검색어를 다시 찾습니다 (대기열).");
                BeginInvoke(new Action(() => { RefreshList(); Pump(); }));
            }
            job.Health = healthBox.Checked;
            if (tagsBox != null && !tagsBox.IsDisposed)
            {
                job.CoupangTags1 = Tags(tagsBox.Text);
                job.CoupangTags = job.CoupangTags1;
                // 검색어 칸에 넣은 단어는 상품명에서 뺌
                foreach (var k in job.Keywords.Skip(1))
                    if (job.CoupangTags.Contains(k.Word, StringComparer.OrdinalIgnoreCase) && !job.DraftTags.Contains(k.Word, StringComparer.OrdinalIgnoreCase)) k.TagReason = k.TagReason ?? "직접";
            }
            if (tagsBox2 != null && !tagsBox2.IsDisposed) job.CoupangTags2 = Tags(tagsBox2.Text);
            if (tagsBox3 != null && !tagsBox3.IsDisposed) job.CoupangTags3 = Tags(tagsBox3.Text);
            Store.SaveJob(job);
        }

        // 대기열에 넣기: "keywords" = 키워드·상품명·쿠팡 검색어 다시, "tags" = 쿠팡 검색어만 다시
        Task Queue(Job job, string what)
        {
            if (job.Status == "진행 중" || (job.Status == "대기" && job.Pending == null)) { Status("[" + job.Code + "] 이미 대기열에 있습니다."); return Task.FromResult(0); }
            ReadBack(job);
            job.OrderMode = set.OrderMode;
            job.Status = "대기"; job.Pending = what; job.Step = what == "tags" ? "쿠팡 검색어 다시 찾기 대기" : "키워드 다시 찾기 대기";
            Store.SaveJob(job);
            RefreshList();
            ShowJob(job);
            Status("[" + job.Code + "] 대기열에 넣었습니다" + (pumping ? " (앞의 상품이 끝나면 차례로 합니다)." : "."));
            Pump();
            return Task.FromResult(0);
        }

        Task RefindKeywords(Job job) { return Queue(job, "keywords"); }

        // msgood4u 에 로그인돼 있으면, 로그인 없이 만든 초안(가격 없음)의 가격을 알아서 채움
        async Task QueuePriceIfLoggedIn()
        {
            if (msgoodView.CoreWebView2 == null || !await msgood.IsLoggedIn()) return;
            bool added = false;
            foreach (var j in jobs.Where(j => j.NeedPrice && j.Status == "검토 대기"))
            { j.Status = "대기"; j.Pending = "price"; j.Step = "가격 다시 계산 대기"; Store.SaveJob(j); added = true; }
            RefreshList();
            if (added) Pump();
        }

        async Task RegisterJob(Job job, Button b, bool failedOnly = false)
        {
            ReadBack(job);
            if (job.Status == "대기" || job.Status == "진행 중")
            {
                MessageBox.Show(this, "시작 단어·카테고리가 바뀌어 키워드를 다시 찾는 중입니다. 끝나면 확인 후 등록하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var errors = new List<string>();
            foreach (var kv in nameBoxes)
            {
                if (kv.Value.TextLength == 0) errors.Add(Naming.NameLabel(kv.Key) + " 상품명이 비었습니다");
                if (kv.Value.TextLength > Naming.MaxLen(kv.Key)) errors.Add(Naming.NameLabel(kv.Key) + " 상품명이 " + Naming.MaxLen(kv.Key) + "자를 넘습니다");
            }
            for (int n = 1; n <= 6; n++) if (job.Floor > 0 && job.Prices[n] < job.Floor * n) errors.Add(n + "개 가격이 하한선(" + Won(job.Floor * n) + ")보다 낮습니다");
            if (job.NeedPrice || job.Prices[1] <= 0) errors.Add("도매가·가격이 없습니다. msgood4u 에 로그인한 뒤 [가격 다시 계산]을 누르세요");
            if (!File.Exists(Pipeline.ImagePath(job, "rep", 1))) errors.Add("대표이미지가 없습니다");
            if (errors.Count > 0) { MessageBox.Show(this, string.Join("\n", errors), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var accs = set.Accounts.Where(a => a.Use).Select(a => "#" + a.Id).ToList();
            string prompt = failedOnly
                ? "[" + job.Code + "] 실패했던 계정/항목만 다시 등록을 시도합니다.\n진행할까요?"
                : "[" + job.Code + "] 를 " + string.Join(", ", accs) + " 계정에 등록합니다.\n" + "네이버는 전시중으로 올라갑니다." +
                  "\n" + (set.CoupangApprove ? "쿠팡은 승인요청까지 합니다." : "쿠팡은 임시저장만 합니다.") + "\n\n진행할까요?";
            if (MessageBox.Show(this, prompt, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            b.Enabled = false;
            resultLabel.Text = failedOnly ? "실패한 계정 재시도 중..." : "등록 중...";
            if (!failedOnly) job.Results.Clear();
            else job.Results.RemoveAll(r => r.StartsWith("✖"));
            try
            {
                await Task.Run(() => Accounts.Register(job, set, m => Status("[" + job.Code + "] " + m), failedOnly));
                bool ok = job.Results.Any(r => r.StartsWith("✔"));
                if (ok)
                {
                    Accounts.Learn(job, learned);
                    Store.Save(Store.LearnPath, learned);
                    job.Status = job.Results.Any(r => r.StartsWith("✖")) ? "일부 실패" : "등록 완료";
                    job.Registered = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                }
                Status("[" + job.Code + "] " + (ok ? "등록했습니다. 고친 내용은 학습했습니다." : "등록하지 못했습니다."));
            }
            catch (Exception ex) { job.Results.Add("✖ " + ex.Message); }
            finally
            {
                Store.SaveJob(job);
                RefreshList();
                if (current == job) ShowJob(job);
            }
        }

        class AccountRowControls
        {
            public AccountPlan Plan;
            public CheckBox Use;
            public TextBox BusinessId;
            public ComboBox Style;
            public TextBox NaverTemplate;
            public TextBox CoupangTemplate;
            public TextBox NaverCounts;
            public TextBox CoupangCounts;
        }

        // ── 설정
        void BuildSettingsTab()
        {
            ClearDispose(settingsTab);
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };
            settingsTab.Controls.Add(flow);
            flow.Controls.Add(Section("계정별 등록 (#1 #2 #3 은 서로 다른 판매자 계정)"));
            flow.Controls.Add(Row(Lbl("", 70), Lbl("사업자ID", 95), Lbl("상품명 스타일", 90), Lbl("네이버 틀번호", 115), Lbl("쿠팡 틀번호", 115), Lbl("스토어수", 75), Lbl("쿠팡수", 75), Lbl("msgood 로그인", 170), Lbl("키 상태", 150)));
            var rows = new List<AccountRowControls>();
            foreach (var a in set.Accounts)
            {
                var use = new CheckBox { Text = "#" + a.Id + " 사용", Checked = a.Use, Width = 70, Margin = new Padding(3, 5, 0, 0) };
                var bid = new TextBox { Width = 95, Text = a.BusinessId ?? "" };
                var style = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
                style.Items.AddRange(new object[] { "기본", "브랜드 앞", "효능 앞", "함량 앞" });
                style.SelectedItem = string.IsNullOrEmpty(a.NameStyle) ? "기본" : a.NameStyle;
                var nt = new TextBox { Width = 115, Text = a.NaverTemplate };
                var ct = new TextBox { Width = 115, Text = a.CoupangTemplate };
                var nc = new TextBox { Width = 75, Text = a.NaverCounts };
                var cc = new TextBox { Width = 75, Text = a.CoupangCounts };
                var cr = MsgoodCred.Load(a.Id);
                string mgStatus = cr == null ? "msgood ✖" : ((cr.HasMember ? "회원✔ " : "") + (cr.HasSeller ? "셀러✔" : "")).Trim();
                if (string.IsNullOrEmpty(mgStatus)) mgStatus = "msgood ✖";
                var mgBtn = Btn("msgood 로그인", () => ShowMsgoodLoginDialog(a.Id));
                var mgLabel = Note(mgStatus);
                string keys;
                try { var c = Accounts.Config(a.Id); keys = c == null ? "키 파일 없음" : (c.HasNaver ? "네이버 ✔" : "네이버 키 없음") + " · " + (c.HasCoupang ? "쿠팡 ✔" : "쿠팡 키 없음"); }
                catch (Exception ex) { keys = "키 파일 오류: " + ex.Message; }
                flow.Controls.Add(Row(use, bid, style, nt, ct, nc, cc, mgBtn, mgLabel, Note(keys)));
                rows.Add(new AccountRowControls { Plan = a, Use = use, BusinessId = bid, Style = style, NaverTemplate = nt, CoupangTemplate = ct, NaverCounts = nc, CoupangCounts = cc });
            }
            flow.Controls.Add(Note("사업자ID: PC 간 상품명 공유 기록에 남길 사업자 식별자 (예: 상호 또는 사업자번호).\n" +
                                   "상품명 스타일: 기본(키워드 순) / 브랜드 앞 / 효능 앞 / 함량 앞.\n" +
                                   "틀 상품: 그 계정에 이미 등록된 상품 하나(배송·반품·A/S·공지 설정을 그대로 복사). 스토어 개수는 1,2 까지, 쿠팡 개수는 1~6 중 쉼표로.\n" +
                                   "#2·#3 키는 api_keys.json 의 naver_commerce_2 / coupang_2 (#3 은 _3) 칸에 넣습니다."));

            flow.Controls.Add(Section("공통"));
            var sharedBox = new TextBox { Width = 360, Text = set.SharedFolder ?? "" };
            var browseBtn = Btn("폴더 선택", () =>
            {
                using (var fbd = new FolderBrowserDialog())
                {
                    if (fbd.ShowDialog() == DialogResult.OK) sharedBox.Text = fbd.SelectedPath;
                }
            });
            flow.Controls.Add(Row(Lbl("공유 키워드 기록 폴더", 150), sharedBox, browseBtn, Note("PC 간 상품명 중복 방지 (예: 구글 드라이브 폴더)")));
            var ratio = new TextBox { Width = 50, Text = set.MaxRatio.ToString() };
            var normal = new TextBox { Width = 50, Text = set.NormalRate.ToString() };
            var stock = new TextBox { Width = 60, Text = set.Stock.ToString() };
            var daily = new TextBox { Width = 50, Text = set.DailyLimit.ToString() };
            var tagMin = new TextBox { Width = 60, Text = set.TagMinVolume.ToString() };
            var reuseN = new TextBox { Width = 40, Text = set.MaxReuse.ToString() };
            var reuseT = new TextBox { Width = 70, Text = set.ReuseMaxTotal.ToString() };
            var paused = new CheckBox { Text = "네이버는 항상 전시중으로 등록합니다", Checked = false, Enabled = false, AutoSize = true };
            var approve = new CheckBox { Text = "쿠팡은 승인요청까지 (끄면 임시저장)", Checked = set.CoupangApprove, AutoSize = true };
            flow.Controls.Add(Row(Lbl("소형키워드 기준: 상품총수량 <", 190), ratio, Lbl("× 월간검색량", 90), Lbl("정상가 배수", 80), normal, Lbl("재고", 40), stock, Lbl("하루 최대", 70), daily));
            flow.Controls.Add(Row(Lbl("쿠팡 검색어: 월간검색량", 150), tagMin, Lbl("이상인 브랜드·위험 단어·상품에 없는 말만 넣음", 300)));
            flow.Controls.Add(Row(Lbl("상품끼리 키워드 겹침: 같은 말은 최대", 230), reuseN, Lbl("개 상품까지, 상품총수량 <", 160), reuseT, Lbl("이고 배율 조건에 맞는 말만", 200)));
            {
                var tk = SmallKeyword.TrendKeys.Load(Store.Dir);
                var trendState = Note(tk.Ready ? "키 있음" + (Pipeline.TrendNote != null ? " · 마지막 조회 " + Pipeline.TrendNote : "")
                                               : "키 없음 → 키 파일의 naver_apihub 칸에 NAVER API HUB 인증 정보(Client ID / Client Secret)를 넣으면 켜집니다");
                flow.Controls.Add(Row(Lbl("최근 급상승 키워드 (검색어 트렌드)", 230), trendState,
                    Btn("키 확인", async () =>
                    {
                        var k = SmallKeyword.TrendKeys.Load(Store.Dir);
                        if (!k.Ready) { trendState.Text = "키 없음: 키 파일의 naver_apihub 칸을 채우고 저장한 뒤 다시 눌러 주세요"; return; }
                        trendState.Text = "확인 중...";
                        try
                        {
                            var r = await System.Threading.Tasks.Task.Run(() => new SmallKeyword.Trend(k).Get(new[] { "오메가3" }, DateTime.Today, null));
                            SmallKeyword.TrendInfo t;
                            trendState.Text = r.TryGetValue("오메가3", out t) ? "정상 ✔ (예: 오메가3 최근 28일 " + (t.Growth < 0 ? "-" : t.Growth.ToString("0.00") + "배") + ")" : "응답은 왔지만 결과가 비어 있습니다";
                        }
                        catch (Exception ex) { trendState.Text = ex.Message; }
                    })));
                flow.Controls.Add(Note("최근 28일 검색이 그 전 석 달 평균보다 1.5배 이상이고 작년 이맘때보다도 많으며 월간검색량 300 이상인 말을 '급상승'으로 보고 상품명에 먼저 넣습니다. 매년 이맘때 오르는 계절 말은 빼고 봅니다."));
            }
            flow.Controls.Add(paused);
            flow.Controls.Add(approve);
            flow.Controls.Add(Row(
                Btn("저장", () =>
                {
                    foreach (var r in rows)
                    {
                        r.Plan.Use = r.Use.Checked;
                        r.Plan.BusinessId = r.BusinessId.Text.Trim();
                        r.Plan.NameStyle = Convert.ToString(r.Style.SelectedItem);
                        r.Plan.NaverTemplate = r.NaverTemplate.Text.Trim();
                        r.Plan.CoupangTemplate = r.CoupangTemplate.Text.Trim();
                        r.Plan.NaverCounts = r.NaverCounts.Text.Trim();
                        r.Plan.CoupangCounts = r.CoupangCounts.Text.Trim();
                    }
                    set.SharedFolder = sharedBox.Text.Trim();
                    double d; int i;
                    if (double.TryParse(ratio.Text, out d) && d > 0) set.MaxRatio = d;
                    if (double.TryParse(normal.Text, out d) && d > 0) set.NormalRate = d;
                    if (int.TryParse(stock.Text, out i) && i > 0) set.Stock = i;
                    if (int.TryParse(daily.Text, out i) && i > 0) set.DailyLimit = i;
                    long lv; if (long.TryParse(tagMin.Text, out lv) && lv >= 0) set.TagMinVolume = lv;
                    if (int.TryParse(reuseN.Text, out i) && i >= 1) set.MaxReuse = i;
                    if (long.TryParse(reuseT.Text, out lv) && lv >= 0) set.ReuseMaxTotal = lv;
                    set.CoupangApprove = approve.Checked;
                    Store.Save(Store.SettingsPath, set);
                    Accounts.Reset();
                    BuildSettingsTab(); RefreshList();
                    Status("설정을 저장했습니다.");
                }),
                Btn("키 파일 열기 (메모장)", () => Process.Start("notepad.exe", "\"" + Accounts.KeyFile + "\"")),
                Btn("키 다시 읽기", () => { Accounts.Reset(); BuildSettingsTab(); })));
            flow.Controls.Add(Note("키 파일: " + Accounts.KeyFile));

            // msgood4u 자동 로그인 안내
            flow.Controls.Add(Section("msgood4u 자동 로그인 (계정별 암호화 저장)"));
            flow.Controls.Add(Note("각 사업자(계정 #1, #2, #3)마다 msgood4u 회원/셀러 로그인을 따로 사용합니다.\n위 '계정별 등록' 표의 각 계정 줄에서 [msgood 로그인] 버튼을 눌러 아이디·비밀번호를 설정하세요."));
            flow.Controls.Add(Row(
                Btn("#1 지금 로그인", async () => { tabs.SelectedTab = msgoodTab; if (await msgood.AutoLogin(Status, MsgoodCred.Load("1"))) await QueuePriceIfLoggedIn(); }),
                Btn("#2 지금 로그인", async () => { tabs.SelectedTab = msgoodTab; if (await msgood.AutoLogin(Status, MsgoodCred.Load("2"))) await QueuePriceIfLoggedIn(); }),
                Btn("#3 지금 로그인", async () => { tabs.SelectedTab = msgoodTab; if (await msgood.AutoLogin(Status, MsgoodCred.Load("3"))) await QueuePriceIfLoggedIn(); })));
            flow.Controls.Add(Note("비밀번호는 이 PC의 Windows 사용자 전용 암호화(DPAPI)로 저장되어 배포 파일에 포함되지 않으며 외부로 유출되지 않습니다."));

            // 휴대폰 앱
            flow.Controls.Add(Section("휴대폰 앱 (안드로이드) — 같은 와이파이 또는 Tailscale 로 이 PC에 접속"));
            var mobOn = new CheckBox { Text = "휴대폰 앱 서버 켜기", Checked = set.MobileOn, AutoSize = true };
            mobOn.CheckedChanged += (s, e) => { set.MobileOn = mobOn.Checked; Store.Save(Store.SettingsPath, set); if (set.MobileOn) mobile.Start(set.MobilePort, () => set.MobileKey, OnMobile, StockLog); else mobile.Stop(); BuildSettingsTab(); };
            flow.Controls.Add(Row(mobOn, Note("상태: " + mobile.State)));
            flow.Controls.Add(new Label { AutoSize = true, Font = Fnt(13f, FontStyle.Bold), ForeColor = Color.FromArgb(30, 60, 120),
                Text = "연결 키: " + set.MobileKey + "\n앱에 넣을 주소: " + string.Join("  /  ", MobileServer.Addresses().Select(a => a.Split(' ')[0] + ":" + set.MobilePort)) });
            flow.Controls.Add(Note(string.Join("\n", MobileServer.Addresses().Select(a => "· " + a))));
            flow.Controls.Add(Note("앱 설치: 휴대폰이 같은 와이파이일 때 휴대폰 인터넷 창에서 http://" + (MobileServer.Addresses().Select(x => x.Split(' ')[0]).FirstOrDefault(x => !x.StartsWith("100.")) ?? "PC주소") + ":" + set.MobilePort + "/app.apk 를 열어 받고 설치 (출처를 알 수 없는 앱 허용 필요)"));
            flow.Controls.Add(Row(Btn("연결 키 새로 만들기", () => { set.MobileKey = NewKey(); Store.Save(Store.SettingsPath, set); BuildSettingsTab(); Status("연결 키를 바꿨습니다. 휴대폰 앱에 새 키를 넣어 주세요."); })));
            flow.Controls.Add(Note("처음 켜면 Windows 방화벽 창이 뜹니다 → '개인 네트워크'와 '공용 네트워크' 모두 허용해 주세요 (Tailscale 연결은 공용으로 잡힐 수 있음).\n" +
                                   "밖에서 쓰려면: PC와 휴대폰에 Tailscale 앱을 설치하고 같은 계정으로 로그인 → 위 주소 중 100. 으로 시작하는 주소를 앱에 넣으세요."));

            // 텔레그램 알림
            flow.Controls.Add(Section("알림 설정 (휴대폰 앱 및 텔레그램)"));
            var notifyDraftBox = new CheckBox { Text = "초안 완성 알림 받기 (체크 해제 시 초안 알림을 끄고 발주·품절 알림만 전송)", AutoSize = true, Checked = set.NotifyDraft };
            notifyDraftBox.CheckedChanged += (s, e) => { set.NotifyDraft = notifyDraftBox.Checked; Store.Save(Store.SettingsPath, set); };
            flow.Controls.Add(Row(notifyDraftBox));
            var tg = TelegramNotify.Cred;
            var tgToken = new TextBox { Width = 330, UseSystemPasswordChar = true };
            var tgOn = new CheckBox { Text = "텔레그램으로 알림 받기", AutoSize = true, Checked = tg != null && tg.On, Enabled = tg != null && !string.IsNullOrEmpty(tg.ChatId) };
            tgOn.CheckedChanged += (s, e) => { var c = TelegramCred.Load(); if (c == null) return; c.On = tgOn.Checked; c.Save(); TelegramNotify.Reload(); };
            string tgState = tg == null || string.IsNullOrEmpty(tg.Token) ? "연결 안 됨"
                           : string.IsNullOrEmpty(tg.ChatId) ? "봇 @" + tg.BotName + " 확인됨 · 텔레그램에서 /start 를 보낸 뒤 '연결 확인'을 눌러 주세요"
                           : "연결됨: " + (string.IsNullOrEmpty(tg.ChatName) ? "대화방 " + tg.ChatId : tg.ChatName) + " (봇 @" + tg.BotName + ")";
            flow.Controls.Add(Row(tgOn, Note("상태: " + tgState + (TelegramNotify.LastError != null ? "  /  마지막 오류: " + TelegramNotify.LastError : ""))));
            flow.Controls.Add(Row(Lbl("봇 토큰", 80), tgToken, Note(tg != null && !string.IsNullOrEmpty(tg.Token) ? "(저장됨 · 바꿀 때만 입력)" : "")));
            flow.Controls.Add(Row(
                Btn("연결 확인", async () =>
                {
                    string token = tgToken.Text.Trim();
                    if (token.Length == 0 && tg != null) token = tg.Token ?? "";
                    if (token.Length == 0) { MessageBox.Show(this, "봇 토큰을 넣어 주세요.", "텔레그램 알림"); return; }
                    string msg;
                    try { msg = await Task.Run(() => TelegramNotify.Connect(token)); }
                    catch (Exception ex) { msg = "연결하지 못했습니다: " + ex.Message; }
                    MessageBox.Show(this, msg, "텔레그램 알림");
                    BuildSettingsTab();
                }),
                Btn("테스트 알림 보내기", async () =>
                {
                    string msg;
                    try { msg = await Task.Run(() => TelegramNotify.Test()); }
                    catch (Exception ex) { msg = "보내지 못했습니다: " + ex.Message; }
                    MessageBox.Show(this, msg, "텔레그램 알림");
                }),
                Btn("연결 지우기", () => { TelegramCred.Delete(); TelegramNotify.Reload(); BuildSettingsTab(); Status("텔레그램 연결을 지웠습니다."); })));
            flow.Controls.Add(Note("처음 한 번만:\n" +
                                   "1) 텔레그램 앱에서 @BotFather 를 찾아 대화 → /newbot 입력 → 봇 이름과 아이디(끝이 bot)를 정하면 '토큰'(숫자:글자)을 줍니다.\n" +
                                   "2) 그 토큰을 위 '봇 토큰' 칸에 붙여 넣고 '연결 확인'을 누릅니다.\n" +
                                   "3) 텔레그램에서 방금 만든 내 봇을 열어 [시작]을 누르고, 다시 '연결 확인'을 누르면 끝. 연결 메시지가 텔레그램에 옵니다.\n" +
                                   "봇은 사람마다 각자 만듭니다. 토큰은 이 PC에 암호화해서 저장하고 배포용에는 들어가지 않습니다. 알림에는 고객 이름·주소를 넣지 않습니다."));

            // 자동 업데이트 (GitHub Releases)
            flow.Controls.Add(Section("프로그램 자동 업데이트 (GitHub Releases)"));
            var repoBox = new TextBox { Width = 260, Text = set.GithubRepo ?? "" };
            var autoChk = new CheckBox { Text = "새 버전이 나오면 묻지 않고 자동 업데이트 (초안 하나가 끝난 뒤, 발주 시간 앞뒤는 피함)", AutoSize = true, Checked = set.AutoUpdateCheck, Margin = new Padding(10, 6, 0, 0) };
            flow.Controls.Add(Row(Lbl("GitHub 저장소", 110), repoBox, autoChk,
                Btn("저장", () =>
                {
                    set.GithubRepo = repoBox.Text.Trim();
                    set.AutoUpdateCheck = autoChk.Checked;
                    Store.Save(Store.SettingsPath, set);
                    Status("GitHub 저장소 설정을 저장했습니다.");
                }),
                Btn("지금 업데이트 확인", async () =>
                {
                    set.GithubRepo = repoBox.Text.Trim();
                    set.AutoUpdateCheck = autoChk.Checked;
                    Store.Save(Store.SettingsPath, set);
                    await AutoUpdater.CheckAndPromptAsync(set.GithubRepo, false);
                })));
            flow.Controls.Add(Note("내 GitHub 저장소(예: 아이디/저장소명, 또는 releases/latest API URL)를 적어 두면,\n내 PC에서 빌드하여 GitHub에 올렸을 때 다른 사용자가 프로그램을 켤 때 알아서 최신 버전으로 자동 업데이트됩니다.\n현재 프로그램 버전: v" + AutoUpdater.CurrentVersion));

            // AI 대표이미지 자동 생성
            flow.Controls.Add(Section("AI 3D 대표이미지 자동 생성"));
            var autoAiChk = new CheckBox { Text = "상품 대기열 추가 시 AI 3D 대표이미지 자동 생성 (공급사 겹치는 사진 대신 나만의 독창적 썸네일)", Checked = set.AutoAiImage, AutoSize = true };
            autoAiChk.CheckedChanged += (s, e) =>
            {
                set.AutoAiImage = autoAiChk.Checked;
                Store.Save(Store.SettingsPath, set);
                Status(set.AutoAiImage ? "AI 대표이미지 자동 생성을 켰습니다." : "AI 대표이미지 자동 생성을 껐습니다.");
            };
            flow.Controls.Add(Row(autoAiChk));
            flow.Controls.Add(Note("켜두시면 상품번호를 넣을 때 공급사의 뻔한 사진 대신, AI가 상품 성분에 맞는 세련된 3D 보틀 이미지를 자동으로 생성해 1~6통 배지를 합성합니다. (검토 화면에서도 언제든 원클릭으로 다시 생성/교체 가능)"));
        }

        // ── 품절·판매
        void StockLog(string m)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => StockLog(m))); return; }
            lock (recentLog) { recentLog.Add(DateTime.Now.ToString("MM-dd HH:mm") + "  " + m); if (recentLog.Count > 80) recentLog.RemoveAt(0); }
            if (stockLog != null) stockLog.AppendText(DateTime.Now.ToString("MM-dd HH:mm") + "  " + m + "\r\n");
            Status(m);
        }

        // auto: 자동(묻지 않고 반영). 아니면 바꿀 목록을 보여 주고 물어봄
        async Task StockRun(bool auto)
        {
            if (stockBusy) return;
            stockBusy = true;
            try
            {
                var plan = await Task.Run(() => StockSync.Plan(set, StockLog));
                if (plan.Count == 0) { StockLog("바꿀 상품이 없습니다 (품절된 상품이 스토어에 판매 중이지 않음)."); return; }
                foreach (var c in plan) StockLog("  · " + c);
                if (!auto && MessageBox.Show(this, string.Join("\n", plan.Select(c => c.ToString()).Take(30)) + (plan.Count > 30 ? "\n…" : "") + "\n\n위 " + plan.Count + "개를 바꿀까요?\n(네이버: 판매중지·판매중 / 쿠팡: 옵션 판매중지·재개)",
                                             "품절 연동", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { StockLog("바꾸지 않았습니다."); return; }
                await Task.Run(() => StockSync.Apply(plan, set, StockLog));
                StockLog("품절 연동을 마쳤습니다.");
                Phone("품절 연동: " + string.Join("\n", plan.Select(c => c.Market + " #" + c.Account + " " + c.Code + " " + c.Action)));
            }
            catch (Exception ex) { StockLog("품절 연동 오류: " + ex.Message); }
            finally { stockBusy = false; }
        }

        // ── 휴대폰 앱 (판매도구 안의 작은 서버)
        static string NewKey()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var rnd = new System.Security.Cryptography.RNGCryptoServiceProvider(); var b = new byte[8]; rnd.GetBytes(b);
            return new string(b.Select(x => chars[x % chars.Length]).ToArray());
        }

        // 네이버 로그인 상태 (로그인 쿠키 NID_AUT 가 있으면 로그인)
        async Task<bool> NaverLoggedIn()
        {
            try
            {
                if (naverView == null || naverView.CoreWebView2 == null) return false;
                var cookies = await naverView.CoreWebView2.CookieManager.GetCookiesAsync("https://nid.naver.com");
                return cookies.Any(c => c.Name == "NID_AUT" && !string.IsNullOrEmpty(c.Value));
            }
            catch { return false; }
        }
        Button naverLoginBtn;
        void UpdateNaverLoginButton(bool loggedIn)
        {
            if (naverLoginBtn == null) return;
            naverLoginBtn.Text = loggedIn ? "네이버 로그인 ✔" : "네이버 로그인 필요";
            naverLoginBtn.BackColor = loggedIn ? Color.FromArgb(220, 245, 225) : Color.FromArgb(255, 228, 200);
        }
        async Task RefreshNaverLoginButton() { UpdateNaverLoginButton(await NaverLoggedIn()); }

        // 실제 마우스·키 입력 (DevTools Input): 코드로 값만 넣으면 네이버가 사람 입력으로 보지 않음
        Task Cdp(Microsoft.Web.WebView2.Core.CoreWebView2 wv, string m, Dictionary<string, object> p)
        { return wv.CallDevToolsProtocolMethodAsync(m, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(p)); }
        async Task CdpClick(Microsoft.Web.WebView2.Core.CoreWebView2 wv, double x, double y)
        {
            await Cdp(wv, "Input.dispatchMouseEvent", new Dictionary<string, object> { { "type", "mouseMoved" }, { "x", x }, { "y", y } });
            await Cdp(wv, "Input.dispatchMouseEvent", new Dictionary<string, object> { { "type", "mousePressed" }, { "x", x }, { "y", y }, { "button", "left" }, { "clickCount", 1 } });
            await Task.Delay(60);
            await Cdp(wv, "Input.dispatchMouseEvent", new Dictionary<string, object> { { "type", "mouseReleased" }, { "x", x }, { "y", y }, { "button", "left" }, { "clickCount", 1 } });
        }
        async Task CdpType(Microsoft.Web.WebView2.Core.CoreWebView2 wv, string text)
        {
            foreach (char ch in text)
            {
                string s1 = ch.ToString();
                var down = new Dictionary<string, object> { { "type", "keyDown" }, { "text", s1 }, { "unmodifiedText", s1 }, { "key", s1 } };
                var up = new Dictionary<string, object> { { "type", "keyUp" }, { "key", s1 } };
                if (char.IsDigit(ch)) { down["code"] = up["code"] = "Digit" + s1; down["windowsVirtualKeyCode"] = up["windowsVirtualKeyCode"] = (int)ch; }
                await Cdp(wv, "Input.dispatchKeyEvent", down);
                await Cdp(wv, "Input.dispatchKeyEvent", up);
                await Task.Delay(90);
            }
        }
        async Task CdpEnter(Microsoft.Web.WebView2.Core.CoreWebView2 wv)
        {
            await Cdp(wv, "Input.dispatchKeyEvent", new Dictionary<string, object> { { "type", "keyDown" }, { "key", "Enter" }, { "code", "Enter" }, { "windowsVirtualKeyCode", 13 }, { "text", "\r" } });
            await Cdp(wv, "Input.dispatchKeyEvent", new Dictionary<string, object> { { "type", "keyUp" }, { "key", "Enter" }, { "code", "Enter" }, { "windowsVirtualKeyCode", 13 } });
        }

        static string mobileHtml;
        static string MobileHtml()
        {
            if (mobileHtml != null) return mobileHtml;
            using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("mobile.html"))
            using (var r = new StreamReader(s, System.Text.Encoding.UTF8)) return mobileHtml = r.ReadToEnd();
        }

        Task<MobileResponse> OnMobile(string method, string path, Dictionary<string, string> q, string body)
        {
            if (path == "/" || path == "/index.html") return Task.FromResult(MobileResponse.Text(MobileHtml(), "text/html; charset=utf-8"));
            if (path == "/api/events")
            {
                long after; long.TryParse(q.ContainsKey("after") ? q["after"] : "0", out after);
                int wait; int.TryParse(q.ContainsKey("wait") ? q["wait"] : "0", out wait);
                return AppEvents.Wait(after, wait).ContinueWith(t => MobileResponse.Json(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(t.Result)));
            }
            if (path == "/app.apk")
            {
                // 휴대폰 앱 설치 파일 (판매도구 폴더의 판매도구_휴대폰앱.apk)
                string f = Path.Combine(Store.Dir, "판매도구_휴대폰앱.apk");
                if (!File.Exists(f)) return Task.FromResult(MobileResponse.Error(404, "앱 파일이 없습니다"));
                return Task.FromResult(new MobileResponse { ContentType = "application/vnd.android.package-archive", Body = File.ReadAllBytes(f) });
            }
            var done = new TaskCompletionSource<MobileResponse>();
            BeginInvoke(new Action(async () =>
            {
                try { done.TrySetResult(await MobileApi(method, path, q, body)); }
                catch (Exception ex) { done.TrySetResult(MobileResponse.Error(500, ex.Message)); }
            }));
            return done.Task;
        }

        async Task<MobileResponse> MobileApi(string method, string path, Dictionary<string, string> q, string body)
        {
            var js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Func<object, MobileResponse> J = o => MobileResponse.Json(js.Serialize(o));
            var b = (string.IsNullOrEmpty(body) ? null : js.DeserializeObject(body)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            Func<string, string> arg = k => q.ContainsKey(k) ? q[k] : b.ContainsKey(k) ? Convert.ToString(b[k]) : "";
            Func<string, Job> find = c => jobs.FirstOrDefault(j => j.Code == (c ?? "").Trim());
            switch (path)
            {
                case "/api/status":
                {
                    var rec = OrderSync.LoadRecord();
                    var run = jobs.FirstOrDefault(j => j.Status == "진행 중");
                    List<string> logs; lock (recentLog) logs = recentLog.Skip(Math.Max(0, recentLog.Count - 30)).ToList();
                    return J(new { today = TodayCount(), limit = set.DailyLimit, waiting = jobs.Count(j => j.Status == "대기"), review = jobs.Count(j => j.Status == "검토 대기"),
                        running = run == null ? null : new { code = run.Code, step = run.Step }, paused,
                        captcha = run != null && (run.Step ?? "").Contains("보안 확인"), autoOrder = set.AutoOrder, ordered = rec.Done.Count,
                        tracked = rec.Done.Values.Count(d => !string.IsNullOrEmpty(d.Tracking)), pending = rec.Pending.Count, lastShortage = rec.LastShortage, log = logs,
                        schedule = OrderScheduleText, nextOrder = NextOrderSlot() });
                }
                case "/api/drafts":
                    return J(jobs.OrderByDescending(j => j.Status == "진행 중").ThenByDescending(j => j.Status == "검토 대기").ThenByDescending(j => j.Added).Take(60).Select(j => new
                    { code = j.Code, status = j.Status, step = j.Status == "대기" || j.Status == "진행 중" ? j.Step : null, name = j.Name, n1 = j.Names.ContainsKey("n1_1") ? j.Names["n1_1"] : null, price1 = j.Prices[1] }).ToList());
                case "/api/job":
                {
                    var j = find(arg("code")); if (j == null) return MobileResponse.Error(404, "상품이 목록에 없습니다");
                    var accs = string.Join(", ", set.Accounts.Where(a => a.Use).Select(a => "#" + a.Id));
                    return J(new { code = j.Code, status = j.Status, name = j.Name, seed = j.Seed, naverCat = j.NaverCatName, coupangCat = j.CoupangCatName,
                        names = j.Names.Select(kv => new { key = kv.Key, label = Naming.NameLabel(kv.Key), text = kv.Value }).ToList(), prices = Enumerable.Range(1, 6).Select(n => j.Prices[n]).ToList(),
                        tags = j.CoupangTags,
                        tags1 = j.CoupangTags1 != null && j.CoupangTags1.Count > 0 ? j.CoupangTags1 : j.CoupangTags,
                        tags2 = j.CoupangTags2 ?? new List<string>(),
                        tags3 = j.CoupangTags3 ?? new List<string>(),
                        results = j.Results, problems = j.Status == "검토 대기" ? RegisterProblems(j) : new List<string>(),
                        hasImage = File.Exists(Pipeline.ImagePath(j, "rep", 1)), extras = Pipeline.ExtraFiles(j).Count,
                        repCount = Enumerable.Range(1, 6).Count(n => File.Exists(Pipeline.ImagePath(j, "rep", n))),
                        naverCount = Enumerable.Range(1, 2).Count(n => File.Exists(Pipeline.ImagePath(j, "naver", n))),
                        imgVer = new[] { "rep", "naver", "extra" }.Select(k => Pipeline.ImagePath(j, k, 1)).Where(File.Exists).Select(f => File.GetLastWriteTime(f).Ticks).DefaultIfEmpty(0).Max(), registerNote = accs + " (" + "네이버 전시중" + " · " + (set.CoupangApprove ? "쿠팡 승인요청" : "쿠팡 임시저장") + "으로)" });
                }
                case "/api/image":
                {
                    var j = find(arg("code")); int n; int.TryParse(arg("n"), out n);
                    // kind=extra → 추가이미지 (등록에 쓰는 extra_1~4), 없으면 대표이미지
                    string kind = arg("kind") == "extra" ? "extra" : arg("kind") == "naver" ? "naver" : "rep";   // naver → 네이버 스토어용(상표 그대로)
                    string f = j == null ? null : Pipeline.ImagePath(j, kind, Math.Max(1, n));
                    if (f == null || !File.Exists(f)) return MobileResponse.Error(404, "이미지 없음");
                    return new MobileResponse { ContentType = f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg", Body = File.ReadAllBytes(f) };
                }
                // ── 네이버 보안 확인을 휴대폰에서 직접 풀기: 작업 화면 캡처를 보여 주고, 사용자가 넣은 답을 작업 화면에 입력 ──
                case "/api/captcha":
                {
                    if (naverView == null || naverView.CoreWebView2 == null) return MobileResponse.Error(404, "작업 화면이 아직 없습니다");
                    // 1순위: 문제 그림(영수증) 자체를 꺼냄 — 작업 화면이 작아 그림이 잘려도 온전하게 보임
                    try
                    {
                        string pic = await naverView.CoreWebView2.ExecuteScriptAsync("(() => { const els = [...document.querySelectorAll('img, canvas')].filter(e => { const r = e.getBoundingClientRect(); return r.width >= 120 && r.height >= 80; });" +
                            " if (!els.length) return ''; els.sort((a, b) => { const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect(); return rb.width * rb.height - ra.width * ra.height; });" +
                            " const e = els[0]; e.scrollIntoView({ block: 'center' });" +
                            " try { if (e.tagName === 'CANVAS') return e.toDataURL('image/png'); if (e.src && e.src.indexOf('data:image') === 0) return e.src;" +
                            "   const c = document.createElement('canvas'); c.width = e.naturalWidth; c.height = e.naturalHeight; c.getContext('2d').drawImage(e, 0, 0); return c.toDataURL('image/png'); } catch (x) { return ''; } })()");
                            pic = js.Deserialize<string>(pic) ?? "";
                        var m = Regex.Match(pic, @"^data:image/(\w+);base64,(.+)$");
                        if (m.Success && m.Groups[2].Value.Length > 2000)
                            return new MobileResponse { ContentType = "image/" + m.Groups[1].Value, Body = Convert.FromBase64String(m.Groups[2].Value) };
                    }
                    catch { }
                    // 2순위: 그림을 가운데로 내린 뒤 작업 화면을 캡처
                    await Task.Delay(300);
                    using (var ms = new MemoryStream())
                    {
                        await naverView.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, ms);
                        return new MobileResponse { ContentType = "image/png", Body = ms.ToArray() };
                    }
                }
                case "/api/captcha_info":
                {
                    if (naverView == null || naverView.CoreWebView2 == null) return J(new { captcha = false, text = "" });
                    string r = await naverView.CoreWebView2.ExecuteScriptAsync("(() => { const t = document.body ? document.body.innerText : ''; return { captcha: t.slice(0, 600).indexOf('보안 확인') >= 0, text: t.replace(/\\n{2,}/g, '\\n').slice(0, 400) }; })()");
                    return MobileResponse.Json(r);
                }
                case "/api/captcha_answer":
                {
                    if (naverView == null || naverView.CoreWebView2 == null) return MobileResponse.Error(404, "작업 화면이 아직 없습니다");
                    string ans = (arg("answer") ?? "").Trim();
                    if (ans.Length == 0) return MobileResponse.Error(400, "답을 넣어 주세요");
                    var wv = naverView.CoreWebView2;
                    // 1) 답 칸·확인 버튼 위치 찾기 (칸은 비워 둠). 네이버는 코드로 넣은 값은 사람이 친 것으로 안 봐서,
                    //    실제 클릭·키 입력(DevTools Input)으로 보냄
                    string q0 = await wv.ExecuteScriptAsync("(() => { const vis = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);" +
                        " const inp = [...document.querySelectorAll('input')].filter(i => vis(i) && /^(text|tel|number|search|)$/i.test(i.getAttribute('type') || ''));" +
                        " if (!inp.length) return { ok: false };" +
                        " const i = inp[0]; i.scrollIntoView({ block: 'center' }); i.focus(); i.select && i.select();" +
                        " Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(i, ''); i.dispatchEvent(new Event('input', { bubbles: true }));" +
                        " const ri = i.getBoundingClientRect();" +
                        " const b = [...document.querySelectorAll('button, input[type=submit], input[type=button], a, [role=button]')].filter(vis).find(x => /^(확인|제출|입력)$/.test((x.innerText || x.value || '').trim()));" +
                        " const rb = b ? b.getBoundingClientRect() : null;" +
                        " return { ok: true, ix: ri.left + ri.width / 2, iy: ri.top + ri.height / 2, bx: rb ? rb.left + rb.width / 2 : -1, by: rb ? rb.top + rb.height / 2 : -1 }; })()");
                    var p0 = js.DeserializeObject(q0) as Dictionary<string, object>;
                    if (p0 == null || !Convert.ToBoolean(p0["ok"])) return J(new { ok = false, passed = false, message = "작업 화면에 답 넣는 칸이 없습니다 (이미 풀렸을 수 있음)" });
                    Func<string, Dictionary<string, object>, Task> cdp = (m, prm) => wv.CallDevToolsProtocolMethodAsync(m, js.Serialize(prm));
                    Func<double, double, Task> click = async (x, y) =>
                    {
                        await cdp("Input.dispatchMouseEvent", new Dictionary<string, object> { { "type", "mouseMoved" }, { "x", x }, { "y", y } });
                        await cdp("Input.dispatchMouseEvent", new Dictionary<string, object> { { "type", "mousePressed" }, { "x", x }, { "y", y }, { "button", "left" }, { "clickCount", 1 } });
                        await Task.Delay(60);
                        await cdp("Input.dispatchMouseEvent", new Dictionary<string, object> { { "type", "mouseReleased" }, { "x", x }, { "y", y }, { "button", "left" }, { "clickCount", 1 } });
                    };
                    await click(Convert.ToDouble(p0["ix"]), Convert.ToDouble(p0["iy"]));
                    await Task.Delay(150);
                    // 2) 한 글자씩 실제 키 입력
                    foreach (char ch in ans)
                    {
                        string s1 = ch.ToString();
                        var down = new Dictionary<string, object> { { "type", "keyDown" }, { "text", s1 }, { "unmodifiedText", s1 }, { "key", s1 } };
                        if (char.IsDigit(ch)) { down["code"] = "Digit" + s1; down["windowsVirtualKeyCode"] = (int)ch; }
                        await cdp("Input.dispatchKeyEvent", down);
                        var up = new Dictionary<string, object> { { "type", "keyUp" }, { "key", s1 } };
                        if (char.IsDigit(ch)) { up["code"] = "Digit" + s1; up["windowsVirtualKeyCode"] = (int)ch; }
                        await cdp("Input.dispatchKeyEvent", up);
                        await Task.Delay(90);
                    }
                    await Task.Delay(300);
                    // 3) 확인 버튼 실제 클릭 (없으면 Enter)
                    double bx = Convert.ToDouble(p0["bx"]), by = Convert.ToDouble(p0["by"]);
                    if (bx >= 0) await click(bx, by);
                    else
                    {
                        await cdp("Input.dispatchKeyEvent", new Dictionary<string, object> { { "type", "keyDown" }, { "key", "Enter" }, { "code", "Enter" }, { "windowsVirtualKeyCode", 13 }, { "text", "\r" } });
                        await cdp("Input.dispatchKeyEvent", new Dictionary<string, object> { { "type", "keyUp" }, { "key", "Enter" }, { "code", "Enter" }, { "windowsVirtualKeyCode", 13 } });
                    }
                    // 4) 풀렸는지 확인 (보안 확인 화면이 사라졌는지)
                    bool still = true; string q1 = "";
                    for (int t = 0; t < 8 && still; t++)
                    {
                        await Task.Delay(700);
                        var c = js.DeserializeObject(await wv.ExecuteScriptAsync("(() => { const t = document.body ? document.body.innerText : ''; return { c: t.slice(0, 600).indexOf('보안 확인') >= 0, q: (t.split('\\n').find(x => /\\?|입니까/.test(x)) || '').trim() }; })()")) as Dictionary<string, object>;
                        still = c == null || Convert.ToBoolean(c["c"]);
                        q1 = c == null ? "" : Convert.ToString(c["q"]);
                    }
                    return J(new { ok = true, passed = !still, message = still ? "아직 보안 확인 화면입니다 (답이 틀렸거나 새 문제가 나왔을 수 있어요). 새 문제를 확인하세요." : "통과했습니다. 이어서 진행합니다.", question = q1 });
                }
                case "/api/captcha_refresh":
                {
                    if (naverView == null || naverView.CoreWebView2 == null) return MobileResponse.Error(404, "작업 화면이 아직 없습니다");
                    // 문제가 안 보이면 다른 문제로 (새로고침·다른 문제 버튼), 없으면 화면 다시 열기
                    string r = await naverView.CoreWebView2.ExecuteScriptAsync("(() => { const vis = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);" +
                        " const b = [...document.querySelectorAll('button, a, [role=button]')].filter(vis).find(x => /새로고침|다른 문제|새로운 문제|refresh/i.test((x.innerText || x.getAttribute('aria-label') || x.title || '').trim()));" +
                        " if (b) { b.click(); return { ok: true, message: '다른 문제로 바꿨습니다' }; } location.reload(); return { ok: true, message: '화면을 다시 열었습니다' }; })()");
                    return MobileResponse.Json(r);
                }
                // ── 네이버 로그인 (휴대폰에서): 네이버 앱의 '일회용 로그인 번호'를 받아 작업 화면에 입력. 비밀번호는 다루지 않음 ──
                case "/api/naver_login":
                    return J(new { loggedIn = await NaverLoggedIn(), holding = SmallKeyword.PriceCompare.LoginHold });
                case "/api/naver_login_start":
                {
                    if (naverView == null || naverView.CoreWebView2 == null) return MobileResponse.Error(404, "작업 화면이 아직 없습니다");
                    if (await NaverLoggedIn()) { UpdateNaverLoginButton(true); return J(new { ok = true, loggedIn = true, message = "이미 네이버에 로그인돼 있습니다" }); }
                    SmallKeyword.PriceCompare.LoginHold = true;   // 초안 만들기는 네이버 조회 직전에 잠시 기다림
                    await Task.Delay(1500);
                    naverView.CoreWebView2.Navigate("https://nid.naver.com/nidlogin.login?mode=number&url=" + Uri.EscapeDataString("https://shopping.naver.com/"));
                    await Task.Delay(2500);
                    // 아이디·비밀번호 탭으로 열렸으면 '일회용 번호' 탭으로 바꿈
                    try
                    {
                        await naverView.CoreWebView2.ExecuteScriptAsync("(() => { const vis = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);" +
                            " const t = [...document.querySelectorAll('a, button, li, span, [role=tab]')].filter(vis).find(x => /^일회용\\s*번호$/.test((x.innerText || '').trim())); if (t) t.click(); })()");
                        await Task.Delay(800);
                    }
                    catch { }
                    return J(new { ok = true, loggedIn = false, message = "작업 화면에 네이버 일회용 번호 로그인 화면을 열었습니다" });
                }
                case "/api/naver_screen":
                {
                    if (naverView == null || naverView.CoreWebView2 == null) return MobileResponse.Error(404, "작업 화면이 아직 없습니다");
                    using (var ms = new MemoryStream())
                    {
                        await naverView.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, ms);
                        return new MobileResponse { ContentType = "image/png", Body = ms.ToArray() };
                    }
                }
                case "/api/naver_login_number":
                {
                    if (naverView == null || naverView.CoreWebView2 == null) return MobileResponse.Error(404, "작업 화면이 아직 없습니다");
                    string num = Regex.Replace(arg("number") ?? "", @"\D", "");
                    if (num.Length < 6) return MobileResponse.Error(400, "네이버 앱의 일회용 로그인 번호(숫자)를 넣어 주세요");
                    if (!SmallKeyword.PriceCompare.LoginHold || !(naverView.Source != null && naverView.Source.Host.Contains("nid.naver.com")))
                        return MobileResponse.Error(400, "먼저 [로그인 시작]을 눌러 주세요");
                    var wv = naverView.CoreWebView2;
                    var p0 = js.DeserializeObject(await wv.ExecuteScriptAsync("(() => { const vis = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);" +
                        " const inp = [...document.querySelectorAll('input')].filter(i => vis(i) && /^(text|tel|number|password|)$/i.test(i.getAttribute('type') || ''));" +
                        " if (!inp.length) return { ok: false };" +
                        " const i = inp[0]; i.scrollIntoView({ block: 'center' }); i.focus(); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(i, ''); i.dispatchEvent(new Event('input', { bubbles: true }));" +
                        " const ri = i.getBoundingClientRect();" +
                        " const b = [...document.querySelectorAll('button, input[type=submit], a, [role=button]')].filter(vis).find(x => /^로그인$/.test((x.innerText || x.value || '').trim()));" +
                        " const rb = b ? b.getBoundingClientRect() : null;" +
                        " return { ok: true, ix: ri.left + ri.width / 2, iy: ri.top + ri.height / 2, bx: rb ? rb.left + rb.width / 2 : -1, by: rb ? rb.top + rb.height / 2 : -1 }; })()")) as Dictionary<string, object>;
                    if (p0 == null || !Convert.ToBoolean(p0["ok"])) return J(new { ok = false, loggedIn = false, message = "작업 화면에 번호 넣는 칸이 없습니다. [로그인 시작]을 다시 눌러 주세요" });
                    await CdpClick(wv, Convert.ToDouble(p0["ix"]), Convert.ToDouble(p0["iy"]));
                    await Task.Delay(150);
                    await CdpType(wv, num);
                    await Task.Delay(300);
                    double bx = Convert.ToDouble(p0["bx"]), by = Convert.ToDouble(p0["by"]);
                    if (bx >= 0) await CdpClick(wv, bx, by); else await CdpEnter(wv);
                    bool ok = false;
                    for (int t = 0; t < 12 && !ok; t++) { await Task.Delay(800); ok = await NaverLoggedIn(); }
                    if (ok)
                    {
                        SmallKeyword.PriceCompare.LoginHold = false;
                        UpdateNaverLoginButton(true);
                        wv.Navigate("https://shopping.naver.com/");
                        return J(new { ok = true, loggedIn = true, message = "네이버에 로그인했습니다. 초안 만들기를 이어서 합니다." });
                    }
                    return J(new { ok = true, loggedIn = false, message = "아직 로그인되지 않았습니다. 번호가 만료됐거나 틀렸을 수 있어요. 화면을 확인하고 새 번호로 다시 해 주세요." });
                }
                case "/api/naver_login_cancel":
                {
                    SmallKeyword.PriceCompare.LoginHold = false;
                    if (naverView != null && naverView.CoreWebView2 != null && naverView.Source != null && naverView.Source.Host.Contains("nid.naver.com"))
                        naverView.CoreWebView2.Navigate("https://shopping.naver.com/");
                    return J(new { ok = true, message = "로그인을 그만두고 초안 만들기를 이어서 합니다" });
                }
                case "/api/add":
                    codesBox.Text = arg("codes"); AddCodes();
                    return J(new { message = statusLabel.Text });
                case "/api/register":
                {
                    var j = find(arg("code")); if (j == null) return MobileResponse.Error(404, "상품이 목록에 없습니다");
                    bool failedOnly = (arg("failedOnly") ?? "").ToLowerInvariant() == "true";
                    var errs = RegisterProblems(j);
                    if (errs.Count > 0 && !failedOnly) return MobileResponse.Error(400, string.Join(" / ", errs));
                    await RegisterCore(j, failedOnly);
                    return J(new { message = "[" + j.Code + "] " + j.Status, results = j.Results });
                }
                case "/api/save_names":
                {
                    var j = find(arg("code")); if (j == null) return MobileResponse.Error(404, "상품이 목록에 없습니다");
                    foreach (var key in NameKeys)
                    {
                        var val = arg("name_" + key);
                        if (!string.IsNullOrEmpty(val)) j.Names[key] = Regex.Replace(val.Trim(), @"\s+", " ");
                    }
                    Store.SaveJob(j);
                    RefreshList();
                    if (current == j) ShowJob(j);
                    return J(new { message = "상품명을 저장했습니다." });
                }
                case "/api/order":
                    await OrderRun(false);
                    { var r = OrderSync.LoadRecord(); return J(new { message = "주문 확인을 마쳤습니다 (발주 " + r.Done.Count + "건 · 확인 필요 " + r.Pending.Count + "건)" }); }
                case "/api/track":
                    await TrackRun(); return J(new { message = "송장 확인을 마쳤습니다" });
                case "/api/stockplan":
                {
                    var plan = await Task.Run(() => StockSync.Plan(set, m => { }));
                    return J(plan.Select(c => new { market = c.Market, account = c.Account, code = c.Code, action = c.Action, name = c.Name }).ToList());
                }
                case "/api/stockapply":
                    await StockRun(true); return J(new { message = "품절 연동을 반영했습니다" });
                case "/api/autoorder":
                    set.AutoOrder = arg("on").ToLowerInvariant() == "true"; Store.Save(Store.SettingsPath, set);
                    StockLog(set.AutoOrder ? "자동 발주를 켰습니다 (휴대폰)." : "자동 발주를 껐습니다 (휴대폰).");
                    return J(new { message = "자동 발주 " + (set.AutoOrder ? "켜짐" : "꺼짐") });
                case "/api/orders":
                {
                    var rec = OrderSync.LoadRecord();
                    return J(new { done = rec.Done.Values.OrderByDescending(d => d.OrderedAt).Take(60).Select(d => new { market = d.Market, account = d.Account, code = d.Code, qty = d.Qty, orderedAt = d.OrderedAt, tracking = d.Tracking, courier = d.Courier }).ToList(),
                                   pending = rec.Pending.Keys.ToList(), lastShortage = rec.LastShortage });
                }
                case "/api/sales":
                {
                    var probs = new List<string>();
                    var rows = await Task.Run(() => SalesStats.Load(set, 30, m => { }, probs));
                    return J(new { count = rows.Sum(r => r.Count), amount = rows.Sum(r => r.Amount), rows = rows.Take(50).Select(r => new { market = r.Market, code = r.Code, name = r.Name, count = r.Count, amount = r.Amount }).ToList(), problems = probs });
                }
                case "/api/continue":
                    if (paused) { paused = false; continueButton.Visible = false; Pump(); return J(new { message = "다시 시작했습니다" }); }
                    return J(new { message = "멈춘 작업이 없습니다" });
            }
            return MobileResponse.Error(404, "없는 기능");
        }

        // 등록 전 확인 (검토 화면과 같은 기준)
        List<string> RegisterProblems(Job job)
        {
            var errors = new List<string>();
            if (job.Status == "대기" || job.Status == "진행 중") errors.Add("아직 만드는 중입니다");
            if (job.Status == "등록 완료") errors.Add("이미 등록했습니다");
            foreach (var kv in job.Names)
            {
                if (kv.Value.Length == 0) errors.Add(Naming.NameLabel(kv.Key) + " 상품명이 비었습니다");
                if (kv.Value.Length > Naming.MaxLen(kv.Key)) errors.Add(Naming.NameLabel(kv.Key) + " 상품명이 " + Naming.MaxLen(kv.Key) + "자를 넘습니다");
            }
            if (job.Names.Count == 0) errors.Add("상품명이 없습니다");
            for (int n = 1; n <= 6; n++) if (job.Floor > 0 && job.Prices[n] < job.Floor * n) errors.Add(n + "개 가격이 하한선(" + Won(job.Floor * n) + ")보다 낮습니다");
            if (job.NeedPrice || job.Prices[1] <= 0) errors.Add("도매가·가격이 없습니다 (msgood 로그인 후 가격 다시 계산)");
            if (!File.Exists(Pipeline.ImagePath(job, "rep", 1))) errors.Add("대표이미지가 없습니다");
            return errors;
        }

        async Task RegisterCore(Job job, bool failedOnly = false)
        {
            if (current == job) ReadBack(job);
            if (!failedOnly) job.Results.Clear();
            else job.Results.RemoveAll(r => r.StartsWith("✖"));
            try
            {
                await Task.Run(() => Accounts.Register(job, set, m => Status("[" + job.Code + "] " + m), failedOnly));
                bool ok = job.Results.Any(r => r.StartsWith("✔"));
                if (ok)
                {
                    Accounts.Learn(job, learned);
                    Store.Save(Store.LearnPath, learned);
                    job.Status = job.Results.Any(r => r.StartsWith("✖")) ? "일부 실패" : "등록 완료";
                    job.Registered = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                }
            }
            catch (Exception ex) { job.Results.Add("✖ " + ex.Message); }
            finally { Store.SaveJob(job); RefreshList(); if (current == job) ShowJob(job); }
        }

        // ── 자동 발주
        async Task OrderRun(bool auto)
        {
            if (orderBusy) return;
            orderBusy = true;
            try
            {
                var problems = new List<string>();
                StockLog("스토어 결제완료 주문 확인 중...");
                var orders = await Task.Run(() => OrderSync.FetchNew(set, StockLog, problems));
                foreach (var p in problems) StockLog("※ " + p);
                foreach (var o in orders.Where(o => o.Problem != null)) StockLog("※ 발주 못 함: " + o);
                var good = orders.Where(o => o.Problem == null).Take(100).ToList();
                if (good.Count == 0) { StockLog("새로 발주할 주문이 없습니다."); return; }
                foreach (var o in good) StockLog("  · " + o);

                // 사업자(계정 1·2·3)별로 분리 발주
                var byAccount = good.GroupBy(o => string.IsNullOrEmpty(o.Account) ? "1" : o.Account).ToList();
                foreach (var grp in byAccount)
                {
                    string accId = grp.Key;
                    var accOrders = grp.ToList();
                    var cred = MsgoodCred.Load(accId);
                    if (cred == null || !cred.HasSeller)
                    {
                        StockLog("※ [계정 " + accId + "] msgood 로그인 필요 (발주 건너뜀 - 대기 주문 " + accOrders.Count + "건)");
                        Phone("⚠ [계정 " + accId + "] msgood 로그인 필요: 대기 주문 " + accOrders.Count + "건");
                        Notify("계정 " + accId + " msgood 로그인 필요", "계정 #" + accId + "의 msgood4u 셀러 로그인 정보가 없습니다.\n[설정] 탭의 계정 #" + accId + " 줄에서 [msgood 로그인]을 완료해 주세요.\n\n발주 대기 주문: " + accOrders.Count + "건");
                        continue;
                    }

                    var rec = OrderSync.LoadRecord();
                    foreach (var o in accOrders) rec.Pending[o.Key] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    OrderSync.SaveRecord(rec);

                    var res = await msgood.BulkOrder(OrderSync.BuildExcel(accOrders), submit: auto, StockLog, cred);
                    rec = OrderSync.LoadRecord();
                    if (res.Ordered)
                    {
                        foreach (var o in accOrders)
                        {
                            rec.Pending.Remove(o.Key);
                            rec.Done[o.Key] = new OrderDone { Market = o.Market, Account = o.Account, Key = o.Key, OrderId = o.OrderId, ProductOrderId = o.ProductOrderId, ShipmentBoxId = o.ShipmentBoxId,
                                VendorItemId = o.VendorItemId, Code = o.Code, Qty = o.Qty, Orderer = o.Orderer, Receiver = o.Receiver, OrderedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
                        }
                        OrderSync.SaveRecord(rec);
                        StockLog("✔ [계정 " + accId + "] " + res.Message + " (" + accOrders.Count + "건, " + res.Total.ToString("#,0") + "원)");
                        Phone("✔ [계정 " + accId + "] 자동 발주 " + accOrders.Count + "건, " + res.Total.ToString("#,0") + "원\n" + string.Join("\n", accOrders.Select(o => o.Market + " " + o.Code + " x" + o.Qty)));
                        await Task.Run(() => OrderSync.ConfirmInStores(accOrders, StockLog));
                    }
                    else if (res.Unknown)
                    {
                        StockLog("✖ [계정 " + accId + "] " + res.Message);
                        Phone("⚠ [계정 " + accId + "] 발주 결과 확인 필요: " + res.Message);
                        Notify("[계정 " + accId + "] 발주 결과 확인 필요", res.Message + "\n\n" + string.Join("\n", accOrders.Select(o => o.ToString()).Take(15)));
                    }
                    else
                    {
                        foreach (var o in accOrders) rec.Pending.Remove(o.Key);
                        if (res.Shortage > 0) rec.LastShortage = DateTime.Now.ToString("MM-dd HH:mm") + " [계정 " + accId + "] " + res.Message;
                        OrderSync.SaveRecord(rec);
                        StockLog("✖ [계정 " + accId + "] " + res.Message);
                        Phone((res.Shortage > 0 ? "⚠ [계정 " + accId + "] 적립금 부족: " : "✖ [계정 " + accId + "] 발주 못 함: ") + res.Message + "\n(대기 주문 " + accOrders.Count + "건, 다음 " + NextOrderSlot() + "에 다시 시도)");
                        Notify(res.Shortage > 0 ? "[계정 " + accId + "] 적립금 부족" : "[계정 " + accId + "] 발주하지 못했습니다", res.Message + "\n\n계정 #" + accId + " 발주 대기 주문 " + accOrders.Count + "건. 다음 " + NextOrderSlot() + "에 다시 시도합니다.");
                    }
                }
            }
            catch (Exception ex) { StockLog("자동 발주 오류: " + ex.Message); }
            finally { orderBusy = false; }
        }

        // ── 적립금 확인: 적립 10분 전, 지금 발주할 주문을 미리보기로 올려 적립금이 모자란지 봄 (발주는 하지 않음)
        async Task CreditCheck(string slot)
        {
            if (orderBusy) return;
            orderBusy = true;
            try
            {
                StockLog("── 적립금 확인 (" + slot + " 10분 전) ──");
                var problems = new List<string>();
                var orders = await Task.Run(() => OrderSync.FetchNew(set, StockLog, problems));
                var good = orders.Where(o => o.Problem == null).Take(100).ToList();
                if (good.Count == 0) { StockLog("발주할 주문이 없어 적립금 확인을 건너뜁니다."); return; }

                var byAccount = good.GroupBy(o => string.IsNullOrEmpty(o.Account) ? "1" : o.Account).ToList();
                foreach (var grp in byAccount)
                {
                    string accId = grp.Key;
                    var accOrders = grp.ToList();
                    var cred = MsgoodCred.Load(accId);
                    if (cred == null || !cred.HasSeller)
                    {
                        StockLog("※ [계정 " + accId + "] msgood 셀러 로그인 필요 (적립금 확인 건너뜀)");
                        Phone("⚠ [계정 " + accId + "] msgood 로그인 필요 (적립금 확인 건너뜀)");
                        Notify("계정 " + accId + " msgood 로그인 필요", "계정 #" + accId + "의 msgood4u 셀러 로그인 정보가 없습니다.");
                        continue;
                    }
                    var res = await msgood.BulkOrder(OrderSync.BuildExcel(accOrders), false, StockLog, cred);
                    if (res.Shortage > 0)
                    {
                        var rec = OrderSync.LoadRecord(); rec.LastShortage = DateTime.Now.ToString("MM-dd HH:mm") + " [계정 " + accId + "] " + res.Message; OrderSync.SaveRecord(rec);
                        StockLog("⚠ [계정 " + accId + "] " + res.Message);
                        Phone("⚠ [계정 " + accId + "] 적립금 부족 (" + slot + " 10분 전): " + res.Message + "\n대기 주문 " + accOrders.Count + "건. 적립 시간 전에 적립금을 넣어 주세요. 다음 발주 " + NextOrderSlot());
                        Notify("[계정 " + accId + "] 적립금 부족", res.Message + "\n\n" + slot + " 적립 전에 적립금을 넣어 주세요.\n계정 #" + accId + " 대기 주문 " + accOrders.Count + "건, 다음 발주 " + NextOrderSlot());
                    }
                    else StockLog("[계정 " + accId + "] 적립금 확인: " + res.Message);
                }
            }
            catch (Exception ex) { StockLog("적립금 확인 오류: " + ex.Message); }
            finally { orderBusy = false; }
        }

        // ── 송장: msgood 주문/배송조회의 송장번호 → 네이버 발송처리 / 쿠팡 송장 업로드
        async Task TrackRun()
        {
            try
            {
                var rec = OrderSync.LoadRecord();
                var todo = rec.Done.Values.Where(d => string.IsNullOrEmpty(d.Tracking)).ToList();
                if (todo.Count == 0) { StockLog("송장을 넣을 주문이 없습니다."); return; }

                var byAccount = todo.GroupBy(d => string.IsNullOrEmpty(d.Account) ? "1" : d.Account).ToList();
                int totalSent = 0;
                foreach (var grp in byAccount)
                {
                    string accId = grp.Key;
                    var accTodo = grp.ToList();
                    var cred = MsgoodCred.Load(accId);
                    if (cred == null || !cred.HasSeller)
                    {
                        StockLog("※ [계정 " + accId + "] msgood 셀러 로그인 필요 (송장 확인 건너뜀)");
                        continue;
                    }
                    StockLog("msgood4u [계정 " + accId + "] 송장 확인 중... (" + accTodo.Count + "건)");
                    var rows = await msgood.OrderList(20, StockLog, cred);
                    if (rows == null) { StockLog("※ [계정 " + accId + "] msgood4u 주문/배송조회를 읽지 못했습니다."); continue; }
                    var used = new HashSet<Dictionary<string, object>>();
                    int sent = 0;
                    foreach (var d in accTodo)
                    {
                        string who = string.IsNullOrEmpty(d.Orderer) || d.Orderer == d.Receiver ? d.Receiver : d.Orderer + "(" + d.Receiver + ")";
                        Func<Dictionary<string, object>, string, string> cell = (r, k) => r.ContainsKey(k) ? Convert.ToString(r[k]) : "";
                        var row = rows.FirstOrDefault(r => !used.Contains(r) && Convert.ToString(r["_html"]).Contains(d.OrderId))
                               ?? rows.FirstOrDefault(r => !used.Contains(r) && (cell(r, "주문자명").Contains(who) || cell(r, "주문자명").Contains(d.Receiver ?? "\u0000")) && Regex.Replace(cell(r, "수량"), "[^0-9]", "") == d.Qty.ToString());
                        if (row == null) continue;
                        string tr = cell(row, "송장번호");
                        var num = Regex.Match(tr.Replace("-", ""), "[0-9]{8,}");
                        if (!num.Success) continue;
                        used.Add(row);
                        try
                        {
                            string err = await Task.Run(() => OrderSync.SendTracking(d, tr, set.DefaultCourier, num.Value));
                            if (err != null) { StockLog("※ 송장 못 넣음 " + d.Market + " " + d.OrderId + ": " + err); continue; }
                            sent++;
                            totalSent++;
                            StockLog("✔ 송장 입력 " + d.Market + " #" + d.Account + " " + d.OrderId + " → " + d.Courier + " " + d.Tracking);
                        }
                        catch (Exception ex) { StockLog("✖ 송장 입력 오류 " + d.Market + " " + d.OrderId + ": " + ex.Message); }
                    }
                }
                OrderSync.SaveRecord(rec);
                StockLog("송장 총 " + totalSent + "건 입력했습니다.");
                if (totalSent > 0) Phone("✔ 송장 " + totalSent + "건 입력했습니다.");
            }
            catch (Exception ex) { StockLog("송장 확인 오류: " + ex.Message); }
        }

        void ShowMsgoodLoginDialog(string accId)
        {
            var cr = MsgoodCred.Load(accId) ?? new MsgoodCred();
            var dlg = new Form { Text = "계정 #" + accId + " msgood4u 로그인 설정", Width = 520, Height = 340, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };

            var mid = new TextBox { Width = 140, Text = cr.Id ?? "" };
            var mpw = new TextBox { Width = 140, UseSystemPasswordChar = true };
            var scode = new TextBox { Width = 100, Text = cr.SellerCode ?? "" };
            var sid = new TextBox { Width = 120, Text = cr.SellerId ?? "" };
            var spw = new TextBox { Width = 120, UseSystemPasswordChar = true };

            p.Controls.Add(new Label { Text = "계정 #" + accId + " msgood4u 회원 로그인 (도매가 확인용)", AutoSize = true, Font = Fnt(9.5f, FontStyle.Bold) });
            p.Controls.Add(Row(Lbl("아이디", 70), mid, Lbl("비밀번호", 60), mpw, Note(cr.HasMember ? "(저장됨 · 변경 시만 입력)" : "")));

            p.Controls.Add(new Label { Text = "계정 #" + accId + " msgood4u 셀러 로그인 (자동발주·송장 확인용)", AutoSize = true, Font = Fnt(9.5f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 0) });
            p.Controls.Add(Row(Lbl("판매자코드", 70), scode, Lbl("아이디", 50), sid, Lbl("비밀번호", 55), spw, Note(cr.HasSeller ? "(저장됨 · 변경 시만 입력)" : "")));

            p.Controls.Add(Note("비밀번호는 이 PC의 Windows 사용자 전용 암호화(DPAPI)로 저장됩니다."));

            var btnRow = Row(
                Btn("저장", () =>
                {
                    var c2 = MsgoodCred.Load(accId) ?? new MsgoodCred();
                    c2.Id = mid.Text.Trim(); if (mpw.Text.Length > 0) c2.Pw = mpw.Text;
                    c2.SellerCode = scode.Text.Trim(); c2.SellerId = sid.Text.Trim(); if (spw.Text.Length > 0) c2.SellerPw = spw.Text;
                    c2.Save(accId);
                    BuildSettingsTab();
                    Status("계정 #" + accId + " msgood4u 로그인 정보를 저장했습니다.");
                    dlg.Close();
                }),
                Btn("자동 로그인 테스트", async () =>
                {
                    var c2 = MsgoodCred.Load(accId) ?? new MsgoodCred();
                    c2.Id = mid.Text.Trim(); if (mpw.Text.Length > 0) c2.Pw = mpw.Text;
                    c2.SellerCode = scode.Text.Trim(); c2.SellerId = sid.Text.Trim(); if (spw.Text.Length > 0) c2.SellerPw = spw.Text;
                    tabs.SelectedTab = msgoodTab;
                    if (await msgood.AutoLogin(Status, c2))
                    {
                        if (c2.HasSeller) await msgood.SellerLogin(Status, c2);
                        Status("계정 #" + accId + " msgood4u 로그인 테스트 성공!");
                    }
                }),
                Btn("저장 정보 삭제", () =>
                {
                    MsgoodCred.Delete(accId);
                    BuildSettingsTab();
                    Status("계정 #" + accId + " msgood4u 로그인 정보를 삭제했습니다.");
                    dlg.Close();
                }),
                Btn("닫기", () => dlg.Close())
            );
            p.Controls.Add(btnRow);
            p.Controls.Add(Note("파일: " + MsgoodCred.FilePathFor(accId)));
            dlg.Controls.Add(p);
            dlg.ShowDialog(this);
        }

        void Notify(string title, string msg)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Notify(title, msg))); return; }
            var f = new Form { Text = "판매도구 - " + title, Width = 520, Height = 300, StartPosition = FormStartPosition.CenterScreen, TopMost = true };
            f.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = msg.Replace("\n", "\r\n"), Font = Fnt(10f, FontStyle.Regular) });
            f.Show(this);
        }

        void BuildStockTab()
        {
            ClearDispose(stockTab);
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10) };
            stockLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f) };
            var auto = new CheckBox { Text = "6시간마다 자동으로 확인하고 바로 반영 (프로그램이 켜져 있을 때)", Checked = set.AutoStock, AutoSize = true };
            auto.CheckedChanged += (s, e) => { set.AutoStock = auto.Checked; Store.Save(Store.SettingsPath, set); };
            var grid = new DataGridView { Dock = DockStyle.Bottom, Height = 260, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            grid.Columns.Add("m", "마켓"); grid.Columns.Add("c", "상품번호"); grid.Columns.Add("n", "상품명"); grid.Columns.Add("q", "판매 개수"); grid.Columns.Add("a", "금액");
            grid.Columns[2].FillWeight = 300;
            top.Controls.Add(Section("품절 연동 (msgood4u 품절·단종 → 스토어 품절 / 다시 들어오면 판매 재개)"));
            top.Controls.Add(Row(Btn("지금 품절 확인", () => StockRun(false)), auto));
            top.Controls.Add(Note("상품번호로 찾습니다: 네이버 판매자관리코드(22021, 22021x2), 쿠팡 등록상품명(22021). 판매 재개는 이 프로그램이 품절 처리한 상품만 합니다."));
            top.Controls.Add(Section("자동 발주·송장 (스토어 결제완료 주문 → msgood4u 상품대량발주(적립금) → 스토어 발주확인 / 송장 입력)"));
            var autoOrder = new CheckBox { Text = "자동 발주 켜기 (" + OrderScheduleText + ")", Checked = set.AutoOrder, AutoSize = true };
            autoOrder.CheckedChanged += (s, e) => { set.AutoOrder = autoOrder.Checked; Store.Save(Store.SettingsPath, set); StockLog(set.AutoOrder ? "자동 발주를 켰습니다." : "자동 발주를 껐습니다."); };
            var courier = new TextBox { Width = 110, Text = set.DefaultCourier };
            courier.Leave += (s, e) => { set.DefaultCourier = courier.Text.Trim(); Store.Save(Store.SettingsPath, set); };
            top.Controls.Add(Row(autoOrder, Btn("지금 주문 확인·발주", () => OrderRun(false)), Btn("지금 송장 입력", () => TrackRun()), Lbl("기본 택배사"), courier,
                Btn("발주 시험 (실제 발주 안 함)", async () =>
                {
                    // 가짜 주문 1줄(22266 1개)을 올려 금액·적립금 계산만 보고 지움
                    var test = new List<StoreOrder> { new StoreOrder { Market = "시험", Account = "1", OrderId = "TEST0001", Code = "22266", Qty = 1, Receiver = "시험", Zip = "13494",
                        Addr = "경기 성남시 분당구 판교역로 235 (삼평동, 에이치스퀘어 엔동)", Tel1 = "010-0000-0000", Memo = "시험" } };
                    var res = await msgood.BulkOrder(OrderSync.BuildExcel(test), false, StockLog);
                    StockLog("발주 시험 결과: " + res.Message);
                })));
            top.Controls.Add(Section("쿠팡 광고 키워드 (반자동: 목록은 자동으로 만들고, 광고센터 등록·입찰은 직접)"));
            var autoAd = new CheckBox { Text = "새 광고 보고서가 들어오면 자동으로 목록 만들기", Checked = set.AutoAdKw, AutoSize = true };
            autoAd.CheckedChanged += (s, e) => { set.AutoAdKw = autoAd.Checked; Store.Save(Store.SettingsPath, set); };
            top.Controls.Add(Row(Btn("지금 광고 키워드 목록 만들기", async () => { await Task.Run(() => AdKeywords.Run(set, StockLog)); }),
                                 Btn("광고보고서 폴더 열기", () => { Directory.CreateDirectory(AdKeywords.ReportDir); Process.Start("explorer.exe", "\"" + AdKeywords.ReportDir + "\""); }),
                                 Btn("마지막 목록 열기", () => { var f = AdKeywords.LastState().LastFile; if (!string.IsNullOrEmpty(f) && File.Exists(f)) Process.Start(f); else StockLog("아직 만든 목록이 없습니다."); }), autoAd));
            var adSt = AdKeywords.LastState();
            top.Controls.Add(Note("쿠팡 광고센터 → 광고 보고서에서 한 달씩 엑셀을 받아 Downloads(이름에 '광고'·'report'·'보고서'가 든 파일)나 광고보고서 폴더에 두면, 상품별 넣을 키워드(광고로 팔린 말·검색어 사전)·제외 키워드(클릭만 받고 안 팔린 말)·광고 끄기 추천을 Downloads\\쿠팡광고키워드_날짜.csv 로 만듭니다." +
                                  (adSt.LastRun != "" ? "\n마지막: " + adSt.LastRun + " → " + Path.GetFileName(adSt.LastFile) : "")));
            var rec0 = OrderSync.LoadRecord();
            top.Controls.Add(Note("발주한 주문 " + rec0.Done.Count + "건 · 송장 입력 " + rec0.Done.Values.Count(d => !string.IsNullOrEmpty(d.Tracking)) + "건 · 결과 확인 필요 " + rec0.Pending.Count + "건" +
                                  (rec0.LastShortage != null ? "   |   마지막 적립금 부족: " + rec0.LastShortage : "") +
                                  "\n상품번호: 네이버 판매자관리코드(22021x2 = 22021 2개), 쿠팡 등록상품명 + 옵션(60정 3개 = 3개). 적립금이 모자라면 얼마 모자란지 알려 주고 발주하지 않습니다. [설정]에 msgood 셀러로그인 정보가 있어야 합니다."));
            top.Controls.Add(Section("판매 실적 (최근 30일 주문 → 팔린 상품명의 단어를 다음 상품명에 먼저 씀)"));
            top.Controls.Add(Row(Btn("판매 실적 불러오기", async () =>
            {
                var problems = new List<string>();
                var rows = await Task.Run(() => SalesStats.Load(set, 30, StockLog, problems));
                grid.Rows.Clear();
                foreach (var r in rows) grid.Rows.Add(r.Market, r.Code ?? "-", r.Name, r.Count, r.Amount.ToString("#,0"));
                int n = SalesStats.Learn(rows, learned);
                Store.Save(Store.LearnPath, learned);
                foreach (var p in problems) StockLog("※ " + p);
                StockLog("판매 실적 " + rows.Count + "개 상품, " + rows.Sum(r => r.Count) + "개 팔림 → 상품명 단어 " + learned.SoldWords.Count + "개를 학습했습니다.");
            })));
            stockTab.Controls.Add(stockLog); stockTab.Controls.Add(grid); stockTab.Controls.Add(top);
        }

        // ── 학습 내역
        void BuildLearnTab()
        {
            ClearDispose(learnTab);
            var lb = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
            var items = new List<Tuple<string, Action>>();
            foreach (var kv in learned.Excluded.OrderBy(k => k.Key)) { var k = kv.Key; items.Add(Tuple.Create("뺄 단어: " + kv.Key + " (" + kv.Value + "회)", (Action)(() => learned.Excluded.Remove(k)))); }
            foreach (var kv in learned.Brands.OrderBy(k => k.Key)) { var k = kv.Key; items.Add(Tuple.Create("브랜드: " + kv.Key + " (상품명 X, 검색량 많으면 쿠팡 검색어)", (Action)(() => learned.Brands.Remove(k)))); }
            foreach (var kv in learned.TagOnly.OrderBy(k => k.Key)) { var k = kv.Key; items.Add(Tuple.Create("쿠팡 검색어로만: " + kv.Key + " (" + kv.Value + "회)", (Action)(() => learned.TagOnly.Remove(k)))); }
            foreach (var kv in learned.Preferred.OrderBy(k => k.Key)) { var k = kv.Key; items.Add(Tuple.Create("우선 단어: " + kv.Key + " (" + kv.Value + "회)", (Action)(() => learned.Preferred.Remove(k)))); }
            foreach (var kv in learned.SeedFor) { var k = kv.Key; items.Add(Tuple.Create("시작 단어: 상품명에 '" + kv.Key + "' → " + kv.Value, (Action)(() => learned.SeedFor.Remove(k)))); }
            foreach (var kv in learned.NaverCatFor) { var k = kv.Key; items.Add(Tuple.Create("네이버 카테고리: " + kv.Key + " → " + kv.Value, (Action)(() => learned.NaverCatFor.Remove(k)))); }
            foreach (var kv in learned.CoupangCatFor) { var k = kv.Key; items.Add(Tuple.Create("쿠팡 카테고리: " + kv.Key + " → " + kv.Value, (Action)(() => learned.CoupangCatFor.Remove(k)))); }
            foreach (var t in items) lb.Items.Add(t.Item1);
            var hist = new TextBox { Dock = DockStyle.Bottom, Height = 180, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Text = "고친 기록 (최근 순)\r\n" + string.Join("\r\n", Enumerable.Reverse(learned.History)) };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4) };
            bar.Controls.Add(Btn("고른 규칙 지우기", () =>
            {
                foreach (int i in lb.SelectedIndices.Cast<int>().ToList()) items[i].Item2();
                Store.Save(Store.LearnPath, learned); BuildLearnTab();
            }));
            bar.Controls.Add(Note("검토 화면에서 상품명 단어를 빼거나 넣고, 시작 단어·카테고리를 바꾸면 등록할 때 여기에 쌓여 다음 상품부터 반영됩니다."));
            learnTab.Controls.Add(lb); learnTab.Controls.Add(hist); learnTab.Controls.Add(bar);
        }

        // ── 작은 도우미
        static List<string> Tags(string text)
        {
            return Regex.Split(text ?? "", @"[,\n]+").Select(t => t.Trim()).Where(t => t.Length > 0 && t.Length <= 20).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        }

        static string Won(long v) { return v.ToString("#,0") + "원"; }
        static Label Lbl(string t, int w = 0) { var l = new Label { Text = t, Margin = new Padding(3, 6, 4, 0) }; if (w > 0) l.Width = w; else l.AutoSize = true; return l; }
        static Label Note(string t, Color? c = null) { return new Label { Text = t, AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = c ?? Color.DimGray, Margin = new Padding(3, 6, 4, 2) }; }
        Label Section(string t) { return new Label { Text = t, AutoSize = true, Font = BoldFont, ForeColor = Color.FromArgb(30, 60, 120), Margin = new Padding(3, 14, 3, 4) }; }
        static FlowLayoutPanel Row(params Control[] cs) { var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 1, 0, 1) }; p.Controls.AddRange(cs); return p; }
        Button Btn(string t, Action a) { var b = new Button { Text = t, AutoSize = true, Height = 28, Margin = new Padding(2, 1, 4, 0) }; b.Click += (s, e) => a(); return b; }
        Button Btn(string t, Func<Task> a) { var b = new Button { Text = t, AutoSize = true, Height = 28, Margin = new Padding(2, 1, 4, 0) }; b.Click += async (s, e) => await a(); return b; }

        // 화면을 다시 그릴 때 이전 컨트롤·이미지를 실제로 없앰 (Controls.Clear만 하면 창 핸들이 쌓여 프로그램이 꺼짐)
        static void ClearDispose(Control parent)
        {
            var old = new List<Control>(); foreach (Control c in parent.Controls) old.Add(c);
            parent.Controls.Clear();
            // 버튼 클릭 중에 화면을 다시 그리는 경우가 있어, 지금 이벤트가 끝난 뒤에 없앰
            Action kill = () => { foreach (var c in old) { try { DisposeTree(c); } catch { } } };
            if (parent.IsHandleCreated && !parent.IsDisposed) parent.BeginInvoke(kill); else kill();
        }
        static void DisposeTree(Control c)
        {
            var kids = new List<Control>(); foreach (Control k in c.Controls) kids.Add(k);
            foreach (var k in kids) DisposeTree(k);
            var pb = c as PictureBox;
            if (pb != null && pb.Image != null) { var im = pb.Image; pb.Image = null; im.Dispose(); }
            c.Dispose();
        }
        // 굵은 글꼴은 한 번만 만들어 같이 씀 (클릭할 때마다 새로 만들면 GDI 핸들이 쌓임)
        Font boldFont; Font BoldFont { get { return boldFont ?? (boldFont = new Font(Font, FontStyle.Bold)); } }
        readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();
        Font Fnt(float size, FontStyle st)
        {
            string k = size + "|" + st; Font f;
            if (!fontCache.TryGetValue(k, out f)) { f = new Font(Font.FontFamily, size, st); fontCache[k] = f; }
            return f;
        }

        static PictureBox Thumb(string file)
        {
            var pb = new PictureBox { Width = 140, Height = 140, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(3), Cursor = Cursors.Hand, BackColor = Color.WhiteSmoke };
            if (File.Exists(file))
            {
                pb.Click += (s, e) => Process.Start(file);
                var load = Task.Run(() =>
                {
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var im = Image.FromStream(fs, false, false)) return new Bitmap(im, 140, 140);
                });
                // 화면에 붙은 뒤(창 핸들이 생긴 뒤)에 다 읽힌 사진을 넣음
                Action hook = () => load.ContinueWith(t =>
                {
                    if (t.Status != TaskStatus.RanToCompletion) return;
                    var bmp = t.Result;
                    try { pb.BeginInvoke(new Action(() => { if (pb.IsDisposed) bmp.Dispose(); else pb.Image = bmp; })); } catch { bmp.Dispose(); }
                });
                if (pb.IsHandleCreated) hook(); else pb.HandleCreated += (s2, e2) => hook();
            }
            return pb;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (current != null && current.Keywords.Count > 0 && current.Status != "대기" && current.Status != "진행 중") ReadBack(current);
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 화면 오류가 나도 프로그램이 꺼지지 않게: 오류창 대신 error.log에 기록 (창을 만들 수 없는 상황에서도 안전)
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + e.Exception + Environment.NewLine + Environment.NewLine); } catch { }
            };
            Application.Run(new MainForm());
        }
    }
}
