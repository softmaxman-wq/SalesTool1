using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SalesTool
{
    public class UpdateInfo
    {
        public string Version;
        public string ReleaseNotes;
        public string ZipUrl;
        public string ApkUrl;
    }

    public static class AutoUpdater
    {
        public const string CurrentVersion = "1.7.3";
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static AutoUpdater()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            CleanOldFiles();
        }

        // 이전 실행에서 남은 .old 임시 파일 정리
        public static void CleanOldFiles()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var f in Directory.GetFiles(exeDir, "*.old"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        // 최신 버전 정보 확인 (GitHub Releases API 또는 raw version.json)
        public static async Task<UpdateInfo> CheckLatestAsync(string repoOrUrl)
        {
            if (string.IsNullOrWhiteSpace(repoOrUrl)) return null;

            string apiUrl;
            bool isDirectJson = repoOrUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

            if (isDirectJson)
            {
                apiUrl = repoOrUrl;
            }
            else
            {
                // GitHub "owner/repo" 형식
                string repo = repoOrUrl.Trim().Trim('/');
                apiUrl = $"https://api.github.com/repos/{repo}/releases/latest";
            }

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(apiUrl);
                req.Method = "GET";
                req.UserAgent = "SalesTool-Updater";
                req.Timeout = 10000;

                string resp;
                using (var res = (HttpWebResponse)await req.GetResponseAsync())
                using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                {
                    resp = await sr.ReadToEndAsync();
                }

                if (isDirectJson)
                {
                    var map = json.Deserialize<Dictionary<string, object>>(resp);
                    return new UpdateInfo
                    {
                        Version = Convert.ToString(map["version"]).Trim().TrimStart('v', 'V'),
                        ReleaseNotes = map.ContainsKey("notes") ? Convert.ToString(map["notes"]) : "",
                        ZipUrl = map.ContainsKey("zipUrl") ? Convert.ToString(map["zipUrl"]) : "",
                        ApkUrl = map.ContainsKey("apkUrl") ? Convert.ToString(map["apkUrl"]) : ""
                    };
                }
                else
                {
                    var map = json.Deserialize<Dictionary<string, object>>(resp);
                    string tag = Convert.ToString(map["tag_name"]).Trim().TrimStart('v', 'V');
                    string body = map.ContainsKey("body") ? Convert.ToString(map["body"]) : "";

                    string zipUrl = null, apkUrl = null;
                    var assets = map.ContainsKey("assets") ? map["assets"] as ArrayList : null;
                    if (assets != null)
                    {
                        // 1순위: 자체 업데이트 전용 경량 패키지 update.zip
                        foreach (Dictionary<string, object> a in assets)
                        {
                            string name = Convert.ToString(a["name"]);
                            string download = Convert.ToString(a["browser_download_url"]);
                            if (string.Equals(name, "update.zip", StringComparison.OrdinalIgnoreCase))
                            {
                                zipUrl = download;
                                break;
                            }
                        }
                        // 2순위: 그 외 zip 파일
                        if (zipUrl == null)
                        {
                            foreach (Dictionary<string, object> a in assets)
                            {
                                string name = Convert.ToString(a["name"]);
                                string download = Convert.ToString(a["browser_download_url"]);
                                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zipUrl = download; break; }
                            }
                        }
                        // apk 파일
                        foreach (Dictionary<string, object> a in assets)
                        {
                            string name = Convert.ToString(a["name"]);
                            string download = Convert.ToString(a["browser_download_url"]);
                            if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) { apkUrl = download; break; }
                        }
                    }

                    return new UpdateInfo
                    {
                        Version = tag,
                        ReleaseNotes = body,
                        ZipUrl = zipUrl,
                        ApkUrl = apkUrl
                    };
                }
            }
            catch
            {
                return null;
            }
        }

        // 버전 비교 (v1 > v2 이면 > 0)
        public static int CompareVersion(string v1, string v2)
        {
            try
            {
                var p1 = Regex.Replace(v1 ?? "", @"[^0-9\.]", "").Trim('.').Split('.').Select(int.Parse).ToArray();
                var p2 = Regex.Replace(v2 ?? "", @"[^0-9\.]", "").Trim('.').Split('.').Select(int.Parse).ToArray();
                for (int i = 0; i < Math.Max(p1.Length, p2.Length); i++)
                {
                    int n1 = i < p1.Length ? p1[i] : 0;
                    int n2 = i < p2.Length ? p2[i] : 0;
                    if (n1 != n2) return n1.CompareTo(n2);
                }
                return 0;
            }
            catch
            {
                return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase);
            }
        }

        // 프로그램 시작 시 또는 설정 탭에서 업데이트 확인
        public static async Task CheckAndPromptAsync(string repoOrUrl, bool silentIfLatest)
        {
            if (string.IsNullOrWhiteSpace(repoOrUrl))
            {
                if (!silentIfLatest)
                {
                    MessageBox.Show("GitHub 저장소 정보가 설정되어 있지 않습니다.\n[설정] 탭의 '자동 업데이트' 항목에 '아이디/저장소명'을 입력해주세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            var info = await CheckLatestAsync(repoOrUrl);
            if (info == null || string.IsNullOrEmpty(info.Version))
            {
                if (!silentIfLatest) MessageBox.Show("업데이트 서버에 연결할 수 없습니다. 저장소 이름을 확인해주세요.", "확인 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (CompareVersion(info.Version, CurrentVersion) > 0)
            {
                // [지금 업데이트 확인]을 누른 것 자체가 업데이트하겠다는 뜻이므로 묻지 않고 바로 적용
                if (!string.IsNullOrEmpty(info.ZipUrl))
                {
                    await PerformUpdateAsync(info.ZipUrl);
                }
            }
            else
            {
                if (!silentIfLatest) MessageBox.Show($"현재 최신 버전(v{CurrentVersion})을 사용하고 있습니다.", "최신 버전", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // 업데이트 다운로드 및 원클릭 자동 무중단 재실행 적용
        public static async Task PerformUpdateAsync(string zipUrl, bool silent = false, Action<string> statusCallback = null)
        {
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            string exeDir = Path.GetDirectoryName(currentExe);
            int currentPid = Process.GetCurrentProcess().Id;

            string tempDir = Path.Combine(Path.GetTempPath(), "SalesToolUpdate");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            string zipFile = Path.Combine(tempDir, "update.zip");

            Form progressDlg = null;
            if (!silent)
            {
                try
                {
                    progressDlg = new Form
                    {
                        Text = "자동 업데이트 진행 중",
                        Size = new Size(380, 140),
                        StartPosition = FormStartPosition.CenterScreen,
                        FormBorderStyle = FormBorderStyle.FixedDialog,
                        MaximizeBox = false,
                        MinimizeBox = false
                    };
                    var lbl = new Label { Text = "최신 버전을 다운로드하고 있습니다...\n잠시 후 프로그램이 자동으로 재실행됩니다.", Location = new Point(20, 20), AutoSize = true };
                    var bar = new ProgressBar { Location = new Point(20, 55), Size = new Size(320, 22), Style = ProgressBarStyle.Marquee };
                    progressDlg.Controls.AddRange(new Control[] { lbl, bar });
                    progressDlg.Show();
                }
                catch { }
            }

            try
            {
                statusCallback?.Invoke("최신 버전 다운로드 중...");
                using (var client = new WebClient())
                {
                    client.Headers["User-Agent"] = "SalesTool-Updater";
                    await client.DownloadFileTaskAsync(new Uri(zipUrl), zipFile);
                }

                if (progressDlg != null)
                {
                    try { progressDlg.Close(); } catch { }
                }

                statusCallback?.Invoke("최신 버전 파일 교체 및 즉시 재실행 중...");

                // 1. 현재 실행 중인 exe를 즉시 .old 로 핫 스왑 (Windows 파일 락 즉시 해제)
                string oldExe = currentExe + ".old";
                if (File.Exists(oldExe)) { try { File.Delete(oldExe); } catch { } }
                try { File.Move(currentExe, oldExe); } catch { }

                // 2. 백그라운드 덮어쓰기 및 자동 재실행 스크립트 작성
                string ps1File = Path.Combine(tempDir, "apply_update.ps1");
                string psScript = @"
$zipFile = '" + zipFile.Replace("'", "''") + @"'
$targetDir = '" + exeDir.Replace("'", "''") + @"'
$exeToStart = '" + currentExe.Replace("'", "''") + @"'
$parentPid = " + currentPid + @"

# 기존 프로세스가 완전히 종료될 때까지 대기
try {
    $parent = Get-Process -Id $parentPid -ErrorAction SilentlyContinue
    if ($parent) {
        $parent.WaitForExit(5000)
    }
} catch {}

Start-Sleep -Milliseconds 600

Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipFile)
    foreach ($entry in $archive.Entries) {
        if ([string]::IsNullOrEmpty($entry.Name)) { continue }
        $dest = Join-Path $targetDir $entry.FullName
        $destParent = Split-Path $dest -Parent
        if (!(Test-Path $destParent)) { New-Item -ItemType Directory -Path $destParent -Force | Out-Null }
        
        # 파일이 락 걸려 있으면 .old 로 변경
        if (Test-Path $dest) {
            try { Remove-Item $dest -Force -ErrorAction Stop }
            catch {
                $oldFile = $dest + '.old'
                if (Test-Path $oldFile) { try { Remove-Item $oldFile -Force -ErrorAction SilentlyContinue } catch {} }
                Rename-Item -Path $dest -NewName (Split-Path $oldFile -Leaf) -Force -ErrorAction SilentlyContinue
            }
        }
        try {
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
        } catch {}
    }
    $archive.Dispose()
} catch {}

# 임시 zip 삭제
Remove-Item $zipFile -Force -ErrorAction SilentlyContinue

# 최신 버전 프로그램 자동 재실행
if (Test-Path $exeToStart) {
    Start-Process -FilePath $exeToStart
} else {
    $alt = Join-Path $targetDir '판매도구.exe'
    if (Test-Path $alt) { Start-Process -FilePath $alt }
}
";
                File.WriteAllText(ps1File, psScript, Encoding.UTF8);

                // 파워쉘을 완전히 숨겨진 창으로 실행하고 본 프로그램 즉시 종료
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + ps1File + "\"",
                    WorkingDirectory = tempDir,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                if (progressDlg != null)
                {
                    try { progressDlg.Close(); } catch { }
                }
                if (!silent)
                {
                    MessageBox.Show("업데이트 적용 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    statusCallback?.Invoke("업데이트 다운로드 실패: " + ex.Message);
                }
            }
        }
    }
}
