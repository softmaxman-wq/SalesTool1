using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SalesTool
{
    // 키워드 사용 장부: 어느 상품이 상품명·쿠팡 검색어에 어떤 말을 썼는지 (상품끼리 키워드가 겹치지 않게)
    public class KeywordLedger
    {
        public Dictionary<string, List<string>> Names = new Dictionary<string, List<string>>();   // 낱말(공백·기호 뺀 소문자) → 상품번호들
        public Dictionary<string, List<string>> Tags = new Dictionary<string, List<string>>();    // 쿠팡 검색어 → 상품번호들
        public Dictionary<string, List<string>> Tags2 = new Dictionary<string, List<string>>();   // 쿠팡 #2 검색어 → 상품번호들 (상품마다 다르게 나누는 데 씀)
        static readonly object lk = new object();
        public static string FilePath { get { return Path.Combine(Store.Dir, "keyword_ledger.json"); } }
        public static string Key(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z가-힣]", "").ToLowerInvariant(); }

        // 지운 상품(작업 폴더가 없는 것)은 장부에서 뺌. 등록 완료된 상품은 폴더가 남아 있으므로 계속 셈
        public static KeywordLedger Load()
        {
            lock (lk)
            {
                var l = File.Exists(FilePath) ? Store.Load<KeywordLedger>(FilePath) : new KeywordLedger();
                if (l.Names == null) l.Names = new Dictionary<string, List<string>>();
                if (l.Tags == null) l.Tags = new Dictionary<string, List<string>>();
                if (l.Tags2 == null) l.Tags2 = new Dictionary<string, List<string>>();
                var alive = new Dictionary<string, bool>();
                Func<string, bool> ok = c => { bool b; if (!alive.TryGetValue(c, out b)) alive[c] = b = Directory.Exists(Store.JobDir(c)); return b; };
                foreach (var d in new[] { l.Names, l.Tags, l.Tags2 })
                    foreach (var k in d.Keys.ToList()) { d[k] = d[k].Where(ok).Distinct().ToList(); if (d[k].Count == 0) d.Remove(k); }
                return l;
            }
        }

        static int Uses(Dictionary<string, List<string>> d, string word, string code)
        {
            List<string> l; return d.TryGetValue(Key(word), out l) ? l.Count(c => c != code) : 0;
        }
        public int NameUses(string word, string code) { return Uses(Names, word, code); }
        public int TagUses(string tag, string code) { return Uses(Tags, tag, code); }
        public int Tag2Uses(string tag, string code) { return Uses(Tags2, tag, code); }
        // 쿠팡 #2 검색어를 가진 다른 상품 수
        public int Tag2Products(string code) { return Tags2.Values.SelectMany(x => x).Where(c => c != code).Distinct().Count(); }

        // 이 상품이 쓴 말을 다시 적음 (전에 적은 것은 지우고)
        public static void Record(string code, IEnumerable<string> nameWords, IEnumerable<string> tags, IEnumerable<string> tags2 = null)
        {
            lock (lk)
            {
                try
                {
                    var l = Load();
                    if (nameWords != null) Set(l.Names, code, nameWords);
                    if (tags != null) Set(l.Tags, code, tags);
                    if (tags2 != null) Set(l.Tags2, code, tags2);
                    Store.Save(FilePath, l);
                }
                catch { }
            }
        }

        static void Set(Dictionary<string, List<string>> d, string code, IEnumerable<string> words)
        {
            foreach (var k in d.Keys.ToList()) { d[k].Remove(code); if (d[k].Count == 0) d.Remove(k); }
            foreach (var w in words.Select(Key).Where(k => k.Length >= 2 && !Regex.IsMatch(k, @"^\d")).Distinct())
            {
                List<string> l; if (!d.TryGetValue(w, out l)) d[w] = l = new List<string>();
                l.Add(code);
            }
        }
    }
}
