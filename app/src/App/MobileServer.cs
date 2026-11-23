using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SalesTool
{
    // 휴대폰 앱용 작은 웹 서버 (판매도구 안에서 돎). 같은 와이파이 또는 Tailscale 로 접속
    //  / = 휴대폰 화면(HTML), /api/... = 데이터 (X-Key 헤더의 연결 키가 맞아야)
    public class MobileResponse
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = new byte[0];
        public static MobileResponse Json(string s) { return new MobileResponse { Body = Encoding.UTF8.GetBytes(s) }; }
        public static MobileResponse Text(string s, string type) { return new MobileResponse { ContentType = type, Body = Encoding.UTF8.GetBytes(s) }; }
        public static MobileResponse Error(int code, string msg) { return new MobileResponse { Status = code, Body = Encoding.UTF8.GetBytes("{\"error\":" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(msg) + "}") }; }
    }

    public class MobileServer
    {
        TcpListener listener;
        CancellationTokenSource cts;
        public string State = "꺼짐";
        public int Port;

        // handler(method, path, query, body) → 응답. 연결 키 검사는 여기서 함
        public void Start(int port, Func<string> key, Func<string, string, Dictionary<string, string>, string, Task<MobileResponse>> handler, Action<string> log)
        {
            Stop();
            Port = port;
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
            }
            catch (Exception ex) { State = "시작 못 함 (" + ex.Message + ")"; log("휴대폰 앱 서버를 시작하지 못했습니다: " + ex.Message); return; }
            cts = new CancellationTokenSource();
            var token = cts.Token;
            State = "켜짐 (포트 " + port + ")";
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await listener.AcceptTcpClientAsync(); }
                    catch { if (token.IsCancellationRequested) break; await Task.Delay(500); continue; }
                    var _ = Task.Run(() => Serve(c, key, handler));
                }
            });
        }

        public void Stop()
        {
            try { if (cts != null) cts.Cancel(); } catch { }
            try { if (listener != null) listener.Stop(); } catch { }
            listener = null; cts = null; State = "꺼짐";
        }

        static async Task Serve(TcpClient c, Func<string> key, Func<string, string, Dictionary<string, string>, string, Task<MobileResponse>> handler)
        {
            using (c)
            {
                try
                {
                    c.ReceiveTimeout = 15000; c.SendTimeout = 30000;
                    var ns = c.GetStream();
                    // 머리글 읽기
                    var buf = new MemoryStream();
                    var one = new byte[1];
                    int headEnd = -1;
                    while (buf.Length < 65536)
                    {
                        int n = await ns.ReadAsync(one, 0, 1);
                        if (n <= 0) return;
                        buf.WriteByte(one[0]);
                        var b = buf.GetBuffer(); long L = buf.Length;
                        if (L >= 4 && b[L - 4] == 13 && b[L - 3] == 10 && b[L - 2] == 13 && b[L - 1] == 10) { headEnd = (int)L; break; }
                    }
                    if (headEnd < 0) return;
                    string head = Encoding.UTF8.GetString(buf.ToArray());
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var first = lines[0].Split(' ');
                    if (first.Length < 2) return;
                    string method = first[0].ToUpperInvariant(), target = first[1];
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var l in lines.Skip(1)) { int i = l.IndexOf(':'); if (i > 0) headers[l.Substring(0, i).Trim()] = l.Substring(i + 1).Trim(); }
                    string body = "";
                    int len; if (headers.ContainsKey("Content-Length") && int.TryParse(headers["Content-Length"], out len) && len > 0 && len < 1000000)
                    {
                        var bb = new byte[len]; int got = 0;
                        while (got < len) { int n = await ns.ReadAsync(bb, got, len - got); if (n <= 0) break; got += n; }
                        body = Encoding.UTF8.GetString(bb, 0, got);
                    }
                    string path = target, qs = "";
                    int q = target.IndexOf('?'); if (q >= 0) { path = target.Substring(0, q); qs = target.Substring(q + 1); }
                    var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in qs.Split('&').Where(x => x.Length > 0)) { var p = kv.Split(new[] { '=' }, 2); query[Uri.UnescapeDataString(p[0])] = p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : ""; }

                    MobileResponse res;
                    if (method == "OPTIONS") res = new MobileResponse();
                    else if (path.StartsWith("/api/"))
                    {
                        string k; headers.TryGetValue("X-Key", out k);
                        if (string.IsNullOrEmpty(k)) query.TryGetValue("key", out k);
                        string want = key();
                        if (string.IsNullOrEmpty(want) || k != want) res = MobileResponse.Error(401, "연결 키가 맞지 않습니다");
                        else
                        {
                            try { res = await handler(method, path, query, body); }
                            catch (Exception ex) { res = MobileResponse.Error(500, ex.Message); }
                        }
                    }
                    else res = await handler(method, path, query, body);

                    var hb = Encoding.UTF8.GetBytes("HTTP/1.1 " + res.Status + " " + (res.Status == 200 ? "OK" : "Error") + "\r\nContent-Type: " + res.ContentType + "\r\nContent-Length: " + res.Body.Length +
                        "\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: X-Key, Content-Type\r\nConnection: close\r\n\r\n");
                    await ns.WriteAsync(hb, 0, hb.Length);
                    await ns.WriteAsync(res.Body, 0, res.Body.Length);
                    await ns.FlushAsync();
                }
                catch { }
            }
        }

        // 이 PC의 주소들 (와이파이·Tailscale)
        public static List<string> Addresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                    foreach (var a in ni.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)))
                    {
                        string ip = a.Address.ToString();
                        bool ts = ip.StartsWith("100.") || ni.Description.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0;
                        list.Add(ip + (ts ? " (Tailscale: 밖에서도)" : " (" + ni.Name + ": 같은 와이파이)"));
                    }
            }
            catch { }
            return list;
        }
    }
    // 휴대폰 앱 알림 목록: 번호(밀리초 시각)가 커지는 순. 앱이 /api/events?after=번호 로 새 알림을 받아 감 (없으면 최대 wait 초 기다렸다가 답함)
    public static class AppEvents
    {
        public class Ev { public long id; public string time, text; }
        static readonly List<Ev> list = new List<Ev>();
        static TaskCompletionSource<bool> signal = new TaskCompletionSource<bool>();
        static long last;

        public static void Add(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            TaskCompletionSource<bool> old;
            lock (list)
            {
                long id = Math.Max(last + 1, DateTime.UtcNow.Ticks / 10000);
                last = id;
                list.Add(new Ev { id = id, time = DateTime.Now.ToString("MM-dd HH:mm"), text = text });
                if (list.Count > 300) list.RemoveAt(0);
                old = signal; signal = new TaskCompletionSource<bool>();
            }
            old.TrySetResult(true);
        }

        public static async Task<List<Ev>> Wait(long after, int waitSec)
        {
            for (int round = 0; round < 2; round++)
            {
                Task t;
                lock (list)
                {
                    var r = list.Where(e => e.id > after).ToList();
                    // 처음 연결(after<=0): 지난 알림은 보내지 않고 마지막 번호만 알려 줌
                    if (after <= 0) return new List<Ev> { new Ev { id = Math.Max(last, DateTime.UtcNow.Ticks / 10000), time = "", text = null } };
                    if (r.Count > 0 || waitSec <= 0 || round == 1) return r;
                    t = signal.Task;
                }
                await Task.WhenAny(t, Task.Delay(Math.Min(55, waitSec) * 1000));
            }
            return new List<Ev>();
        }
    }
}
