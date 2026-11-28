using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ProductUploader;

namespace SalesTool
{
    // #1 #2 #3 판매자 계정: api_keys.json 의 naver_commerce / coupang (#1), naver_commerce_2 / coupang_2 (#2), _3 (#3)
    public static class Accounts
    {
        static readonly Dictionary<string, Registrar> cache = new Dictionary<string, Registrar>();
        static readonly Dictionary<string, MarketConfig> configs = new Dictionary<string, MarketConfig>();

        public static string KeyFile { get { return MarketConfig.Locate(Store.Dir); } }

        public static MarketConfig Config(string id)
        {
            string path = KeyFile;
            if (!File.Exists(path)) return null;
            var c = MarketConfig.Load(path, id == "1" ? "" : "_" + id);
            configs[id] = c;
            return c;
        }

        public static void Reset() { cache.Clear(); configs.Clear(); }

        // withTemplates: 틀 상품까지 불러옴 (등록할 때)
        public static Registrar Registrar(string id, AppSettings set, bool withTemplates)
        {
            Registrar r;
            string k = id + (withTemplates ? "T" : "");
            if (cache.TryGetValue(k, out r)) return r;
            var c = Config(id);
            if (c == null || (!c.HasNaver && !c.HasCoupang)) return null;
            r = new Registrar(c.HasNaver ? new NaverClient(c) : null, c.HasCoupang ? new CoupangClient(c) : null);
            if (withTemplates)
            {
                var plan = set.Accounts.First(a => a.Id == id);
                r.LoadTemplates(c.HasNaver ? plan.NaverTemplate : null, c.HasCoupang ? plan.CoupangTemplate : null);
            }
            cache[k] = r;
            return r;
        }

        public static string Mask(string id, string text)
        {
            MarketConfig c;
            return configs.TryGetValue(id, out c) && c != null ? c.Mask(text) : text;
        }

        static string Double(string v)
        {
            var m = Regex.Match(v ?? "", @"^(\d+(?:\.\d+)?)\s*(.*)$");
            if (!m.Success) return v;
            return (double.Parse(m.Groups[1].Value) * 2).ToString("0.##") + m.Groups[2].Value;
        }

