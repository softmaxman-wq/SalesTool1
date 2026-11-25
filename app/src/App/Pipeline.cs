using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SmallKeyword;

namespace SalesTool
{
    // 상품번호 하나 → msgood 정보, 시작 단어, 키워드, 상품명, 가격, 대표·추가이미지, 카테고리 초안
    public class Pipeline
    {
        readonly MsgoodWeb msgood;
        readonly PriceCompare shop;
        readonly WebView2 naverView;
        readonly Func<AppSettings> settings;
        readonly Func<Learned> learned;
        readonly Action<string> showTab;
        readonly Random rnd = new Random();

        // 상품명·키워드 규칙을 바꿀 때마다 올림 → 예전 규칙으로 만든 초안은 켤 때 자동으로 다시 만듦
        public const int RulesVersion = 43;

        // 네이버 쇼핑을 실제로 열었을 때만 쉼 (저장된 결과를 다시 쓴 경우는 바로 다음으로)
        Task ShopPause() { return shop != null && shop.LastCached ? Task.FromResult(0) : Task.Delay(2000 + rnd.Next(1500)); }

        // msgood 분류로 카테고리를 고정하는 상품 종류 (유산균: 대표이미지에 가르시니아·바나바잎이 크게 있어도 프로바이오틱스로)
        public static string ForcedCatPath(Job job)
        {
            string m = Regex.Replace(job.Category ?? "", @"\s+", "");
            if (m.EndsWith(">유산균") || m == "유산균") return "식품>건강식품>영양제>프로바이오틱스";
            // msgood이 다른 분류(식이섬유 등)로 넣었어도 상품명에 유산균이 있으면 프로바이오틱스로 (반려동물용은 제외)
            string n = Regex.Replace(job.Name ?? "", @"\s+", "");
            if (Regex.IsMatch(n, "유산균|프로바이오틱스") && !Regex.IsMatch(n + m, "강아지|고양이|반려|애견|애묘|펫")) return "식품>건강식품>영양제>프로바이오틱스";
            return null;
        }
        // 다른 카테고리 단어를 붙일 이 카테고리의 대표 말 (체지방감소 → 체지방감소유산균)
        public static string CategoryCoreWord(Job job)
        {
            if (ForcedCatPath(job) != null) return "유산균";
            string leaf = (job.CatPath ?? job.NaverCatName ?? "").Split('>').Last().Trim();
            return Regex.IsMatch(leaf, "^[가-힣]{2,7}$") ? leaf : null;
        }
        // 이 카테고리 상품인지 보여 주는 말이 들어 있나 (유산균 상품의 시작 단어 확인용)
        static bool HasCoreTerm(Job job, string w)
        {
            if (ForcedCatPath(job) == null) return true;
            return Regex.IsMatch(Regex.Replace(w ?? "", @"\s+", ""), "유산균|바이오틱스|비피더스|락토|프로바이오");
        }

        // 어떤 영양제에나 붙일 수 있는 종류 말 (아연영양제·아연보충제)
        static readonly HashSet<string> GenericType = new HashSet<string> { "영양제", "보충제", "영양보충제", "건강식품", "서플리먼트" };

        public Pipeline(MsgoodWeb msgood, WebView2 naverView, Func<AppSettings> settings, Func<Learned> learned, Action<string> showTab)
        {
            this.msgood = msgood; this.naverView = naverView; this.settings = settings; this.learned = learned; this.showTab = showTab;
            shop = new PriceCompare(naverView);
        }

        const string Surnames = "김이박최정강조윤장임한오서신권황안송류전홍고문양손배백허유남심노하곽성차주우구민진지엄채원천방공현함변염여추도소석선설마길연위표명기반왕금옥육인맹제모탁국어은편용예봉경";

        static string Key(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z가-힣]", "").ToLowerInvariant(); }

        public async Task Draft(Job job, Action<string> step)
        {
            var set = settings(); var learn = learned();
            var keys = SearchAdKeys.Load(Store.Dir);
            if (!keys.Ready) throw new Exception("api_keys.json 에 네이버 검색광고 키(naver_searchad)가 없습니다. [설정] 탭의 [키 파일 열기]로 넣어 주세요.");
            var ad = new SearchAd(keys);

            // 1. msgood4u
            step("1/6 msgood4u 상품 정보·도매가 읽는 중");
            showTab("msgood");
            await msgood.Fill(job, step);
            Store.SaveJob(job);
            await DraftAfterMsgood(job, step);
            job.RulesVersion = RulesVersion;
        }

        // 2~6단계 (msgood 정보가 채워진 뒤)
        public async Task DraftAfterMsgood(Job job, Action<string> step)
        {
            var set = settings(); var learn = learned();
            var ad = new SearchAd(SearchAdKeys.Load(Store.Dir));

            // 상세 페이지 글자부터 읽음: 주 키워드(시작 단어)·키워드·"상품에 있는 말인지" 판단에 씀
            step("2/6 상세 페이지 글자 읽는 중");
            job.OcrText = await Task.Run(() => ProductUploader.Ocr.ReadDetailImages(job.DetailHtml, m => { }));
            job.RepText = await Task.Run(() => ReadRepText(job.MainImageUrl));
            job.RepMismatch = RepOtherProduct(job);
            if (job.RepMismatch != null) job.RepText = null;   // 다른 상품 포장의 건강기능식품 표시로 판단하지 않게
            var facts = ProductUploader.ProductFacts.Extract(job.OcrText, job.Name, job.Volume);
            JudgeHealth(job, facts.HealthFunctional);
            if (job.RepMismatch != null)
                job.HealthNote = ((job.HealthNote ?? "") + " / msgood 대표 사진이 다른 상품이라 상세 페이지의 상품 사진으로 대표이미지를 만듦 — 확인 필요").TrimStart(' ', '/');
            Store.SaveJob(job);

            // 2. 카테고리 먼저 (키워드는 이 네이버 카테고리 안에서 찾음). 다시 만들 때는 예전 카테고리를 버리고 새로 찾음
            job.NaverCat = job.CoupangCat = job.NaverCatName = job.CoupangCatName = null;
            step("2/6 네이버·쿠팡 카테고리 정하는 중");
            await FindCategories(job, step);
            job.DraftNaverCat = job.NaverCat; job.DraftCoupangCat = job.CoupangCat;
            Store.SaveJob(job);

            // 3. 시작 단어
            step("3/6 시작 단어 고르는 중");
            var sc = Naming.SeedCandidates(job.Name, job.Brand);
            var cands = sc.Item1.Concat(sc.Item2).ToList();
            string seed = null;

            // 0순위: 대표이미지와 상품명에서 추출된 핵심 성분/원료 (예: "홍화씨유 캡슐") - 조각 단어("씨유") 배제
            string repQuery0 = BuildRepCategoryQuery(job);
            if (!string.IsNullOrEmpty(repQuery0))
            {
                string targetSeed = repQuery0;
                string core0 = repQuery0.Split(' ')[0];
                if (job.NaverCatName != null && (job.NaverCatName.Contains("기타건강보조식품") || job.NaverCatName.Contains("영양제")) && repQuery0.Contains(" "))
                {
                    targetSeed = repQuery0; // "홍화씨유 캡슐" (단독어 "홍화씨유"는 기타기름/식용유로 분류되므로 캡슐 유지)
                }
                else
                {
                    targetSeed = core0;
                }

                if (targetSeed.Length >= 2 && !Naming.BannedFragments.Contains(targetSeed) && !Naming.BannedFragments.Contains(core0) && Key(job.Name).Contains(Key(core0)))
                {
                    seed = targetSeed;
                    job.SeedReason = "대표이미지·상품명 핵심 성분 (" + targetSeed + ")";
                }
            }

            foreach (var w in Regex.Split(job.Name, @"\s+"))
            {
                if (seed != null) break;
                string s; if (learn.SeedFor.TryGetValue(Key(w), out s)) { seed = s; job.SeedReason = "전에 '" + w + "' 상품에서 고른 단어"; break; }
            }
            Dictionary<string, Volume> seedVols = new Dictionary<string, Volume>();
            if (cands.Count > 0) seedVols = await Task.Run(() => ad.Volumes(cands, (d, n) => { }, () => false));
            // 시작 단어 1순위: 상품 종류 단어(상품명 맨 끝, 젖산마그네슘·오트밀)가 검색량 1,000 이상이면 그 단어
            // 아니면 그 단어의 실제로 검색되는 뒷부분(흑삼고)으로 검색한 상품명에 많이 나온 부분 중 검색량 많은 것 (발효흑삼고 → 흑삼)
            if (seed == null && job.DetailMain != null)
            {
                double sh = job.CatTitles.Count == 0 ? 1 : job.CatTitles.Count(t => Key(t).Contains(Key(job.DetailMain))) / (double)job.CatTitles.Count;
                if (sh >= 0.5) { seed = job.DetailMain; job.SeedReason = "상세 페이지 주 키워드 (" + Naming.Occ(job.OcrText, seed) + "번 나옴)"; }
            }
            if (seed == null && job.NaverCatName != null)
            {
                string leafName = job.NaverCatName.Split('>').Last().Trim();
                // 상세 글자 인식이 나빠도(멜라토닌 → 벨라토닌·엘라토닌) msgood 상품명에 카테고리 이름이 있으면 인정 (식물성멜라토닌함유멜라나잇)
                if (Regex.IsMatch(leafName, "^[가-힣]{2,}$") && (Naming.Occ(job.OcrText, leafName) >= 3 || (leafName.Length >= 3 && Key(job.Name).Contains(Key(leafName)))))
                {
                    var lv = await Task.Run(() => ad.Volumes(new List<string> { leafName }, (d, n) => { }, () => false));
                    if (lv.ContainsKey(leafName) && lv[leafName].Total >= 1000) { seed = leafName; job.SeedReason = "카테고리 이름이 상세 페이지에 " + Naming.Occ(job.OcrText, leafName) + "번 나옴"; }
                }
            }
            if (seed == null && job.TypeWord != null)
            {
                string T = job.TypeWord;
                var need = new[] { T, job.TypeSuffix }.Where(x => x != null && !seedVols.ContainsKey(x)).ToList();
                if (need.Count > 0) foreach (var kv in await Task.Run(() => ad.Volumes(need, (d, n) => { }, () => false))) seedVols[kv.Key] = kv.Value;
                Volume tv;
                int occT = Naming.Occ(job.OcrText, T);
                bool ocrOk = string.IsNullOrEmpty(job.OcrText) || occT >= 3;
                // 상세 페이지 주 키워드: 상품 종류 단어와 그 부분 중 검색량 1,000 이상 + 네이버 쇼핑 검색 결과 상품명 70% 이상에 나오는 말 가운데
                // 상세 페이지에 가장 많이 나온 것 (같으면 긴 것)
                Func<string, double> share = w => job.CatTitles.Count == 0 ? 1 : job.CatTitles.Count(t => Key(t).Contains(Key(w))) / (double)job.CatTitles.Count;
                // 한국어 상품 이름은 뒤쪽이 상품(흑삼·마그네슘·유산균), 맨 앞은 꾸밈말(발효·젖산·슬림)이라 맨 앞부분만으로 된 말은 뺌
                string tk = Key(T);
                // 추출·추출물·분말 같은 만드는 방법 말만으로는 상품 이름이 안 됨 (와사비잎추출물 → 추출 X)
                var family = cands.Where(c => { string ck = Key(c); int at = tk.IndexOf(ck, StringComparison.Ordinal); return (at > 0 || ck == tk) && !Regex.IsMatch(ck, "^(추출|추출물|출물|분말|농축|농축액|원액|엑기스|가루|추출분말|물)$"); })
                                  .Concat(new[] { T, job.TypeSuffix }).Where(x => x != null).Distinct().ToList();
                var ok = string.IsNullOrEmpty(job.OcrText) ? new List<string>()
                    : family.Where(c => seedVols.ContainsKey(c) && seedVols[c].Total >= 1000 && share(c) >= 0.7 && Naming.Occ(job.OcrText, c) >= 3).ToList();
                string main = ok.OrderByDescending(c => Naming.Occ(job.OcrText, c)).ThenByDescending(c => c.Length).FirstOrDefault();
                // 더 긴 말이 거의 그만큼 나오면 긴 말로 (유산 37번 → 유산균 35번)
                for (bool changed = main != null; changed; )
                {
                    changed = false;
                    var longer = ok.Where(c => c.Length > main.Length && Key(c).Contains(Key(main)) && Naming.Occ(job.OcrText, c) >= Naming.Occ(job.OcrText, main) * 0.6)
                                   .OrderByDescending(c => Naming.Occ(job.OcrText, c)).FirstOrDefault();
                    if (longer != null) { main = longer; changed = true; }
                }
                if (seedVols.TryGetValue(T, out tv) && tv.Total >= 1000 && ocrOk)
                    { seed = T; job.SeedReason = "상품 종류 단어 (월간검색량 " + tv.Total.ToString("#,0") + (occT > 0 ? ", 상세 페이지에 " + occT + "번" : "") + ")"; }
                else if (main != null)
                    { seed = main; job.SeedReason = "상세 페이지 주 키워드 (" + Naming.Occ(job.OcrText, main) + "번 나옴, 월간검색량 " + seedVols[main].Total.ToString("#,0") + ")"; }
                else if (job.TypeSuffix != null)
                {
                    var titles = job.SuffixTitles.Count > 0 ? job.SuffixTitles : job.CatTitles;
                    var fam = cands.Where(c => Key(T).Contains(Key(c))).Concat(new[] { job.TypeSuffix }).Distinct().ToList();
                    Func<string, int> inT = w => titles.Count(t => Key(t).Contains(Key(w)));
                    int topT = fam.Max(inT);
                    var pool = fam.Where(c => inT(c) >= Math.Max(3, topT * 0.7)).ToList();
                    var pick = pool.Where(seedVols.ContainsKey).OrderByDescending(c => seedVols[c].Total).FirstOrDefault() ?? job.TypeSuffix;
                    seed = pick;
                    Volume pv; seedVols.TryGetValue(pick, out pv);
                    job.SeedReason = "상품 종류 단어 '" + T + "'의 핵심 (" + job.TypeSuffix + "로 검색한 상품명에 많이 나옴" + (pv != null ? ", 월간검색량 " + pv.Total.ToString("#,0") : "") + ")";
                }
            }
            if (seed == null)
            {
                // 상품명 단어 그대로가 검색량 1,000 이상이면 그 단어, 아니면 붙은 말의 부분까지 보고 검색량이 가장 많은 단어
                // 네이버 쇼핑에서 이 상품을 검색했을 때 상품명에 자주 나온 후보만 (발효처럼 흔한 말보다 흑삼처럼 이 상품을 나타내는 말)
                Func<string, int> inTitles = w => job.CatTitles.Count(t => Key(t).Contains(Key(w)));
                int top = cands.Count == 0 ? 0 : cands.Max(inTitles);
                var pool = top >= 3 ? cands.Where(w => inTitles(w) >= Math.Max(3, top * 0.7)).ToList() : cands;
                var fulls = sc.Item1.Where(pool.Contains).ToList();
                var bestFull = fulls.Where(seedVols.ContainsKey).OrderByDescending(c => seedVols[c].Total).FirstOrDefault();
                // 검색량이 있으면 그중 많은 것, 없으면 검색 결과 상품명에 가장 많이 나온 것 (발효처럼 흔한 말로 돌아가지 않게)
                var best = bestFull != null && seedVols[bestFull].Total >= 1000 ? bestFull
                    : pool.Where(seedVols.ContainsKey).OrderByDescending(c => seedVols[c].Total).FirstOrDefault()
                      ?? (top >= 3 ? pool.OrderByDescending(inTitles).ThenByDescending(c => c.Length).FirstOrDefault() : null)
                      ?? cands.Where(seedVols.ContainsKey).OrderByDescending(c => seedVols[c].Total).FirstOrDefault();
                if (best != null) { seed = best; job.SeedReason = seedVols.ContainsKey(best) ? "상품명 단어 중 월간검색량이 가장 많음 (" + seedVols[best].Total.ToString("#,0") + ")" : "검색 결과 상품명에 가장 많이 나온 단어"; }
                else if (sc.Item1.Count > 0) { seed = sc.Item1.OrderByDescending(c => c.Length).First(); job.SeedReason = "검색량을 찾지 못해 가장 긴 단어"; }
                else throw new Exception("상품명에서 시작 단어를 찾지 못했습니다: " + job.Name);
            }
            // 제형(캡슐/정/환/스틱 등) 단독어 또는 금지 조각어("씨유" 등)가 시작단어로 잡혔을 경우 핵심 성분명으로 자동 대체
            Func<string, bool> isBareForm = w => Regex.IsMatch(w ?? "", @"^(캡슐|식물성캡슐|연질캡슐|소프트겔|정|타블렛|환|스틱|분말|가루|알약)$")
                                              || Naming.BannedFragments.Contains(w ?? "");
            if (seed != null && isBareForm(seed))
            {
                var altSeed = cands.Concat(sc.Item1).Where(w => !isBareForm(w) && w.Length >= 2 && !Naming.BannedFragments.Contains(w) && Naming.IsCandidate(w))
                                   .OrderByDescending(w => seedVols.ContainsKey(w) ? seedVols[w].Total : 0).FirstOrDefault();
                if (altSeed != null) { seed = altSeed; job.SeedReason = "부적합 단어 대신 핵심 원료/성분 단어 선정 (" + altSeed + ")"; }
            }
            // 잘린 조각(슬림다·혈당케·다이어트엔·유산균의·폴리시)이 시작 단어가 되지 않게: 앞 단어가 실제로 검색되는 말인지 확인
            if (!string.IsNullOrEmpty(seed))
            {
                string head = seed.Split(' ')[0];
                Volume hv;
                if (!seedVols.TryGetValue(head, out hv))
                {
                    try { var got = await Task.Run(() => ad.Volumes(new List<string> { head }, (d, n) => { }, () => false)); if (got.ContainsKey(head)) { hv = got[head]; seedVols[head] = hv; } } catch { }
                }
                if (hv == null || hv.Total < 100)
                {
                    string alt = null;
                    if (ForcedCatPath(job) != null) alt = "유산균";
                    else
                    {
                        string leafName = (job.NaverCatName ?? job.CatPath ?? "").Split('>').Last().Trim();
                        if (leafName.Length >= 2 && Key(job.Name).Contains(Key(leafName))) alt = leafName;
                        else alt = cands.Concat(sc.Item1).Where(w => w != seed && w.Length >= 2 && !isBareForm(w) && Naming.IsCandidate(w) && seedVols.ContainsKey(w) && seedVols[w].Total >= 100)
                                        .OrderByDescending(w => seedVols[w].Total).FirstOrDefault();
                    }
                    if (alt != null) { job.SeedReason = "'" + head + "'은 검색량이 거의 없는 조각이라 '" + alt + "'(으)로 바꿈"; seed = seed.Contains(" ") ? alt + seed.Substring(seed.IndexOf(' ')) : alt; }
                }
            }
            // 유산균 상품은 시작 단어에 유산균·프로바이오틱스 같은 말이 있어야 함 (다이어트·가르시니아·인지력케어로 시작하면 그 카테고리 상품명이 됨)
            if (!string.IsNullOrEmpty(seed) && !HasCoreTerm(job, seed))
            {
                job.SeedReason = "msgood 분류가 유산균이라 '" + seed + "' 대신 유산균으로 시작";
                seed = seed.Contains(" ") ? "유산균" + seed.Substring(seed.IndexOf(' ')) : "유산균";
            }
            if (string.IsNullOrEmpty(seed) || Naming.BannedFragments.Contains(seed))
            {
                seed = ForcedCatPath(job) != null ? "유산균" : (sc.Item1.OrderByDescending(c => c.Length).FirstOrDefault() ?? Naming.CoreName(job.Name, job.Brand));
                job.SeedReason = "시작 단어를 찾지 못해 상품명 핵심 단어 사용";
            }
            job.Seed = seed; job.DraftSeed = seed;

            // 3. 키워드 (카테고리 안에서)
            await FindKeywords(job, ad, step);

            // 4. 상품명·가격
            step("5/6 상품명·가격 만드는 중");
            MakeNames(job);
            try { await FillNameTotals(job, step); } catch { }
            job.DraftNames = new Dictionary<string, string>(job.Names);
            try { await MakeTags(job, step); }
            catch (Exception ex) { job.CoupangTags = new List<string>(); job.Results.Add("쿠팡 검색어를 찾지 못했습니다: " + ex.Message); }
            job.DraftTags = new List<string>(job.CoupangTags);
            MakePrices(job, set);
            Store.SaveJob(job);

            // 5. 이미지 + 상세 글자 읽기
            step("6/6 대표이미지·추가이미지 만드는 중 (1~2분)");
            await Task.Run(() => MakeImages(job, step));
        }

