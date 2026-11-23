using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RepImages
{
    public class Plate
    {
        public Rectangle Rect;   // 사진 안에서 상품명이 있는 곳 (키워드 글자판으로 덮음)
        public Color Color;      // 글자판 색 (라벨 색에서 고름)
        public int Turn;         // 0 = 가로, -1 = 위→아래로 쓴 세로 글씨, 1 = 아래→위로 쓴 세로 글씨 (스틱 포장)
    }

    public class Layout
    {
        public Bitmap Photo;        // 원본 사진 (브랜드·영문 상품명은 주변 색으로 지운 상태)
        public bool WhiteBg;        // 흰 배경 사진이면 여백을 잘라 크게, 아니면 사진 그대로
        public Rectangle Content;   // 흰 배경일 때 제품이 있는 범위
        public bool Cropped;        // 위쪽 홍보 띠(구매고객 사은품 증정 이벤트!)를 잘라 낸 사진: 여백 두고 크게 맞춤
        public List<Plate> Plates = new List<Plate>();
        public int Erased;          // 지운 브랜드·판매사·영문명 줄 수
        public List<string> BrandWords = new List<string>();   // 브랜드로 본 말 (추가이미지에서 브랜드 나오는 패널을 빼는 데 씀)
        public List<string> PackageText = new List<string>();  // 상품 사진 라벨 글씨 (추가이미지에서 상자 사진 패널을 알아보는 데 씀)
    }

    public static class Composer
    {
        public const int Size = 1000;
        static PrivateFontCollection fonts;
        static FontFamily keywordFont, badgeFont;

        // 무료 글꼴 (SIL Open Font License): Black Han Sans(키워드), Jua(1+1 표시)
        static void LoadFonts()
        {
            if (fonts != null) return;
            fonts = new PrivateFontCollection();
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts");
            foreach (var f in new[] { "BlackHanSans-Regular.ttf", "Jua-Regular.ttf" })
                if (File.Exists(Path.Combine(dir, f))) fonts.AddFontFile(Path.Combine(dir, f));
            // Black Han Sans 는 한글 이름 "검은고딕" 으로 등록됨
            badgeFont = fonts.Families.FirstOrDefault(f => f.Name.Contains("Jua"));
            keywordFont = fonts.Families.FirstOrDefault(f => f != badgeFont && (f.Name.Contains("Black Han") || f.Name.Contains("검은고딕")))
                          ?? fonts.Families.FirstOrDefault(f => f != badgeFont) ?? new FontFamily("맑은 고딕");
            if (badgeFont == null) badgeFont = keywordFont;
        }

        static readonly Regex Company = new Regex(@"제약|약품|헬스|바이오|코리아|팜|푸드|식품\(주\)|\(주\)|주식회사|Pharm|Health|Bio|Korea|\bInc\b\.?|\bCo\.|\bLtd\b", RegexOptions.IgnoreCase);
        static readonly Regex Measure = new Regex(@"^\s*[\d.,]+\s*(g|mg|ml|kg|l|정|포|개|캡슐|kcal)|\d+\s*(mg|g|ml|정|포)\s*[xX×)]|kcal", RegexOptions.IgnoreCase);

        static string Letters(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z가-힣]", ""); }
        static int Hangul(string s) { return s.Count(c => c >= '가' && c <= '힣'); }
        static int Latin(string s) { return s.Count(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')); }

        // 글자 인식이 그림·아이콘을 엉뚱하게 읽은 줄 (한자·기호가 많음)은 상품명 후보에서 뺌
        static bool Clean(string s)
        {
            var t = s.Where(c => !char.IsWhiteSpace(c)).ToList();
            if (t.Count == 0) return false;
            // 한자가 1글자 섞인 건 글자 인식 실수일 때가 많음 (製산마그네슘). 2글자 이상이면 그림을 읽은 것
            int cjk = t.Count(c => c >= 0x4E00 && c <= 0x9FFF);
            if (cjk >= 2 || (cjk == 1 && Hangul(s) < 2)) return false;
            int ok = t.Count(c => (c >= '가' && c <= '힣') || (c >= 0x4E00 && c <= 0x9FFF) || char.IsLetterOrDigit(c) && c < 128 || "%+&·.,()-/".IndexOf(c) >= 0);
            return ok >= t.Count * 0.75;
        }

        // ── 1. 사진 분석: 라벨마다 상품명 자리 + 지울 브랜드·영문명 줄
        public static Layout Analyze(Bitmap src, string brand, Action<string> log, string productName = null, IEnumerable<string> extraBrands = null)
        {
            var photo = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(photo)) { g.Clear(Color.White); g.DrawImage(src, 0, 0, src.Width, src.Height); }
            var lay = new Layout { Photo = photo, WhiteBg = IsWhiteBackground(photo) };
            lay.Content = lay.WhiteBg ? ContentBox(photo) : new Rectangle(0, 0, photo.Width, photo.Height);

            // 4가지로 읽기: 그대로 / 2배로 키워서(로고 밑 작은 브랜드 글씨) / 시계·반시계 방향으로 돌려서(눕힌 스틱 글씨)
            // + 더 크게 돌려 읽기(가장자리 세로 띠의 작은 브랜드 글씨: 병 옆면 "Alpha Nature OMEGA-3")
            var passes = OcrLines.ReadPasses(photo, log, new OcrLines.Pass(), new OcrLines.Pass { Scale = 2f },
                                             new OcrLines.Pass { Scale = 1.5f, Turn = -1 }, new OcrLines.Pass { Scale = 1.5f, Turn = 1 },
                                             new OcrLines.Pass { Scale = 2.5f, Turn = 1 }, new OcrLines.Pass { Scale = 2.5f, Turn = -1 });
            // 위쪽 홍보 띠(구매고객·사은품·증정·이벤트 …)가 있으면 그 아래 제품 부분만 씀 (우리 스토어에 없는 사은품 문구가 나오지 않게)
            var promo = passes[0].Where(l => Promo.IsMatch(Letters(l.Text))).ToList();
            if (promo.Count > 0)
            {
                int pb = promo.Max(l => l.Y + l.H), ph = promo.Max(l => l.H);
                int cut = passes[0].Where(l => l.Y < pb + ph * 2).Max(l => l.Y + l.H) + Math.Max(10, ph / 3);
                if (cut < photo.Height * 0.6)
                {
                    log("홍보 띠를 잘라 냄: 위 " + cut + "px");
                    lay.Content = Rectangle.Intersect(lay.Content, new Rectangle(0, cut, photo.Width, photo.Height - cut));
                    lay.Cropped = true;
                    for (int i = 0; i < passes.Count; i++) passes[i] = passes[i].Where(l => l.Y >= cut).ToList();
                    // 남은 글자(라벨) 범위 + 여백으로 좌우·아래를 좁혀 제품이 크게 보이게
                    var rest = passes[0].Concat(passes[1]).ToList();
                    if (rest.Count > 0)
                    {
                        int pad = photo.Width / 10;
                        int x1 = Math.Max(0, rest.Min(l => l.X) - pad), x2 = Math.Min(photo.Width, rest.Max(l => l.X + l.W) + pad);
                        int y2 = Math.Min(photo.Height, rest.Max(l => l.Y + l.H) + pad);
                        var tight = Rectangle.FromLTRB(x1, cut, x2, y2);
                        if (tight.Width > photo.Width * 0.3 && tight.Height > photo.Height * 0.2) lay.Content = Rectangle.Intersect(lay.Content, tight);
                    }
                }
            }
            var lines = passes[0].Where(l => Letters(l.Text).Length >= 1 && l.H >= 8).ToList();
            var small = passes[1].Where(l => Letters(l.Text).Length >= 2).ToList();
            var vertical = passes[2].Concat(passes[3]).ToList();
            var verticalAll = vertical.Concat(passes[4]).Concat(passes[5]).ToList();
            var words = lines.Where(l => Hangul(l.Text) + Latin(l.Text) >= 2 && !Measure.IsMatch(l.Text) && Clean(l.Text)).ToList();
            foreach (var l in lines.Concat(small).Where(l => Clean(l.Text) && !Measure.IsMatch(l.Text)))
            {
                string t = Letters(l.Text).ToLowerInvariant();
                if (t.Length >= 4 && !lay.PackageText.Contains(t)) lay.PackageText.Add(t);
            }
            if (words.Count == 0) return lay;
            int globalMax = words.Max(l => l.H);

            // 라벨(상자·통)별로 글자 줄 묶기: 가로 위치가 겹치고 세로로 가까운 줄끼리
            var clusters = Cluster(words);
            var erase = new List<Rectangle>();
            string brandKey = Letters(brand).ToLowerInvariant();
            if (brandKey.Length > 12) brandKey = "";
            // 그 밖에 브랜드로 아는 말 (학습한 브랜드 nuily·웰러스, 상세 제품명 앞의 브랜드). msgood 브랜드 칸이 틀릴 때도 있음
            var keys = (extraBrands ?? new string[0]).Select(b => Letters(b).ToLowerInvariant()).Where(k => k.Length >= 2 && k.Length <= 12 && k != brandKey).Distinct().ToList();
            Func<OcrLine, bool> isB = l => IsBrand(l, brandKey) || keys.Any(k => IsBrand(l, k));
            // 물체(상자·통·스틱)마다 상품명은 큰 글씨: 가장 큰 글씨 묶음 3개까지만
            var labelClusters = clusters
                .Select(cl => new { cl, names = cl.Where(l => Hangul(l.Text) >= 2 || Latin(l.Text) >= 4).ToList() })
                .Where(x => x.names.Count > 0 && x.names.Max(l => l.H) >= globalMax * 0.6)
                .OrderByDescending(x => x.names.Max(l => l.H)).Take(3).ToList();
            foreach (var lc in labelClusters)
            {
                var cl = lc.cl; var names = lc.names;
                int cmax = (names.Any(l => !isB(l)) ? names.Where(l => !isB(l)) : names).Max(l => l.H);

                // 가장 큰 글씨가 브랜드 로고(Nuily)면 그다음 큰 글씨가 상품명
                var main = names.Where(l => !isB(l)).OrderByDescending(l => l.H).FirstOrDefault() ?? names.OrderByDescending(l => l.H).First();
                int cy = main.Y + main.H / 2;
                // 상품명 줄: 큰 글씨(60% 이상), 또는 숫자 없는 조금 작은 글씨(45% 이상, 예: "더 원"). 함량 문구(99%, 300mg)는 빼고
                var group = names.Where(l => Math.Abs(l.Y + l.H / 2 - cy) <= cmax * 2.2 && !isB(l) &&
                                             (l.H >= cmax * 0.6 || (l.H >= cmax * 0.45 && !l.Text.Any(char.IsDigit)))).ToList();
                if (group.Count == 0) group.Add(main);
                var name = Union(group);

                // 상품명 글자판 폭: 라벨 글자 폭의 85% 이상 (상품명이 짧게 읽혀도 라벨 폭에 맞게)
                var all = Union(cl);
                int padX = Math.Max(10, (int)(cmax * 0.45)), padY = Math.Max(8, (int)(cmax * 0.32));
                var r = Rectangle.FromLTRB(name.Left - padX, name.Top - padY, name.Right + padX, name.Bottom + padY);
                int minW = (int)(all.Width * 0.85);
                if (r.Width < minW) r = Rectangle.FromLTRB(Math.Min(r.Left, all.Left), r.Top, Math.Max(r.Right, all.Left + minW), r.Bottom);
                // 글자 인식이 놓친 상품명 윗줄·아랫줄(예: "맥스", "유기농"): 바로 위·아래에 글자 같은 무늬가 있으면 넓힘
                // 옆 물체(다른 라벨 묶음) 쪽으로 넘어가지 않게
                foreach (var other in labelClusters.Where(o => o != lc).Select(o => Union(o.cl)))
                {
                    bool vert = other.Top < r.Bottom && other.Bottom > r.Top;
                    if (!vert) continue;
                    if (other.Left >= all.Right - 5 && r.Right > other.Left - 8) r = Rectangle.FromLTRB(r.Left, r.Top, other.Left - 8, r.Bottom);
                    if (other.Right <= all.Left + 5 && r.Left < other.Right + 8) r = Rectangle.FromLTRB(other.Right + 8, r.Top, r.Right, r.Bottom);
                }
                var labelBg = BorderColor(photo, r);
                r = ExtendOverInk(photo, r, cmax, -1, labelBg);
                r = ExtendOverInk(photo, r, cmax, +1, labelBg);
                lay.Plates.Add(new Plate { Rect = r, Color = LabelColor(photo, r, all) });

                // 지울 줄: 브랜드·판매사(어디 있든), 상품명 바로 위아래의 영문 상품명
                foreach (var l in cl)
                {
                    if (group.Contains(l)) continue;
                    var lr = new Rectangle(l.X, l.Y, l.W, l.H);
                    if (r.Contains(lr)) continue;                          // 글자판에 이미 덮임
                    bool latinName = Latin(l.Text) >= 4 && Latin(l.Text) >= Letters(l.Text).Length * 0.6 && !Measure.IsMatch(l.Text)
                                     && (Math.Abs(l.Y - name.Bottom) <= cmax * 1.3 || Math.Abs(name.Top - (l.Y + l.H)) <= cmax * 1.3);
                    if (isB(l) || latinName || SeriesName(l, productName, name, cmax) || (Latin(l.Text) >= 4 && Hangul(l.Text) == 0 && l.Y < name.Top && name.Top - l.Y < cmax * 5.5))
                        erase.Add(lr);
                }
            }
            // 묶음에 안 들어간 줄 중에도 브랜드·판매사명이면 지움
            // 2배로 키워 읽은 작은 글씨(로고 밑 "뉴일리" 등)도 포함. 작은 브랜드 글씨 바로 위의 로고 그림까지 같이 지움
            // msgood 에 브랜드가 없으면 상품명 첫 단어(대개 브랜드)를 작은 글씨에만 맞춰 봄 (뉴일리 마시는마그네슘 → 뉴일리)
            // 글씨 크기와 상관없이 (큰 "N뉴일리" 로고 글씨도)
            string firstWord = (productName ?? "").Trim().Split(' ')[0];
            string nameKey = Letters(firstWord).ToLowerInvariant(); if (nameKey == brandKey) nameKey = "";   // msgood 브랜드 칸이 틀릴 때도 (뉴일리 상품인데 소연골)
            Func<OcrLine, bool> nameBrand = l => nameKey.Length >= 2 && nameKey.Length <= 6 && !l.Vertical
                && Letters(l.Text).Length <= nameKey.Length + 2 && IsBrand(l, nameKey);
            // msgood 에 없는 영문 브랜드: 사진 두 군데 이상에 나오는 영문 단어 (상자·통 양쪽의 "Alpha Nature")
            var latinBrand = LatinBrandWords(lines.Concat(small).Concat(verticalAll).ToList(), labelClusters.Select(x => x.names.OrderByDescending(l => l.H).First()).ToList());
            Func<OcrLine, bool> hasLatinBrand = l => { string t = Letters(l.Text).ToLowerInvariant(); return latinBrand.Any(w => t.Contains(w)); };
            if (latinBrand.Count > 0) log("영문 브랜드로 봄: " + string.Join(", ", latinBrand));
            foreach (var k in new[] { brandKey, nameKey.Length <= 6 ? nameKey : "" }.Concat(latinBrand).Concat(keys.Where(k2 => lines.Concat(small).Any(l => IsBrand(l, k2)))))
                if (!string.IsNullOrEmpty(k) && k.Length >= 2 && !lay.BrandWords.Contains(k)) lay.BrandWords.Add(k);

            var brandLines = lines.Concat(small).Where(l => isB(l) || nameBrand(l) || hasLatinBrand(l)).ToList();
            foreach (var l in brandLines)
            {
                var lr = new Rectangle(l.X, l.Y, l.W, l.H);
                if (lay.Plates.Any(p => p.Rect.Contains(lr)) || erase.Any(e => e.Contains(lr))) continue;
                lr = WithLogoAbove(photo, lr);
                lr = WithLogoBeside(photo, lr, l.H);
                // 브랜드 바로 밑·위에 붙은 작은 글씨 (뉴트로웨이 밑의 "newtroway")
                foreach (var s in lines.Concat(small))
                {
                    var sr = new Rectangle(s.X, s.Y, s.W, s.H);
                    bool near = s.H <= l.H && sr.Left >= lr.Left - l.H && sr.Right <= lr.Right + l.H
                                && ((sr.Top >= lr.Bottom - 2 && sr.Top - lr.Bottom <= l.H * 0.8) || (lr.Top >= sr.Bottom - 2 && lr.Top - sr.Bottom <= l.H * 0.8));
                    if (near && !Measure.IsMatch(s.Text)) lr = Rectangle.Union(lr, sr);
                }
                lr = InkBelow(photo, lr, l.H);
                log("브랜드 지움: " + l.Text + " " + lr);
                erase.RemoveAll(e => lr.Contains(e));
                erase.Add(lr);
            }
            // 세로 띠의 브랜드 (상자·병 옆면) + 같은 문구의 다른 세로 띠(병 쪽은 브랜드 부분을 못 읽어도 "OMEGA-3" 꼬리가 같음)
            var vBrand = verticalAll.Where(l => (isB(l) || hasLatinBrand(l)) && l.H >= l.W * 2.5).ToList();
            foreach (var l in verticalAll.Where(l => l.H >= l.W * 2.5 && (vBrand.Contains(l) || vBrand.Any(b => b != l && CommonRun(Letters(b.Text).ToLowerInvariant(), Letters(l.Text).ToLowerInvariant()) >= 5))).ToList())
            {
                var lr = new Rectangle(l.X, l.Y, l.W, l.H);
                lr.Inflate(2, l.H / 12);
                lr.Intersect(new Rectangle(0, 0, photo.Width, photo.Height));
                if (lay.Plates.Any(p => Overlap(lr, p.Rect) > 0.5) || erase.Any(e => e.Contains(lr))) continue;
                erase.RemoveAll(e => lr.Contains(e));
                erase.Add(lr);
            }

            // 스틱 포장처럼 눕힌 상품명: 세로 글자판으로 덮음
            if (lay.Plates.Count < 4) AddStickPlates(lay, photo, vertical, words, brandKey, erase);
            foreach (var e in erase) Erase(photo, e);
            // 원래 상품명 자리(Plates)도 라벨 배경으로 깨끗하게 지움 (브랜드명·원래상품명 완벽 은폐)
            foreach (var p in lay.Plates) Erase(photo, p.Rect);
            lay.Erased = erase.Count + lay.Plates.Count;

            // 2차 확인: 지운 사진을 다시 읽어서 브랜드가 남았으면 한 번 더 지움
            if (brandKey.Length >= 2 || nameKey.Length >= 2 || latinBrand.Count > 0 || keys.Count > 0)
            {
                var again = OcrLines.ReadPasses(photo, log, new OcrLines.Pass(), new OcrLines.Pass { Scale = 2f });
                foreach (var l in again[0].Concat(again[1]).Where(l => isB(l) || nameBrand(l) || hasLatinBrand(l)))
                {
                    var lr = new Rectangle(l.X, l.Y, l.W, l.H);
                    if (lay.Plates.Any(p => p.Rect.Contains(lr))) continue;
                    lr = WithLogoBeside(photo, lr, l.H);
                    Erase(photo, lr);
                    lay.Erased++;
                    log("2차 확인에서 남은 브랜드를 지움: " + l.Text);
                }
            }
            return lay;
        }

        // 사진 여러 곳(겹치지 않는 자리 2곳 이상)에 나오는 영문 단어 = 브랜드로 봄. 상품명 큰 글씨·흔한 표기는 뺌
        static readonly HashSet<string> CommonLatin = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "made","canada","korea","usa","japan","germany","australia","newzealand","dietary","supplement","health","healthy","functional","food","foods",
            "premium","organic","natural","haccp","omega","vitamin","vitamins","lemon","ginger","juice","collagen","probiotics","lutein","magnesium",
            "calcium","policosanol","protein","kcal","kcai","extract","selenium","zinc","iron","oil","powder","stick","sticks","softgel","softgels","capsule","capsules","tablet","tablets","total","daily","plus","gold","care","with","from","and","the" };

        static List<string> LatinBrandWords(List<OcrLine> all, List<OcrLine> mains)
        {
            var mainText = string.Join(" ", mains.Select(m => Letters(m.Text).ToLowerInvariant()));
            var seen = new Dictionary<string, List<Rectangle>>();
            foreach (var l in all)
                foreach (Match m in Regex.Matches(l.Text, "[A-Za-z]{4,}"))
                {
                    string w = m.Value.ToLowerInvariant();
                    if (CommonLatin.Contains(w) || CommonLatin.Any(c => c.Length >= 5 && w.Contains(c)) || mainText.Contains(w)) continue;
                    List<Rectangle> at; if (!seen.TryGetValue(w, out at)) seen[w] = at = new List<Rectangle>();
                    var r = new Rectangle(l.X, l.Y, l.W, l.H);
                    if (!at.Any(x => Overlap(x, r) > 0.2 || Overlap(r, x) > 0.2)) at.Add(r);
                }
            return seen.Where(kv => kv.Value.Count >= 2).Select(kv => kv.Key).ToList();
        }

        static int CommonRun(string a, string b)
        {
            int best = 0;
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                {
                    int k = 0; while (i + k < a.Length && j + k < b.Length && a[i + k] == b[j + k]) k++;
                    if (k > best) best = k;
                }
            return best;
        }

        // 브랜드 글씨 바로 옆에 붙은 로고 그림(뉴일리 앞의 "N", 뉴트로웨이 앞의 잎 그림)까지 넓힘
        static Rectangle WithLogoBeside(Bitmap b, Rectangle lr, int h)
        {
            int y1 = Math.Max(0, lr.Top - h / 2), y2 = Math.Min(b.Height - 1, lr.Bottom + h / 2);
            int maxExt = (int)(h * 2.8), gapMax = Math.Max(4, h);
            // 바탕색: 글씨 바로 좌우에 붙은 얇은 띠에서 가장 흔한 색 (주변 다른 그림 색에 끌리지 않게)
            var hist = new Dictionary<int, long[]>();
            for (int y = lr.Top; y < lr.Bottom; y++)
                foreach (int x in new[] { lr.Left - 4, lr.Left - 3, lr.Left - 2, lr.Right + 1, lr.Right + 2, lr.Right + 3 })
                {
                    if (x < 0 || x >= b.Width) continue;
                    var c = b.GetPixel(x, y); int key = (c.R / 24) * 4096 + (c.G / 24) * 64 + c.B / 24;
                    long[] a; if (!hist.TryGetValue(key, out a)) hist[key] = a = new long[4];
                    a[0] += c.R; a[1] += c.G; a[2] += c.B; a[3]++;
                }
            if (hist.Count == 0) return lr;
            var md = hist.Values.OrderByDescending(v => v[3]).First();
            var bg = Color.FromArgb((int)(md[0] / md[3]), (int)(md[1] / md[3]), (int)(md[2] / md[3]));
            Func<int, int, bool> ink = (x, y) => { var c = b.GetPixel(x, y); return Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 110; };
            Func<int, bool> colInk = x => { int n = 0; for (int y = y1; y <= y2; y++) if (ink(x, y)) n++; return n >= Math.Max(2, (y2 - y1) / 10); };
            int left = lr.Left, right = lr.Right;
            for (int x = lr.Left - 1, gap = 0; x >= Math.Max(0, lr.Left - maxExt); x--) { if (colInk(x)) { left = x; gap = 0; } else if (++gap > gapMax) break; }
            for (int x = lr.Right, gap = 0; x < Math.Min(b.Width, lr.Right + maxExt); x++) { if (colInk(x)) { right = x + 1; gap = 0; } else if (++gap > gapMax) break; }
            // 너무 멀리까지 잡히면(다른 그림·글씨에 붙음) 그대로
            if (lr.Left - left >= maxExt - 1) left = lr.Left;
            if (right - lr.Right >= maxExt - 1) right = lr.Right;
            if (left == lr.Left && right == lr.Right) return lr;
            // 로고 그림은 글씨보다 위아래로 클 수 있음
            int top = lr.Top, bot = lr.Bottom;
            for (int y = lr.Top - 1; y >= y1; y--) { bool any = false; for (int x = left; x < right; x++) if (ink(x, y)) { any = true; break; } if (any) top = y; else break; }
            for (int y = lr.Bottom; y <= y2; y++) { bool any = false; for (int x = left; x < right; x++) if (ink(x, y)) { any = true; break; } if (any) bot = y + 1; else break; }
            return Rectangle.FromLTRB(left, top, right, bot);
        }

        // 브랜드 밑에 바로 붙은 작은 글씨(영문 브랜드 등)를 글자 인식이 못 읽었을 때: 무늬가 이어지면 넓힘
        static Rectangle InkBelow(Bitmap b, Rectangle lr, int h)
        {
            var bg = BorderColor(b, Rectangle.Inflate(lr, 8, 6));
            int bot = lr.Bottom, gap = 0, limit = Math.Min(b.Height - 1, lr.Bottom + h);
            for (int y = lr.Bottom; y <= limit; y++)
            {
                int n = 0;
                for (int x = lr.Left; x < lr.Right; x++) { var c = b.GetPixel(x, y); if (Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 110) n++; }
                if (n >= 2 && n <= lr.Width * 0.5) { bot = y + 1; gap = 0; } else if (++gap > Math.Max(3, h / 4)) break;
            }
            return Rectangle.FromLTRB(lr.Left, lr.Top, lr.Right, bot);
        }

        // 글자판 테두리 바로 바깥의 가장 흔한 색 (= 라벨 바탕색)
        static Color BorderColor(Bitmap b, Rectangle r)
        {
            var hist = new Dictionary<int, long[]>();
            Action<int, int> take = (x, y) =>
            {
                if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) return;
                var c = b.GetPixel(x, y);
                int key = (c.R / 24) * 4096 + (c.G / 24) * 64 + c.B / 24;
                long[] a; if (!hist.TryGetValue(key, out a)) hist[key] = a = new long[4];
                a[0] += c.R; a[1] += c.G; a[2] += c.B; a[3]++;
            };
            for (int x = r.Left; x < r.Right; x += 2) { take(x, r.Top - 3); take(x, r.Bottom + 3); }
            for (int y = r.Top; y < r.Bottom; y += 2) { take(r.Left - 3, y); take(r.Right + 3, y); }
            if (hist.Count == 0) return Color.White;
            var top = hist.Values.OrderByDescending(v => v[3]).First();
            return Color.FromArgb((int)(top[0] / top[3]), (int)(top[1] / top[3]), (int)(top[2] / top[3]));
        }

        static Rectangle ExtendOverInk(Bitmap b, Rectangle r, int lineH, int dir, Color labelBg)
        {
            for (int step = 0; step < 1; step++)
            {
                int bandH = (int)(lineH * 1.15);
                var band = dir < 0 ? new Rectangle(r.Left + r.Width / 8, r.Top - bandH, r.Width * 3 / 4, bandH)
                                   : new Rectangle(r.Left + r.Width / 8, r.Bottom, r.Width * 3 / 4, bandH);
                if (band.Top < 0 || band.Bottom > b.Height) break;
                // 띠의 바탕색(가장 흔한 색)과 크게 다른 점이 8% 넘으면 글자가 있는 것
                var hist = new Dictionary<int, int>();
                var px = new List<Color>();
                for (int y = band.Top; y < band.Bottom; y += 2)
                    for (int x = band.Left; x < band.Right; x += 2)
                    {
                        var c = b.GetPixel(x, y); px.Add(c);
                        int key = (c.R / 24) * 4096 + (c.G / 24) * 64 + c.B / 24;
                        int n; hist.TryGetValue(key, out n); hist[key] = n + 1;
                    }
                if (px.Count == 0) break;
                int bgKey = hist.OrderByDescending(kv => kv.Value).First().Key;
                int bgR = (bgKey / 4096) * 24 + 12, bgG = ((bgKey / 64) % 64) * 24 + 12, bgB = (bgKey % 64) * 24 + 12;
                // 띠의 바탕이 라벨 바탕과 같은 색일 때만 (사진·다른 물체로 넘어가지 않게)
                if (Math.Abs(bgR - labelBg.R) + Math.Abs(bgG - labelBg.G) + Math.Abs(bgB - labelBg.B) > 75) break;
                int ink = px.Count(c => Math.Abs(c.R - bgR) + Math.Abs(c.G - bgG) + Math.Abs(c.B - bgB) > 120);
                double share = ink / (double)px.Count;
                if (share < 0.08 || share > 0.35) break;                // 비었거나 사진·무늬로 꽉 찬 곳은 아님
                r = dir < 0 ? Rectangle.FromLTRB(r.Left, band.Top, r.Right, r.Bottom) : Rectangle.FromLTRB(r.Left, r.Top, r.Right, band.Bottom);
            }
            return r;
        }

        // 작은 브랜드 글씨 바로 위에 붙은 로고 그림(예: Nuily 밑의 "뉴일리")까지 지울 범위로 넓힘
        static Rectangle WithLogoAbove(Bitmap b, Rectangle lr)
        {
            if (lr.Height > 24) return lr;                        // 큰 글씨면 그 자체가 브랜드 표기
            var ring = Rectangle.Inflate(lr, 6, 4);
            var bg = BorderColor(b, ring);
            int h = lr.Height, cx = lr.Left + lr.Width / 2, half = Math.Max((int)(lr.Width * 1.8), h * 5);
            int x1 = Math.Max(0, cx - half), x2 = Math.Min(b.Width - 1, cx + half);
            int yLimit = Math.Max(0, lr.Top - h * 6);
            Func<int, int, bool> ink = (x, y) => { var c = b.GetPixel(x, y); return Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 110; };
            // 줄마다 무늬 점 수를 세고, 가장 진한 줄의 25% 이상인 줄만 로고로 봄 (바탕 무늬·번개 그림은 빼고)
            var rowInk = new Dictionary<int, int>();
            for (int y = lr.Top - 1; y >= yLimit; y--)
            {
                int n = 0;
                for (int x = x1; x <= x2; x++) if (ink(x, y)) n++;
                rowInk[y] = n;
            }
            if (rowInk.Count == 0) return lr;
            int thr = Math.Max(3, (int)(rowInk.Values.Max() * 0.25));
            int top = lr.Top, gap = 0;
            for (int y = lr.Top - 1; y >= yLimit; y--)
            {
                if (rowInk[y] >= thr) { top = y; gap = 0; } else if (++gap > h * 1.3) break;
            }
            if (top >= lr.Top - 2) return lr;
            int colThr = Math.Max(2, (int)((lr.Top - top) * 0.12));
            var col = new bool[x2 - x1 + 1];
            for (int x = x1; x <= x2; x++)
            {
                int n = 0;
                for (int y = top; y < lr.Top; y++) if (ink(x, y)) n++;
                col[x - x1] = n >= colThr;
            }
            // 브랜드 글씨 쪽에서 좌우로 이어진 무늬만 (떨어진 상자 모서리·그림은 뺌)
            int minX = int.MaxValue, maxX = -1, maxGap = Math.Max(4, h / 2);
            for (int x = lr.Left, g0 = 0; x >= x1; x--) { if (col[x - x1]) { minX = x; g0 = 0; } else if (++g0 > maxGap) break; }
            for (int x = lr.Left, g0 = 0; x <= x2; x++) { if (col[x - x1]) { maxX = x; g0 = 0; if (minX == int.MaxValue) minX = x; } else if (x > lr.Right && ++g0 > maxGap) break; }
            if (maxX < 0) return lr;
            var logo = Rectangle.FromLTRB(Math.Min(minX, lr.Left), top, Math.Max(maxX + 1, lr.Right), lr.Bottom);
            // 너무 크면(그림·다른 글씨까지 잡힘) 글씨만 지움
            if (logo.Height > h * 6 || logo.Width > Math.Max(lr.Width * 4, h * 12)) return lr;
            return logo;
        }

        // 눕힌 글씨 묶음(스틱 포장): 상품명·브랜드 줄 전체를 세로 글자판으로 덮음
        static void AddStickPlates(Layout lay, Bitmap photo, List<OcrLine> vertical, List<OcrLine> horizontal, string brandKey, List<Rectangle> erase)
        {
            var v = vertical.Where(l => l.H >= l.W * 2.5 && l.W >= 10 && (Clean(l.Text) || Regex.IsMatch(l.Text, "[가-힣]{3,}")) && !Measure.IsMatch(l.Text)
                                        && (Hangul(l.Text) >= 3 || Latin(l.Text) >= 6)).ToList();
            // 가로 글씨 줄 위에 걸친 것(가로 글씨를 돌려 읽은 헛것)은 뺌
            v = v.Where(l => !horizontal.Any(hz => Overlap(new Rectangle(l.X, l.Y, l.W, l.H), new Rectangle(hz.X, hz.Y, hz.W, hz.H)) > 0.3)).ToList();
            if (v.Count == 0) return;
            // 같은 스틱: 세로로 겹치고 가로로 가까운 줄끼리
            int n = v.Count; var parent = Enumerable.Range(0, n).ToArray();
            Func<int, int> find = null; find = i => parent[i] == i ? i : (parent[i] = find(parent[i]));
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var a = v[i]; var c = v[j];
                    int yo = Math.Min(a.Y + a.H, c.Y + c.H) - Math.Max(a.Y, c.Y);
                    int xg = Math.Max(a.X, c.X) - Math.Min(a.X + a.W, c.X + c.W);
                    if (yo > Math.Min(a.H, c.H) * 0.3 && xg < Math.Max(a.W, c.W) * 1.2) parent[find(i)] = find(j);
                }
            foreach (var grp in Enumerable.Range(0, n).GroupBy(find).Select(gp => gp.Select(i => v[i]).ToList()))
            {
                var main = grp.OrderByDescending(l => l.W).First();
                if (Hangul(main.Text) < 3 || main.W < 14) continue;
                // 그림·무늬를 엉뚱하게 읽은 줄(한글이 드문드문)은 상품명이 아님
                int nonSpace = main.Text.Count(c => !char.IsWhiteSpace(c));
                if (!Clean(main.Text) && Hangul(main.Text) < nonSpace * 0.5) continue;
                var u = Union(grp);
                int padX = Math.Max(6, (int)(main.W * 0.3)), padY = Math.Max(8, (int)(main.W * 0.4));
                var r = Rectangle.FromLTRB(u.Left - padX, u.Top - padY, u.Right + padX, u.Bottom + padY);
                r.Intersect(new Rectangle(0, 0, photo.Width, photo.Height));
                if (lay.Plates.Any(p => Overlap(r, p.Rect) > 0.15 || Overlap(p.Rect, r) > 0.15)) continue;
                int turn = grp.Sum(l => l.Turn * Hangul(l.Text)) >= 0 ? 1 : -1;
                lay.Plates.Add(new Plate { Rect = r, Color = LabelColor(photo, r, r), Turn = turn });
                erase.RemoveAll(e => r.Contains(e));
            }
        }

        // a 중 b 와 겹치는 비율
        static double Overlap(Rectangle a, Rectangle b)
        {
            var i = Rectangle.Intersect(a, b);
            return i.Width <= 0 || i.Height <= 0 || a.Width * a.Height == 0 ? 0 : (double)i.Width * i.Height / (a.Width * a.Height);
        }

        // 상품명 바로 위의 짧은 한글 줄이 msgood 상품명 일부와 같으면 브랜드·시리즈명 (데일리톡톡)
        static bool SeriesName(OcrLine l, string productName, Rectangle name, float cmax)
        {
            if (string.IsNullOrEmpty(productName) || l.Text.Trim().Contains(" ")) return false;
            string t = Letters(l.Text);
            if (t.Length < 3 || t.Length > 7 || Hangul(t) < t.Length * 0.6 || l.Text.Any(char.IsDigit)) return false;
            if (l.Y + l.H > name.Top + cmax * 0.3 || name.Top - (l.Y + l.H) > cmax * 2.5 || l.H > cmax * 0.8) return false;
            // 상품명 바로 위(글씨 한 줄 거리)의 짧은 한글 이름 줄은 msgood 상품명에 없어도 시리즈명 (데일리톡톡)
            string hg = new string(t.Where(c => c >= '가' && c <= '힣').ToArray());
            if (hg.Length >= 3 && hg.Length <= 6 && name.Top - (l.Y + l.H) <= cmax * 0.8 && l.H >= cmax * 0.2
                && l.X < name.Right && l.X + l.W > name.Left && !GenericAbove.IsMatch(hg)) return true;
            string pn = Letters(productName);
            for (int i = 0; i + t.Length <= pn.Length; i++)
            {
                int hit = 0;
                for (int k = 0; k < t.Length; k++) if (pn[i + k] == t[k]) hit++;
                if (hit >= Math.Ceiling(t.Length * 0.6)) return true;          // 글자 인식이 조금 틀려도 (데일리특특)
            }
            return false;
        }

        // 상품명 위에 흔히 붙는 꾸밈말 (지울 필요 없는 것)
        static readonly Regex GenericAbove = new Regex("^(유기농|국내산|프리미엄|식물성|어린이|어르신|임산부|저분자|고함량|무첨가|건강기능식품)$");

        static bool IsBrand(OcrLine l, string brandKey)
        {
            string t = Letters(l.Text).ToLowerInvariant();
            if (t.Length < 2) return false;
            if (Company.IsMatch(l.Text)) return true;
            if (brandKey.Length >= 2)
            {
                if (t.Contains(brandKey) || (brandKey.Contains(t) && t.Length >= 2)) return true;
                int hit = brandKey.Count(c => t.IndexOf(c) >= 0);        // 글자 인식이 조금 틀려도 (뉴일리 → `뉴일리, *뉴일리)
                if (t.Length <= brandKey.Length + 3 && hit >= Math.Max(2, (int)Math.Ceiling(brandKey.Length * 0.67))) return true;
            }
            return false;
        }

        static List<List<OcrLine>> Cluster(List<OcrLine> lines)
        {
            int n = lines.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            Func<int, int> find = null;
            find = i => parent[i] == i ? i : (parent[i] = find(parent[i]));
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var a = lines[i]; var b = lines[j];
                    int overlap = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
                    bool sameColumn = overlap > Math.Min(a.W, b.W) * 0.3 || Math.Abs(a.X - b.X) < 40;
                    int gap = Math.Max(a.Y, b.Y) - Math.Min(a.Y + a.H, b.Y + b.H);
                    if (sameColumn && gap < Math.Max(a.H, b.H) * 3.2) parent[find(i)] = find(j);
                }
            return Enumerable.Range(0, n).GroupBy(find).Select(gr => gr.Select(i => lines[i]).ToList()).ToList();
        }

        static Rectangle Union(IEnumerable<OcrLine> ls)
        {
            return Rectangle.FromLTRB(ls.Min(l => l.X), ls.Min(l => l.Y), ls.Max(l => l.X + l.W), ls.Max(l => l.Y + l.H));
        }

        // 글자 자리를 위·아래 주변 색으로 자연스럽게 메움 (원래 없었던 것처럼)
        public static void Erase(Bitmap b, Rectangle r)
        {
            r.Inflate(5, 4);
            r.Intersect(new Rectangle(1, 1, b.Width - 2, b.Height - 2));
            if (r.Width <= 0 || r.Height <= 0) return;
            // 세로로 긴 띠(옆면 세로 글씨)는 왼쪽·오른쪽 색으로 채움
            // 가로 글씨라도 위·아래 색이 서로 많이 다르고(위에 다른 그림) 좌우가 비슷하면 좌우로 채움
            bool sideFill = r.Height > r.Width * 2;
            if (!sideFill)
            {
                double tb = 0, lrd = 0; int nt = 0, nl = 0;
                for (int x = 0; x < r.Width; x += 3)
                {
                    var c1 = Avg(b, r.Left + x, Math.Max(0, r.Top - 4), r.Top - 1); var c2 = Avg(b, r.Left + x, r.Bottom, Math.Min(b.Height - 1, r.Bottom + 3));
                    tb += Math.Abs(c1.R - c2.R) + Math.Abs(c1.G - c2.G) + Math.Abs(c1.B - c2.B); nt++;
                }
                for (int y = 0; y < r.Height; y += 3)
                {
                    var c1 = AvgX(b, r.Top + y, Math.Max(0, r.Left - 4), r.Left - 1); var c2 = AvgX(b, r.Top + y, r.Right, Math.Min(b.Width - 1, r.Right + 3));
                    lrd += Math.Abs(c1.R - c2.R) + Math.Abs(c1.G - c2.G) + Math.Abs(c1.B - c2.B); nl++;
                }
                if (nt > 0 && nl > 0 && tb / nt > 60 && lrd / nl < tb / nt * 0.5) sideFill = true;
            }
            if (sideFill)
            {
                var lc = new Color[r.Height]; var rc = new Color[r.Height];
                for (int y = 0; y < r.Height; y++)
                {
                    lc[y] = AvgX(b, r.Top + y, Math.Max(0, r.Left - 4), r.Left - 1);
                    rc[y] = AvgX(b, r.Top + y, r.Right, Math.Min(b.Width - 1, r.Right + 3));
                }
                // 띠 안의 가장 흔한 색(= 글씨 뒤 바탕, 예: 빨간 옆면)과 양옆 색이 다르면 띠 바탕색으로 채움
                var hist = new Dictionary<int, long[]>();
                for (int y = r.Top; y < r.Bottom; y += 2)
                    for (int x = r.Left; x < r.Right; x += 2)
                    {
                        var c = b.GetPixel(x, y); int key = (c.R / 64) * 4096 + (c.G / 64) * 64 + c.B / 64;   // 굵게 나눔 (빨강 계열이 여러 칸으로 흩어지지 않게)
                        long[] a; if (!hist.TryGetValue(key, out a)) hist[key] = a = new long[4];
                        a[0] += c.R; a[1] += c.G; a[2] += c.B; a[3]++;
                    }
                // 흰·검은 글씨가 많아도 색 있는 바탕(빨간 띠)이 20% 이상이면 그 색
                long total = hist.Values.Sum(v => v[3]);
                Func<long[], bool> isColor = v => { int R = (int)(v[0] / v[3]), G = (int)(v[1] / v[3]), B = (int)(v[2] / v[3]); int hi = Math.Max(R, Math.Max(G, B)), lo = Math.Min(R, Math.Min(G, B)); return hi - lo >= 60 && hi >= 80; };
                var mode = hist.Values.Where(v => isColor(v) && v[3] >= total * 0.2).OrderByDescending(v => v[3]).FirstOrDefault()
                           ?? hist.Values.OrderByDescending(v => v[3]).First();
                var own = Color.FromArgb((int)(mode[0] / mode[3]), (int)(mode[1] / mode[3]), (int)(mode[2] / mode[3]));
                Func<Color[], bool> differs = arr => arr.Count(c => Math.Abs(c.R - own.R) + Math.Abs(c.G - own.G) + Math.Abs(c.B - own.B) > 90) > arr.Length * 0.5;
                // 띠 바탕이 흰색·검은색이면 그건 글씨 색일 수 있음 → 양옆 색을 씀
                int mx = Math.Max(own.R, Math.Max(own.G, own.B)), mn = Math.Min(own.R, Math.Min(own.G, own.B));
                bool colorful = mx - mn >= 60 && mx >= 80;
                if (colorful && differs(lc) && differs(rc)) { for (int y = 0; y < r.Height; y++) { lc[y] = own; rc[y] = own; } }
                else if (colorful && differs(lc) && !differs(rc)) lc = rc; else if (colorful && differs(rc) && !differs(lc)) rc = lc;
                lc = Smooth(lc); rc = Smooth(rc);
                for (int x = 0; x < r.Width; x++)
                {
                    double t = (x + 0.5) / r.Width;
                    for (int y = 0; y < r.Height; y++)
                    {
                        var c1 = lc[y]; var c2 = rc[y];
                        b.SetPixel(r.Left + x, r.Top + y, Color.FromArgb((int)(c1.R + (c2.R - c1.R) * t), (int)(c1.G + (c2.G - c1.G) * t), (int)(c1.B + (c2.B - c1.B) * t)));
                    }
                }
                return;
            }
            var top = new Color[r.Width]; var bot = new Color[r.Width];
            for (int x = 0; x < r.Width; x++)
            {
                top[x] = Avg(b, r.Left + x, Math.Max(0, r.Top - 4), r.Top - 1);
                bot[x] = Avg(b, r.Left + x, r.Bottom, Math.Min(b.Height - 1, r.Bottom + 3));
            }
            // 가로로도 조금 부드럽게 (줄무늬 방지)
            top = Smooth(top); bot = Smooth(bot);
            for (int y = 0; y < r.Height; y++)
            {
                double t = (y + 0.5) / r.Height;
                for (int x = 0; x < r.Width; x++)
                {
                    var c1 = top[x]; var c2 = bot[x];
                    b.SetPixel(r.Left + x, r.Top + y, Color.FromArgb((int)(c1.R + (c2.R - c1.R) * t), (int)(c1.G + (c2.G - c1.G) * t), (int)(c1.B + (c2.B - c1.B) * t)));
                }
            }
        }

        static Color Avg(Bitmap b, int x, int y1, int y2)
        {
            int r = 0, g = 0, bl = 0, n = 0;
            for (int y = y1; y <= y2; y++) { var c = b.GetPixel(x, y); r += c.R; g += c.G; bl += c.B; n++; }
            return n == 0 ? Color.White : Color.FromArgb(r / n, g / n, bl / n);
        }

        static Color AvgX(Bitmap b, int y, int x1, int x2)
        {
            int r = 0, g = 0, bl = 0, n = 0;
            for (int x = x1; x <= x2; x++) { var c = b.GetPixel(x, y); r += c.R; g += c.G; bl += c.B; n++; }
            return n == 0 ? Color.White : Color.FromArgb(r / n, g / n, bl / n);
        }

        static Color[] Smooth(Color[] a)
        {
            var o = new Color[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                for (int k = Math.Max(0, i - 6); k <= Math.Min(a.Length - 1, i + 6); k++) { r += a[k].R; g += a[k].G; b += a[k].B; n++; }
                o[i] = Color.FromArgb(r / n, g / n, b / n);
            }
            return o;
        }

        // 테두리 줄의 90% 이상이 거의 흰색이면 흰 배경
        static bool IsWhiteBackground(Bitmap b)
        {
            int white = 0, n = 0;
            for (int x = 0; x < b.Width; x += 4) foreach (int y in new[] { 2, b.Height - 3 }) { var c = b.GetPixel(x, y); n++; if (c.R > 235 && c.G > 235 && c.B > 235) white++; }
            for (int y = 0; y < b.Height; y += 4) foreach (int x in new[] { 2, b.Width - 3 }) { var c = b.GetPixel(x, y); n++; if (c.R > 235 && c.G > 235 && c.B > 235) white++; }
            return white >= n * 0.9;
        }

        static Rectangle ContentBox(Bitmap b)
        {
            int minX = b.Width, minY = b.Height, maxX = 0, maxY = 0;
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var row = new byte[d.Stride];
            for (int y = 0; y < b.Height; y++)
            {
                Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
                for (int x = 0; x < b.Width; x++)
                {
                    int i = x * 3;
                    if (row[i] < 240 || row[i + 1] < 240 || row[i + 2] < 240) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                }
            }
            b.UnlockBits(d);
            if (maxX <= minX) return new Rectangle(0, 0, b.Width, b.Height);
            var r = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            r.Inflate(6, 6);
            r.Intersect(new Rectangle(0, 0, b.Width, b.Height));
            return r;
        }

        // 상품명 둘레의 라벨 색 중 가장 흔한 색. 라벨이 흰색이면 그 라벨의 진한 포인트 색
        static Color LabelColor(Bitmap b, Rectangle r, Rectangle label)
        {
            var counts = new Dictionary<int, long[]>();
            Action<int, int> take = (x, y) =>
            {
                if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) return;
                var c = b.GetPixel(x, y);
                int key = (c.R / 32) * 64 + (c.G / 32) * 8 + c.B / 32;
                long[] s; if (!counts.TryGetValue(key, out s)) counts[key] = s = new long[4];
                s[0] += c.R; s[1] += c.G; s[2] += c.B; s[3]++;
            };
            for (int x = r.Left; x < r.Right; x += 2) { take(x, r.Top - 4); take(x, r.Bottom + 4); }
            for (int y = r.Top; y < r.Bottom; y += 2) { take(r.Left - 4, y); take(r.Right + 4, y); }
            if (counts.Count == 0) return Color.FromArgb(40, 40, 40);
            // 글자판이 라벨 밖(흰 바탕·흰 뚜껑)에 조금 걸쳐도 라벨 색을 고르도록, 흰색은 빼고 셈 (색 있는 점이 30% 이상일 때)
            long total = counts.Values.Sum(v => v[3]);
            var colored = counts.Values.Where(v => Luma(Color.FromArgb((int)(v[0] / v[3]), (int)(v[1] / v[3]), (int)(v[2] / v[3]))) <= 215).ToList();
            var pool = colored.Sum(v => v[3]) >= total * 0.3 ? colored : counts.Values.ToList();
            var top = pool.OrderByDescending(v => v[3]).First();
            var c0 = Color.FromArgb((int)(top[0] / top[3]), (int)(top[1] / top[3]), (int)(top[2] / top[3]));
            if (Luma(c0) <= 200) return c0;

            var area = Rectangle.Inflate(label, label.Width / 6, label.Height / 3);
            area.Intersect(new Rectangle(0, 0, b.Width, b.Height));
            var accent = new Dictionary<int, long[]>();
            for (int y = area.Top; y < area.Bottom; y += 3)
                for (int x = area.Left; x < area.Right; x += 3)
                {
                    var c = b.GetPixel(x, y);
                    int mx = Math.Max(c.R, Math.Max(c.G, c.B)), mn = Math.Min(c.R, Math.Min(c.G, c.B));
                    if (mx - mn < 60 || Luma(c) > 190 || Luma(c) < 25) continue;
                    int key = (c.R / 32) * 64 + (c.G / 32) * 8 + c.B / 32;
                    long[] a; if (!accent.TryGetValue(key, out a)) accent[key] = a = new long[4];
                    a[0] += c.R; a[1] += c.G; a[2] += c.B; a[3]++;
                }
            if (accent.Count == 0) return Color.FromArgb(32, 48, 92);
            var best = accent.Values.OrderByDescending(v => v[3]).First();
            return Color.FromArgb((int)(best[0] / best[3]), (int)(best[1] / best[3]), (int)(best[2] / best[3]));
        }

        static double Luma(Color c) { return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B; }

        // 상표를 지우지 않은 그대로의 사진 (네이버 스토어 1개·2개 대표이미지: 글자판 없이, 2개는 아래 1+1만)
        public static Layout Plain(Bitmap src)
        {
            var photo = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(photo)) { g.Clear(Color.White); g.DrawImage(src, 0, 0, src.Width, src.Height); }
            var lay = new Layout { Photo = photo, WhiteBg = IsWhiteBackground(photo) };
            lay.Content = lay.WhiteBg ? ContentBox(photo) : new Rectangle(0, 0, photo.Width, photo.Height);
            return lay;
        }

        // ── 2. 1000×1000 대표이미지: 제품 사진 + 상품명 위 키워드 글자판 + 아래 n+1
        static readonly Regex Promo = new Regex("구매고객|사은품|증정|이벤트|특가|할인|무료배송|선착순|한정수량");

        public static Bitmap Render(Layout lay, string keyword, int count, int nudgeY = 0, float plateScale = 1f)
        {
            LoadFonts();
            var bmp = new Bitmap(Size, Size, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                // 흰 배경: 여백을 잘라 위쪽 영역(가로 920, 세로 780)에 크게. 아니면 사진 그대로 꽉 차게
                var src = lay.Content;
                RectangleF dst;
                if (lay.WhiteBg || lay.Cropped)
                {
                    float s0 = Math.Min(920f / src.Width, 780f / src.Height);
                    float dw = src.Width * s0, dh = src.Height * s0;
                    dst = new RectangleF((Size - dw) / 2, 40 + (780 - dh) / 2, dw, dh);
                }
                else
                {
                    float s0 = Math.Max(Size / (float)src.Width, Size / (float)src.Height);
                    float dw = src.Width * s0, dh = src.Height * s0;
                    dst = new RectangleF((Size - dw) / 2, (Size - dh) / 2, dw, dh);
                }
                g.DrawImage(lay.Photo, dst, src, GraphicsUnit.Pixel);
                float s = dst.Width / src.Width;

                if (!string.IsNullOrWhiteSpace(keyword))
                    foreach (var p in lay.Plates)
                    {
                        var r = p.Rect;
                        var plate = new RectangleF(dst.X + (r.X - src.X) * s, dst.Y + (r.Y - src.Y) * s + nudgeY, r.Width * s, r.Height * s);
                        float cx = plate.X + plate.Width / 2, cy = plate.Y + plate.Height / 2;
                        plate = new RectangleF(cx - plate.Width * plateScale / 2, cy - plate.Height * plateScale / 2, plate.Width * plateScale, plate.Height * plateScale);

                        // 라벨 지운 자리의 실제 사진 배경 평균 색상 측정 (배경이 흰색이면 진한 검정 계열, 어두우면 흰색으로 글자색 자동 결정)
                        Color actualBg = Color.White;
                        try
                        {
                            long sr = 0, sg = 0, sb = 0, scnt = 0;
                            int stepY = Math.Max(1, r.Height / 10), stepX = Math.Max(1, r.Width / 10);
                            for (int py = r.Top; py < r.Bottom; py += stepY)
                                for (int px = r.Left; px < r.Right; px += stepX)
                                {
                                    if (px >= 0 && px < lay.Photo.Width && py >= 0 && py < lay.Photo.Height)
                                    {
                                        var c = lay.Photo.GetPixel(px, py);
                                        sr += c.R; sg += c.G; sb += c.B; scnt++;
                                    }
                                }
                            if (scnt > 0) actualBg = Color.FromArgb((int)(sr / scnt), (int)(sg / scnt), (int)(sb / scnt));
                        }
                        catch { }

                        // 패키지 악센트/브랜드 포인트 컬러(오렌지, 딥레드 등)
                        Color accentCol = p.Color;
                        if (accentCol.IsEmpty || Luma(accentCol) > 230 || Luma(accentCol) < 15)
                            accentCol = LabelColor(lay.Photo, r, r);

                        if (p.Turn == 0) DrawPlate(g, plate, actualBg, accentCol, keyword.Trim());
                        else
                        {
                            // 세로 글자판: 스틱 글씨 방향에 맞춰 돌려서 그림
                            var state = g.Save();
                            g.TranslateTransform(cx, cy);
                            g.RotateTransform(p.Turn == -1 ? 90 : -90);   // 반시계로 돌려 읽힌 글씨 = 위→아래로 쓴 글씨
                            DrawPlate(g, new RectangleF(-plate.Height / 2, -plate.Width / 2, plate.Height, plate.Width), actualBg, accentCol, keyword.Trim());
                            g.Restore(state);
                        }
                    }

                // n+1 (2통 = 1+1 … 6통 = 5+1). 흰 글씨 + 검은 테두리 + 그림자 (기존 스토어 대표이미지와 같은 모양)
                if (count >= 2)
                    DrawBadge(g, (count - 1) + "+1", new PointF(Size / 2f, 900));
            }
            return bmp;
        }

        // 세련된 패키지 일체형 타이포그래피: 상품 포인트 컬러 및 라벨 배경과 조화를 이루는 프리미엄 텍스트 렌더링
        static void DrawPlate(Graphics g, RectangleF plate, Color baseColor, Color accentColor, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // 실제 패키지/라벨 표면의 밝기 (Luma > 130 이면 밝은 배경: 화이트 박스, 투명 용기 등)
            bool light = Luma(baseColor) > 130;
            
            // 상품 고유의 포인트 컬러(오렌지, 딥레드, 네이비 등)를 살려서 상품과 완벽히 어울리는 브랜드 스타일 글자색 연출
            Color textColor;
            Color outlineColor;
            Color shadowColor;

            if (light)
            {
                // 밝은 배경: 패키지 고유 악센트 컬러가 충분히 짙으면 그 색상 계열(예: 진한 주황/브라운/네이비), 너무 밝으면 딥 톤으로 차분하게 매치
                int r = accentColor.R, gr = accentColor.G, b = accentColor.B;
                double accLuma = Luma(accentColor);
                if (accLuma > 140)
                {
                    // 너무 밝은 악센트 컬러는 가독성을 위해 톤다운(중후하고 깊은 프리미엄 딥 톤)
                    r = (int)(r * 0.55);
                    gr = (int)(gr * 0.55);
                    b = (int)(b * 0.55);
                }
                else if (accLuma < 30)
                {
                    // 너무 검정에 가까우면 살짝 온기 있는 다크 톤
                    r = 35; gr = 30; b = 30;
                }
                textColor = Color.FromArgb(r, gr, b);
                outlineColor = Color.FromArgb(235, 255, 255, 255); // 은은한 화이트 클린 외곽선으로 선명도 극대화
                shadowColor = Color.FromArgb(45, 0, 0, 0);
            }
            else
            {
                // 어두운 배경: 밝고 깨끗한 화이트 텍스트 또는 은은한 크림 화이트
                textColor = Color.White;
                outlineColor = Color.FromArgb(160, Math.Max(0, accentColor.R - 40), Math.Max(0, accentColor.G - 40), Math.Max(0, accentColor.B - 40));
                shadowColor = Color.FromArgb(130, 0, 0, 0);
            }

            var two = SplitTwo(text);
            var lines = two != null && plate.Height > plate.Width * 0.5 ? two : new[] { text };
            float fontSize = FitFont(g, keywordFont, lines, plate.Width * 0.95f, plate.Height * 0.85f);
            using (var f = new Font(keywordFont, fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                float lineH = f.GetHeight(g) * 0.98f, total = lineH * lines.Length;
                float y = plate.Y + (plate.Height - total) / 2;
                foreach (var line in lines)
                {
                    var size = g.MeasureString(line, f, int.MaxValue, StringFormat.GenericTypographic);
                    float x = plate.X + (plate.Width - size.Width) / 2;
                    using (var p = new GraphicsPath())
                    {
                        p.AddString(line, keywordFont, (int)FontStyle.Bold, fontSize, new PointF(x, y + lineH * 0.05f), StringFormat.GenericTypographic);
                        
                        // 1. 깊이감 있는 섀도우
                        using (var shadow = (GraphicsPath)p.Clone())
                        {
                            var m = new Matrix();
                            m.Translate(0, Math.Max(2f, fontSize * 0.045f));
                            shadow.Transform(m);
                            using (var sb = new SolidBrush(shadowColor)) g.FillPath(sb, shadow);
                        }

                        // 2. 외곽선(하이라이트/선명도 스트로크): 어떠한 패키지 재질 위에서도 글자가 배경에 묻히지 않도록 선명한 가독성 보장
                        float strokeW = Math.Max(2f, fontSize * 0.045f);
                        using (var pen = new Pen(outlineColor, strokeW) { LineJoin = LineJoin.Round })
                        {
                            g.DrawPath(pen, p);
                        }

                        // 3. 본문 텍스트 채우기 (밝은 라벨 위 짙은 딥 차콜 블랙으로 극상의 선명함)
                        using (var textBrush = new SolidBrush(textColor))
                        {
                            g.FillPath(textBrush, p);
                        }
                    }
                    y += lineH;
                }
            }
        }

        static void DrawBadge(Graphics g, string text, PointF center)
        {
            float fontSize = 150;
            using (var p = new GraphicsPath())
            {
                var size = MeasureText(g, badgeFont, text, fontSize);
                p.AddString(text, badgeFont, 0, fontSize, new PointF(center.X - size.Width / 2, center.Y - size.Height / 2), StringFormat.GenericTypographic);
                using (var shadow = (GraphicsPath)p.Clone())
                {
                    var m = new Matrix(); m.Translate(5, 6); shadow.Transform(m);
                    using (var sp = new Pen(Color.FromArgb(110, 0, 0, 0), 12) { LineJoin = LineJoin.Round }) g.DrawPath(sp, shadow);
                }
                using (var outline = new Pen(Color.FromArgb(25, 25, 25), 9) { LineJoin = LineJoin.Round }) g.DrawPath(outline, p);
                g.FillPath(Brushes.White, p);
            }
        }

        static SizeF MeasureText(Graphics g, FontFamily ff, string text, float size)
        {
            using (var f = new Font(ff, size, GraphicsUnit.Pixel)) return g.MeasureString(text, f, int.MaxValue, StringFormat.GenericTypographic);
        }

        static float FitFont(Graphics g, FontFamily ff, string[] lines, float maxW, float maxH)
        {
            float size = maxH / lines.Length;
            for (; size > 10; size -= 2)
            {
                float w = lines.Max(l => MeasureText(g, ff, l, size).Width);
                float h;
                using (var f = new Font(ff, size, GraphicsUnit.Pixel)) h = f.GetHeight(g) * 0.98f * lines.Length;
                if (w <= maxW && h <= maxH) break;
            }
            return size;
        }

        // 영양제·식품 상품명에 자주 쓰는 단어. "젖산마그네슘" → "젖산 / 마그네슘"
        static readonly string[] Words = { "마그네슘", "비타민", "유산균", "프로바이오틱스", "영양제", "멜라토닌", "오메가3", "오메가", "콜라겐", "루테인", "아연", "칼슘",
            "철분", "엽산", "효소", "단백질", "프로틴", "글루타치온", "밀크씨슬", "코큐텐", "콘드로이친", "글루코사민", "보스웰리아", "크릴오일", "홍삼", "흑마늘",
            "녹차", "카테킨", "식초", "올리브오일", "레몬즙", "율무차", "차", "환", "분말", "가루", "스틱", "젤리", "캡슐", "추출물", "다이어트", "보충제" };

        static string[] SplitTwo(string t)
        {
            t = t.Trim();
            if (t.Replace(" ", "").Length <= 4) return null;                   // 율무차·루테인 같은 짧은 말은 한 줄
            int sp = t.IndexOf(' ');
            if (sp > 0) return new[] { t.Substring(0, sp), t.Substring(sp + 1).Trim() };
            foreach (var w in Words.OrderByDescending(x => x.Length))
            {
                int i = t.IndexOf(w, 1, StringComparison.Ordinal);
                if (i >= 2 && t.Length - i >= 2) return new[] { t.Substring(0, i), t.Substring(i) };            // 앞말 + 단어 (식물성 / 멜라토닌)
                if (t.StartsWith(w) && t.Length - w.Length >= 2) return new[] { w, t.Substring(w.Length) }; // 단어 + 뒷말 (마그네슘 / 영양제)
            }
            return t.Length >= 8 ? new[] { t.Substring(0, (t.Length + 1) / 2), t.Substring((t.Length + 1) / 2) } : null;
        }

        static RectangleF Inset(RectangleF r, float d) { return new RectangleF(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d); }

        static Color Shade(Color c, int d) { return Color.FromArgb(Math.Max(0, Math.Min(255, c.R + d)), Math.Max(0, Math.Min(255, c.G + d)), Math.Max(0, Math.Min(255, c.B + d))); }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Max(1, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
