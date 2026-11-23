using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SalesTool
{
    // 텔레그램 알림 설정: 각자 만든 봇의 토큰과 알림 받을 대화방 번호.
    // 토큰은 비밀이라 이 PC의 이 Windows 사용자만 풀 수 있게 암호화해서 저장 (배포용에 들어가지 않음)
    public class TelegramCred
    {
        public string Token, ChatId, ChatName, BotName;
        public bool On = true;
        static readonly byte[] Salt = Encoding.UTF8.GetBytes("SalesTool.telegram");
        public static string FilePath { get { return System.IO.Path.Combine(Store.Dir, "telegram.dat"); } }

        public static TelegramCred Load()
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) return null;
                var raw = ProtectedData.Unprotect(System.IO.File.ReadAllBytes(FilePath), Salt, DataProtectionScope.CurrentUser);
                return new JavaScriptSerializer().Deserialize<TelegramCred>(Encoding.UTF8.GetString(raw));
            }
            catch { return null; }
        }

        public void Save()
        {
            var raw = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(this));
            System.IO.File.WriteAllBytes(FilePath, ProtectedData.Protect(raw, Salt, DataProtectionScope.CurrentUser));
        }

        public static void Delete() { try { System.IO.File.Delete(FilePath); } catch { } }
        public bool Ready { get { return On && !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(ChatId); } }
    }

    // 휴대폰 앱 알림과 같은 내용을 텔레그램으로도 보냄 (순서대로 하나씩, 실패하면 몇 번 다시)
    public static class TelegramNotify
    {
        static TelegramCred cred = TelegramCred.Load();
        static readonly Queue<string> queue = new Queue<string>();
        static bool running;
        public static string LastError;

        public static TelegramCred Cred { get { return cred; } }
        public static void Reload() { cred = TelegramCred.Load(); }

        public static void Send(string text)
        {
            var c = cred;
            if (c == null || !c.Ready || string.IsNullOrWhiteSpace(text)) return;
            // 혹시 msgood 안내문에 섞인 전화번호는 가림 (외부 서버를 거치므로)
            text = System.Text.RegularExpressions.Regex.Replace(text, @"0\d{1,2}[-.\s]?\d{3,4}[-.\s]?\d{4}", "***-****-****");
            lock (queue)
            {
                queue.Enqueue(text.Length > 3500 ? text.Substring(0, 3500) + "\n…" : text);
                if (queue.Count > 50) queue.Dequeue();
                if (running) return;
                running = true;
            }
            Task.Run(() => Pump());
        }

        static void Pump()
        {
            while (true)
            {
                string text;
                lock (queue)
                {
                    if (queue.Count == 0) { running = false; return; }
                    text = queue.Dequeue();
                }
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try { SendNow(cred, "판매도구\n" + text); LastError = null; break; }
                    catch (TelegramError ex) when (ex.RetryAfter > 0) { Thread.Sleep(Math.Min(60, ex.RetryAfter) * 1000); }
                    catch (TelegramError ex) { LastError = ex.Message; break; }          // 토큰·대화방 문제: 다시 해도 같음
                    catch (Exception ex) { LastError = ex.Message; Thread.Sleep(5000 * (attempt + 1)); }   // 인터넷 끊김 등
                }
                Thread.Sleep(400);
            }
        }

        public class TelegramError : Exception
        {
            public int RetryAfter;
            public TelegramError(string m, int retry) : base(m) { RetryAfter = retry; }
        }

        static Dictionary<string, object> Call(string token, string method, object body)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var js = new JavaScriptSerializer();
            var req = (HttpWebRequest)WebRequest.Create("https://api.telegram.org/bot" + token + "/" + method);
            req.Method = "POST"; req.ContentType = "application/json; charset=utf-8"; req.Timeout = 20000;
            var data = Encoding.UTF8.GetBytes(js.Serialize(body ?? new Dictionary<string, object>()));
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            string resp;
            try { using (var r = (HttpWebResponse)req.GetResponse()) using (var sr = new System.IO.StreamReader(r.GetResponseStream(), Encoding.UTF8)) resp = sr.ReadToEnd(); }
            catch (WebException ex) when (ex.Response != null)
            {
                using (var sr = new System.IO.StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8)) resp = sr.ReadToEnd();
            }
            var o = js.Deserialize<Dictionary<string, object>>(resp);
            if (o.ContainsKey("ok") && (bool)o["ok"]) return o;
            int retry = 0;
            if (o.ContainsKey("parameters") && o["parameters"] is Dictionary<string, object> p && p.ContainsKey("retry_after")) retry = Convert.ToInt32(p["retry_after"]);
            string desc = o.ContainsKey("description") ? o["description"].ToString() : "알 수 없는 오류";
            if (desc.Contains("Unauthorized") || desc.Contains("Not Found")) desc = "봇 토큰이 맞지 않습니다";
            else if (desc.Contains("chat not found")) desc = "대화방을 찾지 못했습니다. 텔레그램에서 봇에게 /start 를 다시 보내 주세요";
            else if (desc.Contains("blocked")) desc = "텔레그램에서 봇을 차단했습니다. 차단을 풀어 주세요";
            throw new TelegramError(desc, retry);
        }

        static void SendNow(TelegramCred c, string text)
        {
            Call(c.Token, "sendMessage", new Dictionary<string, object> { { "chat_id", c.ChatId }, { "text", text }, { "disable_web_page_preview", true } });
        }

        // 연결: 토큰 확인 → 봇에게 /start 보낸 대화방을 찾아 저장. 반환: 안내 문구
        public static string Connect(string token)
        {
            token = (token ?? "").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(token, @"^\d+:[A-Za-z0-9_-]{20,}$")) return "봇 토큰 모양이 아닙니다. BotFather가 준 '숫자:글자' 전체를 붙여 넣어 주세요.";
            var c = TelegramCred.Load() ?? new TelegramCred();
            var me = (Dictionary<string, object>)Call(token, "getMe", null)["result"];
            c.Token = token; c.BotName = me.ContainsKey("username") ? me["username"].ToString() : "";
            var ups = Call(token, "getUpdates", new Dictionary<string, object> { { "limit", 100 }, { "timeout", 0 } })["result"] as System.Collections.ArrayList;
            Dictionary<string, object> chat = null;
            if (ups != null)
                foreach (Dictionary<string, object> u in ups.Cast<object>().Reverse())
                {
                    var msg = u.ContainsKey("message") ? u["message"] as Dictionary<string, object> : null;
                    var ch = msg != null && msg.ContainsKey("chat") ? msg["chat"] as Dictionary<string, object> : null;
                    if (ch != null && ch.ContainsKey("type") && ch["type"].ToString() == "private") { chat = ch; break; }
                }
            if (chat == null)
            {
                c.Save(); cred = c;
                return "봇 토큰은 맞습니다 (@" + c.BotName + "). 이제 텔레그램에서 @" + c.BotName + " 를 열어 [시작]( /start )을 누른 뒤 '연결 확인'을 다시 눌러 주세요.";
            }
            c.ChatId = Convert.ToString(chat["id"]);
            c.ChatName = ((chat.ContainsKey("first_name") ? chat["first_name"] + " " : "") + (chat.ContainsKey("last_name") ? chat["last_name"].ToString() : "")).Trim();
            c.On = true;
            c.Save(); cred = c;
            SendNow(c, "판매도구\n✔ 텔레그램 알림이 연결되었습니다. 앞으로 발주·품절·초안 알림이 여기로 옵니다.");
            return "연결했습니다: " + (c.ChatName.Length > 0 ? c.ChatName : "대화방 " + c.ChatId) + " (봇 @" + c.BotName + "). 텔레그램에 연결 메시지가 갔는지 확인해 주세요.";
        }

        public static string Test()
        {
            var c = cred;
            if (c == null || string.IsNullOrEmpty(c.ChatId)) return "아직 연결되지 않았습니다.";
            SendNow(c, "판매도구\n테스트 알림입니다 (" + DateTime.Now.ToString("MM-dd HH:mm") + ")");
            return "테스트 알림을 보냈습니다.";
        }
    }
}
