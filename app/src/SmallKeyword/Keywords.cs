using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace SmallKeyword
{
    public class KeywordStat
    {
        public string Word;
        public int Products;   // 이 단어가 들어간 상품 수
        public int Total;      // 전체 등장 횟수
        public int FirstIndex; // 처음 나온 순서 (같은 수일 때 정렬용)
    }

    public static class Keywords
    {
        // 상품명을 나누는 기준: 공백, 괄호, 쉼표, 슬래시, 세로줄 등
        // ( - 와 + 는 "SM-R630", "1+1" 처럼 한 단어 안에 쓰이므로 나누지 않음 )
        static readonly Regex Splitter = new Regex(@"[\s\[\]\(\)\{\}<>【】「」『』〔〕,，/\\|·•ㆍ""“”'‘’`★☆♥♡◆◇■□▶▷※#~!?;:=_]+");
        static readonly Regex Tag = new Regex(@"<[^>]+>");
        static readonly Regex HasWordChar = new Regex(@"[\p{L}\p{N}]");

        public static string CleanTitle(string raw)
        {
            string s = Tag.Replace(raw ?? "", "");
            s = WebUtility.HtmlDecode(s);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        public static List<string> Split(string title)
        {
            var result = new List<string>();
            foreach (string part in Splitter.Split(title))
            {
                string w = part.Trim('-', '+', '.', '&', '%', '*', '^', '$', '@');
                if (w.Length == 0 || !HasWordChar.IsMatch(w)) continue;
                result.Add(w);
            }
            return result;
        }

        public static List<KeywordStat> Count(IList<string> titles)
        {
            var map = new Dictionary<string, KeywordStat>(StringComparer.OrdinalIgnoreCase);
            int order = 0;
            foreach (string title in titles)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string w in Split(title))
                {
                    KeywordStat st;
                    if (!map.TryGetValue(w, out st))
                    {
                        st = new KeywordStat { Word = w, FirstIndex = order++ };
                        map[w] = st;
                    }
                    st.Total++;
                    if (seen.Add(w)) st.Products++;
                }
            }
            return map.Values
                .OrderByDescending(k => k.Products)
                .ThenByDescending(k => k.Total)
                .ThenBy(k => k.FirstIndex)
                .ToList();
        }

        public static bool TitleHas(string title, string word)
        {
            return Split(title).Any(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
        }
    }
}
