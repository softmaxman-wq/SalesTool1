using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SalesTool
{
    // msgood4u 상품 페이지를 프로그램 안 브라우저(로그인 상태 유지)로 열어 도매가·배송비까지 읽음
    public class MsgoodWeb
    {
        public const string Home = "https://www.msgood4u.com/";
        readonly WebView2 view;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public MsgoodWeb(WebView2 view) { this.view = view; }

        // msgood 화면을 한 번에 한 일만 하게 (상품 읽기·발주·송장 읽기가 겹치지 않게)
        public readonly System.Threading.SemaphoreSlim Gate = new System.Threading.SemaphoreSlim(1, 1);

        // 알림창(alert/confirm): 발주 중에는 프로그램이 받아서 기록하고 '확인'을 누름. 평소에는 알림은 바로 닫고, 확인/취소 창만 사용자에게 보여 줌
        public readonly List<string> Dialogs = new List<string>();
        bool armed;
        public void HookDialogs(System.Windows.Forms.Control owner)
        {
            if (view.CoreWebView2 == null) return;
            view.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            view.CoreWebView2.ScriptDialogOpening += (s, e) =>
            {
                lock (Dialogs) Dialogs.Add(e.Message ?? "");
                if (armed) { e.Accept(); return; }
                // 확인 버튼만 있는 알림("○○님 반갑습니다" 등)은 바로 닫음: PC 앞에 사람이 없으면 msgood 화면이 멈춰 상품 읽기가 끝없이 기다림
                if (e.Kind == CoreWebView2ScriptDialogKind.Alert) { e.Accept(); return; }
                var defer = e.GetDeferral();
                owner.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (e.Kind == CoreWebView2ScriptDialogKind.Confirm)
                        { if (System.Windows.Forms.MessageBox.Show(owner, e.Message, "msgood4u", System.Windows.Forms.MessageBoxButtons.OKCancel) == System.Windows.Forms.DialogResult.OK) e.Accept(); }
                        else { System.Windows.Forms.MessageBox.Show(owner, e.Message, "msgood4u"); e.Accept(); }
                    }
                    finally { defer.Complete(); }
                }));
            };
        }

        public class BulkResult
        {
            public bool Ordered, Uploaded, Unknown;
            public long Total = -1, Balance = -1, Shortage;
            public int Rows, NoSell;
            public string Message;
        }

        string Url { get { return view.Source != null ? view.Source.ToString() : ""; } }

        // 셀러 관리 화면 열기 (로그인이 풀려 있으면 셀러로그인)
        async Task<bool> OpenSeller(string path, Action<string> log)
        {
            await Navigate(Home + path);
            await Task.Delay(500);
            if (Url.Contains("seller_login") || Url.Contains("login"))
            {
                if (!await SellerLogin(log)) return false;
                await Navigate(Home + path);
                await Task.Delay(500);
            }
            return !Url.Contains("seller_login");
        }

        // 상품대량발주: 엑셀(HTML 표)을 올려 금액·적립금을 보고, submit 이면 적립금이 충분할 때 발주까지
        public async Task<BulkResult> BulkOrder(string excelHtml, bool submit, Action<string> log)
        {
            var r = new BulkResult();
            await Gate.WaitAsync();
            armed = true;
            try
            {
                if (!await OpenSeller("_admin/html/_dmorder/dmorder_excel_regForm.php", log)) { r.Message = "msgood4u 셀러로그인을 하지 못했습니다 ([설정]에서 셀러 정보를 저장하세요)."; return r; }
                lock (Dialogs) Dialogs.Clear();
                log("msgood4u 대량발주 엑셀 올리는 중...");
                await view.CoreWebView2.ExecuteScriptAsync("(() => { const f = new File([" + json.Serialize(excelHtml) + "], 'order.xls', {type: 'application/vnd.ms-excel'}); const dt = new DataTransfer(); dt.items.add(f);" +
                    " document.querySelector('input[name=excelFile]').files = dt.files; document.getElementById('_attfileName_excel_').value = 'order.xls'; excelUploadCheckForm(document.excelForm); })()");
                Dictionary<string, object> st = null;
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(500);
                    st = await Eval("(() => { const L = document.getElementById('excel_result_list'); const n = s => parseInt(String(s || '').replace(/[^0-9-]/g, '')) ; return {" +
                        " rows: L ? L.querySelectorAll('.activeDelBtn').length : 0, pay: n($('#dmPayPrice').html()), remain: n($('#remainPoint').html()), isPoint: n($('#isPoint').val())," +
                        " nosell: n($('#nosellExcelCount').val()), btn: $('.dmOrderBtn').css('display'), msg: ($('.dmOrderMsg').text() || '').trim(), done: !!(L && (L.innerText.indexOf('결제후') >= 0 || L.querySelectorAll('.activeDelBtn').length > 0)) }; })()");
                    lock (Dialogs) if (Dialogs.Count > 0) break;
                    if (st != null && Convert.ToBoolean(st["done"])) break;
                }
                string dlg; lock (Dialogs) dlg = string.Join(" / ", Dialogs);
                var mt = Regex.Match(dlg, @"발주\s*총액\s*:\s*([\d,]+)"); var mb = Regex.Match(dlg, @"적립금\s*잔액\s*:\s*([\d,]+)");
                if (dlg.Contains("적립금이 부족") || (st != null && Num(st, "remain") < 0))
                {
                    r.Total = mt.Success ? long.Parse(mt.Groups[1].Value.Replace(",", "")) : Num(st, "pay");
                    r.Balance = mb.Success ? long.Parse(mb.Groups[1].Value.Replace(",", "")) : Num(st, "isPoint");
                    r.Shortage = Math.Max(0, r.Total - r.Balance);
                    r.Message = "적립금이 " + r.Shortage.ToString("#,0") + "원 모자랍니다 (발주 총액 " + r.Total.ToString("#,0") + "원 / 적립금 잔액 " + r.Balance.ToString("#,0") + "원)";
                    return r;
                }
                if (st == null) { r.Message = "대량발주 화면을 읽지 못했습니다. " + dlg; return r; }
                r.Rows = (int)Num(st, "rows"); r.NoSell = (int)Math.Max(0, Num(st, "nosell")); r.Total = Num(st, "pay"); r.Balance = Num(st, "isPoint");
                r.Uploaded = r.Rows > 0;
                if (!r.Uploaded) { r.Message = "msgood4u 가 발주 목록을 만들지 못했습니다. " + dlg + " " + Convert.ToString(st["msg"]); return r; }
                if (Convert.ToString(st["btn"]) == "none") { r.Message = "발주할 수 없는 줄이 있습니다 (품절·상품번호 오류 등): " + Convert.ToString(st["msg"]); await ClearUploaded(); return r; }
                if (!submit) { r.Message = "미리보기: " + r.Rows + "줄, 발주 총액 " + r.Total.ToString("#,0") + "원, 적립금 " + r.Balance.ToString("#,0") + "원"; await ClearUploaded(); return r; }

                log("msgood4u 발주하는 중... (" + r.Rows + "줄, " + r.Total.ToString("#,0") + "원)");
                lock (Dialogs) Dialogs.Clear();
                await view.CoreWebView2.ExecuteScriptAsync("excelRegFormCheck(document.excelRegForm)");
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(500);
                    lock (Dialogs) if (Dialogs.Count > 1 || (Dialogs.Count == 1 && !Dialogs[0].Contains("발주하시겠습니까"))) break;
                }
                await Task.Delay(1500);
                lock (Dialogs) dlg = string.Join(" / ", Dialogs.Where(d => !d.Contains("발주하시겠습니까")));
                if (Regex.IsMatch(dlg, "부족|실패|오류|불가|없습니다")) { r.Message = "msgood4u 발주 실패: " + dlg; return r; }
                if (Regex.IsMatch(dlg, "완료|되었습니다|성공|접수")) { r.Ordered = true; r.Message = "발주했습니다: " + dlg; return r; }
                r.Unknown = true; r.Message = "발주 결과 메시지를 받지 못했습니다 (" + dlg + "). msgood4u 주문/배송조회에서 확인해 주세요.";
                return r;
            }
            catch (Exception ex) { r.Unknown = true; r.Message = "발주 중 오류: " + ex.Message; return r; }
            finally { armed = false; Gate.Release(); }
        }

        static long Num(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.ContainsKey(k) || d[k] == null) return -1;
            long v; return long.TryParse(Convert.ToString(d[k]), out v) ? v : -1;
        }

        // 미리보기로 올린 줄은 지움 (나중에 다른 발주와 섞이지 않게)
        async Task ClearUploaded()
        {
            await view.CoreWebView2.ExecuteScriptAsync("(() => { const ids = [...document.querySelectorAll('#excel_result_list .activeDelBtn')].map(b => b.dataset.sellno); ids.forEach(id => fetch('./dmorderExe.php?mode=orders_excel_delete&sell_no=' + encodeURIComponent(id), {credentials: 'include'})); })()");
            await Task.Delay(1500);
        }

        // 주문/배송조회 (최근 days 일): 각 줄의 칸 글자와 줄 전체 HTML
        public async Task<List<Dictionary<string, object>>> OrderList(int days, Action<string> log)
        {
            await Gate.WaitAsync();
            try
            {
                if (!await OpenSeller("_admin/html/_dmorder/dmorder_list.php", log)) return null;
                string q = "?stxtDate=" + DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd") + "&etxtDate=" + DateTime.Now.ToString("yyyy-MM-dd") + "&unit_page=3000";
                string js = "(async () => { const h = await (await fetch('./dmorder_list.php" + q + "', {credentials: 'include'})).text(); const d = new DOMParser().parseFromString(h, 'text/html');" +
                    " const heads = [...d.querySelectorAll('table th')].map(t => t.innerText.trim()); const rows = [];" +
                    " for (const tr of d.querySelectorAll('table tr')) { const tds = [...tr.querySelectorAll('td')]; if (tds.length < 8) continue; rows.push({ cells: tds.map(t => t.innerText.trim()), html: tr.innerHTML }); }" +
                    " window.__olist = JSON.stringify({ heads, rows }); })()";
                await view.CoreWebView2.ExecuteScriptAsync(js);
                string raw = "null";
                for (int i = 0; i < 40 && raw == "null"; i++) { await Task.Delay(500); raw = await view.CoreWebView2.ExecuteScriptAsync("window.__olist || null"); }
                if (raw == "null") return null;
                var obj = json.DeserializeObject(json.Deserialize<string>(raw)) as Dictionary<string, object>;
                var heads = ((IEnumerable)obj["heads"]).Cast<object>().Select(Convert.ToString).ToList();
                var list = new List<Dictionary<string, object>>();
                foreach (Dictionary<string, object> row in (IEnumerable)obj["rows"])
                {
                    var cells = ((IEnumerable)row["cells"]).Cast<object>().Select(Convert.ToString).ToList();
                    var d = new Dictionary<string, object> { { "_html", row["html"] }, { "_text", string.Join(" ", cells) } };
                    // 머리글과 칸 수가 맞으면 이름으로 (번호·발주번호·발주일자·주문자명·배송메세지·제품이미지·묶음배송·제품명·단가·수량·가격·배송비·송장번호·입금상태·발주현황·처리)
                    int off = cells.Count - heads.Count;
                    for (int i = 0; i < heads.Count; i++) { int j = i + off; if (j >= 0 && j < cells.Count && heads[i].Length > 0) d[heads[i]] = cells[j]; }
                    list.Add(d);
                }
                return list;
            }
            finally { await view.CoreWebView2.ExecuteScriptAsync("window.__olist = null"); Gate.Release(); }
        }

        const string ReadJs = @"(() => {
  const body = document.body ? document.body.innerText : '';
  const num = re => { const m = body.match(re); return m ? parseInt(m[1].replace(/,/g, ''), 10) : -1; };
  const box = document.querySelector('.prd_title');
  const t2 = box ? box.querySelector('.t2') : null;
  const notes = box ? [...box.querySelectorAll('.f_blue')].map(e => e.innerText.trim()).filter(t => t) : [];
  const brandEl = document.querySelector('.txt_brandName');
  const cats = [...document.querySelectorAll('.pnCateDetail')].map(a => a.innerText.trim());
  let main = '';
  for (const img of document.querySelectorAll('img')) { const s = img.getAttribute('src') || ''; if (s.indexOf('/files/goods/') >= 0) { main = img.src; break; } }
  const detail = [...document.querySelectorAll('.detailImg.innerContent img')].map(i => i.src).filter(s => s);
  return {
    login: body.indexOf('로그아웃') >= 0,
    name: t2 ? t2.innerText.trim() : '',
    notice: notes.join('  '),
    brand: brandEl ? brandEl.innerText.trim() : '',
    category: cats.join(' > '),
    sale: num(/판매가\s*([\d,]+)\s*원/),
    wholesale: num(/도매가\s*([\d,]+)\s*원/),
    shipping: num(/배송비\s*([\d,]+)\s*원/),
    main: main, detail: detail
  };
})()";

        // 저장해 둔 아이디로 로그인 (로그인이 풀려 있을 때). 성공하면 true
        public async Task<bool> AutoLogin(Action<string> log = null)
        {
            var c = MsgoodCred.Load();
            if (c == null || !c.HasMember || view.CoreWebView2 == null) return false;
            if (log != null) log("msgood4u 자동 로그인 중...");
            await Navigate(Home + "html/member/login.php");
            await Task.Delay(800);
            await view.CoreWebView2.ExecuteScriptAsync("(() => { const f = document.loginForm; if (!f) return; document.getElementById('id').value = " + json.Serialize(c.Id) +
                "; document.getElementById('passwd').value = " + json.Serialize(c.Pw) + "; loginCheck(f); })()");
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                if (await IsLoggedIn()) { if (log != null) log("msgood4u 에 자동으로 로그인했습니다."); return true; }
            }
            if (log != null) log("msgood4u 자동 로그인에 실패했습니다. [설정]에서 아이디·비밀번호를 확인하세요.");
            return false;
        }

        // 셀러로그인 (상품 개별발주용): 판매자 코드·아이디·비밀번호
        public async Task<bool> SellerLogin(Action<string> log = null)
        {
            var c = MsgoodCred.Load();
            if (c == null || !c.HasSeller || view.CoreWebView2 == null) return false;
            if (log != null) log("msgood4u 셀러로그인 중...");
            await Navigate(Home + "_admin/html/seller_login.php");
            await Task.Delay(800);
            await view.CoreWebView2.ExecuteScriptAsync("(() => { const f = document.loginFormSeller; if (!f) return; document.getElementById('_partner_code').value = " + json.Serialize(c.SellerCode) +
                "; document.getElementById('seller_id').value = " + json.Serialize(c.SellerId) + "; document.getElementById('seller_passwd').value = " + json.Serialize(c.SellerPw) + "; seller_loginCheck(f); })()");
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                string url = view.Source != null ? view.Source.ToString() : "";
                if (url.Length > 0 && !url.Contains("seller_login")) { if (log != null) log("msgood4u 셀러로그인 했습니다."); return true; }
            }
            if (log != null) log("msgood4u 셀러로그인에 실패했습니다. [설정]에서 판매자 코드·아이디·비밀번호를 확인하세요.");
            return false;
        }

        // 로그인 유지: msgood4u 로그인은 세션 쿠키(PHPSESSID)라 프로그램을 닫으면 지워짐.
        // 세션 쿠키를 30일짜리로 바꿔 저장해 두면 다음에 켰을 때도 로그인된 채로 열림 (사이트가 세션을 끝내지 않는 한)
        public async Task PersistCookies()
        {
            if (view.CoreWebView2 == null) return;
            try
            {
                var cm = view.CoreWebView2.CookieManager;
                foreach (var site in new[] { "https://msgood4u.com", "https://www.msgood4u.com" })
                    foreach (var c in await cm.GetCookiesAsync(site))
                        if (c.IsSession) { c.Expires = DateTime.Now.AddDays(30); cm.AddOrUpdateCookie(c); }
            }
            catch { }
        }

        // 프로그램이 켜져 있는 동안 10분마다 조용히 접속해 사이트 쪽 로그인이 끝나지 않게 함 (다른 작업 중이면 건너뜀)
        public async Task KeepAlive()
        {
            if (view.CoreWebView2 == null || !Gate.Wait(0)) return;
            try
            {
                if (Url.Contains("msgood4u.com")) await view.CoreWebView2.ExecuteScriptAsync("fetch('/', {credentials: 'include'}).catch(() => {})");
                await PersistCookies();
            }
            catch { }
            finally { Gate.Release(); }
        }

        public async Task<bool> IsLoggedIn()
        {
            if (view.CoreWebView2 == null) return false;
            var r = await Eval("({ login: document.body ? document.body.innerText.indexOf('로그아웃') >= 0 : false })");
            bool ok = r != null && Convert.ToBoolean(r["login"]);
            if (ok) await PersistCookies();
            return ok;
        }

        public async Task Fill(Job job, Action<string> log, bool requireLogin = false)
        {
            await Gate.WaitAsync();
            try { await FillInner(job, log, requireLogin); }
            finally { Gate.Release(); }
        }

        async Task FillInner(Job job, Action<string> log, bool requireLogin)
        {
            if (job.Url == null) job.Url = await Task.Run(() => ExtraImages.Msgood.FindUrl(job.Code));
            log("msgood4u 상품 페이지 여는 중...");
            await Navigate(job.Url);
            Dictionary<string, object> r = null;
            for (int i = 0; i < 30; i++)
            {
                r = await Eval(ReadJs);
                if (r != null && Convert.ToString(r["name"]).Length > 0) break;
                await Task.Delay(500);
            }
            if (r == null || Convert.ToString(r["name"]).Length == 0) throw new Exception("msgood4u 상품 페이지를 읽지 못했습니다.");
            // 로그인이 풀려 있으면 저장한 아이디로 자동 로그인 후 다시 읽음
            if (!Convert.ToBoolean(r["login"]) && await AutoLogin(log))
            {
                await Navigate(job.Url);
                for (int i = 0; i < 30; i++)
                {
                    var r2 = await Eval(ReadJs);
                    if (r2 != null && Convert.ToString(r2["name"]).Length > 0) { r = r2; break; }
                    await Task.Delay(500);
                }
            }
            // 로그인이 안 돼 있어도 계속 (도매가만 비워 두고 나중에 로그인 후 가격 계산)
            bool login = Convert.ToBoolean(r["login"]);
            if (!login && requireLogin) throw new LoginNeeded();

            job.Name = Convert.ToString(r["name"]);
            job.Notice = Convert.ToString(r["notice"]);
            string brand = Convert.ToString(r["brand"]).Trim();
            job.Brand = brand.Length == 0 || brand == "-" || brand.Contains(",") ? null : brand;
            job.Category = Convert.ToString(r["category"]);
            job.SalePrice = Convert.ToInt64(r["sale"]);
            job.Wholesale = Convert.ToInt64(r["wholesale"]);
            job.Shipping = Convert.ToInt64(r["shipping"]);
            job.MainImageUrl = Convert.ToString(r["main"]);
            job.DetailUrls = ((IEnumerable)r["detail"]).Cast<object>().Select(Convert.ToString).Where(s => s.Length > 0).Distinct().ToList();
            if (!login || job.Wholesale <= 0)
            {
                if (requireLogin) throw new Exception("도매가를 읽지 못했습니다. msgood4u 로그인 상태를 확인하세요.");
                job.Wholesale = 0; job.NeedPrice = true;
            }
            else job.NeedPrice = false;
            if (job.Shipping < 0) job.Shipping = 3000;

            // 판매 하한선: "스마트스토어,쿠팡 16,900원이상 등록가능" → 16,900 / "[판매가 절대 준수]" → msgood 판매가
            var m = Regex.Match(job.Notice ?? "", @"스마트스토어[^0-9]{0,20}([\d,]{4,})\s*원\s*이상");
            if (m.Success) { job.Floor = long.Parse(m.Groups[1].Value.Replace(",", "")); job.FloorReason = "스마트스토어·쿠팡 " + job.Floor.ToString("#,0") + "원 이상"; }
            else if (Regex.IsMatch(job.Notice ?? "", @"판매가\s*절대\s*준수") && job.SalePrice > 0) { job.Floor = job.SalePrice; job.FloorReason = "판매가 절대 준수 (" + job.SalePrice.ToString("#,0") + "원)"; }
            else { job.Floor = 0; job.FloorReason = null; }

            job.DetailHtml = job.DetailUrls.Count > 0
                ? string.Join("\r\n", job.DetailUrls.Select(u => "<img src=\"" + u + "\">"))
                : "<img src=\"http://mskorea3118.cafe24.com/" + job.Code + ".jpg\">";
            var vm = Regex.Match(job.Name, @"(\d+)\s*(정|캡슐|포|g|ml|환)", RegexOptions.RightToLeft | RegexOptions.IgnoreCase);
            job.Volume = vm.Success ? vm.Value.Replace(" ", "") : "";
        }

        public async Task Navigate(string url)
        {
            var done = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> h = (s, e) => done.TrySetResult(true);
            view.CoreWebView2.NavigationCompleted += h;
            try
            {
                view.CoreWebView2.Navigate(url);
                await Task.WhenAny(done.Task, Task.Delay(30000));
            }
            finally { view.CoreWebView2.NavigationCompleted -= h; }
        }

        async Task<Dictionary<string, object>> Eval(string js)
        {
            try { return json.DeserializeObject(await view.CoreWebView2.ExecuteScriptAsync(js)) as Dictionary<string, object>; }
            catch { return null; }
        }
    }

    // msgood4u 아이디·비밀번호 (이 PC의 이 Windows 사용자만 풀 수 있게 암호화해서 msgood_login.dat 에 저장)
    public class MsgoodCred
    {
        public string Id, Pw, SellerCode, SellerId, SellerPw;
        static readonly byte[] Salt = Encoding.UTF8.GetBytes("SalesTool.msgood");
        public static string FilePath { get { return System.IO.Path.Combine(Store.Dir, "msgood_login.dat"); } }

        public static MsgoodCred Load()
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) return null;
                var raw = ProtectedData.Unprotect(System.IO.File.ReadAllBytes(FilePath), Salt, DataProtectionScope.CurrentUser);
                return new JavaScriptSerializer().Deserialize<MsgoodCred>(Encoding.UTF8.GetString(raw));
            }
            catch { return null; }
        }

        public void Save()
        {
            var raw = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(this));
            System.IO.File.WriteAllBytes(FilePath, ProtectedData.Protect(raw, Salt, DataProtectionScope.CurrentUser));
        }

        public static void Delete() { try { System.IO.File.Delete(FilePath); } catch { } }
        public bool HasMember { get { return !string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(Pw); } }
        public bool HasSeller { get { return !string.IsNullOrEmpty(SellerCode) && !string.IsNullOrEmpty(SellerId) && !string.IsNullOrEmpty(SellerPw); } }
    }

    public class LoginNeeded : Exception
    {
        public LoginNeeded() : base("msgood4u 로그인이 필요합니다. [msgood4u] 탭에서 로그인한 뒤 [계속]을 누르세요.") { }
    }
}