        // 계정별로: 네이버 스토어 1개·2개 상품 + 쿠팡 옵션 상품 (failedOnly=true 시 실패한 항목만 재시도)
        public static void Register(Job job, AppSettings set, Action<string> log, bool failedOnly = false)
        {
            var facts = job.OcrText != null ? ProductFacts.Extract(job.OcrText, job.Name, job.Volume) : null;
            if (facts != null) facts.HealthFunctional = job.Health;   // 건강기능식품 여부는 검토 화면의 체크(엠굿 분류 기준)를 따름
            var extras = Pipeline.ExtraFiles(job);
            try { Pipeline.EnsureNaverImages(job); } catch (Exception ex) { log("네이버용 대표이미지(상표 그대로) 만들기 실패, 상표 가린 이미지로 등록: " + ex.Message); }
            bool any = false;

            if (failedOnly)
            {
                // 실패한 메시지만 지우고 성공한 메시지는 보존
                job.Results.RemoveAll(r => r.StartsWith("✖"));
            }
            else
            {
                job.Results.Clear();
                job.Registered_Ids.Clear();
            }

            foreach (var plan in set.Accounts.Where(a => a.Use))
            {
                string a = plan.Id;
                Registrar reg;
                try { reg = Registrar(a, set, true); }
                catch (Exception ex) { job.Results.Add("✖ #" + a + " 틀 상품 불러오기 실패: " + Mask(a, ex.Message)); continue; }
                if (reg == null) { job.Results.Add("✖ #" + a + " 계정 키가 api_keys.json 에 없습니다."); continue; }

                var p = new ProductInput
                {
                    Code = job.Code, Brand = job.Brand ?? "", DetailHtml = job.DetailHtml, Volume = job.Volume, SourceName = job.Name,
                    NaverCategory = job.NaverCat, CoupangCategory = job.CoupangCat, Test = set.NaverPaused,
                    CoupangApprove = set.CoupangApprove, Facts = facts, HealthFunctional = job.Health, Stock = set.Stock,
                };
                p.ExtraImages.AddRange(extras);
                // 각 쿠팡 샵 계정(a = "1", "2", "3")에 맞춰 맞춤형 검색어 20개 선정
                var targetTags = (a == "2" && job.CoupangTags2 != null && job.CoupangTags2.Count > 0) ? job.CoupangTags2
                               : (a == "3" && job.CoupangTags3 != null && job.CoupangTags3.Count > 0) ? job.CoupangTags3
                               : (job.CoupangTags1 != null && job.CoupangTags1.Count > 0) ? job.CoupangTags1
                               : job.CoupangTags;

                p.SearchTags.Clear();
                p.SearchTags.AddRange(targetTags);

                foreach (int n in plan.Counts(plan.NaverCounts).Where(n => n <= 2))
                {
                    string key = "n" + a + "_" + n;
                    string label = "#" + a + " 스토어 " + n + "개";

                    if (failedOnly && job.Registered_Ids.ContainsKey(key)) continue;

                    if (!reg.HasNaverTemplate) { job.Results.Add("✖ " + label + ": 네이버 틀 상품이 없습니다 ([설정] 탭)"); continue; }
                    string name; job.Names.TryGetValue(key, out name);
                    var item = new NaverItem
                    {
                        Name = name, ImageFile = Pipeline.NaverImagePath(job, n), Price = job.Prices[n],
                        SellerCode = n == 1 ? job.Code : job.Code + "x" + n, Amount = n == 1 ? job.Volume : Double(job.Volume), Packs = n
                    };
                    item.ExtraImages.AddRange(extras);
                    // 브랜드·제조사 칸: 이 상품명에 못 넣은 키워드 2개
                    var left = Naming.Leftover(name, job.NameWords, 2);
                    if (left.Count > 0) item.BrandName = left[0];
                    if (left.Count > 1) item.MakerName = left[1];
                    any = true;
                    try
                    {
                        string id = reg.RegisterNaver(p, item, log);
                        job.Registered_Ids[key] = id;
                        job.Results.Add("✔ " + label + " 등록: " + id);
                        if (!string.IsNullOrWhiteSpace(set.SharedFolder))
                            SharedNames.Append(set.SharedFolder, job.Code, plan.BusinessId, name, Regex.Split(name ?? "", @"\s+"));
                    }
                    catch (Exception ex) { job.Results.Add("✖ " + label + " 실패: " + Explain(a, ex)); }
                }

                var counts = plan.Counts(plan.CoupangCounts);
                if (counts.Count > 0 && job.CoupangBanned)
                    job.Results.Add("– #" + a + " 쿠팡: msgood '쿠팡등록금지' 상품이라 올리지 않았습니다 (네이버만 등록)");
                else if (counts.Count > 0)
                {
                    string key = "c" + a;
                    string label = "#" + a + " 쿠팡 " + string.Join("·", counts) + "개";

                    if (failedOnly && job.Registered_Ids.ContainsKey(key)) continue;

                    if (!reg.HasCoupangTemplate) { job.Results.Add("✖ " + label + ": 쿠팡 틀 상품이 없습니다 ([설정] 탭)"); continue; }
                    string cname; job.Names.TryGetValue(key, out cname);
                    p.CoupangName = cname;
                    foreach (int n in counts) p.CoupangOptions.Add(new CoupangOption { Count = n, Price = job.Prices[n], ImageFile = Pipeline.ImagePath(job, "rep", n) });
                    any = true;
                    try
                    {
                        string id = reg.RegisterCoupang(p, null, log);
                        job.Registered_Ids[key] = id;
                        job.Results.Add("✔ " + label + " 등록: " + id);
                        if (!string.IsNullOrWhiteSpace(set.SharedFolder))
                            SharedNames.Append(set.SharedFolder, job.Code, plan.BusinessId, cname, Regex.Split(cname ?? "", @"\s+"));

                        // 마지막 단계: 임시저장된 상품의 상품조회에 쿠팡 검색어가 들어갔는지 확인하고 없으면 넣음
                        var pid = Regex.Match(id ?? "", @"\d{6,}");
                        if (pid.Success && targetTags.Count > 0)
                        {
                            try
                            {
                                int n = reg.EnsureCoupangSearchTags(pid.Value, targetTags, log);
                                job.Results.Add(n > 0 ? "✔ #" + a + " 쿠팡 검색어 " + n + "개 저장" : "✖ #" + a + " 쿠팡 검색어가 저장되지 않았습니다 (윙 상품조회에서 직접 넣어 주세요)");
                            }
                            catch (Exception ex) { job.Results.Add("✖ #" + a + " 쿠팡 검색어 넣기 실패: " + Explain(a, ex)); }
                        }
                    }
                    catch (Exception ex) { job.Results.Add("✖ " + label + " 실패: " + Explain(a, ex)); }
                }
            }
            if (!any && job.Results.Count == 0) job.Results.Add("✖ 등록할 계정이 없습니다. [설정] 탭에서 계정을 켜고 틀 상품을 넣으세요.");
        }

