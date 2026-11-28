using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace SalesTool
{
    // PC 간 (사용자 2명 × 3개 사업자 = 총 6개 사업자) 상품명·키워드 중복 방지를 위한 공유 폴더 연동
    public class SharedNameRecord
    {
        public string Code;           // 상품번호
        public string BusinessId;     // 사업자ID (설정의 계정별 사업자명 또는 사업자번호)
        public string Name;           // 생성된 상품명
        public List<string> Keywords; // 사용된 키워드 목록
        public string Machine;        // 작성 PC 이름
        public string Time;           // 작성 시각 (yyyy-MM-dd HH:mm:ss)
    }

    public static class SharedNames
    {
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();
        static readonly object syncLock = new object();

        public static string MachineName
        {
            get
            {
                try { return Environment.MachineName; } catch { return "PC"; }
            }
        }

        // 공유 폴더가 연결 안 되면(네트워크·드라이브 끊김) 확인하는 데 수십 초 멈추므로 2초만 기다리고, 안 되면 5분 동안 건너뜀
        static string okFolder; static DateTime okAt = DateTime.MinValue; static bool okVal;
        public static bool Reachable(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            folder = folder.Trim();
            lock (syncLock) { if (okFolder == folder && DateTime.Now - okAt < TimeSpan.FromMinutes(5)) return okVal; }
            bool r = false;
            try
            {
                var t = System.Threading.Tasks.Task.Run(() => { try { if (!Directory.Exists(folder)) Directory.CreateDirectory(folder); return Directory.Exists(folder); } catch { return false; } });
                r = t.Wait(2000) && t.Result;
            }
            catch { r = false; }
            lock (syncLock) { okFolder = folder; okAt = DateTime.Now; okVal = r; }
            return r;
        }

        public static string GetFilePath(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            return Path.Combine(folder.Trim(), "shared_names_" + MachineName + ".jsonl");
        }

        // 자기 PC 파일에만 덧붙이기 (Append only) - 동시 쓰기 충돌 방지
        public static void Append(string folder, string code, string businessId, string name, IEnumerable<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(name)) return;
            if (!Reachable(folder)) return;
            try
            {
                var path = GetFilePath(folder);
                var rec = new SharedNameRecord
                {
                    Code = code ?? "",
                    BusinessId = businessId ?? "",
                    Name = name.Trim(),
                    Keywords = (keywords ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList(),
                    Machine = MachineName,
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                var line = json.Serialize(rec);
                lock (syncLock)
                {
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var sw = new StreamWriter(fs, Encoding.UTF8))
                    {
                        sw.WriteLine(line);
                    }
                }
            }
            catch { }
        }

        // 공유 폴더의 모든 PC 파일(shared_names_*.jsonl)을 읽어 특정 상품번호에 쓰인 상품명 목록 반환
        public static HashSet<string> GetUsedNames(string folder, string code)
        {
            var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Reachable(folder)) return res;
            try
            {
                var files = Directory.GetFiles(folder, "shared_names_*.jsonl");
                foreach (var f in files)
                {
                    try
                    {
                        using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs, Encoding.UTF8))
                        {
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                try
                                {
                                    var rec = json.Deserialize<SharedNameRecord>(line);
                                    if (rec != null && (string.IsNullOrEmpty(code) || rec.Code == code) && !string.IsNullOrWhiteSpace(rec.Name))
                                    {
                                        res.Add(rec.Name.Trim());
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return res;
        }

        // 특정 상품번호에 이미 쓰인 키워드 조합/집합 반환
        public static List<HashSet<string>> GetUsedKeywordSets(string folder, string code)
        {
            var list = new List<HashSet<string>>();
            if (!Reachable(folder)) return list;
            try
            {
                var files = Directory.GetFiles(folder, "shared_names_*.jsonl");
                foreach (var f in files)
                {
                    try
                    {
                        using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs, Encoding.UTF8))
                        {
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                try
                                {
                                    var rec = json.Deserialize<SharedNameRecord>(line);
                                    if (rec != null && (string.IsNullOrEmpty(code) || rec.Code == code) && rec.Keywords != null && rec.Keywords.Count > 0)
                                    {
                                        list.Add(new HashSet<string>(rec.Keywords, StringComparer.OrdinalIgnoreCase));
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }
    }
}
