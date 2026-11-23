using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RepImages
{
    public class OcrLine
    {
        public int X, Y, W, H; // 원본 이미지 기준 위치
        public string Text;
        public bool Vertical;  // 옆으로 눕힌 글씨 (스틱 포장 등)
        public int Turn;       // 읽을 때 돌린 방향 (1 = 위→아래로 쓴 글씨, -1 = 아래→위로 쓴 글씨)
        public int Pass;       // 몇 번째 읽기에서 나온 줄인지
        public override string ToString() { return Y + " " + Text; }
    }

    // Windows 기본 글자 인식(무료). 글자 줄마다 위치(y)까지 돌려줌.
    // 글자 인식 부품(WinRT)은 PowerShell 에서 바로 쓸 수 있어서, 이미지를 잘라 저장한 뒤 PowerShell 로 읽힘
    public static class OcrLines
    {
        const int Slice = 3000; // 한 번에 읽을 높이 (최대 10000px). 줄이 끊기지 않게 조금씩 겹쳐 자름
        const int Overlap = 150;

        const string Script = @"param([string]$List, [string]$Out)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Foundation, ContentType = WindowsRuntime]
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait() | Out-Null; $task.Result }
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language 'ko'))
if ($engine -eq $null) { 'NO_KOREAN_OCR' | Out-File $Out -Encoding utf8; exit }
$lines = @()
foreach ($row in (Get-Content $List -Encoding UTF8)) {
  $p = $row.Split([char]9); $img = $p[0]; $offset = [int]$p[1]; $tag = $p[2]
  $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($img)) ([Windows.Storage.StorageFile])
  $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
  $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
  $bmp = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
  $res = Await ($engine.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])
  foreach ($l in $res.Lines) {
    $x1 = 99999; $y1 = 99999; $x2 = 0; $y2 = 0
    foreach ($w in $l.Words) { $r = $w.BoundingRect; if ($r.X -lt $x1) { $x1 = $r.X }; if ($r.Y -lt $y1) { $y1 = $r.Y }; if ($r.X + $r.Width -gt $x2) { $x2 = $r.X + $r.Width }; if ($r.Y + $r.Height -gt $y2) { $y2 = $r.Y + $r.Height } }
    $lines += $tag + [char]9 + ([int]$x1).ToString() + [char]9 + ([int]$y1 + $offset).ToString() + [char]9 + ([int]($x2 - $x1)).ToString() + [char]9 + ([int]($y2 - $y1)).ToString() + [char]9 + $l.Text
  }
  $stream.Dispose()
}
$lines | Out-File $Out -Encoding utf8
";

        public class Pass { public float Scale = 1f; public int Turn; }

        // 한 장만 그대로 읽기
        public static List<OcrLine> Read(Bitmap img, Action<string> log)
        {
            return ReadPasses(img, log, new Pass())[0];
        }

        // 여러 방식으로 한 번에 읽기 (PowerShell 은 한 번만 띄움)
        //  Scale: 크게 키워서 읽기 (작은 로고 글씨), Turn: 1 = 시계방향 90도 / -1 = 반시계방향 90도 돌려서 읽기 (눕힌 글씨)
        // 돌아온 줄의 위치는 모두 원본 사진 기준
        public static List<List<OcrLine>> ReadPasses(Bitmap img, Action<string> log, params Pass[] passes)
        {
            string dir = Path.Combine(Path.GetTempPath(), "RepImages_ocr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var tiles = new List<string>();
                var sizes = new List<Size>();
                for (int pi = 0; pi < passes.Length; pi++)
                {
                    var ps = passes[pi];
                    int sw = (int)(img.Width * ps.Scale), sh = (int)(img.Height * ps.Scale);
                    sizes.Add(new Size(sw, sh));
                    using (var big = new Bitmap(sw, sh, PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(big))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.DrawImage(img, 0, 0, sw, sh);
                        }
                        if (ps.Turn == 1) big.RotateFlip(RotateFlipType.Rotate90FlipNone);
                        else if (ps.Turn == -1) big.RotateFlip(RotateFlipType.Rotate270FlipNone);
                        for (int y = 0; y < big.Height; y += Slice - Overlap)
                        {
                            int h = Math.Min(Slice, big.Height - y);
                            using (var part = big.Clone(new Rectangle(0, y, big.Width, h), PixelFormat.Format24bppRgb))
                            {
                                string f = Path.Combine(dir, "t" + tiles.Count.ToString("000") + ".png");
                                part.Save(f, ImageFormat.Png);
                                tiles.Add(f + "\t" + y + "\t" + pi);
                            }
                            if (y + h >= big.Height) break;
                        }
                    }
                }
                log("글자 읽는 중... (" + tiles.Count + "조각)");
                string list = Path.Combine(dir, "list.txt"), script = Path.Combine(dir, "ocr.ps1"), outFile = Path.Combine(dir, "out.txt");
                File.WriteAllLines(list, tiles, new UTF8Encoding(true));
                File.WriteAllText(script, Script, new UTF8Encoding(true));
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -List \"" + list + "\" -Out \"" + outFile + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                string err;
                using (var p = Process.Start(psi))
                {
                    err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(300000)) { p.Kill(); throw new Exception("글자 인식이 너무 오래 걸려 멈췄습니다."); }
                }
                if (!File.Exists(outFile)) throw new Exception("글자 인식 실패: " + err);
                var result = passes.Select(x => new List<OcrLine>()).ToList();
                var seen = new HashSet<string>();
                foreach (string row in File.ReadAllLines(outFile, Encoding.UTF8))
                {
                    if (row.StartsWith("NO_KOREAN_OCR")) throw new Exception("이 PC에 한국어 글자 인식이 없습니다. (Windows 설정 → 시간 및 언어 → 언어 → 한국어 → 언어 옵션 → 광학 문자 인식 설치)");
                    var c = row.Split('\t');
                    if (c.Length < 6) continue;
                    int pi = int.Parse(c[0]);
                    var ps = passes[pi]; var sz = sizes[pi];
                    var l = new OcrLine { X = int.Parse(c[1]), Y = int.Parse(c[2]), W = int.Parse(c[3]), H = int.Parse(c[4]), Text = c[5].Trim(), Pass = pi, Turn = ps.Turn };
                    if (!seen.Add(pi + "|" + (l.Y / 8) + "|" + l.Text)) continue; // 겹친 부분에서 두 번 읽힌 줄
                    int x = l.X, y = l.Y, lw = l.W, lh = l.H;
                    if (ps.Turn == 1) { l.X = y; l.Y = sz.Height - x - lw; l.W = lh; l.H = lw; l.Vertical = true; }
                    else if (ps.Turn == -1) { l.X = sz.Width - y - lh; l.Y = x; l.W = lh; l.H = lw; l.Vertical = true; }
                    if (ps.Scale != 1f)
                    {
                        l.X = (int)(l.X / ps.Scale); l.Y = (int)(l.Y / ps.Scale);
                        l.W = (int)Math.Ceiling(l.W / ps.Scale); l.H = (int)Math.Ceiling(l.H / ps.Scale);
                    }
                    result[pi].Add(l);
                }
                foreach (var r in result) r.Sort((a, b) => a.Y.CompareTo(b.Y));
                return result;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