        static string Explain(string id, Exception ex)
        {
            var api = ex as ApiException;
            string body = api != null ? api.Body : ex.Message;
            if (body.Contains("IP_NOT_ALLOWED") || body.Contains("허용되지 않은 IP") || (api != null && api.Status == 403 && body.Contains("IP")))
                body = "이 PC의 인터넷 IP가 등록되어 있지 않습니다. 커머스API센터 / 쿠팡 윙 OPEN API 에서 지금 IP를 추가하세요. " + body;
            body = Mask(id, body);
            return body.Length > 400 ? body.Substring(0, 400) + "…" : body;
        }

        // 프로그램이 만든 초안과 검토 화면에서 고친 결과를 비교해 기억
        public static void Learn(Job job, Learned l)
        {
            var draft = Naming.Words(job.DraftNames.Values);
            var final = Naming.Words(job.Names.Values);
            var tags = new HashSet<string>(job.CoupangTags, StringComparer.OrdinalIgnoreCase);
            var draftTags = new HashSet<string>(job.DraftTags, StringComparer.OrdinalIgnoreCase);
            var notes = new List<string>();
            // 쿠팡 검색어에 직접 넣은 단어 → 다음부터 상품명 대신 쿠팡 검색어로
            foreach (var w in tags.Where(w => !draftTags.Contains(w)))
            {
                string k = w.ToLowerInvariant(); int n; l.TagOnly.TryGetValue(k, out n); l.TagOnly[k] = n + 1;
                l.Preferred.Remove(k); notes.Add("→쿠팡검색어 " + w);
            }
            foreach (var w in draft.Where(w => !final.Contains(w) && !tags.Contains(w)))
            {
                string k = w.ToLowerInvariant(); int n; l.Excluded.TryGetValue(k, out n); l.Excluded[k] = n + 1;
                l.Preferred.Remove(k); notes.Add("-" + w);
            }
            foreach (var w in final.Where(w => !draft.Contains(w)))
            {
                string k = w.ToLowerInvariant(); int n; l.Preferred.TryGetValue(k, out n); l.Preferred[k] = n + 1;
                l.Excluded.Remove(k); notes.Add("+" + w);
            }
            if (!string.IsNullOrEmpty(job.Seed) && job.Seed != job.DraftSeed)
            {
                // 상품명 단어 중 새 시작 단어를 포함하는 단어(없으면 첫 단어)에 연결
                var words = Regex.Split(job.Name ?? "", @"\s+").Where(w => w.Length > 0).ToList();
                var hit = words.FirstOrDefault(w => w.Contains(job.Seed)) ?? words.FirstOrDefault(w => !string.Equals(w, job.Brand, StringComparison.OrdinalIgnoreCase));
                if (hit != null) { l.SeedFor[Regex.Replace(hit, @"[^0-9A-Za-z가-힣]", "").ToLowerInvariant()] = job.Seed; notes.Add("시작단어 " + job.DraftSeed + "→" + job.Seed); }
            }
            if (job.Category != null && job.NaverCat != job.DraftNaverCat && !string.IsNullOrEmpty(job.NaverCat)) { l.NaverCatFor[job.Category] = job.NaverCat; notes.Add("네이버 카테고리 " + job.NaverCat); }
            if (job.Category != null && job.CoupangCat != job.DraftCoupangCat && !string.IsNullOrEmpty(job.CoupangCat)) { l.CoupangCatFor[job.Category] = job.CoupangCat; notes.Add("쿠팡 카테고리 " + job.CoupangCat); }
            if (notes.Count > 0) l.History.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " [" + job.Code + "] " + string.Join(", ", notes));
            if (l.History.Count > 500) l.History.RemoveRange(0, l.History.Count - 500);
        }
    }
}