        // 가격비교 1페이지 → 후보 단어 → 월간검색량 → 브랜드·인물 제외 → 상품총수량
        public async Task FindKeywords(Job job, SearchAd ad, Action<string> step)
        {
            var set = settings(); var learn = learned();
            if (ad == null)
            {
                var keys = SearchAdKeys.Load(Store.Dir);
                if (!keys.Ready) throw new Exception("api_keys.json 에 네이버 검색광고 키가 없습니다.");
                ad = new SearchAd(keys);
            }
            string seed = job.Seed;
            string cat = job.NaverCat;   // 이 카테고리 안에서만 (예: 오트밀 카테고리에서 "오버나이트" → 생리대가 아닌 오트밀 상품)
            step("3/6 '" + seed + "' 네이버 쇼핑 1~3페이지 읽는 중" + (cat != null ? " (" + (job.NaverCatName ?? cat) + " 카테고리)" : ""));
            showTab("naver");
            await shop.Init();

            // 1~3페이지 상품명 수집 (같은 카테고리의 실제 상위 노출 상품명에서 키워드를 추출하여 카테고리 일치율 극대화)
            var titles = new List<string>();
            var allBrands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SmallKeyword.PageResult page1 = null;

            for (int pIdx = 1; pIdx <= 3; pIdx++)
            {
                try
                {
                    step($"3/6 '{seed}' 네이버 쇼핑 {pIdx}페이지 상품명 수집 중...");
                    var p = await shop.Read(seed, true, step, () => false, cat, false, pIdx);
                    if (p != null)
                    {
                        if (pIdx == 1) page1 = p;
                        for (int i = 0; i < p.Titles.Count; i++)
                        {
                            if (!p.Ads[i]) titles.Add(p.Titles[i]);
                        }
                        foreach (var b in p.Brands) allBrands.Add(b);
                        if (p.Total <= titles.Count || p.Titles.Count < 10) break; // 더 이상 없으면 중단
                    }
                }
                catch (NaverBlocked) { throw; }
                catch { }
                await Task.Delay(1500 + rnd.Next(1000));
            }

            long totalCount = page1 != null ? page1.Total : 0;
            var rows = new List<KwRow> { new KwRow { Word = seed, Total = totalCount, InPage = titles.Count(t => t.Replace(" ", "").IndexOf(seed, StringComparison.OrdinalIgnoreCase) >= 0) } };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SearchAd.Clean(seed) };

            // 1~3페이지 상품명에서 등장 빈도순으로 키워드 후보 1차 추출 (동일 카테고리 적합도 최고)
            foreach (var k in Keywords.Count(titles))
            {
                string cleanWord = SearchAd.Clean(k.Word);
                if (Naming.IsCandidate(k.Word) && !Naming.BannedFragments.Contains(cleanWord) && seen.Add(cleanWord))
                {
                    rows.Add(new KwRow { Word = cleanWord, InPage = k.Products });
                }
            }

            // 시작 단어의 흔한 오타도 후보로 (검색량이 있으면 상품명에 넣음: 젓산마그네슘·젓산마그네숨)
            foreach (var t in Naming.TypoVariants(seed))
                if (seen.Add(SearchAd.Clean(t))) rows.Add(new KwRow { Word = SearchAd.Clean(t), InPage = 0, Typo = true });

            // 키워드는 이 카테고리 상품명에서만 모음. 상세 페이지는 거르는 데만 씀 (상세의 안내 문구·주소·회사 이름이 키워드가 되지 않게)
            foreach (var d in Naming.DetailWords(job.OcrText))
            {
                var ex = rows.FirstOrDefault(r => string.Equals(SearchAd.Clean(r.Word), SearchAd.Clean(d.Item1), StringComparison.OrdinalIgnoreCase));
                if (ex != null) { ex.FromDetail = true; ex.DetailCount = d.Item2; }
                // 단, 상세 페이지의 핵심(주 키워드의 절반 이상 나온 말: 체지방·장건강·다이어트)은 상품명에 없더라도 후보로
                // (기능·효과를 말하는 말만: 메일·시간·방법·가족 같은 일반 단어는 아님)
                else if (Naming.IsFunctionWord(d.Item1) && seen.Add(SearchAd.Clean(d.Item1)))
                    rows.Add(new KwRow { Word = SearchAd.Clean(d.Item1), InPage = 0, FromDetail = true, DetailCount = d.Item2 });
            }

            // 상세 페이지 검증: 상세(또는 msgood 상품명)에 나온 말인지 먼저 표시 → 나온 말만 네이버에서 확인 (빠르게)
            int seedOccF = Math.Max(3, Naming.Occ(job.OcrText, seed));
            var companies = Naming.CompanyWords(job.OcrText);
            foreach (var k in rows.Skip(1))
            {
                k.Mentioned = Key(k.Word).Length >= 2 && Naming.StrictMention(job.Name, job.OcrText, k.Word);
                k.Core = !string.IsNullOrEmpty(job.OcrText) && Naming.IsCore(job.OcrText, k.Word, seedOccF);
                k.InName = Key(k.Word).Length >= 2 && Key(job.Name).Contains(Key(k.Word));
                k.Function = k.Mentioned && Naming.IsFunctionWord(k.Word);
                // 상세의 제조원·판매원 등에 적힌 회사 이름(네이처프라임 등)은 브랜드
                if (companies.Any(c => Key(k.Word) == c || (c.Length >= 3 && Key(k.Word).Contains(c)))) k.Excluded = "브랜드";
            }

            step("3/6 월간검색량 조회 중 (" + rows.Count + "개)");
            var vols = await Task.Run(() => ad.Volumes(rows.Select(r => r.Word).ToList(), (d, n) => step("3/6 월간검색량 조회 중... " + d + "/" + n), () => false));
            foreach (var r in rows) { Volume v; if (vols.TryGetValue(r.Word, out v)) r.Volume = v.Total; }

            // 브랜드: 가격비교 필터의 브랜드·제조사 + msgood 브랜드. 학습: 전에 상품명에서 뺀 단어
            var brands = allBrands.Select(Key).Where(b => b.Length >= 2).ToList();
            if (Key(job.Brand).Length >= 2) brands.Add(Key(job.Brand));
            {
                // 상품명 첫 단어(동의삼·뉴일리 등)가 대개 만든 곳 (msgood 브랜드 칸이 틀린 경우도 있음)
                var ws = Regex.Split((job.Name ?? "").Trim(), @"\s+");
                if (FirstIsMaker(job) && !Key(ws[0]).Contains(Key(seed))) brands.Add(Key(ws[0]));
            }
            // 상품 이름표(브레인레스큐·허니드)와 그 앞 조각(브레인)
            var nameBrands = NameBrandTokens(job, seed, rows);
            foreach (var r in rows.Skip(1))
            {
                string k = Key(r.Word);
                if (learn.IsPreferred(r.Word)) continue;   // 직접 상품명에 넣기로 한 말 (브랜드 아님: 오나오)
                if (brands.Any(b => k == b || k.Contains(b)) || Naming.IsKnownBrand(r.Word) || IsBrandPiece(r.Word, nameBrands)) r.Excluded = "브랜드";
                else if (learn.IsBrand(r.Word)) r.Excluded = "브랜드(학습)";
                else if (learn.IsExcluded(r.Word)) r.Excluded = "전에 뺀 단어";
            }

            // 상품총수량 + 카테고리 확인: 상품명에 들어갈 만한 단어(1페이지 2번 이상 또는 상세 페이지에서 찾은 말)를 하나씩 네이버 쇼핑에서 검색
            //  - 상품총수량: 네이버 쇼핑 전체 기준 (원래 소형키워드 규칙)
            //  - 카테고리: 검색 결과 상품들이 이 상품 카테고리인지 (비타민D → 비타민D 카테고리라서 프로바이오틱스 상품명에는 안 씀)
            string leaf = job.CatPath != null ? job.CatPath.Split('>').Last() : null;
            // 같은 카테고리로 보는 범위: 끝 이름의 바로 위 단계까지 (식품>건강식품>건강즙/과일즙 … 레몬즙·생강즙·기타건강즙 모두 같은 쪽)
            var segs = job.CatPath != null ? job.CatPath.Split('>') : new string[0];
            string parent = segs.Length >= 3 ? string.Join(">", segs.Take(segs.Length - 1)) : job.CatPath;
            // 영양제 아래는 종류마다 다른 상품 (오메가3·비타민D·감마리놀렌산·프로바이오틱스) → 끝 이름이 같아야 같은 카테고리
            if (parent != null && (parent.EndsWith(">영양제") || parent.EndsWith(">건강식품"))) parent = null;
            // 검색 결과 상품 중 이 상품 카테고리인 비율 (-1 = 판단할 만큼 안 나옴)
            Func<SmallKeyword.PageResult, double> share = p =>
            {
                var cats = p.Categories.Where(c => c.Length > 0).ToList();
                return cats.Count >= 3 ? cats.Count(c => c.Split('>').Last() == leaf || (parent != null && c.StartsWith(parent + ">"))) / (double)cats.Count : -1;
            };
            var todo = rows.Skip(1).Where(r => r.Excluded == null && r.Volume >= Naming.MinVolume && (r.Mentioned || r.Typo) && Naming.IsCandidate(r.Word))
                           .OrderByDescending(r => r.Typo).ThenByDescending(r => r.Volume).Take(15).ToList();
            for (int i = 0; i < todo.Count; i++)
            {
                step("3/6 상품총수량·카테고리 확인 중... " + (i + 1) + "/" + todo.Count + " (" + todo[i].Word + ")");
                try
                {
                    var p = await shop.Read(todo[i].Word, false, step, () => false, null, leaf != null);
                    if (p != null)
                    {
                        todo[i].Total = p.Total;
                        // 그 단어로 검색했을 때 브랜드 목록에 그 단어가 있으면 다른 회사 제품·브랜드 이름 (덴티백·코어맥스 등)
                        string wk = Key(todo[i].Word);
                        if (p.Brands.Any(b => Key(b) == wk))
                            todo[i].Excluded = "브랜드";
                        if (leaf != null)
                        {
                            todo[i].CatChecked = true;
                            todo[i].CatShare = share(p);
                        }
                    }
                }
                catch (NaverBlocked) { throw; }
                catch { }
                await ShopPause();
            }

            // 다른 카테고리 단어(체지방감소·가르시니아·바나바잎·포스파티딜세린)는 단독으로 쓰지 않고, 이 카테고리 대표 말을 붙인 조합어
            // (체지방감소유산균·다이어트유산균·혈당유산균)가 실제로 검색되고 검색 결과가 이 카테고리면 그 조합어로 바꿔 씀
            string coreWord = CategoryCoreWord(job);
            if (leaf != null && coreWord != null)
            {
                bool forced = ForcedCatPath(job) != null;
                // 핵심 성분·상품명 말인데 카테고리 확인을 안 한 말(체지방감소 등)도 확인 (저장된 조회 결과가 있으면 네이버를 다시 열지 않음)
                if (forced)
                {
                    var unchecked0 = rows.Skip(1).Where(r => r.Excluded == null && !r.CatChecked && (r.Core || r.InName || r.Mentioned) && r.Volume >= Naming.MinVolume
                                                          && Naming.IsCandidate(r.Word) && !Key(r.Word).Contains(Key(coreWord)) && !HasCoreTerm(job, r.Word) && !Key(seed).Contains(Key(r.Word)))
                                         .OrderByDescending(r => r.Core).ThenByDescending(r => r.Volume).Take(6).ToList();
                    foreach (var r in unchecked0)
                    {
                        step("3/6 카테고리 확인 중... (" + r.Word + ")");
                        try
                        {
                            var p = await shop.Read(r.Word, false, step, () => false, null, true);
                            if (p != null) { r.CatChecked = true; r.CatShare = share(p); if (r.Total < 0) r.Total = p.Total; }
                        }
                        catch (NaverBlocked) { throw; }
                        catch { }
                        await ShopPause();
                    }
                }
                var others =rows.Skip(1).Where(r => r.Excluded == null && (forced || !r.Core) && r.CatChecked && r.CatShare >= 0 && r.CatShare < 0.5 && (r.Mentioned || r.InName)
                                                  && Key(r.Word).Length >= 2 && !Key(r.Word).Contains(Key(coreWord)) && !Key(seed).Contains(Key(r.Word)))
                                     .OrderByDescending(r => r.Volume).Take(8).ToList();
                var combos = others.Select(r => Key(r.Word) + coreWord).Distinct().ToList();
                Dictionary<string, Volume> cv = new Dictionary<string, Volume>();
                if (combos.Count > 0) try { cv = await Task.Run(() => ad.Volumes(combos, (d, n) => { }, () => false)); } catch { }
                foreach (var r in others)
                {
                    string combo = Key(r.Word) + coreWord;
                    // 원래 단어는 단독으로 상품명·검색어에 안 씀
                    r.Excluded = "다른 카테고리 (" + (int)Math.Round(r.CatShare * 100) + "%)";
                    Volume v;
                    if (!cv.TryGetValue(combo, out v) || v.Total < Naming.RestMinVolume || rows.Any(x => Key(x.Word) == Key(combo))) continue;
                    step("3/6 조합어 카테고리 확인 중... (" + combo + ")");
                    try
                    {
                        var p = await shop.Read(combo, false, step, () => false, null, true);
                        double s = p != null ? share(p) : -1;
                        if (p != null && s >= 0.5)
                        {
                            rows.Add(new KwRow { Word = combo, Volume = v.Total, Total = p.Total, InPage = r.InPage, Mentioned = true, CatChecked = true, CatShare = s });
                            r.Excluded = "다른 카테고리 → " + combo;
                        }
                    }
                    catch (NaverBlocked) { throw; }
                    catch { }
                    await ShopPause();
                }
            }

