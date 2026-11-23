using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace SmallKeyword
{
    // 검색어 트렌드 (NAVER API HUB, 네이버 클라우드 플랫폼). 키: api_keys.json 의 naver_apihub (clientId / clientSecret)
    public class TrendKeys
    {
        public string ClientId, ClientSecret;
        public bool Ready { get { return ClientId != null && ClientSecret != null; } }

        public static TrendKeys Load(string exeDir)
        {
            var k = new TrendKeys();
            foreach (string f in SearchAdKeys.Files(exeDir))
            {
                if (!File.Exists(f)) continue;
                Dictionary<string, object> root;
                try { root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>; }
                catch { continue; }
                var d = SearchAdKeys.Sub(root, "naver_apihub");
                if (d == null) continue;
                k.ClientId = k.ClientId ?? SearchAdKeys.Val(d, "clientId");
                k.ClientSecret = k.ClientSecret ?? SearchAdKeys.Val(d, "clientSecret");
                if (k.Ready) break;
            }
            return k;
        }
    }

    // 키워드 하나의 최근 검색 흐름 (일별 상대값으로 계산)
    public class TrendInfo
    {
        public double Growth = -1;   // 최근 28일 ÷ 그 전 84일의 28일 평균
        public double YoY = -1;      // 최근 28일 ÷ 작년 같은 28일 (작년에 검색이 없었으면 -1)
    }

    public class Trend
    {
        const string Url = "https://naverapihub.apigw.ntruss.com/search-trend/v1/search";
        readonly TrendKeys keys;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public Trend(TrendKeys keys)
        {
            this.keys = keys;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        // 한 번에 5개씩 (그룹 하나 = 키워드 하나)
        public Dictionary<string, TrendInfo> Get(IList<string> words, DateTime today, Action<int, int> progress)
        {
            var result = new Dictionary<string, TrendInfo>(StringComparer.OrdinalIgnoreCase);
            DateTime end = today.Date.AddDays(-1), start = end.AddDays(-(364 + 84 + 28));
            var list = words.Where(w => SearchAd.Clean(w).Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < list.Count; i += 5)
            {
                var part = list.Skip(i).Take(5).ToList();
                var body = new Dictionary<string, object>
                {
                    { "startDate", start.ToString("yyyy-MM-dd") }, { "endDate", end.ToString("yyyy-MM-dd") }, { "timeUnit", "date" },
                    { "keywordGroups", part.Select(w => new Dictionary<string, object> { { "groupName", w }, { "keywords", new[] { w } } }).ToArray() }
                };
                var o = Post(json.Serialize(body));
                var res = o.ContainsKey("results") ? o["results"] as ArrayList : null;
                if (res != null)
                    foreach (Dictionary<string, object> g in res)
                    {
                        string title = Convert.ToString(g["title"]);
                        var byDay = new Dictionary<DateTime, double>();
                        var data = g.ContainsKey("data") ? g["data"] as ArrayList : null;
                        if (data != null)
                            foreach (Dictionary<string, object> d in data)
                            {
                                DateTime p;
                                if (DateTime.TryParse(Convert.ToString(d["period"]), out p)) byDay[p.Date] = Convert.ToDouble(d["ratio"]);
                            }
                        result[title] = Calc(byDay, end);
                    }
                if (progress != null) progress(Math.Min(i + 5, list.Count), list.Count);
                System.Threading.Thread.Sleep(150);
            }
            return result;
        }

        // 빠진 날은 검색 0
        public static TrendInfo Calc(Dictionary<DateTime, double> byDay, DateTime end)
        {
            Func<DateTime, int, double> sum = (last, days) => { double s = 0; for (int d = 0; d < days; d++) { double v; if (byDay.TryGetValue(last.AddDays(-d), out v)) s += v; } return s; };
            double recent = sum(end, 28), before = sum(end.AddDays(-28), 84) / 3, lastYear = sum(end.AddDays(-364), 28);
            var t = new TrendInfo();
            if (before > 0) t.Growth = recent / before;
            else if (recent > 0) t.Growth = 9.99;   // 전에는 거의 없다가 새로 생긴 말
            if (lastYear > 0) t.YoY = recent / lastYear;
            return t;
        }

        Dictionary<string, object> Post(string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(Url);
            req.Method = "POST"; req.ContentType = "application/json; charset=utf-8"; req.Timeout = 20000;
            req.Headers["X-NCP-APIGW-API-KEY-ID"] = keys.ClientId;
            req.Headers["X-NCP-APIGW-API-KEY"] = keys.ClientSecret;
            var data = Encoding.UTF8.GetBytes(body);
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            int code = 200; string resp;
            try { using (var r = (HttpWebResponse)req.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) resp = sr.ReadToEnd(); }
            catch (WebException ex) when (ex.Response != null)
            {
                code = (int)((HttpWebResponse)ex.Response).StatusCode;
                using (var sr = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8)) resp = sr.ReadToEnd();
            }
            if (code == 401 || code == 403) throw new Exception("검색어 트렌드 키가 맞지 않습니다 (api_keys.json 의 naver_apihub 칸 확인)");
            if (code == 429) throw new Exception("검색어 트렌드 호출 한도를 넘었습니다");
            if (code != 200) throw new Exception("검색어 트렌드 오류 " + code + ": " + (resp.Length > 200 ? resp.Substring(0, 200) : resp));
            return json.Deserialize<Dictionary<string, object>>(resp);
        }
    }
}
