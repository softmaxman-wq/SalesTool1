using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using ProductUploader;

namespace SalesTool
{
    // 쿠팡 광고 키워드 목록 (반자동): 쿠팡 광고센터에서 받은 광고 보고서(엑셀)를 읽어 상품별로
    //  ① 넣을 키워드: 광고로 실제 주문이 난 검색어 + 검색어 사전(사장님이 고른 말·경쟁 브랜드 검색어, 경쟁비 좋은 순)
    //  ② 제외 키워드: 클릭·광고비만 쓰고 주문이 없는 검색어
    //  ③ 광고 끄기·입찰 낮추기 추천 상품
    //  을 Downloads\쿠팡광고키워드_날짜.csv 로 만듦. 광고센터 등록(입찰·예산 변경)은 사장님이 직접 (돈이 걸린 일이라 자동으로 안 함)
    //  보고서 찾는 곳: 프로그램 폴더\광고보고서, Downloads (이름에 광고·report·보고서가 든 xlsx)
    public static class AdKeywords
    {
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public static string ReportDir { get { return Path.Combine(Store.Dir, "광고보고서"); } }
        static string Downloads { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); } }
        static string MapPath { get { return Path.Combine(Store.Dir, "coupang_items.json"); } }
        static string StatePath { get { return Path.Combine(Store.Dir, "adkw_state.json"); } }
        public class State { public string LastInputs = ""; public string LastFile = ""; public string LastRun = ""; }
        public class ItemMap { public Dictionary<string, string> Items = new Dictionary<string, string>(); public Dictionary<string, string> Products = new Dictionary<string, string>(); }

        // 기준값
        const int ExcludeClicks = 10, ExcludeCost = 3000;        // 주문 0인데 클릭 10번 이상, 또는 클릭 3번 이상·광고비 3천원 이상 → 제외 키워드 (클릭 1~2번은 우연일 수 있어 뺌)
        const int OffCost = 20000;                               // 주문 0인데 광고비 2만원 이상 → 광고 끄기 추천
        const double LowRoas = 2.0;                              // 광고비 2만원 이상인데 ROAS 200% 미만 → 입찰 낮추기 검토

        class Row { public string Period, Campaign, Group, AdOpt, AdName, ConvOpt, Area, Kw; public long Imp, Click, Cost, Orders, Sales; }

        // ── 보고서 찾기
        public static List<string> FindReports()
        {
            var files = new List<string>();
            try { Directory.CreateDirectory(ReportDir); files.AddRange(Directory.GetFiles(ReportDir, "*.xlsx")); } catch { }
            try { files.AddRange(Directory.GetFiles(Downloads, "*.xlsx").Where(f => Regex.IsMatch(Path.GetFileName(f), "광고|report|보고서", RegexOptions.IgnoreCase))); } catch { }
            return files.Where(f => !Path.GetFileName(f).StartsWith("~$")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string InputsKey(List<string> files) { return string.Join(";", files.OrderBy(f => f).Select(f => f + "@" + File.GetLastWriteTime(f).Ticks)); }

        // 새 보고서가 들어왔을 때만 (자동)
        public static string RunIfNew(AppSettings set, Action<string> log)
        {
            var files = FindReports();
            var st = Store.Load<State>(StatePath);
            if (files.Count == 0 || InputsKey(files) == st.LastInputs) return null;
            return Run(set, log);
        }

        public static string Run(AppSettings set, Action<string> log)
        {
            var files = FindReports();
            if (files.Count == 0) { log("광고 보고서가 없습니다. 쿠팡 광고센터 → 광고 보고서에서 받은 엑셀을 Downloads나 " + ReportDir + " 에 넣어 주세요."); return null; }
            var rows = new Dictionary<string, Row>();
            foreach (var f in files)
            {
                try { int n = 0; foreach (var r in ReadReport(f)) { string k = string.Join("|", r.Period, r.Campaign, r.Group, r.AdOpt, r.ConvOpt, r.Area, r.Kw); if (!rows.ContainsKey(k)) { rows[k] = r; n++; } } if (n > 0) log("광고 보고서 읽음: " + Path.GetFileName(f) + " (" + n + "줄)"); }
                catch (Exception ex) { log("광고 보고서를 못 읽음: " + Path.GetFileName(f) + " — " + ex.Message); }
            }
            if (rows.Count == 0) { log("광고 보고서에서 광고 실적 줄을 찾지 못했습니다 (키워드·광고집행 옵션ID 열이 있는 엑셀이어야 합니다)."); return null; }

            var map = LoadMap(set, rows.Values.Select(r => r.AdOpt).Distinct().ToList(), log);
            Func<Row, string> codeOf = r => { string c; return map.Items.TryGetValue(r.AdOpt ?? "", out c) ? c : ""; };
            Func<Row, string> prodKey = r => { string c = codeOf(r); return c != "" ? c : "상품 " + Regex.Replace((r.AdName ?? "").Split(',')[0], @"\s+", ""); };   // 상품번호를 모르면(지운 상품) 광고 상품명으로 묶음

            var outRows = new List<string[]>();
            var book = KeywordBook.Data;
            foreach (var g in rows.Values.GroupBy(prodKey).OrderByDescending(g => g.Sum(r => r.Cost)))
            {
                string code = codeOf(g.First());
                string name = g.Select(r => r.AdName).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
                string opts = string.Join(" ", g.Select(r => r.AdOpt).Distinct().Take(5));
                long cost = g.Sum(r => r.Cost), orders = g.Sum(r => r.Orders), sales = g.Sum(r => r.Sales), clicks = g.Sum(r => r.Click);
                string roas = cost > 0 ? Math.Round(sales * 100.0 / cost) + "%" : "";
                // ③ 상품 추천
                if (cost >= OffCost && orders == 0) outRows.Add(new[] { "광고 끄기 추천", code, name, opts, "", "광고비 " + cost.ToString("N0") + "원·클릭 " + clicks + "번인데 주문 0", "0", clicks.ToString(), cost.ToString(), "" });
                else if (cost >= OffCost && sales < cost * LowRoas) outRows.Add(new[] { "입찰 낮추기 검토", code, name, opts, "", "ROAS " + roas + " (200% 미만)", orders.ToString(), clicks.ToString(), cost.ToString(), roas });

                var kws = g.Where(r => r.Area != null && r.Area.Contains("검색") && !r.Area.Contains("비검색") && !string.IsNullOrWhiteSpace(r.Kw) && r.Kw != "-")
                           .GroupBy(r => r.Kw.Trim(), StringComparer.OrdinalIgnoreCase)
                           .Select(k => new { Kw = k.Key, Click = k.Sum(r => r.Click), Cost = k.Sum(r => r.Cost), Orders = k.Sum(r => r.Orders), Sales = k.Sum(r => r.Sales) }).ToList();
                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // ① 넣을 키워드: 광고로 팔린 말
                foreach (var k in kws.Where(k => k.Orders > 0).OrderByDescending(k => k.Orders).ThenByDescending(k => k.Sales))
                {
                    have.Add(Key(k.Kw));
                    outRows.Add(new[] { "넣을 키워드", code, name, opts, k.Kw, "광고로 팔림 " + k.Orders + "건", k.Orders.ToString(), k.Click.ToString(), k.Cost.ToString(), k.Cost > 0 ? Math.Round(k.Sales * 100.0 / k.Cost) + "%" : "" });
                }
                // ② 제외 키워드
                var excl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in kws.Where(k => k.Orders == 0 && (k.Click >= ExcludeClicks || (k.Click >= 3 && k.Cost >= ExcludeCost))).OrderByDescending(k => k.Cost))
                {
                    excl.Add(Key(k.Kw));
                    outRows.Add(new[] { "제외 키워드", code, name, opts, k.Kw, "클릭 " + k.Click + "번·광고비 " + k.Cost.ToString("N0") + "원인데 주문 0", "0", k.Click.ToString(), k.Cost.ToString(), "0%" });
                }
                // ① 넣을 키워드: 검색어 사전 (이 상품에 고른 말·경쟁 브랜드 검색어)
                if (code != "")
                {
                    var bk = book.topics.Where(t => t.codes != null && t.codes.Contains(code)).SelectMany(t => t.kws ?? new List<BookKw>())
                                 .Where(k => k.c != "해시태그" && !string.IsNullOrWhiteSpace(k.w))
                                 .GroupBy(k => Key(k.w)).Select(x => x.OrderByDescending(k => k.o).First())
                                 .Where(k => !have.Contains(Key(k.w)) && !excl.Contains(Key(k.w)))
                                 .Where(k => k.c == "광고" || k.c == "광고브랜드" || k.c == "오타" || (k.v >= 50 && k.t >= 0 && k.t <= k.v * 3.0))
                                 .OrderByDescending(k => k.c == "광고" || k.c == "광고브랜드").ThenBy(k => k.v > 0 && k.t >= 0 ? k.t / (double)k.v : 99).Take(25).ToList();
                    foreach (var k in bk)
                        outRows.Add(new[] { "넣을 키워드", code, name, opts, k.w.Trim(),
                            k.c == "광고브랜드" ? "경쟁 브랜드 검색어 (광고로 팔림)" : k.c == "광고" ? "광고로 팔림" : "검색어 사전" + (k.c != "" ? "·" + k.c : "") + (k.v > 0 ? " (검색 " + k.v + "·경쟁비 " + (k.t >= 0 ? Math.Round(k.t / (double)k.v, 1).ToString() : "?") + ")" : ""),
                            k.o > 0 ? k.o.ToString() : "", "", "", "" });
                }
            }
            string file = Path.Combine(Downloads, "쿠팡광고키워드_" + DateTime.Now.ToString("yyyyMMdd") + ".csv");
            var sb = new StringBuilder();
            sb.AppendLine("구분,상품번호,광고 상품명,옵션ID,키워드,근거,주문,클릭,광고비,ROAS");
            foreach (var r in outRows) sb.AppendLine(string.Join(",", r.Select(Csv)));
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
            var st = new State { LastInputs = InputsKey(files), LastFile = file, LastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            Store.Save(StatePath, st);
            log("광고 키워드 목록을 만들었습니다: " + file + " (넣을 " + outRows.Count(r => r[0] == "넣을 키워드") + " · 제외 " + outRows.Count(r => r[0] == "제외 키워드") + " · 광고 끄기 추천 " + outRows.Count(r => r[0] == "광고 끄기 추천") + " · 입찰 낮추기 검토 " + outRows.Count(r => r[0] == "입찰 낮추기 검토") + ")");
            return file;
        }

        public static State LastState() { return Store.Load<State>(StatePath); }

        static string Key(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z가-힣]", "").ToLowerInvariant(); }
        static string Csv(string s) { s = s ?? ""; return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }

        // ── 옵션ID → 상품번호 (쿠팡 등록상품명이 상품번호). 처음 한 번 쿠팡 상품 목록을 읽어 저장해 두고, 모르는 옵션이 나오면 그때 다시 읽음
        static ItemMap LoadMap(AppSettings set, List<string> need, Action<string> log)
        {
            var map = Store.Load<ItemMap>(MapPath);
            if (need.All(o => string.IsNullOrEmpty(o) || map.Items.ContainsKey(o))) return map;
            foreach (var a in set.Accounts)
            {
                MarketConfig c; try { c = Accounts.Config(a.Id); } catch { continue; }
                if (c == null || !c.HasCoupang) continue;
                try
                {
                    var cp = new CoupangClient(c);
                    log("#" + a.Id + " 쿠팡 상품 목록 읽는 중 (광고 옵션 → 상품번호 연결, 처음 한 번은 몇 분 걸립니다)...");
                    string token = ""; int n = 0;
                    for (int p = 1; p <= 100; p++)
                    {
                        var res = Obj(json.DeserializeObject(cp.Send("GET", "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products?vendorId=" + cp.Vendor + "&maxPerPage=100" + (token != "" ? "&nextToken=" + token : ""), null)));
                        foreach (var d in Arr(res["data"]))
                        {
                            string id = S(d, "sellerProductId"), code = StockSync.CodeOf(S(d, "sellerProductName"));
                            if (id == null || code == null || map.Products.ContainsKey(id)) continue;
                            try
                            {
                                var det = Obj(Obj(json.DeserializeObject(cp.Send("GET", "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products/" + id, null)))["data"]);
                                foreach (var it in Arr(det["items"])) { string vid = S(it, "vendorItemId"); if (!string.IsNullOrEmpty(vid)) map.Items[vid] = code; }
                                map.Products[id] = code; n++;
                            }
                            catch { }
                            System.Threading.Thread.Sleep(200);
                        }
                        token = S(res, "nextToken") ?? "";
                        if (token == "") break;
                    }
                    log("#" + a.Id + " 쿠팡 상품 " + n + "개 연결");
                }
                catch (Exception ex) { log("#" + a.Id + " 쿠팡 상품 목록을 못 읽음: " + ex.Message); }
            }
            Store.Save(MapPath, map);
            return map;
        }
        static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        static IEnumerable<Dictionary<string, object>> Arr(object o) { return (o as IEnumerable ?? new object[0]).OfType<Dictionary<string, object>>(); }
        static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null; }

        // ── 엑셀(xlsx) 읽기: 모든 시트에서 "키워드"와 "광고집행 옵션ID" 열이 있는 머리줄을 찾아 그 아래 줄을 읽음
        static IEnumerable<Row> ReadReport(string path)
        {
            var list = new List<Row>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var shared = new List<string>();
                var se = zip.GetEntry("xl/sharedStrings.xml");
                if (se != null) using (var s = se.Open()) shared = XDocument.Load(s).Root.Elements(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToList();
                foreach (var e in zip.Entries.Where(x => x.FullName.StartsWith("xl/worksheets/sheet") && x.FullName.EndsWith(".xml")))
                {
                    XDocument doc; using (var s = e.Open()) doc = XDocument.Load(s);
                    Dictionary<string, int> col = null;
                    foreach (var r in doc.Descendants(ns + "row"))
                    {
                        var cells = new Dictionary<int, string>();
                        foreach (var c in r.Elements(ns + "c"))
                        {
                            string refc = (string)c.Attribute("r") ?? ""; int ci = ColIndex(refc); if (ci < 0) continue;
                            string t = (string)c.Attribute("t"); var v = c.Element(ns + "v");
                            string val = t == "s" && v != null ? shared[int.Parse(v.Value)] : t == "inlineStr" ? string.Concat(c.Descendants(ns + "t").Select(x => x.Value)) : v != null ? v.Value : "";
                            cells[ci] = val;
                        }
                        if (col == null)
                        {
                            if (cells.Values.Any(x => x == "키워드") && cells.Values.Any(x => x.Contains("광고집행 옵션"))) col = cells.Where(kv => !string.IsNullOrEmpty(kv.Value)).GroupBy(kv => kv.Value.Trim()).ToDictionary(g => g.Key, g => g.First().Key);
                            continue;
                        }
                        Func<string, string> get = h => { var k = col.Keys.FirstOrDefault(x => x == h) ?? col.Keys.FirstOrDefault(x => x.Contains(h)); string v; return k != null && cells.TryGetValue(col[k], out v) ? v : ""; };
                        Func<string, long> num = h => { double d; return double.TryParse(get(h), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d) ? (long)Math.Round(d) : 0; };
                        string opt = get("광고집행 옵션ID"); if (string.IsNullOrEmpty(opt)) continue;
                        list.Add(new Row
                        {
                            Period = get("보고서 기간") != "" ? get("보고서 기간") : get("날짜"), Campaign = get("캠페인 ID"), Group = get("광고그룹"),
                            AdOpt = opt, AdName = get("광고집행 상품명"), ConvOpt = get("광고전환매출발생 옵션ID"), Area = get("광고 노출 지면"), Kw = get("키워드"),
                            Imp = num("노출수"), Click = num("클릭수"), Cost = num("광고비"), Orders = num("총 주문수(14일)"), Sales = num("총 전환매출액(14일)")
                        });
                    }
                }
            }
            return list;
        }
        static int ColIndex(string r) { var m = Regex.Match(r ?? "", "^[A-Z]+"); if (!m.Success) return -1; int n = 0; foreach (char ch in m.Value) n = n * 26 + (ch - 'A' + 1); return n - 1; }
    }
}
