using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SmallKeyword
{
    public class PageResult
    {
        public long Total = -1;   // "전체 N" (검색 결과 없음 = 0, 못 읽음 = -1)
        public List<string> Titles = new List<string>();
        public List<bool> Ads = new List<bool>();
        public List<string> Brands = new List<string>(); // 필터의 브랜드·제조사 이름 (키워드에서 뺄 때 씀)
        public List<string> Categories = new List<string>(); // 상품마다 카테고리 경로 (식품>건강식품>홍삼>홍삼액)
    }

    // 네이버가 짧은 시간에 너무 많이 읽었다고 잠시 막은 경우
    public class NaverBlocked : Exception
    {
        public NaverBlocked() : base("네이버가 쇼핑 접속을 잠시 막았습니다 (짧은 시간에 너무 많이 읽음). 30분~1시간 뒤 [계속]을 누르세요. [네이버 로그인]을 해 두면 덜 막힙니다.") { }
    }

    // 네이버 가격비교 검색 화면 (search.shopping.naver.com/search/all) 을 프로그램 안 브라우저로 열어 읽음
    public class PriceCompare
    {
        const string Host = "search.shopping.naver.com";

        // 전체 상품 수 + 상품명 (광고 포함, 화면 순서대로). 보안 확인 화면이면 state = captcha
        const string ReadJs = @"(() => {
  const body = document.body ? document.body.innerText : '';
  if (body.slice(0, 600).indexOf('보안 확인') >= 0) return { state: 'captcha' };
  if (body.indexOf('접속이 일시적으로 제한') >= 0) return { state: 'blocked' };
  let total = -1;
  for (const a of document.querySelectorAll('[class*=""subFilter_filter""]')) {
    const t = (a.innerText || '').replace(/\s+/g, '');
    if (t.indexOf('전체') === 0) { const n = a.querySelector('[class*=""subFilter_num""]'); if (n) { total = parseInt(n.innerText.replace(/[^0-9]/g, ''), 10); break; } }
  }
  const noResult = /검색결과가 없습니다|검색 결과가 없습니다/.test(body);
  if (total < 0 && noResult) total = 0;
  const items = [...document.querySelectorAll('[class*=""product_title__""], [class*=""adProduct_title__""]')].map(el => ({
    t: el.innerText.replace(/^\s*예약\s*/, '').replace(/\s+/g, ' ').trim(),
    ad: (el.className || '').indexOf('adProduct') >= 0
  })).filter(x => x.t);
  const brands = [];
  for (const col of document.querySelectorAll('[class*=""filterWrapper_finder_col__""]')) {
    const h = col.querySelector('[class*=""filterWrapper_finder_tit__""]');
    if (!h || !/브랜드|제조사/.test(h.innerText)) continue;
    for (const a of col.querySelectorAll('a, button, label, li')) { const t = (a.innerText || '').trim(); if (t && t.length <= 20 && !/더보기|브랜드|제조사/.test(t)) brands.push(t); }
  }
  const cats = [...document.querySelectorAll('[class*=""product_depth__""]')].map(d => [...d.querySelectorAll('[class*=""product_category__""]')].map(x => x.innerText.trim()).filter(x => x).join('>')).filter(x => x);
  const inp = document.querySelector('input[name=""query""], input[type=""search""]');
  const q = new URLSearchParams(location.search).get('query') || '';
  return { state: total >= 0 || items.length > 0 ? 'ok' : 'loading', total: total, items: items, brands: [...new Set(brands)], cats: cats, q: q, input: inp ? inp.value : null };
})()";

        readonly WebView2 view;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public PriceCompare(WebView2 view) { this.view = view; }

        // 상품명 키워드 추출기와 같은 브라우저 저장소를 써서 네이버 로그인을 함께 씀
        public static string ProfileDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProductKeyword", "WebView2"); }
        }

        public bool Ready { get { return view.CoreWebView2 != null; } }

        public async Task Init()
        {
            if (Ready) return;
            var env = await CoreWebView2Environment.CreateAsync(null, ProfileDir);
            await view.EnsureCoreWebView2Async(env);
        }

        // catId: 네이버 쇼핑 카테고리 번호 (스마트스토어 카테고리와 같은 번호). 주면 그 카테고리 안에서만 검색
        public static string SearchUrl(string query, string catId = null, int pageIndex = 1)
        {
            string url = "https://" + Host + "/search/all?query=" + Uri.EscapeDataString(query);
            if (!string.IsNullOrEmpty(catId)) url += "&catId=" + Uri.EscapeDataString(catId);
            if (pageIndex > 1) url += "&pagingIndex=" + pageIndex;
            return url;
        }

        public void OpenLogin()
        {
            view.CoreWebView2.Navigate("https://nid.naver.com/nidlogin.login?url=" + Uri.EscapeDataString("https://shopping.naver.com/"));
        }

        // withTitles: 1페이지 상품명까지 (아래로 내려가며 40개를 다 불러옴). 아니면 전체 상품 수만
        // needCats: 상품마다 카테고리 경로까지 (5개 이상 보일 때까지 조금 기다리고 내림)
        public async Task<PageResult> Read(string query, bool withTitles, Action<string> report, Func<bool> cancelled, string catId = null, bool needCats = false, int pageIndex = 1)
        {
            int catTries = 0;
            await Navigate(SearchUrl(query, catId, pageIndex));
            DateTime giveUp = DateTime.Now.AddMinutes(10);
            int lastCount = -1, still = 0, waits = 0, stale = 0;
            while (true)
            {
                if (cancelled()) return null;
                if (DateTime.Now > giveUp) throw new Exception("10분 동안 화면이 나오지 않아 멈췄습니다.");
                var r = await Eval(ReadJs);
                string state = r == null ? "loading" : Convert.ToString(r["state"]);
                if (state == "captcha")
                {
                    report("네이버 보안 확인이 나왔습니다. 왼쪽 아래 작업 화면에서 풀어 주세요 ([크게]를 누르면 크게 보입니다).");
                    await Task.Delay(1000);
                    continue;
                }
                if (state == "blocked") throw new NaverBlocked();
                if (state != "ok") { if (++waits > 40) throw new Exception("'" + query + "' 화면을 읽지 못했습니다."); await Task.Delay(500); continue; }
                // 화면이 정말 이 검색어 결과인지 (주소·검색창이 다르면 이전 검색 화면이 남아 있는 것 → 기다렸다가 다시 열기)
                string shownQ = Convert.ToString(r.ContainsKey("q") ? r["q"] : ""), shownInput = r.ContainsKey("input") && r["input"] != null ? Convert.ToString(r["input"]) : null;
                if (!SameQuery(shownQ, query) || (shownInput != null && shownInput.Length > 0 && !SameQuery(shownInput, query)))
                {
                    if (++stale > 6) throw new Exception("'" + query + "' 검색 화면으로 바뀌지 않습니다.");
                    await Task.Delay(800);
                    if (stale % 3 == 0) await Navigate(SearchUrl(query, catId, pageIndex));
                    continue;
                }

                var page = new PageResult { Total = Convert.ToInt64(r["total"]) };
                object bl;
                if (r.TryGetValue("brands", out bl) && bl is IEnumerable) foreach (var b in (IEnumerable)bl) page.Brands.Add(Convert.ToString(b));
                if (r.TryGetValue("cats", out bl) && bl is IEnumerable) foreach (var c in (IEnumerable)bl) page.Categories.Add(Convert.ToString(c));
                foreach (var o in ((IEnumerable)r["items"]).Cast<Dictionary<string, object>>())
                {
                    page.Titles.Add(Convert.ToString(o["t"]));
                    page.Ads.Add(Convert.ToBoolean(o["ad"]));
                }
                // 카테고리는 10개 이상 보일 때까지 내리며 읽음 (몇 개만 보고 판단하면 틀리기 쉬움)
                if (!withTitles && needCats && page.Total > 0 && page.Categories.Count < 10 && catTries++ < 20)
                {
                    await Eval("window.scrollBy(0, 700)");
                    await Task.Delay(500);
                    continue;
                }
                if (!withTitles || page.Total == 0) return page;

                int organic = page.Ads.Count(a => !a);
                if (organic >= 40) { await Eval("window.scrollTo(0, 0)"); return page; }
                if (organic == lastCount) still++; else { still = 0; lastCount = organic; }
                if (still >= 8) { await Eval("window.scrollTo(0, 0)"); return page; } // 상품이 40개보다 적은 검색어
                await Eval("window.scrollBy(0, 1500)");
                await Task.Delay(500);
            }
        }

        static bool SameQuery(string a, string b)
        {
            Func<string, string> k = x => System.Text.RegularExpressions.Regex.Replace(x ?? "", @"\s+", "").ToLowerInvariant();
            return k(a) == k(b);
        }

        async Task Navigate(string url)
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
            return json.DeserializeObject(await view.CoreWebView2.ExecuteScriptAsync(js)) as Dictionary<string, object>;
        }
    }
}
