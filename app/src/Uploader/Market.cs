using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ProductUploader
{
    public class ApiException : Exception
    {
        public int Status;
        public string Body;
        public ApiException(int status, string body, string message) : base(message) { Status = status; Body = body; }
    }

    // market_api.json 읽기. "_" 로 시작하는 안내 칸은 무시
    public class MarketConfig
    {
        public string NaverId, NaverSecret, CoupangAccess, CoupangSecret, CoupangVendor;

        public const string SharedName = "api_keys.json";

        // 키 파일 찾기: 프로그램 폴더의 market_api.json → 프로그램 폴더의 api_keys.json → 한 단계 위 폴더의 api_keys.json
        // (판매도구 묶음에서는 맨 위 api_keys.json 하나를 상품등록기·키워드선택기가 같이 씀)
        public static string Locate(string exeDir)
        {
            string parent = Path.GetDirectoryName(exeDir.TrimEnd('\\', '/'));
            var candidates = new List<string> { Path.Combine(exeDir, "market_api.json"), Path.Combine(exeDir, SharedName) };
            if (parent != null) candidates.Add(Path.Combine(parent, SharedName));
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return candidates[candidates.Count - 1];
        }

        // suffix: "" = #1 계정 (naver_commerce, coupang), "_2" = #2 계정 (naver_commerce_2, coupang_2), "_3" = #3
        public static MarketConfig Load(string path, string suffix = "")
        {
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            var c = new MarketConfig();
            var n = Sub(root, "naver_commerce" + suffix) ?? (suffix == "" ? Sub(root, "naver") : null);
            var k = Sub(root, "coupang" + suffix);
            c.NaverId = Val(n, "clientId");
            c.NaverSecret = Val(n, "clientSecret");
            c.CoupangAccess = Val(k, "accessKey");
            c.CoupangSecret = Val(k, "secretKey");
            // 업체코드는 "<업체코드 A00000000>" 처럼 안내 문구가 남아 있어도 코드만 골라 씀
            object rawVendor = null;
            if (k != null) k.TryGetValue("vendorId", out rawVendor);
            var m = System.Text.RegularExpressions.Regex.Match(rawVendor == null ? "" : rawVendor.ToString(), @"\bA\d{5,}\b");
            c.CoupangVendor = m.Success && m.Value != "A00000000" ? m.Value : Val(k, "vendorId");
            return c;
        }

        public bool HasNaver { get { return NaverId != null && NaverSecret != null; } }
        public bool HasCoupang { get { return CoupangAccess != null && CoupangSecret != null && CoupangVendor != null; } }

        public string Mask(string text)
        {
            foreach (var s in new[] { NaverSecret, CoupangSecret, CoupangAccess, NaverId })
                if (!string.IsNullOrEmpty(s)) text = text.Replace(s, "****");
            return text;
        }

        static Dictionary<string, object> Sub(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        static string Val(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            string s = v.ToString().Trim();
            // < > 를 남긴 채 키를 넣어도 받아줌. 안내 문구 그대로면 빈 칸
            if (s.Length > 2 && s.StartsWith("<") && s.EndsWith(">"))
            {
                string inner = s.Substring(1, s.Length - 2).Trim();
                if (inner.IndexOf(' ') >= 0) return null;
                s = inner;
            }
            return s.Length == 0 ? null : s;
        }
    }

    static class Http
    {
        static Http()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        // 호출이 몰려 429(요청이 많음)가 오면 잠깐 쉬었다가 다시 시도
        public static string Send(string method, string url, Dictionary<string, string> headers, string contentType, byte[] body)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { return SendOnce(method, url, headers, contentType, body); }
                catch (ApiException ex)
                {
                    if (ex.Status != 429 || attempt >= 5) throw;
                    System.Threading.Thread.Sleep(1000 * attempt);
                }
            }
        }

        static string SendOnce(string method, string url, Dictionary<string, string> headers, string contentType, byte[] body)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 60000;
            if (headers != null) foreach (var h in headers) req.Headers[h.Key] = h.Value;
            if (contentType != null) req.ContentType = contentType;
            if (body != null)
            {
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            }
            try
            {
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                var res = ex.Response as HttpWebResponse;
                if (res == null) throw new ApiException(0, "", "인터넷 연결을 확인하세요. (" + ex.Message + ")");
                string text;
                using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd();
                throw new ApiException((int)res.StatusCode, text, "응답 오류 " + (int)res.StatusCode + "\n" + text);
            }
        }
    }

    // 네이버 커머스API: 전자서명(bcrypt)으로 토큰을 받아 Bearer 로 호출
    public class NaverClient
    {
        const string Base = "https://api.commerce.naver.com/external";
        readonly MarketConfig cfg;
        string token;
        DateTime tokenUntil;

        public NaverClient(MarketConfig cfg) { this.cfg = cfg; }

        public string Token()
        {
            if (token != null && DateTime.Now < tokenUntil) return token;
            string ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string hashed = BCrypt.Net.BCrypt.HashPassword(cfg.NaverId + "_" + ts, cfg.NaverSecret);
            string sign = Convert.ToBase64String(Encoding.UTF8.GetBytes(hashed));
            string form = "client_id=" + Uri.EscapeDataString(cfg.NaverId) +
                          "&timestamp=" + ts +
                          "&client_secret_sign=" + Uri.EscapeDataString(sign) +
                          "&grant_type=client_credentials&type=SELF";
            string res = Http.Send("POST", Base + "/v1/oauth2/token", null, "application/x-www-form-urlencoded", Encoding.UTF8.GetBytes(form));
            var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(res);
            token = (string)d["access_token"];
            int sec = Convert.ToInt32(d["expires_in"]);
            tokenUntil = DateTime.Now.AddSeconds(Math.Max(60, sec - 300));
            return token;
        }

        public string Get(string path)
        {
            return Http.Send("GET", Base + path, Auth(), null, null);
        }

        public string Post(string path, string json)
        {
            return Http.Send("POST", Base + path, Auth(), "application/json", Encoding.UTF8.GetBytes(json));
        }

        public string Delete(string path)
        {
            return Http.Send("DELETE", Base + path, Auth(), null, null);
        }

        public string Put(string path, string json)
        {
            return Http.Send("PUT", Base + path, Auth(), "application/json", Encoding.UTF8.GetBytes(json));
        }

        // 상품 이미지 업로드 → 네이버 이미지 서버 주소(https://shop-phinf.pstatic.net/...) 를 돌려줌
        public List<string> UploadImages(IList<string> files)
        {
            string boundary = "----pu" + Guid.NewGuid().ToString("N");
            var ms = new MemoryStream();
            foreach (string f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                string type = ext == ".png" ? "image/png" : ext == ".gif" ? "image/gif" : "image/jpeg";
                string head = "--" + boundary + "\r\nContent-Disposition: form-data; name=\"imageFiles\"; filename=\"img" + ext + "\"\r\nContent-Type: " + type + "\r\n\r\n";
                Write(ms, head);
                byte[] data = File.ReadAllBytes(f);
                ms.Write(data, 0, data.Length);
                Write(ms, "\r\n");
            }
            Write(ms, "--" + boundary + "--\r\n");
            string res = Http.Send("POST", Base + "/v1/product-images/upload", Auth(), "multipart/form-data; boundary=" + boundary, ms.ToArray());
            var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(res);
            var urls = new List<string>();
            foreach (var o in (object[])d["images"]) urls.Add((string)((Dictionary<string, object>)o)["url"]);
            return urls;
        }

        static void Write(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        Dictionary<string, string> Auth()
        {
            return new Dictionary<string, string> { { "Authorization", "Bearer " + Token() } };
        }
    }

    // 쿠팡 Open API: 요청마다 HMAC-SHA256 서명
    public class CoupangClient
    {
        const string Host = "https://api-gateway.coupang.com";
        readonly MarketConfig cfg;

        public CoupangClient(MarketConfig cfg) { this.cfg = cfg; }

        public string Vendor { get { return cfg.CoupangVendor; } }

        public string Send(string method, string pathAndQuery, string json)
        {
            string path = pathAndQuery, query = "";
            int q = pathAndQuery.IndexOf('?');
            if (q >= 0) { path = pathAndQuery.Substring(0, q); query = pathAndQuery.Substring(q + 1); }
            string date = DateTime.UtcNow.ToString("yyMMdd'T'HHmmss'Z'");
            string sig;
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(cfg.CoupangSecret)))
                sig = BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(date + method + path + query))).Replace("-", "").ToLowerInvariant();
            var headers = new Dictionary<string, string>
            {
                { "Authorization", "CEA algorithm=HmacSHA256, access-key=" + cfg.CoupangAccess + ", signed-date=" + date + ", signature=" + sig },
                { "X-Requested-By", cfg.CoupangVendor }
            };
            // 본문 없는 PUT/POST(판매중지·재개 등)도 길이 0을 보내야 쿠팡이 받아 줌 (안 보내면 411)
            byte[] body = json != null ? Encoding.UTF8.GetBytes(json) : (method == "GET" || method == "DELETE" ? null : new byte[0]);
            return Http.Send(method, Host + pathAndQuery, headers, "application/json;charset=UTF-8", body);
        }
    }
}