            // 인물명: 상품명에 들어갈 만한 단어만 네이버 통합검색에서 프로필이 나오는지 확인
            var likely = Naming.Ordered(rows.Skip(1), job.OrderMode ?? set.OrderMode, set.MaxRatio, learn).Take(18).ToList();
            for (int i = 0; i < likely.Count; i++)
            {
                // 사람 이름은 한글 2~4자 + 흔한 성씨로 시작 (스피드·맥스 같은 말은 확인 안 함)
                if (!Regex.IsMatch(likely[i].Word, "^[가-힣]{2,4}$") || Surnames.IndexOf(likely[i].Word[0]) < 0 || Naming.IsRisky(likely[i].Word)) continue;
                step("3/6 인물 이름인지 확인 중... (" + likely[i].Word + ")");
                if (await IsPerson(likely[i].Word)) likely[i].Excluded = "인물";
                await Task.Delay(500 + rnd.Next(500));
            }
            // 상품명을 채울 연관 키워드 (시작 단어가 들어간 것, 예: 오버나이트오트밀·귀리오트밀)
            var fullRel = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // 시작 단어(레몬생강즙)와 그 짧은 형태(생강즙)의 연관 키워드 — 시작 단어로는 연관 키워드가 거의 없을 때가 있음
                var bases = new List<string> { seed };
                // 시작 단어에 제형이 붙어 있으면(홍화씨유 캡슐) 앞 단어(홍화씨유)로도 찾음 — 붙은 채로는 연관 키워드가 거의 안 나옴
                if (seed.Contains(" ")) { string head0 = seed.Split(' ')[0]; if (head0.Length >= 2) bases.Add(head0); }
                // 유산균 상품은 '유산균'으로도 (시작 단어가 슈퍼프로바이오틱스처럼 좁으면 연관 키워드가 적음)
                string catWord = CategoryCoreWord(job);
                if (ForcedCatPath(job) != null && catWord != null && !bases.Any(x => Key(x) == Key(catWord))) bases.Add(catWord);
                if (job.TypeSuffix != null && !string.Equals(job.TypeSuffix, seed, StringComparison.OrdinalIgnoreCase) && Key(seed).Contains(Key(job.TypeSuffix))) bases.Add(job.TypeSuffix);
                var all = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in bases)
                    foreach (var kv in await Task.Run(() => ad.Related(b)))
                        if ((fullRel[kv.Key] = kv.Value) >= Naming.MinVolume && Key(kv.Key).Contains(Key(b)) && Key(kv.Key) != Key(b) && !all.ContainsKey(kv.Key)) all[kv.Key] = kv.Value;
                job.Related = all.OrderByDescending(kv => kv.Value).Take(100).Select(kv => new KwRow { Word = kv.Key, Volume = kv.Value }).ToList();
            }
            catch { job.Related = new List<KwRow>(); }

            // 상세에 나온 기능 말(면역력·눈피로·집중력)의 연관 검색어도 (고민 키워드를 찾으려고)
            foreach (var f in rows.Skip(1).Where(r => r.Excluded == null && (r.Function || r.Core) && r.Mentioned).OrderByDescending(r => r.Volume).Take(2).ToList())
                try { foreach (var kv in await Task.Run(() => ad.Related(f.Word))) if (!fullRel.ContainsKey(kv.Key)) fullRel[kv.Key] = kv.Value; } catch { }
            // 상품 종류 말(달맞이꽃종자유)의 연관 검색어도 (PMS영양제·생리전증후군 같은 이 카테고리 고민 키워드)
            if (!string.IsNullOrEmpty(job.TypeWord) && Key(job.TypeWord) != Key(seed))
                try { foreach (var kv in await Task.Run(() => ad.Related(job.TypeWord))) if (!fullRel.ContainsKey(kv.Key)) fullRel[kv.Key] = kv.Value; } catch { }

            // 연관 검색어 중 시작 단어 오타 (포스타딜세린·마그네시움·루데인): 네이버가 시작 단어로 고쳐서 보여 주면(상품수가 비슷) 오타
            var nearTypos = fullRel.Where(kv => kv.Value >= Naming.MinVolume && Naming.IsNearTypo(kv.Key, seed) && seen.Add(SearchAd.Clean(kv.Key)))
                                   .OrderByDescending(kv => kv.Value).Take(5).ToList();
            // 고민·상황·대상 키워드 후보 (이 상품의 시작 단어나 상세에 나온 말이 들어간 것, 또는 대상+영양제)
            var mentionedKeys = rows.Skip(1).Where(r => r.Excluded == null && r.Mentioned && Key(r.Word).Length >= 2 && !GenericType.Contains(Key(r.Word))).Select(r => Key(r.Word)).ToList();
            mentionedKeys.Add(Key(seed));
            bool supplement = (job.CatPath ?? "").Contains("건강식품") || (job.CatPath ?? "").Contains("영양제");
            var concernCands = fullRel.Where(kv => kv.Value >= Naming.MinVolume && kv.Value <= 50000
                                     && ((Naming.IsConcern(kv.Key) && mentionedKeys.Any(m => Key(kv.Key).Contains(m))) || (supplement && Naming.IsAudienceSupplement(kv.Key))
                                         || Naming.IsMentionedNeed(kv.Key, job.OcrText))
                                     && !learn.IsBrand(kv.Key) && !learn.IsExcluded(kv.Key) && !Naming.IsKnownBrand(kv.Key)
                                     && Naming.TagFilterReason(kv.Key, seed, job.Name, job.OcrText) == null
                                     // 다른 카테고리로 확인된 말이 들어간 고민 키워드는 뺌 (오메가3 상품의 비타민D부족증상)
                                     && !rows.Any(r => r.OtherCategory && !r.InName && Key(r.Word).Length >= 2 && Key(kv.Key).Contains(Key(r.Word)) && !Key(seed).Contains(Key(r.Word))))
                                  .OrderByDescending(kv => kv.Value).Take(6).ToList();
            job.Concern = new List<KwRow>();
            if (nearTypos.Count + concernCands.Count > 0)
            {
                long seedAll = -1;
                try { var sp = await shop.Read(seed, false, step, () => false, null, false); if (sp != null) seedAll = sp.Total; } catch (NaverBlocked) { throw; } catch { }
                await ShopPause();
                foreach (var kv in nearTypos)
                {
                    step("3/6 오타 확인 중... (" + kv.Key + ")");
                    try
                    {
                        var p = await shop.Read(kv.Key, false, step, () => false, null, false);
                        // 네이버가 시작 단어로 고쳐 보여 주면 상품수가 거의 같음 (다른 말이면 크게 다름)
                        if (p != null && seedAll > 0 && p.Total >= seedAll * 0.6 && p.Total <= seedAll * 1.6)
                            rows.Add(new KwRow { Word = SearchAd.Clean(kv.Key), Volume = kv.Value, Total = p.Total, InPage = 0, Typo = true });
                    }
                    catch (NaverBlocked) { throw; }
                    catch { }
                    await ShopPause();
                }
                foreach (var kv in concernCands)
                {
                    step("3/6 고민 키워드 상품수 확인 중... (" + kv.Key + ")");
                    try
                    {
                        // 상품수와 함께 카테고리도 확인: 그 말로 찾은 상품이 이 상품 카테고리가 아니면 안 씀 (감마리놀렌산의 콜레스테롤낮추는방법)
                        var p = await shop.Read(kv.Key, false, step, () => false, null, leaf != null);
                        if (p != null) job.Concern.Add(new KwRow { Word = SearchAd.Clean(kv.Key), Volume = kv.Value, Total = p.Total, CatChecked = leaf != null, CatShare = leaf != null ? share(p) : -1 });
                    }
                    catch (NaverBlocked) { throw; }
                    catch { }
                    await ShopPause();
                }
            }

            // 시작 단어의 오타(젓산마그네숨): 오타로 찾는 사람이 많고 경쟁 상품이 적어서 상품명에 꼭 넣음
            string sk0 = Key(seed);
            foreach (var r in rows.Skip(1).Where(r => r.Excluded == null))
            {
                string k = Key(r.Word);
                if (Naming.IsTypoOf(r.Word, seed)) r.Typo = true;
            }
            // 다른 회사 상품 이름: 시작 단어와 앞 두 글자가 같은데 오타가 아니고 시작 단어도 안 들어간 말 (멜라토닌 → 멜라드림·멜라바인)
            if (sk0.Length >= 3)
                foreach (var r in rows.Skip(1).Where(r => r.Excluded == null && !r.Typo))
                {
                    string k = Key(r.Word);
                    if (k.Length >= 3 && k.StartsWith(sk0.Substring(0, 2)) && !k.Contains(sk0) && !sk0.Contains(k) && r.InPage <= 2 && Naming.Occ(job.OcrText, r.Word) == 0) r.Excluded = "다른 상품 이름";
                }
            // 검색량이 거의 없는 오타는 버림
            rows.RemoveAll(r => r.Typo && r.InPage == 0 && r.Volume < Naming.MinVolume);

            // 브랜드·인물 이름이 들어간 단어(이시형박사, 닥터린마그네슘 등)도 뺌
            var bad = rows.Where(r => r.Excluded == "브랜드" || r.Excluded == "인물").ToList();
            foreach (var r in rows.Skip(1).Where(r => r.Excluded == null))
            {
                var hit = bad.FirstOrDefault(b => Key(r.Word).Contains(Key(b.Word)));
                if (hit != null) r.Excluded = hit.Excluded;
            }
            // 최근 검색 흐름 (검색어 트렌드 키가 있을 때만): 상품명에 들어갈 만한 말 중 최근 확 늘어난 말을 앞으로
            await FillTrends(rows.Skip(1).Where(r => r.Excluded == null && r.Volume >= 300 && (r.Mentioned || r.Typo)).OrderByDescending(r => r.Volume).Take(40).ToList(), step);
            job.Keywords = rows;
            showTab("review");
        }

        // 포장의 건강기능식품 표시 글자 (글자 인식이 '칸강기능식등'·'건강기능식풍'처럼 읽어도)
        static readonly Regex HfMark = new Regex(@"강\s*기\s*능\s*식\s*[품풍픔등]");
        // "건강기능식품이 아닙니다" (일반식품 상세에 흔함)
        static readonly Regex HfNot = new Regex(@"건\s*강\s*기\s*능\s*식\s*품\s*(이|은|가)?\s*(아\s*닙|아\s*니|아\s*님|X)");
        // 상세 이미지에서 건강기능식품 표시로 볼 수 있는 줄: 마크 글자만 있는 줄, 협회 심의 문구, "의약품이 아닌 건강기능식품입니다", 영문 병기
        static readonly Regex HfDetail = new Regex(@"건강기능식품협회|아\s*닌\s*건\s*강\s*기\s*능\s*식\s*품|건\s*강\s*기\s*능\s*식\s*[품풍]\s*[|1Il/]\s*Dietary|Dietary\s*supp", RegexOptions.IgnoreCase);

        // msgood 대표 사진이 다른 상품인지: 사진 글자에 상품명 단어가 하나도 없고, 사진에 두 번 이상 나온 상품 이름(천명환)이 상세 페이지에는 없을 때
        //  → 그 이름을 돌려줌 (아니면 null)
        public static string RepOtherProduct(Job job)
        {
            if (string.IsNullOrEmpty(job.RepText) || string.IsNullOrEmpty(job.OcrText)) return null;
            string rk = Key(job.RepText);
            if (Regex.Matches(rk, "[가-힣]").Count < 6) return null;
            var nameWords = Naming.SeedCandidates(job.Name, job.Brand).Item1.Concat(Regex.Split(job.Name ?? "", @"\s+"))
                                  .Select(w => Regex.Replace(Key(w), "[0-9a-z]+", "")).Where(k => k.Length >= 2).Distinct().ToList();
            if (nameWords.Count == 0) return null;
            if (nameWords.Any(w => rk.Contains(w) || Enumerable.Range(0, w.Length - 1).Any(i => rk.Contains(w.Substring(i, 2))))) return null;
            // 사진 글자 중 상세 페이지에 없는 세 글자 이상 말이 둘 이상 (천명환·무주덕유산…)
            string ok = Key(job.OcrText);
            var foreign = Regex.Matches(job.RepText, "[가-힣]{3,}").Cast<Match>().Select(m => m.Value)
                               .GroupBy(x => x).Where(g => !ok.Contains(Key(g.Key))).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            if (foreign.Count < 2) return null;
            return foreign.FirstOrDefault(w => Regex.IsMatch(w, "(환|정|고|즙|차|액)$")) ?? foreign[0];
        }

        // 대표 이미지(msgood 상품 사진) 글자 인식 → 줄 목록 (포장에 인쇄된 건강기능식품 표시를 찾는 데 씀)
        public static string ReadRepText(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                byte[] data; using (var wc = new System.Net.WebClient()) data = wc.DownloadData(url);
                using (var img = new System.Drawing.Bitmap(new System.IO.MemoryStream(data)))
                {
                    var ps = RepImages.OcrLines.ReadPasses(img, m => { }, new RepImages.OcrLines.Pass(), new RepImages.OcrLines.Pass { Scale = 2f });
                    return string.Join("\n", ps.SelectMany(p => p).Select(l => l.Text).Distinct());
                }
            }
            catch { return null; }
        }

        // 건강기능식품인지: 1순위 대표 이미지(포장)의 건강기능식품 표시, 2순위 상세 이미지의 표시·"건강기능식품이 아닙니다" 문구,
        // 3순위 msgood 분류. 이미지 판단과 msgood 분류가 엇갈리면 검토 화면에서 확인하게 표시
        public static void JudgeHealth(Job job, bool fromDetail)
        {
            string cat = Regex.Replace(job.Category ?? "", @"\s+", "");
            bool? byCat = cat.StartsWith("건강기능식품") ? true : (cat.StartsWith("건강일반식품") || cat.Contains("일반식품")) ? (bool?)false : null;
            var repLines = (job.RepText ?? "").Split('\n').Where(l => !HfNot.IsMatch(l) && !l.Contains("협회")).ToList();
            bool rep = repLines.Any(l => HfMark.IsMatch(l));
            var lines = (job.OcrText ?? "").Split('\n').Select(l => l.Trim()).ToList();
            bool not = lines.Any(l => HfNot.IsMatch(l));
            bool detail = lines.Any(l => !HfNot.IsMatch(l) && (HfDetail.IsMatch(l) || Key(l) == "건강기능식품"));
            string catNote = byCat.HasValue ? " (엠굿 분류: " + job.Category + ")" : "";
            if (rep)
            {
                job.Health = true;
                job.HealthNote = "대표 이미지 포장에 건강기능식품 표시가 있음" + catNote;
            }
            else if (detail && !not)
            {
                job.Health = true;
                job.HealthNote = "상세 이미지에 건강기능식품 표시가 있음" + catNote;
            }
            else if (not)
            {
                job.Health = false;
                job.HealthNote = "상세 이미지에 '건강기능식품이 아닙니다' 문구가 있음" + catNote;
            }
            else if (byCat.HasValue)
            {
                job.Health = byCat.Value;
                job.HealthNote = (byCat.Value ? "⚠ 확인 필요: 이미지에서 건강기능식품 표시를 못 찾아 엠굿 분류로 판단했습니다" : "이미지에 건강기능식품 표시 없음") + catNote;
                return;
            }
            else
            {
                job.Health = false;
                job.HealthNote = "이미지에 건강기능식품 표시 없음 (가공식품)";
                return;
            }
            if (byCat.HasValue && byCat.Value != job.Health)
                job.HealthNote = "⚠ 확인 필요: " + job.HealthNote + " — 엠굿 분류와 다릅니다. 제품의 건강기능식품 마크를 확인해 주세요";
        }

        public static string TrendNote;   // 마지막 트렌드 조회 결과 (설정 화면 표시용)

        async Task FillTrends(List<KwRow> targets, Action<string> step)
        {
            var keys = TrendKeys.Load(Store.Dir);
            if (!keys.Ready || targets.Count == 0) return;
            try
            {
                var got = await Task.Run(() => new Trend(keys).Get(targets.Select(r => r.Word).ToList(), DateTime.Today, (d, n) => step("3/6 최근 검색 흐름 확인 중... " + d + "/" + n)));
                foreach (var r in targets) { TrendInfo t; if (got.TryGetValue(r.Word, out t)) { r.Growth = t.Growth; r.YoY = t.YoY; } }
                TrendNote = DateTime.Now.ToString("MM-dd HH:mm") + " 정상 (" + got.Count + "개, 급상승 " + targets.Count(r => r.Rising) + "개)";
            }
            catch (Exception ex) { TrendNote = DateTime.Now.ToString("MM-dd HH:mm") + " " + ex.Message; step("3/6 최근 검색 흐름 확인 실패: " + ex.Message); }
        }

        async Task<bool> IsPerson(string word)
        {
            var done = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> h = (s, e) => done.TrySetResult(true);
            naverView.CoreWebView2.NavigationCompleted += h;
            try
            {
                naverView.CoreWebView2.Navigate("https://search.naver.com/search.naver?query=" + Uri.EscapeDataString(word));
                await Task.WhenAny(done.Task, Task.Delay(20000));
            }
            finally { naverView.CoreWebView2.NavigationCompleted -= h; }
            await Task.Delay(800);
            string r = await naverView.CoreWebView2.ExecuteScriptAsync(
                "(() => [...document.querySelectorAll('h2, h3, .api_title, .title_area')].some(e => /^(프로필|같은 이름 다른 인물|함께 출연한 다른 인물)$/.test((e.innerText || '').trim())))()");
            return r == "true";
        }

        // msgood 상품명 첫 단어가 만든 곳 이름인지: 뒤에 상품 이름 단어가 따로 있을 때만 (뉴트로웨이 굿모닝곰피 90정 → 뉴트로웨이)
        //  "브레인레스큐 60캡슐"처럼 첫 단어 말고는 용량뿐이면 첫 단어가 상품 이름
        static bool FirstIsMaker(Job job)
        {
            var ws = Regex.Split((job.Name ?? "").Trim(), @"\s+");
            if (ws.Length < 2 || Key(ws[0]).Length < 2) return false;
            var cands = Naming.SeedCandidates(string.Join(" ", ws.Skip(1)), job.Brand).Item1;
            return cands.Count > 0;
        }

        // msgood 상품명 단어 중 상품 이름표(브랜드·제품 라인)인 말: 시작 단어가 안 들어 있고 검색량도 적은 말
        //  (브레인레스큐·브레인맥스솔루션1200·허니드·관절엔·부부. 굿모닝곰피·해삼왕환은 시작 단어가 들어 있어 상품 이름으로 씀)
        List<string> NameBrandTokens(Job job, string seed, List<KwRow> rows = null)
        {
            string sk = Key(seed);
            if (sk.Length < 2) return new List<string>();
            var lr = learned();
            var kws = rows ?? job.Keywords;
            return Naming.SeedCandidates(job.Name, job.Brand).Item1
                .Where(w =>
                {
                    string k = Key(w);
                    if (k.Length < 2 || k.Contains(sk) || lr.IsPreferred(w)) return false;
                    var r = kws.FirstOrDefault(x => Key(x.Word) == k) ?? job.Related.FirstOrDefault(x => Key(x.Word) == k);
                    // 이 카테고리 1페이지 상품들이 함께 쓰는 핵심 말(구증구포)은 브랜드가 아님
                    if (r != null && (r.Core || (r.Mentioned && r.InPage >= 2 && r.CatShare >= 0.5))) return false;
                    return r == null || r.Volume < 1000;
                })
                .Select(Key).Distinct().ToList();
        }

        // "원료+영양제"(빌베리영양제)는 그 원료가 이 상품의 기능성 원료일 때만 씀.
        // 상세에 원료로는 나오지만(부원료·원재료 목록) 상품명에도 없고 기능성 문구("~에 도움을 줄 수 있음")에도 없으면 뺌
        // 상세에 아예 없는 말(두뇌영양제·관절연골영양제 같은 대상·부위)은 여기서 판단하지 않음
        public static bool SubIngredientSupplement(Job job, string w)
        {
            string k = Key(w);
            if (!k.EndsWith("영양제") || k.Length <= 4) return false;
            return IsSubIngredient(job, k.Substring(0, k.Length - 3));
        }

        // 이 상품의 부원료(기능성 원료가 아닌 원료)인가: 상세에 원료로 나오지만 상품명·시작 단어에 없고, 기능성 문구에도 없음
        //  labeled = true: 상세에 "부원료"라고 적힌 곳 근처에 나온 원료만 (검색어·이름 조각 빼기용, 더 엄격)
        public static bool IsSubIngredient(Job job, string x, bool labeled = false)
        {
            x = Key(x);
            string ocr = Key(job.OcrText ?? "");
            if (x.Length < 2 || !ocr.Contains(x)) return false;
            string seed = Key(job.Seed ?? ""), name = Key(job.Name ?? "");
            if (name.Contains(x) || seed.Contains(x) || (seed.Length >= 2 && x.Contains(seed))) return false;
            // 원료로 쓰인 말만 (빌베리추출물·블루베리분말, 또는 원재료·부원료 줄). 두뇌·관절연골·면역 같은 목적·부위 말은 해당 없음
            bool ingredient = Regex.IsMatch(ocr, Regex.Escape(x) + "(추출물|추출분말|분말|농축|오일|엑기스|가루|즙|유래)");
            var lines = (job.OcrText ?? "").Split('\n');
            if (!ingredient) ingredient = lines.Any(line => Key(line).Contains(x) && Regex.IsMatch(line, "원재료|원료명|부원료"));
            if (!ingredient) return false;
            var nameWords = Regex.Matches(job.Name ?? "", "[가-힣]{2,}").Cast<Match>().Select(m => m.Value).ToList();
            // 상품명 단어와 같은 원료를 다르게 쓴 것 (베르가못 ↔ 베르가모트)
            if (x.Length >= 3 && name.Contains(x.Substring(0, 3))) return false;
            var hit = Enumerable.Range(0, lines.Length).Where(i => Key(lines[i]).Contains(x)).ToList();
            Func<int, int, string> near = (i, r) => string.Join("\n", lines.Skip(Math.Max(0, i - r)).Take(r * 2 + 1));
            // 기능성 원료: 그 줄이나 바로 옆 줄에 기능성 문구(~에 도움을 줄 수 있음·기능성원료), 또는 상품명 성분을 풀이한 줄('루테인'은 마리골드꽃 추출물을 뜻함)
            foreach (int i in hit)
            {
                if (Regex.IsMatch(lines[i], "뜻함") || nameWords.Any(n => lines[i].Contains(n) && !Regex.IsMatch(lines[i], "원재료|원료명"))) return false;
                if (Regex.IsMatch(near(i, 2), "도움을|기능성\\s*원료|기능성을\\s*인정|기능성\\s*(내용|정보)\\s*[:：]|생리활성|뜻함")) return false;
            }
            // "부원료" 라고 적힌 곳 근처에 나오면 확실한 부원료 (빌베리)
            if (labeled) return hit.Any(i => near(i, 3).Contains("부원료"));
            return true;
        }

        // 추출 공법 말(초임계·rTG 등)은 msgood 상품명이나 상세 페이지에 그 공법이 나올 때만 씀
        // (상세의 "초임계 추출 공법" 소개도 인정 — 쏘팔메토 20011, 사용자 확인)
        static readonly Regex MethodWord = new Regex("초임계|저온추출|냉압착|rtg", RegexOptions.IgnoreCase);
        public static bool MethodClaimOk(Job job, string w)
        {
            var m = MethodWord.Match(Key(w)); if (!m.Success) return true;
            string mk = m.Value.ToLowerInvariant();
            return Key(job.Name ?? "").ToLowerInvariant().Contains(mk) || Key(job.OcrText ?? "").ToLowerInvariant().Contains(mk);
        }

        // 추가이미지 고르기용 부원료 이름 (부원료 표시가 없어도 기능성 문구 없이 원료로만 나오는 것: 석류·석류착즙)
        public static List<string> SubIngredientNames(Job job)
        {
            return Regex.Matches(job.OcrText ?? "", "([가-힣]{2,8})\\s*(추출물|추출분말|착즙분말|분말|농축|오일|엑기스)").Cast<Match>().Select(m => m.Groups[1].Value)
                        .Where(x => x.Length <= 6 && !Regex.IsMatch(x, "추출|주출|추즐|혼합|농축|능축|발효|함유|원료"))
                        .Concat(Regex.Matches(job.OcrText ?? "", "([가-힣]{2,4})착즙").Cast<Match>().Select(m => m.Groups[1].Value))
                        .Distinct().Where(x => IsSubIngredient(job, x)).Select(Key).ToList();
        }

        // 상세에 나오는 부원료 이름들 (빌베리·블루베리·결명자 …): 상품명·쿠팡 검색어에서 뺄 말
        public static List<string> SubIngredients(Job job)
        {
            return Regex.Matches(job.OcrText ?? "", "([가-힣]{2,8})\\s*(추출물|추출분말|분말|농축|오일|엑기스)").Cast<Match>().Select(m => m.Groups[1].Value)
                        .Where(x => x.Length <= 6 && !Regex.IsMatch(x, "추출|주출|추즐|혼합|농축|능축|발효|함유|원료"))   // 글자 인식 조각(추출·혼합 …)은 빼고
                        .Distinct().Where(x => IsSubIngredient(job, x, true)).ToList();
        }

        // 브랜드 이름과 그 앞 조각(브레인레스큐 → 브레인). 조각은 3글자 이상만 (관절엔 → 관절 은 안 뺌)
        static bool IsBrandPiece(string w, IEnumerable<string> brands)
        {
            string k = Key(w);
            return k.Length >= 2 && brands.Any(b => b.Length >= 2 && (k.Contains(b) || (k.Length >= 3 && b.Length > k.Length && b.StartsWith(k))));
        }

        public void MakeNames(Job job)
        {
            var set = settings();
            // 상품명에 못 쓰는 단어(위험 단어, 상품에 없는 유기농·파우더 등)는 [쿠팡 검색어]로
            string mine = Key((job.Name ?? "") + " " + (job.OcrText ?? ""));
            int seedOcc = Math.Max(3, Naming.Occ(job.OcrText, job.Seed));
            foreach (var k in job.Keywords.Skip(1))
            {
                k.Core = !string.IsNullOrEmpty(job.OcrText) && Naming.IsCore(job.OcrText, k.Word, seedOcc);
                k.InName = Key(k.Word).Length >= 2 && Key(job.Name).Contains(Key(k.Word));
                k.Mentioned = Key(k.Word).Length >= 2 && Naming.StrictMention(job.Name, job.OcrText, k.Word);
                k.Function = k.Mentioned && Naming.IsFunctionWord(k.Word);
                // 오타 키워드는 상세에 없는 게 당연하므로 "상품에 없는 말" 검사를 하지 않음 (오매가3)
                if (k.TagReason != "직접") k.TagReason = k.Excluded == null && !k.Typo ? Naming.TagReason(k.Word, job.Name, job.OcrText, learned()) : null;
            }
            var ordered = Naming.Ordered(job.Keywords.Skip(1).Where(k => k.Use), job.OrderMode ?? set.OrderMode, set.MaxRatio, learned()).Select(k => k.Word).ToList();
            // 상품명이 짧으면 채울 말: 연관 키워드(시작 단어 포함) 중 브랜드·인물·위험 단어·상품에 없는 말·정보성 검색어가 아닌 것
            var bad = job.Keywords.Skip(1).Where(k => k.Excluded != null || k.TagReason != null || !k.Use).Select(k => Key(k.Word)).Where(k => k.Length >= 2).ToList();
            bad.AddRange(learned().Brands.Keys.Select(Key));
            bad.AddRange(Naming.KnownBrands.Select(Key).Where(b => b.Length >= 2));
            // msgood 에 브랜드가 없으면 상품명 첫 단어(동의삼 등)가 대개 만든 곳 이름
            string first = Regex.Split((job.Name ?? "").Trim(), @"\s+")[0];
            if (Key(job.Brand).Length >= 2) bad.Add(Key(job.Brand));
            // msgood 브랜드 칸이 틀릴 때도 있음 (뉴일리 상품인데 브랜드 '소연골') → 첫 단어도 만든 곳으로
            bool firstMaker = FirstIsMaker(job) && !(Key(job.Seed).Length >= 2 && Key(first).Contains(Key(job.Seed)));
            if (firstMaker) bad.Add(Key(first));
            // 상품 이름표(브레인레스큐·허니드·관절엔)와 그 앞 조각(브레인), 브랜드라고 알려 준 말(뉴일리)
            var nameBrands = NameBrandTokens(job, job.Seed);
            var brandSet = new List<string>(nameBrands);
            brandSet.AddRange(job.Keywords.Skip(1).Select(k => Key(k.Word)).Where(k => k.Length >= 3 && nameBrands.Any(b => b.Length > k.Length && b.StartsWith(k))));
            brandSet.AddRange(learned().Brands.Keys.Select(Key).Where(b => b.Length >= 2));
            bad.AddRange(brandSet);
            ordered = ordered.Where(w => !IsBrandPiece(w, brandSet) && !(firstMaker && Key(w).Contains(Key(first)))).ToList();
            // 시작 단어를 뺀 나머지(오버나이트오트밀 → 오버나이트)가 이 카테고리 1페이지 상품명이나 msgood 상품명에 나온 말이어야 함
            // (오트밀크·오트밀죽·오트밀닭죽 같은 다른 상품·요리 이름은 빠짐)
            var pageWords = new HashSet<string>(job.Keywords.Select(k => Key(k.Word)).Where(k => k.Length >= 2));
            string sk = Key(job.Seed), nameKey = Key(job.Name);
            Func<string, bool> related = w => { string rest = Key(w).Replace(sk, ""); return rest.Length >= 2 && (pageWords.Contains(rest) || nameKey.Contains(rest)); };
            // 연관 키워드도 시작 단어를 뺀 나머지가 상세 페이지·상품명에 나온 말이어야 (리포좀멜라토닌·식물성멜라토닌 X, 다이어트유산균 O)
            // (상세에 몇 번 나왔다고 되는 게 아님: "현미보다 단백질이 높은 오트밀"처럼 비교로만 나온 현미 → 현미오트밀 X)
            //  나머지가 msgood 상품명에 있거나, 상세의 핵심 말이거나, 이미 검증을 통과한 키워드일 때만
            var passed = new HashSet<string>(ordered.Select(Key));
            // 상품명에 따로 쓰지 않는 말(셀렌: 검색하면 셀레늄 카테고리)도 이 상품에 있는 말이면 붙은 말(아연셀레늄)을 고를 때는 인정
            foreach (var k in job.Keywords.Skip(1).Where(k => k.Mentioned && k.Excluded == null && k.TagReason == null && Naming.NameBlockReason(k) != null)) passed.Add(Key(k.Word));
            int seedOccR = Math.Max(3, Naming.Occ(job.OcrText, job.Seed));
            string sk2 = job.TypeSuffix != null && sk.Contains(Key(job.TypeSuffix)) ? Key(job.TypeSuffix) : sk;
            Func<string, bool> restMentioned = w => { string rest = Key(w).Contains(sk) ? Key(w).Replace(sk, "") : Key(w).Replace(sk2, ""); return rest.Length >= 2 && !Naming.IsDetailStop(rest) && (nameKey.Contains(rest) || passed.Contains(rest) || GenericType.Contains(rest) || Naming.StrictMention(job.Name, job.OcrText, rest) && passed.Any(p2 => Naming.SameMeaning(p2, rest)) || Naming.IsCore(job.OcrText, rest, seedOccR) || (Naming.IsFunctionWord(rest) && Naming.Occ(job.OcrText, rest) >= 1)); };
            // 연관 검색어로 자리 채우기: 월 검색량 100 이상만 (프로바이오틱스아연 70 같은 말은 안 씀)
            var filler = job.Related.Where(r => r.Volume >= Naming.RestMinVolume && !bad.Any(b => Key(r.Word).Contains(b)) && restMentioned(r.Word) && Naming.GoodFiller(r.Word, job.Seed, job.Name, job.OcrText, learned()))
                                    .Select(r => r.Word).ToList();
            // 그보다 먼저: 이 상품 자신의 말 — msgood 상품명 단어(흑도라지·발효흑삼고)와 상세에 나온 키워드(흑삼농축액)
            // (슬림다이어트앤유산균처럼 8자 이상 붙은 상품 이름은 브랜드·제품명이라 뺌)
            // 이 상품 자신의 이름(굿모닝곰피)은 네이버 브랜드 목록에 '굿모닝'이 있어도 씀. 만든 곳 이름(뉴트로웨이)·이름난 브랜드만 뺌
            var maker = new List<string>(Naming.KnownBrands.Select(Key).Where(b => b.Length >= 2));
            if (Key(job.Brand).Length >= 2) maker.Add(Key(job.Brand));
            if (firstMaker) maker.Add(Key(first));
            maker.AddRange(brandSet);
            var own = Naming.SeedCandidates(job.Name, job.Brand).Item1.Where(w => w.Length < 8)
                // (키워드 표의 단어는 위 순서 목록에서 이미 거른 것만 쓰므로 여기서는 msgood 상품명 단어만)
                .Where(w => !maker.Any(b => Key(w).Contains(b)) && !learned().IsExcluded(w) && Naming.IsCandidate(w) && Naming.TagReason(w, job.Name, job.OcrText, learned()) == null)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            filler = own.Concat(filler).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // 고민·상황·대상 키워드: 상품수 10만 미만이고 배율 좋은 것 최대 3개 (스토어 상품명에 먼저)
            var concern = (job.Concern ?? new List<KwRow>()).Where(c => c.Total >= 0 && c.Volume > 0 && c.Total < Naming.HugeTotal && c.Total <= Naming.GoodRatio * c.Volume
                                                                && !c.OtherCategory && !bad.Any(b => Key(c.Word).Contains(b)) && !learned().IsExcluded(c.Word))
                                .OrderBy(c => c.Total / (double)c.Volume).Take(3).Select(c => c.Word).ToList();
            // 상품명에 넣지 않는 말: 정보성 검색어(고르는법·만드는법·많은음식), 질병 이름(변비·PMS), 검색하면 다른 카테고리가 주로 나오는 말(셀렌·항산화)
            //  → 상품명에서 빼고 쿠팡 검색어 후보로 (NameWords 에 남겨 둠)
            Func<string, KwRow> rowOf = w => job.Keywords.FirstOrDefault(x => Key(x.Word) == Key(w)) ?? (job.Concern ?? new List<KwRow>()).FirstOrDefault(x => Key(x.Word) == Key(w));
            // 부원료 이름(빌베리·석류착즙·모로오렌지): 시작 단어가 없는 말이면 상품명에 안 씀 (부원료 표시가 없어도 기능성 문구 없이 원료로만 나오는 것)
            var subNames = SubIngredients(job).Concat(SubIngredientNames(job)).Select(Key).Where(x => x.Length >= 2 && !Key(job.Seed).Contains(x)).Distinct().ToList();
            Func<string, bool> nameOk = w =>
            {
                if (Naming.IsInfoQuery(w) || Naming.IsDisease(w)) return false;
                if (SubIngredientSupplement(job, w) || subNames.Any(x => Key(w).Contains(x) && !Key(w).Contains(Key(job.Seed)))) return false;   // 부원료 이름이 든 말(빌베리영양제·빌베리루테인)
                if (!MethodClaimOk(job, w)) return false;
                var r = rowOf(w);
                return r == null || Naming.NameBlockReason(r) == null;
            };
            var blocked = ordered.Concat(filler).Concat(concern).Where(w => !nameOk(w)).ToList();
            concern = concern.Where(nameOk).ToList();
            filler = filler.Where(nameOk).ToList();
            ordered = ordered.Where(w => nameOk(w) || job.Keywords.Any(k => k.Typo && string.Equals(k.Word, w, StringComparison.OrdinalIgnoreCase))).ToList();
            int nTypo = ordered.TakeWhile(w => job.Keywords.Any(k => k.Typo && string.Equals(k.Word, w, StringComparison.OrdinalIgnoreCase))).Count();
            ordered = ordered.Take(nTypo).Concat(concern).Concat(ordered.Skip(nTypo).Where(w => !concern.Contains(w, StringComparer.OrdinalIgnoreCase))).ToList();
            // 상품명은 20~50자: 억지로 채우지 않음. 용량·수량(60정·2개)은 넣지 않음 (1개·2개 상품이 헷갈리고, 쿠팡은 옵션으로 자동으로 붙음)
            // 쿠팡 상품명에는 맛 이름만 덧붙임 (애플시나몬맛). 고민·대상·연관 키워드는 쿠팡 검색어 20개로
            var cextra = Naming.CoupangExtra(job.Name, job.Brand, job.OcrText).Where(w => !Regex.IsMatch(w, @"\d") && w.EndsWith("맛")).ToList();

            // 키워드 사용 장부: 다른 상품이 이미 쓴 말은 뒤로. 그래도 상품수가 적고 배율이 좋은 말(1페이지에 오를 만한 말)은
            // 최대 MaxReuse 개 상품까지 겹쳐 씀. 그 밖의 겹치는 말은 맨 끝 (자리가 남을 때만)
            var ledger = KeywordLedger.Load();
            var reuseOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Func<string, int> reuse = w =>
            {
                int r;
                if (reuseOf.TryGetValue(w, out r)) return r;
                if (Key(w) == sk || learned().IsPreferred(w)) return reuseOf[w] = 0;
                int u = ledger.NameUses(w, job.Code);
                if (u == 0) return reuseOf[w] = 0;
                var st = WordStatOf(job, w);
                bool strong = st.Total >= 0 && st.Total < set.ReuseMaxTotal && st.Volume > 0 && st.Total <= set.MaxRatio * st.Volume;
                return reuseOf[w] = strong && u < set.MaxReuse ? 1 : 2;
            };
            var head = ordered.Take(nTypo).ToList();
            var body = ordered.Skip(nTypo).ToList();
            var tail = body.Concat(filler).Where(w => reuse(w) == 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            ordered = head.Concat(body.Where(w => reuse(w) == 0)).Concat(body.Where(w => reuse(w) == 1)).ToList();
            filler = filler.Where(w => reuse(w) < 2).OrderBy(reuse).Concat(tail).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            job.Names = Naming.BuildForAccounts(job, set, ordered, filler, cextra, w => WordStatOf(job, w));
            // 상품명이 20자도 안 되면 (해삼 해삼왕환) 이 상품에 적힌 말 중 같은 상품을 파는 1페이지 상품명에 3번 이상 나온 말로 채움
            //  (검색량이 적거나 카테고리 확인에서 빠진 말이라도: 해삼환·건해삼·복분자)
            if (job.Names.ContainsKey("n1_1") && job.Names["n1_1"].Length < Naming.NaverMinLen)
            {
                var have = new HashSet<string>(ordered.Concat(filler).Select(Key));
                Func<string, bool> fine = w => Naming.IsCandidate(w) && !Naming.IsDetailStop(w) && !Naming.IsOrigin(w) && !Naming.IsClaimOrDecor(w) && !Naming.IsInfoQuery(w) && !Naming.IsDisease(w)
                                               && !Naming.IsRisky(w) && !bad.Any(b => Key(w).Contains(b) || (Key(w).Length >= 3 && b.StartsWith(Key(w)))) && !have.Contains(Key(w)) && !learned().IsExcluded(w)
                                               && !Regex.IsMatch(w, @"(올레산|지방산|불포화|포화지방|리놀산|리놀레산|씨앗|종자|산가|과산화물가|콜레스테롤)")
                                               // 부원료(석류착즙분말·모로오렌지)·공법 말 규칙도 같이
                                               && !SubIngredientSupplement(job, w) && !subNames.Any(x => Key(w).Contains(x) && !Key(w).Contains(Key(job.Seed))) && MethodClaimOk(job, w);
                // 1) 이 상품에 적혀 있고 1페이지 상품명에도 2번 이상 나온 말 (검색하면 다른 카테고리가 나오는 원료 이름도: 강황·아르기닌)
                //    검색하면 다른 카테고리가 주로 나오는 말은 원재료 표시에 원산지와 함께 적힌 주원료(강황(인도산)·검정콩(국산)·복분자(국산))만.
                //    첨가물·부원료(L-아르기닌·치커리·야관문)나 꾸밈말(밸런스)은 안 씀
                Func<string, bool> mainIngredient = w => Regex.IsMatch(job.OcrText ?? "", Regex.Escape(w) + @"[\s0-9.,%°/]{0,8}[\(\[（〔]\s*[가-힣]{1,5}산");
                var backup = job.Keywords.Skip(1).Where(k => k.Excluded == null && k.TagReason == null && k.Mentioned && k.InPage >= 2 && !k.Typo && (!k.OtherCategory || mainIngredient(k.Word)) && fine(k.Word)
                                                           // 두 글자 말은 상품 검색으로 확인된 원료 이름만 (강황·현미 O, 우리·자란·영양 X)
                                                           && (Key(k.Word).Length >= 3 || (k.CatChecked && k.Total >= 0 && k.Total < 1000000)))
                                             .OrderByDescending(k => k.InPage).ThenByDescending(k => k.Volume).Select(k => k.Word).ToList();
                // 2) 상세 페이지에 3번 이상 또렷이 나온 말 (곰피추출분말·치커리)
                //    글자 인식 조각(다시·있다·찾으시)이 섞이지 않게: 다른 판매자 상품명에도 쓰인 말이거나 원료 이름(…추출분말·…분말·…추출물·…농축액)만
                Func<string, bool> real = w => job.CatTitles.Any(t => Key(t).Contains(Key(w))) || job.Related.Any(r => Key(r.Word) == Key(w))
                                               || Regex.IsMatch(w, "^[가-힣]{2,}(추출분말|추출물|농축분말|농축액|분말|추출액)$");
                backup.AddRange(Naming.DetailWords(job.OcrText).Where(d => d.Item2 >= 3 && Regex.IsMatch(d.Item1, "^[가-힣]{3,10}$") && fine(d.Item1) && real(d.Item1)
                                                                          && !job.Keywords.Any(k => { string kk = Key(k.Word), dk = Key(d.Item1), basek = Regex.Replace(dk, "(추출분말|추출물|농축분말|농축액|분말|추출액)$", "");
                                                                                                       return (kk == dk && (k.Excluded != null || k.TagReason != null)) || ((kk == dk || kk == basek) && k.OtherCategory); }))
                                                       .Select(d => d.Item1));
                // 0) 상품 형태를 붙인 말 (부부 부추 = 환 → 부추환): 1페이지 상품명에 2번 이상 나온 키워드일 때
                string mcatF = Regex.Replace(job.Category ?? "", @"\s+", ""), nm = job.Name ?? "";
                string form = Regex.IsMatch(nm, "캡슐") ? "캡슐" : Regex.IsMatch(nm, @"\d\s*정(?![가-힣])") ? "정" : Regex.IsMatch(nm + " " + mcatF, "환") ? "환" : null;
                if (form != null && !string.IsNullOrEmpty(job.Seed) && !Key(job.Seed).EndsWith(form))
                {
                    var fr = job.Keywords.FirstOrDefault(k => Key(k.Word) == Key(job.Seed + form));
                    if (fr != null && fr.InPage >= 2 && fr.Excluded == null && !have.Contains(Key(fr.Word))) backup.Insert(0, fr.Word);
                }
                backup = backup.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (backup.Count > 0)
                {
                    filler = filler.Concat(backup).ToList();
                    job.Names = Naming.BuildForAccounts(job, set, ordered, filler, cextra, w => WordStatOf(job, w));
                }
            }
            // 네이버 검색 최적화: 같은 말이 3번 이상 들어가면 "유의어 포함 반복된 단어" 경고 → 같은 말이 든 단어는 2개까지만 (토마토환 구운토마토 토마토)
            foreach (var k in job.Names.Keys.Where(k => k.StartsWith("n")).ToList()) job.Names[k] = Naming.LimitRepeats(job.Names[k], job.Seed);
            // 유산균 상품: 스토어에서 잘 팔린 상품명 틀(용도 조합어·균 이름·영어 학명·대상·변형 표기)로 다시 짬
            StoreStyle.Apply(job, ordered.Concat(filler).ToList(), nameOk);
            job.NameWords = ordered.Concat(filler).Concat(blocked).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            KeywordLedger.Record(job.Code, job.Names.Values.SelectMany(n => Regex.Split(n ?? "", @"\s+")), null);
            if (!string.IsNullOrWhiteSpace(set.SharedFolder))
            {
                foreach (var a in set.Accounts)
                {
                    string nKey = "n" + a.Id + "_1";
                    string cKey = "c" + a.Id;
                    if (job.Names.ContainsKey(nKey))
                        SharedNames.Append(set.SharedFolder, job.Code, a.BusinessId, job.Names[nKey], Regex.Split(job.Names[nKey], @"\s+"));
                    if (job.Names.ContainsKey(cKey))
                        SharedNames.Append(set.SharedFolder, job.Code, a.BusinessId, job.Names[cKey], Regex.Split(job.Names[cKey], @"\s+"));
                }
            }
        }

        // 단어의 상품총수량·월간검색량: 상품명 단어를 따로 확인한 값 → 키워드 표 → 연관 키워드
        static Naming.WordStat WordStatOf(Job job, string w)
        {
            string k = Key(w);
            var s = new Naming.WordStat();
            var row = job.Keywords.FirstOrDefault(x => Key(x.Word) == k) ?? job.Related.FirstOrDefault(x => Key(x.Word) == k) ?? (job.Concern ?? new List<KwRow>()).FirstOrDefault(x => Key(x.Word) == k);
            if (row != null) { s.Known = true; s.Total = row.Total; s.Volume = row.Volume; s.Typo = row.Typo; s.Rising = row.Rising; }
            long t, v;
            if (job.WordTotals != null && job.WordTotals.TryGetValue(k, out t)) { s.Known = true; s.Total = t; }
            if (job.WordVolumes != null && job.WordVolumes.TryGetValue(k, out v)) { s.Known = true; s.Volume = v; }
            return s;
        }

        // 상품명에 들어간 단어 중 상품총수량을 모르는 것(연관 키워드·msgood 상품명 단어)과 시작 단어를 네이버 쇼핑 전체에서 확인 → 상품명 다시
        public async Task FillNameTotals(Job job, Action<string> step)
        {
            if (job.WordTotals == null) job.WordTotals = new Dictionary<string, long>();
            if (job.WordVolumes == null) job.WordVolumes = new Dictionary<string, long>();
            var extra = new HashSet<string>(Naming.CoupangExtra(job.Name, job.Brand, job.OcrText).Select(Key));
            var words = new[] { "n1_1", "c1" }.Where(job.Names.ContainsKey).SelectMany(n => Regex.Split(job.Names[n], @"\s+"))
                .Where(w => Key(w).Length >= 2 && !extra.Contains(Key(w))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var todo = words.Where(w => !job.WordTotals.ContainsKey(Key(w)) && (Key(w) == Key(job.Seed) || WordStatOf(job, w).Total < 0) && !WordStatOf(job, w).Typo).Take(10).ToList();
            if (todo.Count == 0) return;
            // 검색량 모르는 말은 검색광고에서
            var noVol = todo.Where(w => WordStatOf(job, w).Volume < 0).ToList();
            if (noVol.Count > 0)
                try
                {
                    var keys = SearchAdKeys.Load(Store.Dir);
                    if (keys.Ready)
                    {
                        var vols = await Task.Run(() => new SearchAd(keys).Volumes(noVol, (d, n) => { }, () => false));
                        foreach (var kv in vols) job.WordVolumes[Key(kv.Key)] = kv.Value.Total;
                    }
                }
                catch { }
            showTab("naver");
            try
            {
                await shop.Init();
                for (int i = 0; i < todo.Count; i++)
                {
                    step("5/6 상품명 단어 상품수 확인 중... " + (i + 1) + "/" + todo.Count + " (" + todo[i] + ")");
                    try
                    {
                        var p = await shop.Read(todo[i], false, step, () => false, null, false);
                        if (p != null && p.Total >= 0) job.WordTotals[Key(todo[i])] = p.Total;
                    }
                    catch (NaverBlocked) { break; }
                    catch { }
                    await ShopPause();
                }
            }
            finally { showTab("review"); }
            MakeNames(job);
        }

        // 쿠팡 검색어: 상품명에 못 쓴 단어(위험 단어·상품에 없는 말·브랜드, 월간검색량 많은 것)를 시작 단어와 붙여
        // 쿠팡 자동완성에 실제로 나오는 검색어로 (예: 아기 → "아기 오트밀", 그래놀라 → "그래놀라용 오트밀")
        public async Task MakeTags(Job job, Action<string> step)
        {
            var set = settings();
            // 다른 회사 브랜드는 쿠팡 검색어에 넣지 않음 (쿠팡 검색어 가이드: "타 브랜드명은 입력하지 마세요" — 어기면 판매자 상품 전체가 검색에서 빠질 수 있음)
            var tagWords = job.Keywords.Skip(1)
                .Where(k => k.TagReason != null)
                .Where(k => k.TagReason == "직접" || k.Volume >= set.TagMinVolume)
                .OrderByDescending(k => k.TagReason == "직접").ThenByDescending(k => k.Volume)
                .Select(k => k.Word).Take(12).ToList();
            // 검색어의 중심 단어: 상품 종류 단어(상품명 맨 끝, 예: 오트밀). 시작 단어가 그와 다르면(오버나이트) 상품 종류 단어로
            //  ("오버나이트"는 쿠팡에서 생리대·기저귀 검색어가 대부분이라)
            string type = Naming.TypeQueries(job.Name, job.Brand).FirstOrDefault();
            string center = job.DetailMain != null || (job.SeedReason ?? "").Contains("카테고리 이름") ? job.Seed
                          : type != null && !(Key(job.Seed).Contains(Key(type)) || Key(type).Contains(Key(job.Seed))) ? type : job.Seed;
            // 카테고리 상품명에 나온 낱말들
            var universe = job.Keywords.Select(k => k.Word)
                .Concat(job.CatTitles.SelectMany(t => Regex.Split(t, @"[\s,/()\[\]]+")))
                .Where(w => w.Length >= 1).ToList();
            showTab("naver");
            try
            {
                // 이 상품과 형태·성분·수량·주장이 다른 검색어는 고르지 않음 (아연 젤리·비타민d 아연·30포·유기농 …)
                Func<string, bool> fits = t => Naming.TagFilterReason(t, center, job.Name, job.OcrText) == null;
                // 인물 이름·전에 뺀 단어가 들어간 검색어는 뺌 (여에스더 젖산마그네슘 등)
                var banned = job.Keywords.Where(k => k.Excluded == "인물" || k.Excluded == "전에 뺀 단어").Select(k => Key(k.Word)).Where(k => k.Length >= 2).ToList();
                banned.AddRange(Naming.OtherUse.Where(u => Naming.Occ(job.OcrText, u) == 0).Select(Key));
                // 숫자(5mg·30정·20)가 든 검색어는 그 숫자가 msgood 상품명에 있을 때만 (폴리코사놀 20 O, 멜라토닌 5mg X)
                string nameKeyT = Key(job.Name);
                Func<string, bool> numsOk = t => Regex.Matches(t, @"\d+").Cast<Match>().All(m => nameKeyT.Contains(m.Value));
                // 다른 회사 브랜드가 든 검색어 X (이 상품의 브랜드는 괜찮음)
                string ownBrand = Key(job.Brand);
                var brandWords = job.Keywords.Where(k => k.Excluded == "브랜드" || k.Excluded == "브랜드(학습)").Select(k => Key(k.Word))
                    .Where(k => k.Length >= 2 && (ownBrand.Length < 2 || !(k.Contains(ownBrand) || ownBrand.Contains(k)))).ToList();
                var learnB = learned();
                // 이 카테고리 상품명 맨 앞 낱말(보통 브랜드: 다농원·자임·키즈텐 …) 중 이 상품에 없는 말도 다른 회사 브랜드로 봄
                var firstWords = new HashSet<string>(job.CatTitles.Concat(job.SuffixTitles ?? new List<string>())
                    .Select(t => t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
                    .Select(w => Key(Regex.Replace(w, @"^해외", ""))).Where(k => k.Length >= 2 && !Regex.IsMatch(k, @"\d")));
                string seedB = Key(job.Seed), centerB = Key(center);
                Func<string, bool> firstWordBrand = w =>
                {
                    string k = Key(w);
                    if (k.Length < 2 || k.Contains(seedB) || k.Contains(centerB) || Naming.IsAudienceRest(k) || Regex.IsMatch(k, "^(아기|아이|어린이|키즈|유아|여성|남성|임산부|엄마|국산|국내산|캐나다|미국|독일|일본|호주|뉴질랜드|프리미엄|식물성|유기농)$")) return false;
                    if (ownBrand.Length >= 2 && k.Contains(ownBrand)) return false;
                    if (Naming.StrictMention(job.Name, job.OcrText, w)) return false;
                    // 상품명 맨 앞 낱말 (다른 상품의 브랜드)
                    return firstWords.Contains(k);
                };
                Func<string, bool> otherBrand = t => brandWords.Any(b => Key(t).Contains(b))
                    || t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Any(firstWordBrand)
                    || t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Any(w => (Naming.IsKnownBrand(w) || learnB.IsBrand(w)) && (ownBrand.Length < 2 || !Key(w).Contains(ownBrand)));
                // 질병 이름·치료 표현 X (식품표시광고법), 일반식품에 식약처 인정·인증 X
                Func<string, bool> claimOk = t => !Naming.IsDisease(t) && !Regex.IsMatch(Key(t), "치료|완치|예방|처방")
                    && (job.Health || !Regex.IsMatch(Key(t), "식약[처청]|인정|인증"));
                Func<string, bool> fitsAll = t => fits(t) && !banned.Any(b => Key(t).Contains(b)) && !Naming.IsMedicine(t) && numsOk(t) && !otherBrand(t) && claimOk(t);
                // 20개를 채울 말: 상품명에 못 넣은 좋은 키워드 → 연관 키워드 → 고민·상황 키워드 (모두 상품에 맞는 말만)
                string inNames = Key(string.Join(" ", job.Names != null ? job.Names.Values : (IEnumerable<string>)new string[0]));
                var bad = job.Keywords.Where(k => k.Excluded != null).Select(k => Key(k.Word)).Where(k => k.Length >= 2).ToList();
                // '효능'·'효과' 검색어는 모든 상품에 (질병·치료 표현이 아닌 일반 단어)
                var extra = new List<string> { center + " 효능", center + " 효과" };
                extra.AddRange((job.NameWords ?? new List<string>()).Where(w => !inNames.Contains(Key(w))));
                // 연관 키워드는 시작 단어를 뺀 나머지가 이 상품에 있는 말이거나 흔한 붙임말일 때만 (오메가시계·대추생강즙·오트밀크 같은 다른 상품 X)
                string seedK = Key(job.Seed), centerK = Key(center);
                string[] generic = { "효능", "효과", "영양제", "추천", "선물", "선물세트", "고르는법", "먹는법", "복용법", "섭취법" };
                Func<string, bool> relOk = w =>
                {
                    string k = Key(w);
                    string rest = k.Contains(seedK) ? k.Replace(seedK, "") : k.Contains(centerK) ? k.Replace(centerK, "") : null;
                    return rest != null && (rest.Length == 0 || generic.Contains(rest) || (rest.Length >= 2 && Naming.StrictMention(job.Name, job.OcrText, rest)));
                };
                extra.AddRange(job.Related.Where(r => r.Volume >= Naming.MinVolume && relOk(r.Word) && !bad.Any(b => Key(r.Word).Contains(b)) && !Naming.IsRisky(r.Word)
                                                      && Naming.TagFilterReason(r.Word, job.Seed, job.Name, job.OcrText) == null)
                                          .OrderByDescending(r => r.Volume).Select(r => r.Word));
                extra.AddRange((job.Concern ?? new List<KwRow>()).Where(c => !c.OtherCategory && !bad.Any(b => Key(c.Word).Contains(b))).OrderByDescending(c => c.Volume).Select(c => c.Word));

                // 카테고리 트렌드 키워드 반영 (CategoryTrends.Get)
                try
                {
                    string cat = CategoryTrends.MatchCategory(job.Seed, job.Name, job.NaverCatName);
                    var trendData = CategoryTrends.Get(cat);
                    if (trendData != null && trendData.Items != null)
                    {
                        var cleanTrends = trendData.Items
                            .Where(it => it.RiskType == "일반" && it.Volume >= set.TagMinVolume && fitsAll(it.Word))
                            .OrderByDescending(it => it.Growth >= 1.2 ? it.Volume * 1.5 : it.Volume)
                            .Select(it => it.Word)
                            .Take(15);
                        extra.AddRange(cleanTrends);
                    }
                }
                catch { }
                // 시작 단어가 없는 정보성·고민 검색어(콜레스테롤낮추는방법)는 시작 단어와 붙이면 말이 안 되고,
                // 일반식품에 기능성(콜레스테롤·면역 …)을 붙이면 건강기능식품으로 오인될 수 있어 뺌
                string ck = Key(center);
                extra = extra.Where(w => w.Length <= 20 && fitsAll(w) && (Key(w).Contains(ck) || (!Naming.IsInfoQuery(w) && !Naming.IsConcern(w) && (job.Health || !Naming.IsFunctionWord(w)))))
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var alts = Naming.TypeQueries(job.Name, job.Brand).Concat(new[] { job.TypeSuffix, job.Seed }).Where(a => !string.IsNullOrEmpty(a) && Key(a) != Key(center)).ToList();
                var ledger = KeywordLedger.Load();
                var tags = await new CoupangSuggest(naverView).Build(center, tagWords, universe, step, fitsAll, extra, alts, t => ledger.TagUses(t, job.Code));
                var subs = SubIngredients(job).Select(Key).ToList();   // 부원료(빌베리 …)가 들어간 검색어는 뺌
                tags = tags.Where(t => !banned.Any(b => Key(t).Contains(b)) && !otherBrand(t) && claimOk(t) && !subs.Any(x => Key(t).Contains(x)) && MethodClaimOk(job, t)).ToList();
                // 효능·효과 검색어는 꼭 넣음 (자동완성에 이미 있으면 그대로, 없으면 앞쪽에)
                foreach (string g in new[] { "효과", "효능" })
                    if (!tags.Any(t => Key(t).EndsWith(g)) && fitsAll(center + " " + g)) tags.Insert(Math.Min(2, tags.Count), center + " " + g);

                // 위험 검색어(약, 치료약, 브랜드명, 인물명) 발굴 및 UI 검토용 목록 구성 (쿠팡 검색어에는 오직 안전하고 규격에 맞는 태그만 반영)
                try
                {
                    step("5/6 위험·인물 검색어 발굴 및 UI 검토 목록 생성 중... (약·치료약·브랜드·의사·약사·유명인)");
                    var personK = job.Keywords.Where(k => k.Excluded == "인물").Select(k => Key(k.Word)).Where(k => k.Length >= 2).ToList();

                    // 유명인/의사/약사/전문가 (연예인, 의사, 약사, 한의사, 명의 등) 정규식
                    var celebRe = new Regex(@"(유재석|강호동|신동엽|이만기|송가인|임영웅|정동원|장민호|영탁|이찬원|김종국|아이유|여에스더|오은영|이경제|이시형|서재걸|권오중|남재현|박용우|유태우|홍혜걸|고약사|약들약|리틀약사|정세연|김연아|박서준|차승원|조승우|이병헌|김혜수|전지현|이영애|공유|현빈|마동석|조정석|남궁민|안정환|추성훈|김동현|하정우|성동일|의사|약사|한의사|전문의|약사추천|의사추천|한의사추천|교수|박사|원장|명의|닥터)");

                    // 노인, 수험생, 여성, 남성 등 연령/성별/대상군 단어 엄격 제외 정규식
                    var audienceRe = new Regex(@"(노인|어르신|부모님|아버지|어머니|시부모|시어머니|시아버지|수험생|고3|청소년|중학생|고등학생|대학생|어린이|유아|소아|임산부|임신|수유부|임부|여자|여성|남자|남성|성인|직장인|군인|군대|피엑스|\bpx\b|10대|20대|30대|40대|50대|60대|70대|80대)");

                    Func<string, string> riskWhy = t =>
                    {
                        string k = Key(t);
                        if (!audienceRe.IsMatch(t) && (personK.Any(p => k.Contains(p)) || celebRe.IsMatch(t))) return "인물";
                        if (otherBrand(t)) return "다른 브랜드";
                        if (!claimOk(t)) return "질병·치료";
                        if (Naming.IsMedicine(t) || Regex.IsMatch(k, @"(^|[가-힣])약($|[0-9])") || t.Split(' ').Any(w => Regex.IsMatch(Key(w), @"[가-힣]약$"))) return "의약품";
                        return null;
                    };

                    // 쿠팡 자동완성에 약, 치료, 치료제, 처방, 영양제, 추천, 효능 및 의사·약사·유명인 다각도 질의
                    var riskQueries = new List<string> {
                        center,
                        center + " 약",
                        center + " 치료",
                        center + " 치료제",
                        center + " 처방",
                        center + " 영양제",
                        center + " 효능",
                        center + " 효과",
                        center + " 추천",
                        center + " 가격",
                        center + " 복용법",
                        center + " 부작용",
                        center + " 성분",
                        center + " 비교",
                        center + " 약사",
                        center + " 의사",
                        center + " 약사추천",
                        center + " 의사추천",
                        center + " 유재석",
                        center + " 여에스더",
                        center + " 이만기",
                        center + " 닥터"
                    };
                    if (type != null && Key(type) != Key(center))
                    {
                        riskQueries.Add(type + " 약");
                        riskQueries.Add(type + " 치료");
                        riskQueries.Add(type + " 효능");
                    }
                    var raw = await new CoupangSuggest(naverView).Raw(riskQueries.Distinct());

                    var cand = new List<KeyValuePair<string, long>>();
                    foreach (var s in raw) cand.Add(new KeyValuePair<string, long>(s, 0));
                    foreach (var r in job.Keywords.Concat(job.Related).Concat(job.Concern ?? new List<KwRow>()))
                        cand.Add(new KeyValuePair<string, long>(r.Word, Math.Max(0, r.Volume)));

                    // 건강기능식품·영양제 대표 의사·약사·연예인·유명인 키워드 시드 풀
                    var celebSeedKeywords = new List<string> {
                        center + " 약사추천",
                        center + " 의사추천",
                        center + " 약국",
                        center + " 닥터",
                        "약사 " + center,
                        "의사 " + center,
                        "유재석 " + center,
                        "여에스더 " + center,
                        "이만기 " + center,
                        "유재석 비타민",
                        "유재석 영양제",
                        "유재석 고려은단",
                        "이만기 호관원",
                        "여에스더 유산균",
                        "여에스더 글루타치온",
                        "여에스더 어린콜라겐",
                        "송가인 관절보궁",
                        "임영웅 영양제",
                        "임영웅 관절",
                        "김종국 익스트림",
                        "김종국 프로틴",
                        "신동엽 숙취해소",
                        "이경제 황제녹용",
                        "박용우 다이어트",
                        "약들약 오메가3",
                        "약들약 마그네슘",
                        "리틀약사 영양제",
                        "정세연 한의사",
                        "오은영 락토핏",
                        "오은영 영양제",
                        "강호동 영양제",
                        "아이유 비타민",
                        "조정석 영양제",
                        "남궁민 아르기닌",
                        "약사추천 영양제",
                        "의사추천 영양제",
                        "약사추천 유산균",
                        "약사추천 오메가3",
                        "약사추천 마그네슘",
                        "약사추천 비타민",
                        "의사추천 비타민",
                        "원장추천 영양제"
                    };
                    foreach (var cw in celebSeedKeywords)
                    {
                        if (!cand.Any(x => x.Key.Equals(cw, StringComparison.OrdinalIgnoreCase)))
                            cand.Add(new KeyValuePair<string, long>(cw, 0));
                    }

                    // 위험 검색어 목록 추출 (중복 제거 및 검색량 높은 순 정렬) -> UI 검토/점검 전용 (job.RiskTags)
                    var riskList = cand.Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Key.Length <= 20)
                        .Select(x => new { w = x.Key.Trim(), v = x.Value, why = riskWhy(x.Key) })
                        .Where(x => x.why != null)
                        .GroupBy(x => Key(x.w)).Select(g => g.OrderByDescending(x => x.v).First())
                        .OrderByDescending(x => x.v)
                        .ToList();

                    // UI 위험 검색어 후보 목록: 상위 40개 표시 (검토 및 판매자 선택용)
                    job.RiskTags = riskList.Take(40).Select(x => x.w + "\t" + x.why).ToList();

                    // 쿠팡 검색어 후보 단어들 및 검색량 매핑 (월간검색량 > 30 필수!)
                    var volMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in job.Keywords.Concat(job.Related).Concat(job.Concern ?? new List<KwRow>()))
                    {
                        if (r.Volume > 30 && !string.IsNullOrWhiteSpace(r.Word))
                            volMap[SearchAd.Clean(r.Word)] = Math.Max(volMap.ContainsKey(SearchAd.Clean(r.Word)) ? volMap[SearchAd.Clean(r.Word)] : 0, r.Volume);
                    }

                    // 추가 검색어 후보들의 검색량도 조회하여 월간검색량 30 초과 확보
                    var unkWords = cand.Select(x => x.Key).Concat(tags).Concat(extra).Concat(celebSeedKeywords).Where(w => !volMap.ContainsKey(SearchAd.Clean(w)) && w.Length >= 2 && w.Length <= 20).Distinct(StringComparer.OrdinalIgnoreCase).Take(160).ToList();
                    if (unkWords.Count > 0)
                    {
                        try
                        {
                            var keys = SearchAdKeys.Load(Store.Dir);
                            if (keys.Ready)
                            {
                                var uv = await Task.Run(() => new SearchAd(keys).Volumes(unkWords, (d, n) => { }, () => false));
                                foreach (var kv in uv) if (kv.Value.Total > 30) volMap[SearchAd.Clean(kv.Key)] = kv.Value.Total;
                            }
                        }
                        catch { }
                    }

                    // 상품명에 들어가는 너무 안전하고 뻔한 단어들 수집 (제외 대상)
                    var nameTooSafe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (job.Names != null)
                    {
                        foreach (var nm in job.Names.Values)
                        {
                            if (string.IsNullOrWhiteSpace(nm)) continue;
                            foreach (var part in nm.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                                nameTooSafe.Add(part);
                        }
                    }
                    if (job.NameWords != null)
                    {
                        foreach (var nw in job.NameWords)
                            if (!string.IsNullOrWhiteSpace(nw)) nameTooSafe.Add(nw);
                    }
                    if (!string.IsNullOrWhiteSpace(job.Seed)) nameTooSafe.Add(job.Seed);

                    // 이 상품과 관계있는 말만: 시작 단어·상품 종류가 들어간 검색어(홍화씨유 효능),
                    // 또는 이 상품 상품명·상세에 나오고 이 카테고리에서 검색되는 키워드. 경쟁 상품명에만 있던 말(영진약품·독일·VIP·솔잎농축액)은 뺌
                    var relKeys = new[] { center, (job.Seed ?? "").Split(' ')[0], type, job.TypeSuffix }
                        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(Key)
                        // 제형·용기 말(식물성캡슐·캡슐·정·분말)은 상품 이름이 아님: '비타민d 식물성캡슐' 같은 다른 상품 검색어가 들어옴
                        .Select(x => Regex.Replace(x, @"\d+$", ""))
                        .Where(x => x.Length >= 2 && !Regex.IsMatch(x, @"^(식물성|연질|경질|장용성|하드|소프트)?(캡슐|정|환|분말|가루|스틱|포|젤리|액|시럽|타블렛|소프트겔)$"))
                        .Distinct().ToList();
                    var rowByKey = new Dictionary<string, KwRow>();
                    foreach (var r in job.Keywords.Concat(job.Concern ?? new List<KwRow>()))
                        if (!string.IsNullOrWhiteSpace(r.Word) && !rowByKey.ContainsKey(Key(r.Word))) rowByKey[Key(r.Word)] = r;
                    // 수입 원료 상품(스페인산 등)에 '국산·국내산' 검색어를 넣으면 원산지를 잘못 알리게 됨
                    bool imported = Regex.IsMatch((job.Name ?? "") + " " + (job.OcrText ?? ""), @"(스페인|미국|중국|캐나다|호주|뉴질랜드|독일|프랑스|이탈리아|일본|인도|칠레|페루|베트남|태국|덴마크|노르웨이|영국|네덜란드)\s*산")
                                    && !Regex.IsMatch(job.Name ?? "", "국산|국내산");
                    Func<string, bool> relevant = w =>
                    {
                        string k = Key(w);
                        if (k.Length < 2 || otherBrand(w)) return false;
                        if (imported && Regex.IsMatch(k, "국산|국내산")) return false;
                        // 원산지 말만 있는 검색어(스페인산·국산)는 사람들이 찾는 말이 아님
                        if (Regex.IsMatch(k, @"^(국|국내|수입|외국|스페인|미국|중국|캐나다|호주|뉴질랜드|독일|프랑스|이탈리아|일본|인도|칠레|페루|베트남|태국|덴마크|노르웨이|영국|네덜란드)산$")) return false;
                        // '프로' 같은 짧은 조각은 이 상품 키워드 표에 있는 말일 때만
                        if (k.Length < 3 && !relKeys.Contains(k)) return false;
                        // 다른 종류의 상품 검색어 (영양제인데 고양이 츄르·치약·염색약·음료·스프레이 …): 이 상품 이름·상세에 그 말이 없으면 뺌
                        var otherType = Regex.Match(k, "고양이|강아지|반려|애견|애묘|츄르|사료|펫|치약|가글|염색약|샴푸|린스|스프레이|화장품|크림|로션|팩|음료|요구르트|요거트|우유|아이스크림|과자|사탕|젤리|철분|비타민|칼슘|마그네슘|오메가|루테인|콜라겐|프로틴|단백질");
                        if (otherType.Success && !Key(job.Name).Contains(otherType.Value) && Naming.Occ(job.OcrText, otherType.Value) < 2) return false;
                        if (relKeys.Any(x => k.Contains(x))) return true;
                        KwRow row;
                        return rowByKey.TryGetValue(k, out row) && row.Excluded == null && !row.OtherCategory
                            && (row.InName || row.Typo || row.Core || ((row.Mentioned || row.Function) && (!row.CatChecked || row.CatShare >= 0.5)));
                    };

                    // 1) 선을 넘나드는 연관/효능/고민 보충 풀: 월 검색수 > 30 내림차순, 상품명에 들어갈 만한 안전한 기본 단어는 제외!
                    var semiSafePool = cand.Select(x => x.Key).Concat(tags).Concat(extra)
                        .Where(w => !string.IsNullOrWhiteSpace(w) && w.Length >= 2 && w.Length <= 20)
                        .Where(relevant)
                        .Where(w => volMap.ContainsKey(SearchAd.Clean(w)) && volMap[SearchAd.Clean(w)] > 30)
                        .Where(w => !nameTooSafe.Contains(w)) // 상품명 너무 안전한 단어 제외!
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(w => volMap[SearchAd.Clean(w)])
                        .ToList();

                    // 2) 무조건 20개 꽉 채우기용 긴급 보충 풀 (월간검색량 > 30 내림차순)
                    var emergencyPool = cand.Select(x => x.Key).Concat(tags).Concat(extra)
                        .Where(w => !string.IsNullOrWhiteSpace(w) && w.Length >= 2 && w.Length <= 20)
                        .Where(relevant)
                        .Where(w => volMap.ContainsKey(SearchAd.Clean(w)) && volMap[SearchAd.Clean(w)] > 30)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(w => volMap[SearchAd.Clean(w)])
                        .ToList();

                    // 3) 최종 비상 풀: 혹시 검색량 30 초과 키워드가 희귀한 경우에도 무조건 20개를 100% 채우는 전체 후보군
                    var allFallback = cand.Select(x => x.Key).Concat(tags).Concat(extra)
                        .Where(w => !string.IsNullOrWhiteSpace(w) && w.Length >= 2 && w.Length <= 20)
                        .Where(relevant)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(w => volMap.ContainsKey(SearchAd.Clean(w)) ? volMap[SearchAd.Clean(w)] : 0)
                        .ToList();

                    // ── [쿠팡 #1 샵]: 질병 및 치료제 위험 키워드 전자동 즉시 주입 (월간검색량 > 30 필수, 검색량 내림차순)
                    var diseaseWords = cand.Select(x => x.Key)
                        .Where(w => w.Length >= 2 && w.Length <= 20 && volMap.ContainsKey(SearchAd.Clean(w)) && volMap[SearchAd.Clean(w)] > 30)
                        .Where(relevant)   // 홍화씨유 상품에 '다이어트'처럼 상품과 관계없는 말은 빼고
                        .Where(w => {
                            string rk = riskWhy(w);
                            return rk == "질병·치료" || rk == "의약품" || Regex.IsMatch(w, @"(치료|효능|효과|약|처방|예방|염|증상|통증|개선|골다공증|관절염|골절|혈압|혈행|혈관|다이어트|당뇨|피부|비염|탈모)");
                        })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(w => volMap[SearchAd.Clean(w)])
                        .ToList();
                    job.DiseaseWords = diseaseWords;

                    // 5) 20개가 안 될 때만: 쿠팡 자동완성에 실제로 나온 검색어 (네이버 검색량 기록은 없어도 쿠팡에서 사람들이 치는 말)
                    //    이 상품 이름이 들어간 말만, 검색에 쓴 질의어(○○ 치료 등) 자체와 위험 표현은 빼고
                    var askedQ = new HashSet<string>(riskQueries.Select(Key));
                    // 시작 단어에 제형이 붙어 있으면(홍화씨유 캡슐) 앞 단어(홍화씨유)로도 쿠팡 자동완성을 받아 둠
                    var autoRaw = new List<string>(raw);
                    string seedHead = (job.Seed ?? "").Split(' ')[0];
                    if (seedHead.Length >= 2 && !askedQ.Contains(Key(seedHead)))
                    {
                        try { autoRaw.AddRange(await new CoupangSuggest(naverView).Raw(new[] { seedHead })); } catch { }
                    }
                    var autoPool = autoRaw.Where(s => !string.IsNullOrWhiteSpace(s) && s.Length >= 2 && s.Length <= 20)
                        .Where(s => !askedQ.Contains(Key(s)) || Key(s) == Key(center))
                        .Where(s => relKeys.Any(x => Key(s).Contains(x)) && relevant(s) && riskWhy(s) == null)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                    var tags1 = new List<string>();
                    var seen1 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // 1순위: 질병·치료 위험키워드 우선 꽉 채우기
                    foreach (var w in diseaseWords) { if (tags1.Count >= 20) break; if (seen1.Add(w)) tags1.Add(w); }
                    // 2순위: 20개 미달 시 월검색수 > 30 내림차순 보충 (상품명 안전단어 제외, 선을 넘나드는 연관/효능 키워드)
                    foreach (var w in semiSafePool) { if (tags1.Count >= 20) break; if (seen1.Add(w)) tags1.Add(w); }
                    // 3순위: 무조건 20개 꽉 채우기 (emergencyPool)
                    foreach (var w in emergencyPool) { if (tags1.Count >= 20) break; if (seen1.Add(w)) tags1.Add(w); }
                    // 4순위: 그래도 20개가 안 되면 쿠팡 자동완성 실제 검색어로 (앞 샵이 안 쓴 말 먼저)
                    foreach (var w in autoPool.Where(x => true).Concat(autoPool)) { if (tags1.Count >= 20) break; if (seen1.Add(w)) tags1.Add(w); }

                    // ── [쿠팡 #2 샵]: 의사/약사/연예인/유명인 키워드 전자동 즉시 주입 (월간검색량 > 30 필수, 검색량 내림차순)
                    // 노인, 수험생, 여자, 남자 등 일반 대상군은 엄격 배제!
                    var personWords = cand.Select(x => x.Key)
                        .Where(w => w.Length >= 2 && w.Length <= 20 && volMap.ContainsKey(SearchAd.Clean(w)) && volMap[SearchAd.Clean(w)] > 30)
                        .Where(w => !audienceRe.IsMatch(w)) // 노인, 수험생, 여자, 남자 등 대상군 단어 엄격 제외!
                        .Where(w => {
                            string rk = riskWhy(w);
                            return rk == "인물" || personK.Any(p => Key(w).Contains(p)) || celebRe.IsMatch(w);
                        })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(w => volMap[SearchAd.Clean(w)])
                        .ToList();
                    job.PersonWords = personWords;

                    var tags2 = new List<string>();
                    var seen2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // 1순위: 의사·약사·연예인·유명인 키워드 우선 채우기 (월간검색량 > 30 내림차순, 대상군 단어 제외)
                    foreach (var w in personWords) { if (tags2.Count >= 20) break; if (seen2.Add(w)) tags2.Add(w); }
                    // 2순위: 20개 미달 시 semiSafePool에서 보충하되, 대상군(노인, 수험생, 여자, 남자 등)은 철저히 제외!
                    foreach (var w in semiSafePool.Where(x => !tags1.Contains(x, StringComparer.OrdinalIgnoreCase) && !audienceRe.IsMatch(x)).Concat(semiSafePool.Where(x => !audienceRe.IsMatch(x))))
                    {
                        if (tags2.Count >= 20) break;
                        if (seen2.Add(w)) tags2.Add(w);
                    }
                    // 3순위: emergencyPool에서도 대상군(노인, 수험생, 여자, 남자 등) 제외하고 보충!
                    foreach (var w in emergencyPool.Where(x => !audienceRe.IsMatch(x)))
                    {
                        if (tags2.Count >= 20) break;
                        if (seen2.Add(w)) tags2.Add(w);
                    }
                    // 4순위: 그래도 20개가 안 되면 쿠팡 자동완성 실제 검색어 중 대상군 제외하고 채우기
                    foreach (var w in autoPool.Where(x => !tags1.Contains(x, StringComparer.OrdinalIgnoreCase) && !audienceRe.IsMatch(x)).Concat(autoPool.Where(x => !audienceRe.IsMatch(x))))
                    {
                        if (tags2.Count >= 20) break;
                        if (seen2.Add(w)) tags2.Add(w);
                    }
                    // 5순위: 무조건 20개 100% 채우기 (allFallback 중 대상군 제외)
                    foreach (var w in allFallback.Where(x => !audienceRe.IsMatch(x)))
                    {
                        if (tags2.Count >= 20) break;
                        if (seen2.Add(w)) tags2.Add(w);
                    }

                    // ── [쿠팡 #3 샵]: 증가배수(트렌드 급상승) 키워드 전자동 즉시 주입 (Growth▲ 내림차순 정렬!)
                    var risingWords = job.Keywords.Concat(job.Concern ?? new List<KwRow>())
                        .Where(r => r.Growth >= 1.2 || r.Rising)
                        .Where(r => relevant(r.Word))
                        .Where(r => !string.IsNullOrWhiteSpace(r.Word) && r.Word.Length >= 2 && r.Word.Length <= 20 && volMap.ContainsKey(SearchAd.Clean(r.Word)) && volMap[SearchAd.Clean(r.Word)] > 30)
                        .OrderByDescending(r => r.Growth)
                        .ThenByDescending(r => volMap.ContainsKey(SearchAd.Clean(r.Word)) ? volMap[SearchAd.Clean(r.Word)] : 0)
                        .Select(r => r.Word)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    job.RisingWords = risingWords;

                    var tags3 = new List<string>();
                    var seen3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // 1순위: 최근 급상승 키워드 우선 꽉 채우기
                    foreach (var w in risingWords) { if (tags3.Count >= 20) break; if (seen3.Add(w)) tags3.Add(w); }
                    // 2순위: 20개 미달 시 월검색수 > 30 내림차순 보충 (상품명 안전단어 제외, 선을 넘나드는 연관/효능 키워드)
                    foreach (var w in semiSafePool.Where(x => !tags1.Contains(x, StringComparer.OrdinalIgnoreCase) && !tags2.Contains(x, StringComparer.OrdinalIgnoreCase)).Concat(semiSafePool)) { if (tags3.Count >= 20) break; if (seen3.Add(w)) tags3.Add(w); }
                    // 3순위: 무조건 20개 꽉 채우기 (emergencyPool)
                    foreach (var w in emergencyPool) { if (tags3.Count >= 20) break; if (seen3.Add(w)) tags3.Add(w); }
                    // 4순위: 그래도 20개가 안 되면 쿠팡 자동완성 실제 검색어로 (앞 샵이 안 쓴 말 먼저)
                    foreach (var w in autoPool.Where(x => !tags1.Contains(x, StringComparer.OrdinalIgnoreCase) && !tags2.Contains(x, StringComparer.OrdinalIgnoreCase)).Concat(autoPool)) { if (tags3.Count >= 20) break; if (seen3.Add(w)) tags3.Add(w); }

                    // Job 객체에 3개 쿠팡 샵 태그 각각 저장
                    job.CoupangTags1 = tags1;
                    job.CoupangTags2 = tags2;
                    job.CoupangTags3 = tags3;
                    job.CoupangTags = tags1; // 기본 #1 샵 태그
                    job.DraftTags = new List<string>(job.CoupangTags);

                    KeywordLedger.Record(job.Code, null, job.CoupangTags1.Concat(job.CoupangTags2).Concat(job.CoupangTags3));
                }
                catch
                {
                    job.CoupangTags = tags.Where(t => fitsAll(t)).Take(20).ToList();
                    KeywordLedger.Record(job.Code, null, job.CoupangTags);
                }
            }
            finally { showTab("review"); }
        }

        // 가격설정기 식 (원가 = 도매가, 배송비 = msgood 배송비) 그대로, 단 하한선 × 개수 아래로는 안 내려감
        // 대기열의 "가격 다시 계산": msgood4u 에 로그인한 상태로 도매가·배송비를 다시 읽어 가격만 다시 만듦
        public async Task RefreshPrice(Job job, Action<string> step)
        {
            step("msgood4u 도매가 읽는 중");
            await msgood.Fill(job, step, true);
            MakePrices(job, settings());
        }

        // 대기열의 "키워드 다시 찾기": 시작 단어·네이버 카테고리(검토 화면에서 바꾼 것)로 키워드 → 상품명 → 쿠팡 검색어
        public async Task Refind(Job job, Action<string> step)
        {
            var reg = Accounts.Registrar("1", settings(), false);
            if (reg != null && job.NaverCat != null)
                await Task.Run(() => { try { job.NaverCatName = reg.NaverCategoryName(job.NaverCat); } catch { } });
            await FindKeywords(job, null, step);
            MakeNames(job);
            try { await FillNameTotals(job, step); } catch { }
            job.DraftNames = new Dictionary<string, string>(job.Names);
            await MakeTags(job, step);
            job.DraftTags = new List<string>(job.CoupangTags);
        }

        public static void MakePrices(Job job, AppSettings set)
        {
            if (job.Wholesale <= 0) { for (int n = 1; n <= 6; n++) { job.Prices[n] = 0; job.FormulaPrices[n] = 0; } return; }   // 도매가 없음: 로그인 후 계산
            for (int n = 1; n <= 6; n++)
            {
                var row = PriceSetter.Pricing.Calc(job.Wholesale, job.Shipping, set.NormalRate, n, n == 1 ? set.NormalRate : PriceSetter.Pricing.DefaultRates[n - 1]);
                long f = (long)Math.Round(row.Sale);
                job.FormulaPrices[n] = f;
                job.Prices[n] = Math.Max(f, job.Floor * n);
            }
        }

        // 상세 페이지 제품명 앞에 붙은 브랜드 (msgood 상품명 "소연골 뮤코다당단백…" ↔ 상세 "웰러스 소연골 뮤코다당단백…" → 웰러스)
        //  잘못 잡지 않게: 세 글자 이상이고 대표 사진(포장)에도 적혀 있는 말만
        public static List<string> DetailBrands(Job job)
        {
            var res = new List<string>();
            var ws = Regex.Split((job.Name ?? "").Trim(), @"\s+").Where(w => Regex.IsMatch(w, "^[가-힣]{2,}$")).ToList();
            if (ws.Count == 0 || string.IsNullOrEmpty(job.OcrText) || string.IsNullOrEmpty(job.RepText)) return res;
            string nameKey = Key(job.Name), repKey = Key(job.RepText);
            foreach (Match m in Regex.Matches(job.OcrText, @"(?<![가-힣A-Za-z])([가-힣]{3,6}|[A-Za-z]{4,12})\s*" + Regex.Escape(ws[0])))
            {
                string b = m.Groups[1].Value;
                if (nameKey.Contains(Key(b)) || !repKey.Contains(Key(b)) || Regex.IsMatch(b, "제품명|상품명|품명|건강기능식품|식품|유기농|국내산|국산|프리미엄|원재료|함유|추출물|분말|섭취|기준|영양|함량|정보|제조|판매")) continue;
                if (!res.Contains(b)) res.Add(b);
            }
            return res;
        }

        // 상세 페이지 구간 중 흰 바탕에 상품(통·상자)만 있는 사진: 가장자리가 거의 흰색이고, 안쪽에 큰 덩어리(글자만 있는 구간보다 많이 칠해짐)
        static Bitmap ProductShot(Bitmap detail, List<ExtraImages.Candidate> cands)
        {
            Bitmap best = null; double bestScore = 0;
            foreach (var c in cands.Where(c => !c.IsTable && c.Bottom - c.Top >= 250).Take(30))
            {
                Bitmap b;
                try { b = ExtraImages.Sections.Render(detail, c); } catch { continue; }
                int edge = 0, edgeWhite = 0, inner = 0, innerInk = 0;
                for (int x = 0; x < b.Width; x += 4) foreach (int y in new[] { 2, b.Height - 3 }) { edge++; if (White(b.GetPixel(x, y))) edgeWhite++; }
                for (int y = 0; y < b.Height; y += 4) foreach (int x in new[] { 2, b.Width - 3 }) { edge++; if (White(b.GetPixel(x, y))) edgeWhite++; }
                for (int y = b.Height / 5; y < b.Height * 4 / 5; y += 6) for (int x = b.Width / 5; x < b.Width * 4 / 5; x += 6) { inner++; if (!White(b.GetPixel(x, y))) innerInk++; }
                double ew = edgeWhite / (double)Math.Max(1, edge), ink = innerInk / (double)Math.Max(1, inner);
                double score = ew >= 0.85 && ink >= 0.15 && ink <= 0.75 ? ew + (c.Package ? 0.2 : 0) + ink * 0.3 : 0;
                if (score > bestScore) { if (best != null) best.Dispose(); best = b; bestScore = score; } else b.Dispose();
            }
            return best;
        }
        static bool White(Color p) { return p.R >= 232 && p.G >= 232 && p.B >= 232; }

        void MakeImages(Job job, Action<string> step)
        {
            string dir = Store.JobDir(job.Code);
            Directory.CreateDirectory(dir);
            var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            var q = new EncoderParameters(1); q.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 93L);

            // 대표이미지 1~6통 (상품명 자리에 시작 단어)
            step("5/6 대표이미지: 상품 사진 받는 중");
            string mainUrl = string.IsNullOrEmpty(job.MainImageUrl) ? RepImages.Msgood.MainImageUrl(job.Url, out _) : job.MainImageUrl;
            var brandWords = new List<string>(); var packageText = new List<string>();
            // msgood 대표 사진이 다른 상품이면(부부 부추 → 천명환 사진) 상세 페이지의 첫 상품 사진 구간으로
            Bitmap alt = null;
            if (job.RepMismatch != null)
            {
                try
                {
                    var durls = job.DetailUrls.Count > 0 ? job.DetailUrls : ExtraImages.Msgood.DetailImageUrls(job.Url);
                    using (var detail = ExtraImages.Msgood.DownloadDetail(durls, m => { }))
                    {
                        var dl = ExtraImages.OcrLines.Read(detail, m => { });
                        var pkg = Naming.SeedCandidates(job.Name, job.Brand).Item1.Select(Key).Where(x => x.Length >= 4).ToList();
                        alt = ProductShot(detail, ExtraImages.Sections.Find(detail, dl, null, pkg, job.Seed));
                    }
                }
                catch { alt = null; }
            }
            Bitmap aiPhoto = null;
            if (settings != null && settings().AutoAiImage)
            {
                try
                {
                    step("5/6 대표이미지: AI 3D 대표이미지 생성 중");
                    string dummy;
                    string prompt = AiImageGen.BuildPrompt(job, out dummy);
                    aiPhoto = AiImageGen.GenerateFreeAsync(prompt, m => step("5/6 대표이미지: " + m)).GetAwaiter().GetResult();
                }
                catch { aiPhoto = null; }
            }
            using (var photo = aiPhoto ?? alt ?? RepImages.Msgood.DownloadImage(mainUrl))
            {
                step("5/6 대표이미지: 라벨 글자 읽는 중");
                var lay = RepImages.Composer.Analyze(photo, job.Brand, m => { }, job.Name, learned().Brands.Keys.Concat(DetailBrands(job)).ToList());
                // 네이버 스토어용: 상표 그대로, 글자판 없이 1개짜리 + 1+1 (naver_1, naver_2). 홍보 띠를 잘라 냈으면 같은 범위만
                var plain = RepImages.Composer.Plain(photo);
                if (lay.Cropped) { plain.Content = lay.Content; plain.Cropped = true; }
                for (int n = 1; n <= 2; n++)
                    using (var bmp = RepImages.Composer.Render(plain, null, n))
                        bmp.Save(Path.Combine(dir, "naver_" + n + ".jpg"), enc, q);
                plain.Photo.Dispose();
                if (alt == null) foreach (var b in job.EraseBoxes ?? new List<int[]>()) if (b != null && b.Length == 4) RepImages.Composer.Erase(lay.Photo, new System.Drawing.Rectangle(b[0], b[1], b[2], b[3]));   // 직접 지정한 지울 곳 (msgood 사진 기준)
                for (int n = 1; n <= 6; n++)
                    using (var bmp = RepImages.Composer.Render(lay, job.Seed, n))
                        bmp.Save(Path.Combine(dir, "rep_" + n + ".jpg"), enc, q);
                brandWords = lay.BrandWords; packageText = lay.PackageText;
                lay.Photo.Dispose();
            }

            // 추가이미지 4장 (상세 이미지에서 구간 자르기)
            step("5/6 추가이미지: 상세 이미지 받는 중");
            var urls = job.DetailUrls.Count > 0 ? job.DetailUrls : ExtraImages.Msgood.DetailImageUrls(job.Url);
            using (var detail = ExtraImages.Msgood.DownloadDetail(urls, m => { }))
            {
                step("5/6 추가이미지: 상세 글자 읽는 중");
                var lines = ExtraImages.OcrLines.Read(detail, m => { });
                var found = ExtraImages.Sections.Find(detail, lines, brandWords, packageText, job.Seed);
                // 부원료만 소개하는 패널(와사비잎추출물 상품의 "유기농석류100% 석류착즙분말")은 빼고, 주원료 설명을 씀
                var subsAll = SubIngredientNames(job);
                string sk3 = Key(job.Seed), core3 = sk3.Length >= 3 ? sk3.Substring(0, 3) : sk3;
                Func<ExtraImages.Candidate, string> textOf = c => Key(string.Join(" ", lines.Where(l => l.Y >= c.Top && l.Y < c.Bottom).Select(l => l.Text)));
                // 주원료(시작 단어 앞 3글자: 와사비) 설명 패널을 먼저
                if (core3.Length >= 2) foreach (var c in found.Where(c => !c.IsTable)) if (textOf(c).Contains(core3)) c.Score += 15;
                if (subsAll.Count > 0)
                {
                    // 부원료 이름 앞부분(석류착즙 → 석류)도 봄: 글자 인식이 "석류착즙"을 다르게 읽어도
                    var roots = subsAll.Concat(subsAll.Where(x => x.Length >= 4).Select(x => x.Substring(0, 2))).Where(x => !sk3.Contains(x)).Distinct().ToList();
                    var keep = found.Where(c =>
                    {
                        string t = textOf(c);
                        if (t.Contains(core3)) return true;
                        if (c.IsTable && Regex.IsMatch(t, "영양정보|영양성분")) return true;
                        return !roots.Any(x => t.Contains(x));
                    }).ToList();
                    if (keep.Count(c => !c.IsTable) >= 2) found = keep;
                }
                var picks = ExtraImages.Sections.DefaultPick(found);
                for (int i = 0; i < 4; i++)
                {
                    string f = Path.Combine(dir, "extra_" + (i + 1) + ".jpg");
                    if (File.Exists(f)) File.Delete(f);
                    if (i < picks.Count) using (var bmp = ExtraImages.Sections.Render(detail, picks[i])) bmp.Save(f, enc, q);
                }
            }
        }

        static bool IsCandidateRow(KwRow r)
        {
            return Naming.IsCandidate(r.Word) && (r.InPage >= 2 || (r.FromDetail && r.Volume >= Naming.MinVolume));
        }

        // 상세 페이지 주 키워드: msgood 상품명 단어와 그 안의 상품 이름 부분(맨 앞 꾸밈말 빼고) 중
        // 검색량 1,000 이상 + 상세에 3번 이상, 상세에 가장 많이 나온 것 (더 긴 말이 거의 그만큼 나오면 긴 말)
        async Task<string> DetailMainKeyword(Job job)
        {
            if (string.IsNullOrEmpty(job.OcrText)) return null;

            // 0순위: 대표이미지와 상품명에서 추출된 핵심 성분 (예: "홍화씨유") - "씨유" 같은 엉뚱한 조각 반환 차단
            string repQuery = BuildRepCategoryQuery(job);
            if (!string.IsNullOrEmpty(repQuery))
            {
                string core = repQuery.Split(' ')[0];
                if (core.Length >= 2 && !Naming.BannedFragments.Contains(core) && Key(job.Name).Contains(Key(core)))
                    return core;
            }

            // msgood 분류의 끝 이름(건강기능식품 > 아연)이 상품명에 들어 있고 상세에 3번 이상 나오면 그것이 주 키워드 (아연셀렌 → 아연)
            string mcat = Regex.Replace((job.Category ?? "").Split('>').Last(), @"[^가-힣A-Za-z0-9]", "");
            if (mcat.Length >= 2 && Key(job.Name).Contains(Key(mcat)) && Naming.Occ(job.OcrText, mcat) >= 3) return mcat;

            // 상품 종류 단어(상품명 맨 끝: 발효흑삼고·젖산마그네슘)와 그 안의 상품 이름 부분만 (흑도라지의 도라지 같은 다른 단어는 안 봄)
            var sc = Naming.SeedCandidates(job.Name, job.Brand);
            if (sc.Item1.Count == 0) return null;
            string T = Naming.MainTypeWord(job.Name, job.Brand) ?? sc.Item1[sc.Item1.Count - 1], tk = Key(T);
            var cands = new List<string> { T };
            // 두 글자 부분은 끝쪽(끝에서 한 글자 안쪽까지)만: 흑삼(발효흑삼고)·유산(…유산균)은 되고 나다(캐나다네이처…)는 안 됨
            Func<string, string, bool> partOk = (p, whole) =>
            {
                string pk = Key(p), wk = Regex.Replace(Key(whole), "[0-9]+$", "");
                string ph = Regex.Replace(pk, "[0-9]+$", "");   // 오메가3 → 오메가 로 찾음
                int at = wk.IndexOf(ph, StringComparison.Ordinal);
                if (at <= 0) return false;
                return ph.Length >= 3 || at + ph.Length >= wk.Length - 1;
            };
            cands.AddRange(sc.Item2.Where(p => partOk(p, T)));
            cands = cands.Distinct().Where(c => Naming.Occ(job.OcrText, c) >= 3).ToList();
            var keys = SearchAdKeys.Load(Store.Dir);
            if (!keys.Ready) return null;
            Dictionary<string, Volume> vols = new Dictionary<string, Volume>();
            if (cands.Count > 0) { try { vols = await Task.Run(() => new SearchAd(keys).Volumes(cands, (d, n) => { }, () => false)); } catch { } }
            // 조각 빼기: 그 말이 나온 자리 대부분(80% 이상)이 더 긴 후보 안이면 조각 (자유 ← 종자유)
            Func<string, IEnumerable<string>, bool> fragment = (c, pool) => pool.Any(l => l.Length > c.Length && Key(l).Contains(Key(c)) && Naming.Occ(job.OcrText, l) >= Naming.Occ(job.OcrText, c) * 0.8);
            var allNames = cands.Concat(sc.Item1).Concat(sc.Item2).Distinct().ToList();
            var ok = cands.Where(c => vols.ContainsKey(c) && vols[c].Total >= 1000 && !Naming.IsClaimOrDecor(c) && !fragment(c, allNames)).ToList();
            // 다른 단어(앞쪽 단어)의 상품 부분이 상세에 2배 이상 많이 나오면 그쪽 (캐나다네이처오메가3+비타민D → 오메가3)
            var others = new List<string>();
            foreach (var full in sc.Item1.Take(sc.Item1.Count - 1))
            {
                string fk = Key(full);
                others.Add(full);
                others.AddRange(sc.Item2.Where(p => partOk(p, full) || (Regex.IsMatch(p, @"\d$") && partOk(p.Substring(0, p.Length - 1), full))));
            }
            others = others.Distinct().Where(c => Naming.Occ(job.OcrText, c) >= 3 && !cands.Contains(c) && !Naming.IsClaimOrDecor(c) && !fragment(c, allNames)).ToList();
            string bestOther = null;
            if (others.Count > 0)
            {
                Dictionary<string, Volume> ov;
                try { ov = await Task.Run(() => new SearchAd(keys).Volumes(others, (d, n) => { }, () => false)); } catch { ov = new Dictionary<string, Volume>(); }
                bestOther = others.Where(c => ov.ContainsKey(c) && ov[c].Total >= 1000).OrderByDescending(c => Naming.Occ(job.OcrText, c)).ThenByDescending(c => c.Length).FirstOrDefault();
            }
            string bestType = ok.Contains(T) ? T : ok.OrderByDescending(c => Naming.Occ(job.OcrText, c)).ThenByDescending(c => c.Length).FirstOrDefault();
            if (bestOther != null && (bestType == null || Naming.Occ(job.OcrText, bestOther) >= 2 * Naming.Occ(job.OcrText, bestType))) return bestOther;
            if (ok.Contains(T)) return T;   // 상품 종류 단어 자체가 상세에 3번 이상 + 검색량 1,000 이상
            string main = ok.OrderByDescending(c => Naming.Occ(job.OcrText, c)).ThenByDescending(c => c.Length).FirstOrDefault();
            for (bool changed = main != null; changed; )
            {
                changed = false;
                var longer = ok.Where(c => c.Length > main.Length && Key(c).Contains(Key(main)) && Naming.Occ(job.OcrText, c) >= Naming.Occ(job.OcrText, main) * 0.45)
                               .OrderByDescending(c => Naming.Occ(job.OcrText, c)).FirstOrDefault();
                if (longer != null) { main = longer; changed = true; }
            }
            return main;
        }

        // 대표이미지 라벨 OCR 텍스트와 상품명에서 [핵심 성분명 + 제형] 최우선 검색어 생성 (예: "홍화씨유 캡슐")
        public static string BuildRepCategoryQuery(Job job)
        {
            if (job == null) return null;
            string name = job.Name ?? "";
            string rep = job.RepText ?? "";

            // 1. 제형 파악 (캡슐, 정, 환, 스틱, 분말, 젤리 등)
            string form = null;
            if (Regex.IsMatch(name + " " + rep, @"(식물성캡슐|연질캡슐|소프트겔|캡슐)")) form = "캡슐";
            else if (Regex.IsMatch(name + " " + rep, @"(타블렛|정제|\b\d+\s*정\b|[가-힣]정\b)")) form = "정";
            else if (Regex.IsMatch(name + " " + rep, @"(환제|[가-힣]환\b)")) form = "환";
            else if (Regex.IsMatch(name + " " + rep, @"(분말스틱|젤리스틱|스틱)")) form = "스틱";
            else if (Regex.IsMatch(name + " " + rep, @"(추출분말|분말|가루|파우더)")) form = "분말";
            else if (Regex.IsMatch(name + " " + rep, @"(구미|젤리)")) form = "젤리";

            // 2. 불용어 및 제형어 패턴
            var stopPattern = @"^(냉압착|압착|프리미엄|유기농|순수|자연|고함량|캐나다|미국|국내산|뉴트리|골드|플러스|100%|건강기능식품|건강식품|식품|캡슐|정|환|스틱|분말|오일류|식물성캡슐|연질캡슐|소프트겔|타블렛|정제|환제|씨유|화씨|화씨유)$";

            // 첫 단어(브랜드일 가능성이 높음) 배제
            string firstWord = name.Trim().Split(' ')[0];

            // 3. 대표이미지 텍스트(RepText)와 상품명(Name)에서 공통 핵심 성분 단어 탐색
            var repMatches = Regex.Matches(rep, @"[가-힣]{2,}")
                                  .Cast<Match>()
                                  .Select(m => m.Value)
                                  .Where(w => !Regex.IsMatch(w, stopPattern) && !Naming.BannedFragments.Contains(w) && (job.Brand == null || !w.Contains(job.Brand)) && (string.IsNullOrEmpty(firstWord) || w != firstWord))
                                  .Distinct()
                                  .ToList();

            string bestCore = null;
            var common = repMatches.Where(w => name.Contains(w)).OrderByDescending(w => w.Length).ToList();
            if (common.Count > 0)
            {
                bestCore = common[0];
            }
            else
            {
                var sc = Naming.SeedCandidates(name, job.Brand).Item1;
                bestCore = sc.FirstOrDefault(w => !Regex.IsMatch(w, stopPattern) && !Naming.BannedFragments.Contains(w) && w.Length >= 2);
            }

            if (!string.IsNullOrEmpty(bestCore))
            {
                if (!string.IsNullOrEmpty(form) && !bestCore.EndsWith(form))
                {
                    return bestCore + " " + form;
                }
                return bestCore;
            }
            return null;
        }

        // 카테고리: msgood 상품명(핵심)으로 네이버 쇼핑을 검색해 1페이지 상품들의 카테고리 중 가장 많은 것 (예: 흑삼고 → 식품>건강식품>홍삼>홍삼액)
        async Task FindCategories(Job job, Action<string> step)
        {
            var learn = learned();
            string c;
            if (job.Category != null && learn.NaverCatFor.TryGetValue(job.Category, out c)) job.NaverCat = c;
            if (job.Category != null && learn.CoupangCatFor.TryGetValue(job.Category, out c)) job.CoupangCat = c;

            // 0순위: 대표이미지와 상품명에서 추출한 [핵심 성분 + 제형] (예: "홍화씨유 캡슐")
            string repQuery = BuildRepCategoryQuery(job);

            // 상품명 맨 끝 단어(상품 종류)와 그 뒷부분으로 검색해 나온 카테고리를 합산 (전체 상품명으로 찾으면 같은 상품 파는 곳의 엉뚱한 카테고리가 나올 수 있음)
            string core = Naming.CoreName(job.Name, job.Brand);
            var queries = Naming.TypeQueries(job.Name, job.Brand);
            // 뒷부분 중 검색량이 100 이상인 가장 긴 말 하나 (젖산마그네슘 → 마그네슘, 발효흑삼고 → 흑삼고. 산마그네슘 같은 조각은 안 씀)
            var sufs = Naming.TypeSuffixes(queries.FirstOrDefault());
            job.TypeSuffix = null;   // 예전 초안의 값이 남지 않게
            if (sufs.Count > 0)
            {
                try
                {
                    var keys = SearchAdKeys.Load(Store.Dir);
                    if (keys.Ready)
                    {
                        var vols = await Task.Run(() => new SearchAd(keys).Volumes(sufs, (d, n) => { }, () => false));
                        var good = sufs.FirstOrDefault(x => vols.ContainsKey(x) && vols[x].Total >= 100);
                        if (good != null) { queries.Add(good); job.TypeSuffix = good; }
                    }
                }
                catch { }
            }
            job.CatTitles = new List<string>(); job.CatPath = null; job.SuffixTitles = new List<string>();
            job.TypeWord = queries.FirstOrDefault();
            // 상세 페이지 주 키워드로 먼저 검색 (상품 이름 중 상세에 가장 많이 나온 말: 유산균·흑삼·젖산마그네슘)
            job.DetailMain = await DetailMainKeyword(job);
            if (job.DetailMain != null && (string.IsNullOrEmpty(repQuery) || job.DetailMain.Length >= 3))
            {
                queries.Insert(0, job.DetailMain);
            }
            if (!string.IsNullOrEmpty(repQuery))
            {
                queries.Insert(0, repQuery);
            }
            queries = queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var tally = new Dictionary<string, int>();
            // 환·정·캡슐 같은 가공 형태(또는 엠굿 건강기능식품)면 원물 카테고리(농산물>토마토·수산물>미역)는 맞지 않음
            //  (구운토마토환 → 토마토로 찾으면 생토마토, 굿모닝곰피 90정 → 곰피로 찾으면 생미역이 나옴)
            string mcat = Regex.Replace(job.Category ?? "", @"\s+", "");
            bool processed = mcat.StartsWith("건강기능식품") || Regex.IsMatch(mcat, "환|정|캡슐")
                          || Regex.IsMatch(job.Name ?? "", @"\d\s*(정|캡슐|환)(?![가-힣])|[가-힣]환(?![가-힣])");
            Func<string, bool> rawFood = p => Regex.IsMatch(p, @"^식품>(농산물|수산물|축산물)");
            Func<int> okSum = () => tally.Where(kv => !processed || !rawFood(kv.Key)).Sum(kv => kv.Value);
            showTab("naver");
            await shop.Init();
            for (int qi = 0; qi < queries.Count + 1; qi++)
            {
                string q = qi < queries.Count ? queries[qi] : core;   // 모자라면 마지막에 핵심 상품명 전체로
                if (qi == queries.Count && okSum() >= 5) break;
                // 주 키워드 검색만으로 카테고리가 충분히 나오면 거기서 끝 (상품 종류 단어 검색은 시작 단어 확인용으로 하나만 더)
                if (qi >= 2 && job.DetailMain != null && okSum() >= 20) break;
                if (q.Length < 2) continue;
                step("2/6 '" + q + "' 네이버 쇼핑에서 카테고리 찾는 중");
                var page = await shop.Read(q, true, step, () => false);
                for (int i = 0; i < page.Titles.Count; i++) if (!page.Ads[i]) { job.CatTitles.Add(page.Titles[i]); if (q == job.TypeSuffix) job.SuffixTitles.Add(page.Titles[i]); }
                foreach (var p in page.Categories.Where(p => p.StartsWith("식품"))) { int n; tally.TryGetValue(p, out n); tally[p] = n + 1; }
                await ShopPause();
            }
            // 가공 형태인데 원물 카테고리만 나왔으면 (부부 부추 → 생부추만) 형태를 붙여 한 번 더 (부추환)
            if (processed && !tally.Keys.Any(k => !rawFood(k)))
            {
                string form = Regex.IsMatch(job.Name ?? "", @"캡슐") ? "캡슐" : Regex.IsMatch(job.Name ?? "", @"\d\s*정(?![가-힣])") ? "정" : "환";
                string baseWord = job.DetailMain ?? job.TypeWord ?? core;
                if (!string.IsNullOrEmpty(baseWord) && !baseWord.EndsWith(form))
                {
                    string q = baseWord + form;
                    step("2/6 '" + q + "' 네이버 쇼핑에서 카테고리 찾는 중");
                    var page = await shop.Read(q, true, step, () => false);
                    for (int i = 0; i < page.Titles.Count; i++) if (!page.Ads[i]) job.CatTitles.Add(page.Titles[i]);
                    foreach (var p in page.Categories.Where(p => p.StartsWith("식품"))) { int n; tally.TryGetValue(p, out n); tally[p] = n + 1; }
                }
            }
            if (tally.Count > 0)
            {
                var pool = processed && tally.Keys.Any(k => !rawFood(k)) ? tally.Where(kv => !rawFood(kv.Key)) : tally;
                var top = pool.OrderByDescending(kv => kv.Value).First();
                job.CatPath = top.Key;
                // 상세 페이지에 건강기능식품 표시가 있으면 건강식품 쪽 카테고리를 우선 (그쪽이 20% 이상 나왔을 때)
                if (job.Health && !top.Key.Contains("건강식품"))
                {
                    var hf = tally.Where(kv => kv.Key.Contains("건강식품")).OrderByDescending(kv => kv.Value).FirstOrDefault();
                    if (hf.Key != null && hf.Value >= top.Value * 0.2) job.CatPath = hf.Key;
                }
            }
            // msgood 분류가 유산균이면 프로바이오틱스(또는 프리바이오틱스)로 고정: 가르시니아·카테킨·바나바잎 카테고리로 잡히면 상품명도 그쪽 단어로 채워짐
            string forcedCat = ForcedCatPath(job);
            if (forcedCat != null)
            {
                string lf = (job.CatPath ?? "").Split('>').Last();
                if (lf != "프로바이오틱스" && lf != "프리바이오틱스")
                {
                    step("2/6 msgood 분류가 유산균이라 카테고리를 프로바이오틱스로 정함 (검색 결과: " + (job.CatPath ?? "없음") + ")");
                    job.CatPath = forcedCat;
                    job.NaverCat = null; job.NaverCatName = null;
                }
                repQuery = null;   // 쿠팡도 대표 성분(가르시니아)이 아니라 유산균으로 찾음
            }

            var reg = Accounts.Registrar("1", settings(), false);
            if (reg == null) return;

            // 0순위: 쿠팡에 직접 들어가서 [주원료 + 제형](예: "홍화씨유 캡슐") 검색 후 상위 5개 상품 카테고리 실시간 확인
            string cpSearchQuery = forcedCat != null ? "유산균" : repQuery;
            if (string.IsNullOrWhiteSpace(cpSearchQuery))
            {
                string leafWord = job.CatPath != null ? job.CatPath.Split('>').Last() : "";
                cpSearchQuery = (job.DetailMain ?? leafWord ?? core).Trim();
            }

            Tuple<string, string> liveCoupangCat = null;
            if (!string.IsNullOrWhiteSpace(cpSearchQuery) && naverView != null)
            {
                try
                {
                    step("2/6 쿠팡에서 '" + cpSearchQuery + "' 상위 5개 상품 카테고리 실시간 확인 중...");
                    liveCoupangCat = await new CoupangSuggest(naverView).FindTopCoupangCategory(cpSearchQuery, step);
                }
                catch { }
            }

            await Task.Run(() =>
            {
                try
                {
                    if (job.NaverCat == null && job.CatPath != null) { var t = reg.NaverCategoryByPath(job.CatPath); if (t != null) { job.NaverCat = t.Item1; job.NaverCatName = t.Item2; } }
                    if (job.NaverCat == null) { var n = reg.SuggestNaverCategory(job.Name); if (n != null) { job.NaverCat = n.Item1; job.NaverCatName = n.Item2; } }
                    else if (job.NaverCatName == null) job.NaverCatName = reg.NaverCategoryName(job.NaverCat);
                }
                catch { }
                try
                {
                    // 1순위: 쿠팡 상위 5개 상품 크롤링 다수결 카테고리 채택 (비환/비분말 상품의 건강환/건강분말 오분류 방지)
                    if (liveCoupangCat != null && !string.IsNullOrWhiteSpace(liveCoupangCat.Item1))
                    {
                        bool badPill = (liveCoupangCat.Item2 ?? "").Contains("환") && !Regex.IsMatch(job.Name ?? "", @"\b환\b|[가-힣]환");
                        bool badPowder = (liveCoupangCat.Item2 ?? "").Contains("분말") && !Regex.IsMatch(job.Name ?? "", "분말|가루");
                        if (!badPill && !badPowder)
                        {
                            job.CoupangCat = liveCoupangCat.Item1;
                            job.CoupangCatName = !string.IsNullOrWhiteSpace(liveCoupangCat.Item2) ? liveCoupangCat.Item2 : (job.CatPath != null ? job.CatPath.Split('>').Last() : "");
                        }
                    }
                    // msgood 분류가 유산균이면 쿠팡도 '유산균' 카테고리로 고정 (상위 5개 상품 다수결이 MCT오일/영양제 등으로 빗나가는 일이 많음)
                    if (forcedCat != null && (job.CoupangCatName ?? "") != "유산균") { job.CoupangCat = "58991"; job.CoupangCatName = "유산균"; }

                    // 2순위: API 기반 예측 (CoupangCat이 미결정되었을 때만 fallback)
                    if (job.CoupangCat == null)
                    {
                        string leaf = job.NaverCatName != null ? job.NaverCatName.Split('>').Last() : "";
                        string rep = forcedCat != null ? "유산균" : BuildRepCategoryQuery(job);
                        string basis = !string.IsNullOrEmpty(rep) ? rep : (job.DetailMain ?? leaf);
                        var k = reg.SuggestCoupangCategory((basis + " " + leaf).Trim());
                        if (k != null) { job.CoupangCat = k.Item1; job.CoupangCatName = k.Item2; }

                        if (job.CoupangCatName != null && leaf.Length > 0 && !Regex.IsMatch(job.CoupangCatName, "식품|건강|영양|비타민|유산균|차|즙|분말|" + Regex.Escape(leaf)))
                        {
                            var k2 = reg.SuggestCoupangCategory(leaf + " 건강식품");
                            if (k2 != null) { job.CoupangCat = k2.Item1; job.CoupangCatName = k2.Item2; }
                        }

                        bool badPowder = job.CoupangCatName != null && job.CoupangCatName.Contains("분말") && !Regex.IsMatch(job.Name ?? "", "분말|가루");
                        bool badPill = job.CoupangCatName != null && job.CoupangCatName.Contains("환") && !Regex.IsMatch(job.Name ?? "", @"\b환\b|[가-힣]환");

                        if (badPowder || badPill)
                        {
                            var k3 = reg.SuggestCoupangCategory(basis);
                            if (k3 != null && !k3.Item2.Contains("분말") && !k3.Item2.Contains("환")) { job.CoupangCat = k3.Item1; job.CoupangCatName = k3.Item2; }
                            else
                            {
                                var k4 = reg.SuggestCoupangCategory(job.Name);
                                if (k4 != null && !k4.Item2.Contains("분말") && !k4.Item2.Contains("환")) { job.CoupangCat = k4.Item1; job.CoupangCatName = k4.Item2; }
                                else
                                {
                                    var k5 = reg.SuggestCoupangCategory("기타건강보조식품");
                                    if (k5 != null && !k5.Item2.Contains("분말") && !k5.Item2.Contains("환")) { job.CoupangCat = k5.Item1; job.CoupangCatName = k5.Item2; }
                                }
                            }
                        }
                    }
                }
                catch { }
            });
        }

        public static string ImagePath(Job job, string kind, int n) { return Path.Combine(Store.JobDir(job.Code), kind + "_" + n + ".jpg"); }

        // 예전에 만든 상품(naver_1 없음): msgood 대표 사진으로 네이버용 1개·1+1 이미지를 만듦 (글자 읽기 없이 금방)
        // msgood 사진이 다른 상품인 경우(RepMismatch)는 상세 사진이 필요하므로 대표이미지 다시 만들기 때 함께 만들어짐
        public static void EnsureNaverImages(Job job)
        {
            if (File.Exists(ImagePath(job, "naver", 1)) || job.RepMismatch != null || !File.Exists(ImagePath(job, "rep", 1))) return;
            string url = string.IsNullOrEmpty(job.MainImageUrl) ? RepImages.Msgood.MainImageUrl(job.Url, out _) : job.MainImageUrl;
            var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            var q = new EncoderParameters(1); q.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 93L);
            using (var photo = RepImages.Msgood.DownloadImage(url))
            {
                var plain = RepImages.Composer.Plain(photo);
                for (int n = 1; n <= 2; n++)
                    using (var bmp = RepImages.Composer.Render(plain, null, n))
                        bmp.Save(ImagePath(job, "naver", n), enc, q);
                plain.Photo.Dispose();
            }
        }

        // 네이버 스토어 대표이미지: 상표 그대로인 naver_n, 없으면(예전에 만든 상품) 상표 가린 rep_n
        public static string NaverImagePath(Job job, int n) { var f = ImagePath(job, "naver", n); return File.Exists(f) ? f : ImagePath(job, "rep", n); }

        public static List<string> ExtraFiles(Job job)
        {
            return Enumerable.Range(1, 4).Select(i => ImagePath(job, "extra", i)).Where(File.Exists).ToList();
        }
    }
}
