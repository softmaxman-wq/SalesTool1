using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SalesTool
{
    // 사장님이 직접 고른 검색어 사전 (keyword_book.json, 프로그램 폴더). 원본: 키워드 엑셀(검색량·상품수·경쟁비·분류)
    //  - 같은 상품번호 묶음의 검색어: 그대로 후보에 (사장님이 이 상품에 고른 말)
    //  - 다른 상품 묶음의 검색어: 시작 단어(유산균)가 든 조합어만, 붙은 말(공복·저녁·장용성100억·가세리)이 이 상품에 맞을 때만
    //    (숫자·원료·균 이름은 이 상품 상품명·상세에 있어야 함, 시간대·일반 대상 말은 그대로)
    //  - 오타 분류: 실제 검색량이 있는 오타(가르니시아·콘드레이친)는 넣음. 해시태그 분류(살빼는약 등)는 안 씀
    //  사전 파일은 깃허브에 올리지 않음 (사장님 자료) — 다른 PC는 같은 파일을 프로그램 폴더에 복사
    //  - 광고 분류: 쿠팡 광고로 실제 주문이 난 검색어(o = 주문수). 검증된 말이라 맨 앞. 광고브랜드(경쟁 브랜드 검색어)는 광고 입찰용으로만 두고 상품명에는 안 씀
    public class BookKw { public string w; public long v; public long t = -1; public string c; public int o; }
    public class BookTopic { public string m; public List<string> codes = new List<string>(); public string cat; public List<BookKw> kws = new List<BookKw>(); public List<string> names = new List<string>(); }
    public class BookData { public string source; public List<BookTopic> topics = new List<BookTopic>(); }

    public static class KeywordBook
    {
        static BookData data; static DateTime loadedAt = DateTime.MinValue;
        public static string PathOf { get { return Path.Combine(Store.Dir, "keyword_book.json"); } }
        static string Key(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z가-힣]", "").ToLowerInvariant(); }

        public static BookData Data
        {
            get
            {
                try
                {
                    var t = File.Exists(PathOf) ? File.GetLastWriteTime(PathOf) : DateTime.MinValue;
                    if (data == null || t != loadedAt) { data = t == DateTime.MinValue ? new BookData() : Store.Load<BookData>(PathOf); loadedAt = t; }
                }
                catch { data = new BookData(); }
                return data ?? new BookData();
            }
        }

        // 다른 상품 묶음의 조합어에서 그대로 써도 되는 붙은 말 (시간대·일반 대상)
        static readonly Regex General = new Regex("^(공복|저녁|아침|식전|식후|자기전|취침전|하루한번|하루한알|매일|데일리|국산|국내산|고함량|고용량|순수|프리미엄)+$");
        // 대상 말: 상품명에 있을 때만 (어린이 상품에 어르신유산균이 붙지 않게). 대상이 안 정해진 상품엔 일반 성인 대상만
        static readonly Regex Persona = new Regex("(아이|어린이|키즈|유아|아기|영유아|초등|임산부|임신|산모|수유|여성|여자|우먼|남성|남자|맨|어르신|노인|시니어|실버|부모님|중년|청소년|수험생|학생|중학생|고등학생|대학생|직장인|성인|온가족|가족|[2-7]0대)");
        static readonly Regex AdultPersona = new Regex("^(직장인|성인|온가족|가족|[3-5]0대)+$");   // 어르신·중년은 상품명에 있을 때만 (다이어트 상품에 어르신유산균이 붙지 않게)
        static readonly Regex SpecificPersona = new Regex("키즈|어린이|아이|유아|임산부|임신|산모|우먼|여성|맨|남성|시니어|실버|청소년|수험생|학생");

        static string BaseCode(string c) { return Regex.Replace(c ?? "", @"-\d+$", ""); }

        // 이 상품에 쓸 사전 검색어 (FromBook = true, 같은 상품번호 묶음이면 BookMine)
        public static List<KwRow> Candidates(Job job, string seed, string coreWord)
        {
            var res = new Dictionary<string, KwRow>();
            var d = Data; if (d.topics == null || d.topics.Count == 0) return new List<KwRow>();
            string text = Key((job.Name ?? "") + " " + (job.OcrText ?? ""));
            var cores = new[] { coreWord, (seed ?? "").Split(' ')[0] }.Where(x => !string.IsNullOrEmpty(x)).Select(Key).Where(x => x.Length >= 2).Distinct().ToList();
            string code = job.Code ?? "";
            string nameKey = Key(job.Name);
            bool specific = SpecificPersona.IsMatch(job.Name ?? "");
            Action<BookKw, bool> add = (k, mine) =>
            {
                string kk = Key(k.w);
                if (kk.Length < 2 || k.c == "해시태그" || k.c == "광고브랜드" || Naming.IsKnownBrand(k.w ?? "")) return;
                KwRow r;
                if (!res.TryGetValue(kk, out r)) { r = new KwRow { Word = (k.w ?? "").Trim(), Volume = k.v, Total = k.t, Mentioned = true, FromBook = true }; res[kk] = r; }
                if (mine) r.BookMine = true;
                if (k.c == "오타") r.Typo = true;
                if (k.c == "광고" && k.o > 0) r.BookProven = true;
                if (k.v > r.Volume) r.Volume = k.v;
                if (r.Total < 0 && k.t >= 0) r.Total = k.t;
                r.BookCount++;
            };
            foreach (var t in d.topics)
            {
                bool mine = t.codes != null && t.codes.Any(c => c == code || (BaseCode(code).Length >= 4 && BaseCode(c) == BaseCode(code)));
                foreach (var k in t.kws ?? new List<BookKw>())
                {
                    if (mine) { add(k, true); continue; }
                    string kk = Key(k.w);
                    if (k.c == "오타") { if (Naming.IsNearTypo(k.w ?? "", seed ?? "")) add(k, false); continue; }
                    string core = cores.FirstOrDefault(c => kk.Contains(c) && kk != c);
                    if (core == null) continue;
                    string mod = kk.Replace(core, "");
                    if (mod.Length == 0 || mod == "간장") continue;   // 간장: 글자 인식이 '간·장'을 붙여 읽은 말
                    bool ok;
                    if (Persona.IsMatch(mod)) ok = nameKey.Contains(mod) || (!specific && AdultPersona.IsMatch(mod));   // 대상 말: msgood 상품명에 있거나, 대상이 정해지지 않은 상품의 일반 성인 대상만
                    else if (Regex.IsMatch(mod, @"\d") && !Regex.IsMatch(mod, "^([2-7]0대)+$")) ok = mod.Length >= 2 && text.Contains(mod);   // 숫자 든 말(100억·1200)은 상품에 그대로 있어야
                    else ok = (mod.Length >= 2 && text.Contains(mod)) || General.IsMatch(mod);                         // 균 이름·원료(두 글자 이상)는 상품에 있어야, 시간대 말은 그대로
                    if (ok) add(k, false);
                }
            }
            // 다른 상품 묶음에서만 온 말은 여러 묶음에 나온 것·경쟁비 좋은 것 순으로 20개까지
            var mineList = res.Values.Where(r => r.BookMine).ToList();
            var others = res.Values.Where(r => !r.BookMine).OrderByDescending(r => r.BookCount).ThenBy(r => r.Ratio).Take(20).ToList();
            return mineList.Concat(others).ToList();
        }
    }
}
