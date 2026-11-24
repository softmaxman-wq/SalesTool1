using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SalesTool
{
    // 쿠팡 검색창 자동완성 (단어 입력 시 드롭다운 검색어) → 쿠팡 검색어(searchTags)
    public class CoupangSuggest
    {
        readonly WebView2 view;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly Random rnd = new Random();
        static readonly SemaphoreSlim sem = new SemaphoreSlim(1, 1);
        static readonly Dictionary<string, List<string>> cache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        static int reqSeq = 0;
        bool helperInjected = false;

        public CoupangSuggest(WebView2 view = null) { this.view = view; }

        public static void ClearCache()
        {
            lock (cache) { cache.Clear(); }
        }

        public static string Key(string s)
        {
            return Regex.Replace(s ?? "", @"[^0-9A-Za-z가-힣]", "").ToLowerInvariant();
        }

        public static bool ValidateKeyword(string kw)
        {
            if (string.IsNullOrWhiteSpace(kw)) return false;
            string s = kw.Trim();
            return s.Length >= 2 && s.Length <= 20;
        }

        public static List<string> FallbackSuggest(string q)
        {
            var list = new List<string>();
            q = (q ?? "").Trim();
            if (string.IsNullOrEmpty(q)) return list;

            if (ValidateKeyword(q)) list.Add(q);
            // 자동완성을 못 읽었을 때 '○○ 추천·먹는법' 같은 검색어를 지어내지 않음 (실제로 검색되는지 모름)
            return list;
        }

        public static List<string> ParseSuggestPayload(string rawJson)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(rawJson) || rawJson == "null" || rawJson == "undefined")
                return list;

            try
            {
                string text = rawJson.Trim();
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                if (text.StartsWith("\"") && text.EndsWith("\""))
                {
                    try { text = serializer.Deserialize<string>(text) ?? text; }
                    catch { }
                }

                object deserialized = null;
                try { deserialized = serializer.DeserializeObject(text); }
                catch { }

                if (deserialized is IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item is Dictionary<string, object> dict)
                        {
                            object k;
                            if (dict.TryGetValue("keyword", out k) && k != null)
                            {
                                string s = System.Net.WebUtility.HtmlDecode(Convert.ToString(k).Trim());
                                if (ValidateKeyword(s) && !list.Contains(s)) list.Add(s);
                            }
                        }
                        else if (item is string str)
                        {
                            string s = System.Net.WebUtility.HtmlDecode(str.Trim());
                            if (ValidateKeyword(s) && !list.Contains(s)) list.Add(s);
                        }
                    }
                }
            }
            catch { }

            return list;
        }

        async Task<bool> EnsureOpen()
        {
            if (view == null || view.CoreWebView2 == null) return false;
            await SmallKeyword.PriceCompare.WaitLogin(null);   // 네이버 로그인 중에는 작업 화면을 쓰지 않음
            try
            {
                if (view.Source != null && view.Source.Host != null && view.Source.Host.EndsWith("coupang.com"))
                {
                    await EnsureHelperInjected();
                    return true;
                }

                var done = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs> h = (s, e) => done.TrySetResult(e != null && e.IsSuccess);
                view.CoreWebView2.NavigationCompleted += h;
                try
                {
                    view.CoreWebView2.Navigate("https://www.coupang.com/");
                    var winner = await Task.WhenAny(done.Task, Task.Delay(12000));
                    if (winner != done.Task || !done.Task.Result) return false;
                }
                finally { view.CoreWebView2.NavigationCompleted -= h; }
                await Task.Delay(300);
                await EnsureHelperInjected();
                return true;
            }
            catch { return false; }
        }

        async Task EnsureHelperInjected()
        {
            if (helperInjected || view == null || view.CoreWebView2 == null) return;
            string js = @"
            if (!window.__cpSuggest) {
                window.__cpSuggest = {
                    cache: {},
                    async fetchOne(kw) {
                        kw = (kw || '').trim();
                        if (!kw) return [];
                        if (this.cache[kw]) return this.cache[kw];
                        try {
                            let url = '/n-api/web-adapter/search?keyword=' + encodeURIComponent(kw) + '&_=' + Date.now();
                            let r = await fetch(url, { credentials: 'include' });
                            if (!r.ok) return [];
                            let arr = await r.json();
                            let res = [];
                            if (Array.isArray(arr)) {
                                for (let item of arr) {
                                    if (item && item.keyword) {
                                        let s = (item.keyword || '').trim();
                                        if (s && s.length >= 2 && s.length <= 20 && !res.includes(s)) res.push(s);
                                    }
                                }
                            }
                            this.cache[kw] = res;
                            return res;
                        } catch(e) { return []; }
                    },
                    async fetchBatch(keywords, delayMs) {
                        let out = {};
                        for (let k of keywords) {
                            out[k] = await this.fetchOne(k);
                            if (delayMs > 0) await new Promise(r => setTimeout(r, delayMs));
                        }
                        return out;
                    },
                    async findTopCategory(query) {
                        query = (query || '').trim();
                        if (!query) return null;
                        try {
                            let sUrl = '/np/search?component=&q=' + encodeURIComponent(query) + '&channel=user';
                            let r = await fetch(sUrl, { credentials: 'include', headers: { 'Accept': 'text/html' } });
                            if (!r.ok) return null;
                            let html = await r.text();
                            let doc = new DOMParser().parseFromString(html, 'text/html');

                            let items = Array.from(doc.querySelectorAll('li.search-product:not(.search-product__ad) a.search-product-link, li.search-product a[href*=""/vp/products/""]'));
                            let productLinks = [];
                            for (let a of items) {
                                let href = a.getAttribute('href');
                                if (href && href.includes('/vp/products/')) {
                                    if (!href.startsWith('http')) href = 'https://www.coupang.com' + href;
                                    if (!productLinks.includes(href)) {
                                        productLinks.push(href);
                                        if (productLinks.length >= 5) break;
                                    }
                                }
                            }
                            if (productLinks.length < 5) {
                                let allA = Array.from(doc.querySelectorAll('a[href*=""/vp/products/""]'));
                                for (let a of allA) {
                                    let href = a.getAttribute('href');
                                    if (href && href.includes('/vp/products/')) {
                                        if (!href.startsWith('http')) href = 'https://www.coupang.com' + href;
                                        if (!productLinks.includes(href)) {
                                            productLinks.push(href);
                                            if (productLinks.length >= 5) break;
                                        }
                                    }
                                }
                            }
                            if (productLinks.length === 0) return null;

                            let votes = {};
                            let promises = productLinks.map(async (url) => {
                                try {
                                    let pr = await fetch(url, { credentials: 'include', headers: { 'Accept': 'text/html' } });
                                    if (!pr.ok) return null;
                                    let phtml = await pr.text();
                                    let pdoc = new DOMParser().parseFromString(phtml, 'text/html');

                                    let bcs = Array.from(pdoc.querySelectorAll('#breadcrumb li a, .breadcrumb li a, nav[aria-label=""breadcrumb""] a, .breadcrumb-item a'));
                                    let catId = null;
                                    let catName = null;

                                    for (let i = bcs.length - 1; i >= 0; i--) {
                                        let a = bcs[i];
                                        let href = a.getAttribute('href') || '';
                                        let m = href.match(/\/categories\/(\d+)/);
                                        let name = (a.innerText || '').trim();
                                        if (m && name) {
                                            catId = m[1];
                                            catName = name;
                                            break;
                                        }
                                    }

                                    if (!catId) {
                                        let ldScripts = Array.from(pdoc.querySelectorAll('script[type=""application/ld+json""]'));
                                        for (let s of ldScripts) {
                                            try {
                                                let text = s.innerText || '';
                                                if (text.includes('BreadcrumbList')) {
                                                    let data = JSON.parse(text);
                                                    if (data['@type'] === 'BreadcrumbList' && Array.isArray(data.itemListElement)) {
                                                        let list = data.itemListElement;
                                                        for (let j = list.length - 1; j >= 0; j--) {
                                                            let item = list[j];
                                                            if (item && item.item) {
                                                                let m = String(item.item).match(/\/categories\/(\d+)/);
                                                                if (m) {
                                                                    catId = m[1];
                                                                    catName = item.name || catName;
                                                                    break;
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            } catch(e) {}
                                            if (catId) break;
                                        }
                                    }

                                    if (!catId) {
                                        let m = phtml.match(/\/categories\/(\d+)/);
                                        if (m) catId = m[1];
                                    }

                                    if (catId) return { id: catId, name: catName || '' };
                                } catch(e) {}
                                return null;
                            });

                            let results = await Promise.all(promises);
                            for (let r of results) {
                                if (r && r.id) {
                                    let k = r.id + '|||' + (r.name || '');
                                    votes[k] = (votes[k] || 0) + 1;
                                }
                            }

                            let bestKey = null;
                            let maxCount = 0;
                            for (let k in votes) {
                                if (votes[k] > maxCount) {
                                    maxCount = votes[k];
                                    bestKey = k;
                                }
                            }

                            if (bestKey) {
                                let p = bestKey.split('|||');
                                return { id: p[0], name: p[1], count: maxCount, sampleCount: productLinks.length };
                            }
                            return null;
                        } catch(e) { return null; }
                    }
                };
            }";
            try
            {
                await view.CoreWebView2.ExecuteScriptAsync(js);
                helperInjected = true;
            }
            catch { }
        }

        public async Task<List<string>> Suggest(string q)
        {
            q = (q ?? "").Trim();
            if (q.Length == 0) return new List<string>();

            lock (cache)
            {
                List<string> cached;
                if (cache.TryGetValue(q, out cached)) return new List<string>(cached);
            }

            if (!await EnsureOpen())
            {
                var fb = FallbackSuggest(q);
                lock (cache) { cache[q] = fb; }
                return new List<string>(fb);
            }

            string slot = "__ac_res_" + Interlocked.Increment(ref reqSeq);
            string js = "(function() { window['" + slot + "'] = null; window.__cpSuggest.fetchOne(" + json.Serialize(q) + ").then(r => { window['" + slot + "'] = JSON.stringify(r); }).catch(e => { window['" + slot + "'] = '[]'; }); })();";
            await view.CoreWebView2.ExecuteScriptAsync(js);

            string raw = "null";
            for (int i = 0; i < 40 && (raw == "null" || raw == null); i++)
            {
                await Task.Delay(50);
                raw = await view.CoreWebView2.ExecuteScriptAsync("window['" + slot + "']");
            }
            try { await view.CoreWebView2.ExecuteScriptAsync("delete window['" + slot + "']"); } catch { }

            var list = ParseSuggestPayload(raw);
            if (list.Count == 0) list = FallbackSuggest(q);

            lock (cache) { cache[q] = list; }
            return new List<string>(list);
        }

        public async Task<Dictionary<string, List<string>>> BatchSuggest(IEnumerable<string> queries, int delayMs = 50)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();

            lock (cache)
            {
                foreach (var q in queries.Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    List<string> c;
                    if (cache.TryGetValue(q, out c)) result[q] = new List<string>(c);
                    else missing.Add(q);
                }
            }

            if (missing.Count == 0) return result;

            if (!await EnsureOpen())
            {
                foreach (var m in missing)
                {
                    var fb = FallbackSuggest(m);
                    result[m] = fb;
                    lock (cache) { cache[m] = fb; }
                }
                return result;
            }

            string slot = "__batch_res_" + Interlocked.Increment(ref reqSeq);
            string kwJson = json.Serialize(missing);
            string js = "(function() { window['" + slot + "'] = null; window.__cpSuggest.fetchBatch(" + kwJson + ", " + delayMs + ").then(r => { window['" + slot + "'] = JSON.stringify(r); }).catch(e => { window['" + slot + "'] = '{}'; }); })();";
            await view.CoreWebView2.ExecuteScriptAsync(js);

            int maxWaitTicks = (missing.Count * (delayMs + 80) + 1500) / 50;
            string raw = "null";
            for (int i = 0; i < maxWaitTicks && (raw == "null" || raw == null); i++)
            {
                await Task.Delay(50);
                raw = await view.CoreWebView2.ExecuteScriptAsync("window['" + slot + "']");
            }
            try { await view.CoreWebView2.ExecuteScriptAsync("delete window['" + slot + "']"); } catch { }

            if (raw != "null" && !string.IsNullOrEmpty(raw))
            {
                try
                {
                    string text = json.Deserialize<string>(raw) ?? raw;
                    var dict = json.Deserialize<Dictionary<string, object>>(text);
                    if (dict != null)
                    {
                        foreach (var kv in dict)
                        {
                            var words = new List<string>();
                            if (kv.Value is IEnumerable en)
                            {
                                foreach (var obj in en)
                                {
                                    string s = System.Net.WebUtility.HtmlDecode(Convert.ToString(obj).Trim());
                                    if (ValidateKeyword(s) && !words.Contains(s)) words.Add(s);
                                }
                            }
                            if (words.Count == 0) words = FallbackSuggest(kv.Key);
                            result[kv.Key] = words;
                            lock (cache) { cache[kv.Key] = words; }
                        }
                    }
                }
                catch { }
            }

            foreach (var m in missing)
            {
                if (!result.ContainsKey(m))
                {
                    var fb = FallbackSuggest(m);
                    result[m] = fb;
                    lock (cache) { cache[m] = fb; }
                }
            }

            return result;
        }

        public async Task<List<string>> Raw(IEnumerable<string> queries)
        {
            var list = queries.Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).ToList();
            if (list.Count == 0) return new List<string>();

            var batch = await BatchSuggest(list, 50);
            var all = new List<string>();
            foreach (var q in list)
            {
                List<string> words;
                if (batch.TryGetValue(q, out words) && words != null) all.AddRange(words);
            }
            return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // 쿠팡 실시간 검색 기반 상위 5개 상품 카테고리 수집 (다수결 최다 빈도 카테고리 ID, 카테고리명 반환)
        public async Task<Tuple<string, string>> FindTopCoupangCategory(string query, Action<string> step = null)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return null;

            if (!await EnsureOpen()) return null;

            step?.Invoke("2/6 쿠팡에서 '" + query + "' 상위 5개 상품 카테고리 분석 중...");

            string slot = "__cp_cat_" + Interlocked.Increment(ref reqSeq);
            string js = "(function() { window['" + slot + "'] = null; window.__cpSuggest.findTopCategory(" + json.Serialize(query) + ").then(r => { window['" + slot + "'] = JSON.stringify(r); }).catch(e => { window['" + slot + "'] = 'null'; }); })();";
            await view.CoreWebView2.ExecuteScriptAsync(js);

            string raw = "null";
            for (int i = 0; i < 160 && (raw == "null" || raw == null); i++)
            {
                await Task.Delay(50);
                raw = await view.CoreWebView2.ExecuteScriptAsync("window['" + slot + "']");
            }
            try { await view.CoreWebView2.ExecuteScriptAsync("delete window['" + slot + "']"); } catch { }

            if (!string.IsNullOrWhiteSpace(raw) && raw != "null" && raw != "\"null\"")
            {
                try
                {
                    string text = json.Deserialize<string>(raw) ?? raw;
                    var dict = json.Deserialize<Dictionary<string, object>>(text);
                    if (dict != null && dict.ContainsKey("id"))
                    {
                        string id = Convert.ToString(dict["id"]).Trim();
                        string name = dict.ContainsKey("name") ? Convert.ToString(dict["name"]).Trim() : "";
                        if (!string.IsNullOrEmpty(id))
                        {
                            return Tuple.Create(id, name);
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        public async Task<List<string>> Build(string seed, IList<string> tagWords, IList<string> allWords, Action<string> step, Func<string, bool> fits = null,
                                              IList<string> extra = null, IList<string> alts = null, Func<string, int> used = null)
        {
            await sem.WaitAsync();
            try
            {
                const int Want = 20, Pool = 30;
                string sk = Key(seed);
                var universe = new HashSet<string>((allWords ?? new List<string>()).Select(Key).Where(k => k.Length >= 2 && k != sk));
                var picked = new List<string>();
                var tagKeys = (tagWords ?? new List<string>()).Select(Key).Where(k => k.Length >= 2).ToList();

                string[] Generic = { "효능", "효과", "영양제", "추천", "선물", "선물세트", "정품", "식약청인증", "식약처인증", "식약청인정", "식약처인정", "고르는법", "먹는법", "복용법", "섭취법" };
                var genericKeys = new HashSet<string>(Generic.Select(Key));

                // 검색어의 모든 낱말이 상품 단어(오트밀)를 포함하거나, 고른 단어(아기·그래놀라…)이거나, 카테고리 상품명 말이거나, 유효 수식어(효능·추천…)여야 함
                Func<string, bool> wordsOk = s => s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).All(t =>
                {
                    string tk = Key(t);
                    return tk.Length > 0 && (tk.Contains(sk) || tagKeys.Contains(tk) || universe.Contains(tk) || genericKeys.Contains(tk));
                });
                Func<string, bool> ok = s => s.Length >= 2 && s.Length <= 20 && Key(s).Contains(sk) && wordsOk(s) && (fits == null || fits(s)) && !picked.Any(p => Key(p) == Key(s));

                Func<string, string, bool> tokOk = (t, center) =>
                {
                    string tk = Key(t), ck = Key(center);
                    if (tk.Length == 0) return false;
                    string rest = tk.Contains(ck) ? tk.Replace(ck, "") : tk;
                    if (rest.Length == 0 || genericKeys.Contains(rest) || tagKeys.Contains(rest)) return true;
                    return rest.Length >= 2 && universe.Contains(rest);
                };

                var seedList = await Suggest(seed);

                // 1) tagWords 조합 배치 검색
                var combQueries = new List<string>();
                if (tagWords != null)
                {
                    foreach (var w in tagWords)
                    {
                        combQueries.Add(w + " " + seed);
                        combQueries.Add(seed + " " + w);
                    }
                }
                var combResults = await BatchSuggest(combQueries, 50);

                int n = 0;
                if (tagWords != null)
                {
                    foreach (var w in tagWords)
                    {
                        if (picked.Count >= Want - 6) break;
                        step?.Invoke("5/6 쿠팡 자동완성 검색어 찾는 중... (" + (++n) + "/" + tagWords.Count + ") " + w + " " + seed);
                        string wk = Key(w);
                        var got = new List<string>(seedList);
                        List<string> r1, r2;
                        if (combResults.TryGetValue(w + " " + seed, out r1) && r1 != null) got.AddRange(r1);
                        if (combResults.TryGetValue(seed + " " + w, out r2) && r2 != null) got.AddRange(r2);

                        foreach (var s in got.Where(s => Key(s).Contains(wk) && ok(s)).Distinct()
                                             .OrderBy(s => Key(s) == wk + sk || Key(s) == sk + wk ? 0 : 1).ThenBy(s => s.Length).Take(2))
                            if (ok(s)) picked.Add(s);
                    }
                }

                // 2) 시작 단어 자동완성 중 이 상품의 키워드가 들어간 것
                if (picked.Count < Pool)
                {
                    step?.Invoke("5/6 쿠팡 자동완성 검색어 찾는 중... (" + seed + ")");
                    foreach (var s in seedList)
                        if (picked.Count < Want - 6 && ok(s) && universe.Any(u => Key(s).Contains(u))
                            && s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).All(t => tokOk(t, seed))) picked.Add(s);
                }

                Func<string, string, bool> okLoose = (s, center) => s.Length >= 2 && s.Length <= 20 && Key(s).Contains(Key(center)) && (fits == null || fits(s))
                                                                   && s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).All(t => tokOk(t, center))
                                                                   && !picked.Any(p => Key(p) == Key(s));

                // 3) 시작 단어 자동완성의 나머지
                if (picked.Count < Pool)
                    foreach (var s in seedList) if (picked.Count < Pool && okLoose(s, seed)) picked.Add(s);

                // 4) 상품에 맞는 키워드 그대로
                if (extra != null)
                    foreach (var w in extra)
                    {
                        if (picked.Count >= Pool) break;
                        string wk = Key(w);
                        if (wk.Length < 2) continue;
                        string t = wk.Contains(sk) || (alts != null && alts.Any(a => Key(a).Length >= 2 && wk.Contains(Key(a)))) ? w : w + " " + seed;
                        if (t.Length <= 20 && (fits == null || fits(t)) && !picked.Any(p => Key(p) == Key(t))) picked.Add(t);
                    }

                // 5) 상품 종류의 다른 이름
                if (alts != null)
                {
                    var altQueries = alts.Where(a => Key(a).Length >= 2 && Key(a) != sk).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
                    if (altQueries.Count > 0)
                    {
                        var altResults = await BatchSuggest(altQueries, 50);
                        foreach (var a in altQueries)
                        {
                            if (picked.Count >= Want) break;
                            step?.Invoke("5/6 쿠팡 자동완성 검색어 찾는 중... (" + a + ")");
                            List<string> aWords;
                            if (altResults.TryGetValue(a, out aWords) && aWords != null)
                            {
                                foreach (var s in aWords) if (picked.Count < Pool && okLoose(s, a) && Key(s).Contains(sk)) picked.Add(s);
                            }
                        }
                    }
                }

                // Fallback 보충 (CoupangTags 가 절대로 비지 않도록 보장)
                if (picked.Count < Want)
                {
                    foreach (var fb in FallbackSuggest(seed))
                    {
                        if (picked.Count >= Want) break;
                        if (okLoose(fb, seed) && !picked.Any(p => Key(p) == Key(fb)))
                            picked.Add(fb);
                    }
                }

                var res = used == null ? picked : picked.Select((p, i) => new { p, i }).OrderBy(x => used(x.p)).ThenBy(x => x.i).Select(x => x.p).ToList();
                return res.Take(Want).ToList();
            }
            finally
            {
                sem.Release();
            }
        }
    }
}