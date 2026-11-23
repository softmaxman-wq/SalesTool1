using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace SmallKeyword
{
    public class Volume
    {
        public long Pc, Mobile;
        public bool PcLow, MobileLow; // "< 10" 으로 나온 경우
        public long Total { get { return Pc + Mobile; } }
    }

    // 네이버 검색광고 API 키 (판매도구 api_keys.json 의 naver_searchad, 또는 naver_api.json 의 searchad)
    public class SearchAdKeys
    {
        public string AccessLicense, SecretKey, CustomerId;
        public string Path;
        public bool Ready { get { return AccessLicense != null && SecretKey != null && CustomerId != null; } }

        public static List<string> Files(string exeDir)
        {
            string parent = System.IO.Path.GetDirectoryName(exeDir.TrimEnd('\\', '/'));
            var list = new List<string> { System.IO.Path.Combine(exeDir, "api_keys.json"), System.IO.Path.Combine(exeDir, "naver_api.json") };
            if (parent != null) list.Add(System.IO.Path.Combine(parent, "api_keys.json"));
            return list;
        }

        public static SearchAdKeys Load(string exeDir)
        {
            var k = new SearchAdKeys();
            foreach (string f in Files(exeDir))
            {
                if (!File.Exists(f)) continue;
                if (k.Path == null) k.Path = f;
                Dictionary<string, object> root;
                try { root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>; }
                catch { continue; }
                var d = Sub(root, "naver_searchad") ?? Sub(root, "searchad");
                if (d == null) continue;
                k.AccessLicense = k.AccessLicense ?? Val(d, "accessLicense");
                k.SecretKey = k.SecretKey ?? Val(d, "secretKey");
                k.CustomerId = k.CustomerId ?? Val(d, "customerId");
                if (k.Ready) { k.Path = f; break; }
            }
            if (k.Path == null) k.Path = System.IO.Path.Combine(exeDir, "api_keys.json");
            return k;
        }

        internal static Dictionary<string, object> Sub(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        internal static string Val(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
            if (s.Length > 2 && s.StartsWith("<") && s.EndsWith(">"))
            {
                string inner = s.Substring(1, s.Length - 2).Trim();
                if (inner.IndexOf(' ') >= 0) return null;
                s = inner;
            }
            return s.Length == 0 || s.StartsWith("<") ? null : s;
        }

        public string Mask(string text)
        {
            foreach (var s in new[] { SecretKey, AccessLicense }) if (!string.IsNullOrEmpty(s)) text = text.Replace(s, "****");
            return text;
        }
    }

    // 키워드도구(keywordstool)로 월간검색수 조회. 한 번에 5개까지
    public class SearchAd
    {
        readonly SearchAdKeys keys;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public SearchAd(SearchAdKeys keys)
        {
            this.keys = keys;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        // 검색광고 API 가 받는 글자만 남김 (공백·기호 제거)
        public static string Clean(string kw)
        {
            return Regex.Replace(kw ?? "", @"[^0-9A-Za-z가-힣]", "");
        }

        public Dictionary<string, Volume> Volumes(IList<string> keywords, Action<int, int> progress, Func<bool> cancelled)
        {
            var result = new Dictionary<string, Volume>(StringComparer.OrdinalIgnoreCase);
            var list = keywords.Select(Clean).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < list.Count; i += 5)
            {
                if (cancelled()) break;
                var batch = list.Skip(i).Take(5).ToList();
                foreach (var row in Query(batch))
                {
                    string rel = Convert.ToString(Get(row, "relKeyword"), CultureInfo.InvariantCulture);
                    var hit = batch.FirstOrDefault(b => string.Equals(b, rel, StringComparison.OrdinalIgnoreCase));
                    if (hit == null) continue; // 연관 키워드는 쓰지 않음
                    var pc = Get(row, "monthlyPcQcCnt");
                    var mo = Get(row, "monthlyMobileQcCnt");
                    result[hit] = new Volume { Pc = ToLong(pc), Mobile = ToLong(mo), PcLow = IsLow(pc), MobileLow = IsLow(mo) };
                }
                progress(Math.Min(i + 5, list.Count), list.Count);
                Thread.Sleep(250);
            }
            return result;
        }

        // 연관 키워드 전체 (검색광고 키워드도구가 주는 목록) → 키워드, 월간검색량
        public Dictionary<string, long> Related(string hint)
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Query(new List<string> { Clean(hint) }))
            {
                string rel = Convert.ToString(Get(row, "relKeyword"), CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(rel)) continue;
                result[rel] = ToLong(Get(row, "monthlyPcQcCnt")) + ToLong(Get(row, "monthlyMobileQcCnt"));
            }
            return result;
        }

        List<Dictionary<string, object>> Query(List<string> hints)
        {
            for (int attempt = 1; ; attempt++)
            {
                string ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                string sig;
                using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(keys.SecretKey)))
                    sig = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(ts + ".GET./keywordstool")));
                var req = (HttpWebRequest)WebRequest.Create("https://api.searchad.naver.com/keywordstool?hintKeywords=" + Uri.EscapeDataString(string.Join(",", hints)) + "&showDetail=1");
                req.Headers["X-Timestamp"] = ts;
                req.Headers["X-API-KEY"] = keys.AccessLicense;
                req.Headers["X-Customer"] = keys.CustomerId;
                req.Headers["X-Signature"] = sig;
                req.Timeout = 30000;
                try
                {
                    using (var res = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    {
                        var d = json.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                        var arr = Get(d, "keywordList") as IEnumerable;
                        return arr == null ? new List<Dictionary<string, object>>() : arr.Cast<object>().OfType<Dictionary<string, object>>().ToList();
                    }
                }
                catch (WebException ex)
                {
                    var res = ex.Response as HttpWebResponse;
                    if (res == null) throw new Exception("네이버 검색광고 API에 연결하지 못했습니다. 인터넷 연결을 확인하세요.");
                    int code = (int)res.StatusCode;
                    string body;
                    using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd();
                    if (code == 429 && attempt < 5) { Thread.Sleep(1000 * attempt); continue; }
                    if (code == 401 || code == 403) throw new Exception("검색광고 API 키가 맞지 않습니다. api_keys.json 의 naver_searchad 칸을 확인하세요.");
                    throw new Exception("검색광고 API 오류 (" + code + "): " + keys.Mask(body.Length > 200 ? body.Substring(0, 200) : body));
                }
            }
        }

        static object Get(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v : null;
        }

        static bool IsLow(object v) { return v == null || (v is string && ((string)v).Contains("<")); }

        // "< 10" 은 검색광고 API 가 정확한 수를 주지 않는 경우라 5 로 봄 (키워드선택기와 같은 기준)
        static long ToLong(object v)
        {
            if (IsLow(v)) return 5;
            long n;
            return long.TryParse(Regex.Replace(Convert.ToString(v, CultureInfo.InvariantCulture), "[^0-9]", ""), out n) ? n : 0;
        }
    }
}
