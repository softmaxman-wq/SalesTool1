using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SalesTool
{
    // 내 판매 자료(엑셀) 넣기: 이 PC 사장님 사업자에서 팔린 상품명·팔린 검색어를 이 PC의 검색어 사전(keyword_book.json)에 넣음
    //  - 학습 자료는 PC마다 따로 (깃허브·업데이트 파일에 안 들어감) → 다른 사장님 PC의 학습에 영향 없음
    //  - 엑셀 양식: 머리줄에 '상품명' 또는 '검색어'(키워드) 열. 있으면 '상품번호'(상품코드·판매자관리코드)·'검색량'·'상품수'·'주문수'(판매수) 열도 읽음
    //    시트 이름에 '쿠팡'이 있으면 쿠팡 자료, 아니면 네이버 자료
    //  - 팔린 검색어는 '실제로 팔린 말'(광고 분류)로 넣어 상품명 앞쪽에 씀. 팔린 상품명의 단어는 상품명 단어 순서에 반영
    //  - 같은 엑셀 파일 이름으로 다시 넣으면 그 파일에서 넣었던 것만 바꿈
    public static class SalesBookImport
    {
        public const string Mark = "sold";

        public static string Import(string path)
        {
            string src = Path.GetFileName(path);
            var topics = new Dictionary<string, BookTopic>();
            int nameN = 0, kwN = 0;
            foreach (var sheet in ReadSheets(path))
            {
                string market = sheet.Item1.Contains("쿠팡") || sheet.Item1.ToLowerInvariant().Contains("coupang") ? "c" : "n";
                Dictionary<string, int> col = null;
                foreach (var cells in sheet.Item2)
                {
                    if (col == null)
                    {
                        if (cells.Values.Any(v => Regex.IsMatch(v ?? "", "상품명|검색어|키워드")))
                            col = cells.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).GroupBy(kv => kv.Value.Trim()).ToDictionary(g => g.Key, g => g.First().Key);
                        continue;
                    }
                    Func<string, string> get = re =>
                    {
                        var k = col.Keys.FirstOrDefault(x => Regex.IsMatch(x, re)); string v;
                        return k != null && cells.TryGetValue(col[k], out v) ? (v ?? "").Trim() : "";
                    };
                    Func<string, long> num = re => { double d; return double.TryParse(get(re).Replace(",", ""), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d) ? (long)Math.Round(d) : -1; };
                    string code = get("상품번호|상품코드|관리코드|^코드");
                    var m = Regex.Match(code, @"^\d{4,5}(-\d)?"); code = m.Success ? m.Value : "";
                    if (code == "") { var m2 = Regex.Match(get("^상품명|판매.*상품명|팔린.*상품명"), @"^(\d{4,5}(-\d)?)\s"); if (m2.Success) code = m2.Groups[1].Value; }
                    string key = market + ":" + code;
                    BookTopic t;
                    if (!topics.TryGetValue(key, out t)) { t = new BookTopic { m = Mark, cat = src }; if (code != "") t.codes.Add(code); topics[key] = t; }
                    string name = get("^상품명|판매.*상품명|팔린.*상품명");
                    if (name.Length >= 4 && !t.names.Contains(name)) { t.names.Add(name); nameN++; }
                    foreach (var w in Regex.Split(get("검색어|키워드"), @"\s*[,/|]\s*").Select(x => x.Trim()).Where(x => x.Length >= 2))
                    {
                        if (t.kws.Any(k => k.w == w)) continue;
                        long orders = num("주문수|판매수|판매량"), vol = num("검색량"), tot = num("상품수");
                        t.kws.Add(new BookKw { w = w, v = Math.Max(0, vol), t = tot, c = "광고", o = (int)Math.Max(1, orders) });
                        kwN++;
                    }
                }
            }
            if (nameN + kwN == 0) throw new Exception("읽을 줄이 없습니다. 첫 줄(머리줄)에 '상품명' 또는 '검색어' 열이 있어야 합니다.");
            var book = File.Exists(KeywordBook.PathOf) ? (Store.Load<BookData>(KeywordBook.PathOf) ?? new BookData()) : new BookData();
            if (book.topics == null) book.topics = new List<BookTopic>();
            book.topics.RemoveAll(x => x.m == Mark && x.cat == src);
            book.topics.AddRange(topics.Values.Where(x => x.kws.Count > 0 || x.names.Count > 0));
            if (string.IsNullOrEmpty(book.source)) book.source = src;
            Store.Save(KeywordBook.PathOf, book);
            return src + ": 팔린 상품명 " + nameN + "개, 팔린 검색어 " + kwN + "개를 이 PC 학습 자료에 넣었습니다 (상품 묶음 " + topics.Count + "개).";
        }

        // 넣은 판매 자료의 상품명 단어 → 나온 횟수 (상품명 단어 순서에 반영)
        static Dictionary<string, int> words; static DateTime wordsAt = DateTime.MinValue;
        public static int NameWordScore(string w)
        {
            try
            {
                var t = File.Exists(KeywordBook.PathOf) ? File.GetLastWriteTime(KeywordBook.PathOf) : DateTime.MinValue;
                if (words == null || t != wordsAt)
                {
                    words = new Dictionary<string, int>();
                    foreach (var tp in KeywordBook.Data.topics.Where(x => x.m == Mark))
                        foreach (var n in tp.names ?? new List<string>())
                            foreach (var x in Regex.Split(n, @"\s+").Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length >= 2 && !Regex.IsMatch(s, "^[0-9]")).Distinct())
                            { int c; words.TryGetValue(x, out c); words[x] = c + 1; }
                    wordsAt = t;
                }
                int r; return words.TryGetValue((w ?? "").ToLowerInvariant(), out r) ? r : 0;
            }
            catch { return 0; }
        }

        static List<Tuple<string, List<Dictionary<int, string>>>> ReadSheets(string path)
        {
            var res = new List<Tuple<string, List<Dictionary<int, string>>>>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                XNamespace rns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
                XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
                var shared = new List<string>();
                var se = zip.GetEntry("xl/sharedStrings.xml");
                if (se != null) using (var s = se.Open()) shared = XDocument.Load(s).Root.Elements(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToList();
                var rels = new Dictionary<string, string>();
                var re = zip.GetEntry("xl/_rels/workbook.xml.rels");
                if (re != null) using (var s = re.Open()) foreach (var r in XDocument.Load(s).Root.Elements(pr + "Relationship")) rels[(string)r.Attribute("Id")] = ((string)r.Attribute("Target") ?? "").TrimStart('/').Replace("xl/", "");
                var sheets = new List<Tuple<string, string>>();
                var wbE = zip.GetEntry("xl/workbook.xml");
                if (wbE != null) using (var s = wbE.Open())
                    foreach (var sh in XDocument.Load(s).Descendants(ns + "sheet"))
                    { string id = (string)sh.Attribute(rns + "id"), tg; if (id != null && rels.TryGetValue(id, out tg)) sheets.Add(Tuple.Create((string)sh.Attribute("name") ?? "", "xl/" + tg)); }
                if (sheets.Count == 0) sheets = zip.Entries.Where(x => x.FullName.StartsWith("xl/worksheets/sheet")).Select(x => Tuple.Create("", x.FullName)).ToList();
                foreach (var sh in sheets)
                {
                    var e = zip.GetEntry(sh.Item2); if (e == null) continue;
                    XDocument doc; using (var s = e.Open()) doc = XDocument.Load(s);
                    var rows = new List<Dictionary<int, string>>();
                    foreach (var r in doc.Descendants(ns + "row"))
                    {
                        var cells = new Dictionary<int, string>();
                        foreach (var c in r.Elements(ns + "c"))
                        {
                            var m = Regex.Match((string)c.Attribute("r") ?? "", "^[A-Z]+"); if (!m.Success) continue;
                            int ci = 0; foreach (char ch in m.Value) ci = ci * 26 + (ch - 'A' + 1);
                            string t = (string)c.Attribute("t"); var v = c.Element(ns + "v");
                            cells[ci - 1] = t == "s" && v != null ? shared[int.Parse(v.Value)] : t == "inlineStr" ? string.Concat(c.Descendants(ns + "t").Select(x => x.Value)) : v != null ? v.Value : "";
                        }
                        rows.Add(cells);
                    }
                    res.Add(Tuple.Create(sh.Item1, rows));
                }
            }
            return res;
        }
    }
}
