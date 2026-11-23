using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ExtraImages
{
    public class Candidate
    {
        public string Label;     // 화면에 보일 이름
        public int Top, Bottom;  // 상세 이미지에서 잘라낼 세로 범위
        public bool IsTable;     // 영양정보 / 제품정보 표
        public int Left, Right;  // 표: 글씨 있는 폭만 (0,0 = 전체 폭)
        public bool Package;     // 상품 상자·통 사진이 들어간 패널
        public double Score;
        public override string ToString() { return Label + " (" + Top + "~" + Bottom + ")"; }
    }

    // 상세 이미지(세로로 긴 한 장)를 추가이미지 후보 패널로 나눔.
    // 스마트스토어(팝스프라이스) 기존 추가이미지를 보고 맞춘 규칙:
    //  - 상세페이지의 한 덩어리(카드·패널)를 구간 사이 여백에 맞춰 통째로 자름 (높이는 폭의 0.5~1.45배)
    //  - 1000×1000 흰 바탕 가운데에 비율 그대로 넣음
    //  - 특징 요약(아이콘 정리), 추천 대상, 핵심 함량·원료, 인증, 제조 공법 패널을 고름. 맨 위 로고·안내문은 쓰지 않음
    //  - 마지막 장은 영양정보(없으면 제품정보/성분 표)
    public static class Sections
    {
        public const int Size = 1000;
        const double MinAspect = 0.42, MaxAspect = 1.45;   // 짧은 단락(추천 대상 목록 등)도 다음 단락을 붙이지 않고 그대로

        static readonly Regex Good = new Regex(@"함유|함량|%|mg|㎎|mcg|원료|추천|이런\s*분|공법|과정|특징|포인트|POINT|CHECK|비교|기능|무첨가|식물성|국내산|유기농|100|저당|무설탕|간편|스틱|1일|하루", RegexOptions.IgnoreCase);
        static readonly Regex Info = new Regex(@"^(제품명|식품의\s*유형|원료명|원재료명|영양정보|영양·기능|영양\s*기능|제품정보|정보고시|Q\s*&\s*A|주의사항|섭취\s*시\s*주의|보관\s*(시|방법)|교환|반품|배송|소비자\s*상담|부정\s*·?\s*불량)");
        static readonly Regex Bad = new Regex(@"지적재산권|무단|복제|사용\s*시\s*처벌|AI로\s*생성|가상인물|본사에서\s*직접|공식판매");
        // 내 스토어 기존 추가이미지(직접 만든 것)에 없는 것: 판매·제조사 안내, Q&A, 주의·보관 안내, 인증서·증명서 사진, 영문 성분표
        static readonly Regex NotInStore = new Regex(@"^\s*부\s*원료\s*([:：]|$)|HACCP|해썹|GMP|우수\s*건강|적용\s*업소|식품의약품안전[처저]|^\s*건강\s*기능\s*식품\s*$|^\s*기능\s*\S?\s*품\s*$|식품\s*안전\s*관리|안전\s*관리\s*인증|판매원|제조원|수입원|유통전문|소비자\s*상담|고객\s*센터|고객\s*상담|교환|반품|주소|전화|Q\s*&\s*A|Q\s*\.|자주\s*묻는|궁금|보관하십시오|보관\s*방법|주의\s*사항|섭취\s*시\s*주의|인증서|증명서|지정서|성적서|보험|Nutrition\s*Facts|Serving|Supplement\s*Facts|\(주\)|㈜|주식회사", RegexOptions.IgnoreCase);
        // 부원료 소개 패널은 보통 쓰지 않음 (패널이 모자랄 때만 씀)
        static readonly Regex SubIngredient = new Regex(@"부\s*원료");
        static readonly Regex Nutrition = new Regex(@"^(영양\s*정보|영양\s*[·•ㆍ∙./]?\s*기능\s*정보|영양성분\s*(정보|표)?$)");
        // 표 칸의 숫자만 있는 줄 (12g, 30%, 0mg …) / 영양성분 기준치 안내
        static readonly Regex NumCell = new Regex(@"^[\d.,\s]+(g|mg|㎎|%|kcal|mcg|㎍|μg|ug|9)?\s*%?\.?$", RegexOptions.IgnoreCase);
        static readonly Regex DailyValue = new Regex(@"영양성분\s*기준|기준치에\s*대한");
        // 제품정보 표 줄 / 그 아래 회사 안내 줄 (여기서 표를 끊음)
        static readonly Regex InfoRow = new Regex(@"^(제품명|식품의\s*유형|내용량|원료명|원재료명)");
        static readonly Regex CompanyRow = new Regex(@"수출국|제조업체|수입업체|수입원|판매원|제조원|소비자|유통전문|소재지|고객|반품|교환");
        static string Head(string s) { return Regex.Replace(s ?? "", @"^[\s|Il1•·\-]+", ""); }   // 줄 앞 표 칸 선(|) 등
        static readonly Regex InfoTable = new Regex(@"^(제품명|제품\s*(상세\s*)?정보|정보고시|식품의\s*유형)");
        // 표 끝 줄: "※ 1일 영양성분 기준치에 대한 비율(%)은 2,000kcal 기준이므로…" (OCR이 기준치를 기준시로 읽어도)
        static readonly Regex TableEnd = new Regex(@"^(Q\s*&\s*A|교환|반품|배송|※|Nutrition\s*Facts|Supplement\s*Facts)|기준이므로|비율\s*임|\(%\)\s*은|2[,.]\s*000\s*k");
        static readonly Regex SectionMark = new Regex(@"^(POINT|Point|CHECK|Check|CHEOK|Chenk)\s*\.?\s*\d*$|^(POINT|CHECK)", RegexOptions.IgnoreCase);

        // 색이 고른 띠(구간 사이 여백)를 기준으로 내용 덩어리로 나눔. 덩어리 = [시작, 끝, 평균 밝기]
        public static List<int[]> Blocks(Bitmap img)
        {
            int w = img.Width, h = img.Height;
            var flat = new bool[h];
            var bright = new double[h];
            var data = img.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[data.Stride];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                    double sum = 0, sq = 0; int n = 0;
                    for (int x = 0; x < w; x += 4) { int v = row[x * 3] + row[x * 3 + 1] + row[x * 3 + 2]; sum += v; sq += (double)v * v; n++; }
                    double mean = sum / n;
                    bright[y] = mean / 3;
                    flat[y] = Math.Sqrt(Math.Max(0, sq / n - mean * mean)) < 12;
                }
            }
            finally { img.UnlockBits(data); }

            // 덩어리 = [시작, 끝, 평균 밝기, 앞 여백에서 배경색이 바뀌었으면 1 (흰 바탕 → 파란 띠 같은 다른 단락)]
            var blocks = new List<int[]>();
            int start = 0, y2 = 0, change = 0;
            while (y2 < h)
            {
                if (!flat[y2]) { y2++; continue; }
                int s = y2;
                double lo = 999, hi = -1;
                while (y2 < h && flat[y2]) { lo = Math.Min(lo, bright[y2]); hi = Math.Max(hi, bright[y2]); y2++; }
                if (y2 - s >= 12)
                {
                    if (s - start >= 30) { blocks.Add(new[] { start, s, Avg(bright, start, s), change }); change = 0; }
                    start = y2;
                    if (hi - lo > 25) change = 1;
                }
            }
            if (h - start >= 30) blocks.Add(new[] { start, h, Avg(bright, start, h), change });
            return blocks;
        }

        static int Avg(double[] v, int a, int b)
        {
            double sum = 0;
            for (int i = a; i < b; i++) sum += v[i];
            return (int)(sum / Math.Max(1, b - a));
        }

        static string Letters(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z가-힣]", "").ToLowerInvariant(); }

        // 브랜드가 나오는 줄 (상자 사진·로고·"나를 위한 매일습관, 뉴일리" 같은 문구). 글자 인식이 조금 틀려도
        static bool HasBrand(string text, List<string> brands)
        {
            string t = Letters(text);
            if (t.Length < 2 || brands == null) return false;
            foreach (var b in brands)
            {
                if (b.Length < 2) continue;
                if (t.Contains(b)) return true;
                bool hangul = b.Any(c => c >= '가' && c <= '힣');
                if (hangul && b.Length >= 3)
                    for (int i = 0; i + b.Length <= t.Length; i++)
                    {
                        int hit = 0; for (int k = 0; k < b.Length; k++) if (t[i + k] == b[k]) hit++;
                        // 한 글자 틀림 (뉴일리 → 뉴알리). 첫 글자는 맞아야 ("관절 및 연골"의 '및연골'을 소연골로 보지 않게)
                        if (hit >= b.Length - 1 && t[i] == b[0]) return true;
                    }
            }
            return false;
        }

        // 상품 상자·통 사진이 들어간 줄: 상품 사진 라벨 글씨와 같은 글씨 (주 키워드 하나뿐이거나 흔한 표기는 빼고)
        static readonly Regex CommonLabel = new Regex("건강기능식품|dietarysupplement|haccp|식품의약품안전처|유기가공식품|농림축산식품부|안전관리인증|gmp|madein|수입완제품|캡슐|정당|포|kcal|영양성분|기준치");
        static bool IsPackageLine(string text, List<string> package, string seed)
        {
            if (package == null || package.Count == 0) return false;
            string t = Letters(text), sd = Letters(seed);
            if (t.Length < 4) return false;
            foreach (var p in package)
            {
                string common = p.Length <= t.Length ? (t.Contains(p) ? p : null) : (p.Contains(t) ? t : null);
                if (common == null || common.Length < 4) continue;
                string rest = sd.Length > 0 ? common.Replace(sd, "") : common;
                if (rest.Length < 3 || CommonLabel.IsMatch(common)) continue;
                return true;
            }
            return false;
        }

        public static List<Candidate> Find(Bitmap img, List<OcrLine> lines, List<string> brands = null, List<string> package = null, string seed = null)
        {
            int w = img.Width, h = img.Height;
            var blocks = Blocks(img);

            // 정보 영역(제품명·영양정보·Q&A …)이 처음 나오는 곳. 특징 패널은 그 위에서만 고름
            // 상세 중간의 "보관방법" 같은 말이 아니라, 맨 끝 정보표(제품명·식품의 유형)가 시작되는 곳
            var info = lines.LastOrDefault(l => InfoTable.IsMatch(l.Text) && l.Y > h * 0.4)
                       ?? lines.LastOrDefault(l => Nutrition.IsMatch(l.Text) && l.Y > h * 0.4);
            int infoTop = info != null ? info.Y - 20 : h;

            Func<List<int[]>, bool, List<Candidate>> panels = (bl, strict) =>
            {
                // 이어진 덩어리들을 묶어 높이가 폭의 0.5~1.45배인 패널 후보를 모두 만듦 (여백에서 시작해 여백에서 끝남)
                var all = new List<Candidate>();
                for (int i = 0; i < bl.Count; i++)
                {
                    for (int j = i; j < bl.Count; j++)
                    {
                        // 배경 밝기가 크게 바뀌는 곳(흰 바탕 ↔ 어두운 배너)은 다른 단락이므로 넘어가지 않음
                        int b0 = bl[j - (j > i ? 1 : 0)][2], b1 = bl[j][2];
                        if (j > i && (Math.Abs(b1 - b0) > 100 || bl[j][3] == 1 || Math.Min(b0, b1) < 110 && Math.Max(b0, b1) > 160)) break;   // 어두운 배너 ↔ 밝은 바탕
                        int top = Math.Max(0, bl[i][0] - 8), bottom = Math.Min(h, bl[j][1] + 8);
                        if (bottom > infoTop) break;
                        double aspect = (bottom - top) / (double)w;
                        if (aspect > MaxAspect) break;
                        if (aspect < MinAspect) continue;
                        var c = new Candidate { Top = top, Bottom = bottom };
                        c.Score = ScorePanel(c, lines, w, h);
                        // 브랜드·판매사 안내·Q&A·인증서가 들어간 패널은 쓰지 않음 (내 스토어 추가이미지에는 없음)
                        var inside = lines.Where(l => l.Y >= top && l.Y + l.H <= bottom).ToList();
                        if (inside.Any(l => HasBrand(l.Text, brands) || NotInStore.IsMatch(l.Text) || (strict && SubIngredient.IsMatch(l.Text)))) c.Score = -100;
                        // 다음 단락의 시작(큰 제목·POINT/CHECK 표시)이 패널 아래쪽에 걸리면 두 장면이 섞이므로 쓰지 않음
                        else if (NextSectionInside(inside, top, bottom, w)) c.Score = -100;
                        // 앞 단락의 꼬리(※·* 각주, 출처)로 시작하는 패널도 두 장면이 섞임
                        else if (inside.Any(l => l.Y - top < (bottom - top) * 0.15 && Regex.IsMatch(l.Text.TrimStart(), @"^[※*＊]|출처"))) c.Score = -100;
                        // 영양정보·제품정보 표는 마지막 장에서 따로 (패널로 또 넣지 않음)
                        else if (inside.Any(l => Nutrition.IsMatch(Head(l.Text)) || InfoTable.IsMatch(Head(l.Text)) || InfoRow.IsMatch(Head(l.Text)) || DailyValue.IsMatch(l.Text))
                                 || inside.Count(l => NumCell.IsMatch(l.Text.Trim())) >= 8) c.Score = -100;
                        // 상품 상자·통 사진이 있는 패널은 뒤로 (상자에 브랜드가 찍혀 있음). 다른 패널이 모자랄 때만
                        else if (inside.Any(l => IsPackageLine(l.Text, package, seed))) { c.Package = true; c.Score -= 30; }
                        if (c.Score > -50) all.Add(c);
                    }
                }

                // 점수 높은 순으로 겹치지 않게 고름. 글자 내용이 거의 같은 패널(같은 표·같은 안내 반복)은 하나만
                var pk = new List<Candidate>();
                Func<Candidate, HashSet<string>> words = c => new HashSet<string>(lines.Where(l => l.Y >= c.Top && l.Y + l.H <= c.Bottom).Select(l => Letters(l.Text)).Where(t => t.Length >= 2));
                Func<HashSet<string>, HashSet<string>, double> same = (a, b) => a.Count == 0 || b.Count == 0 ? 0 : a.Intersect(b).Count() / (double)Math.Min(a.Count, b.Count);
                foreach (var c in all.OrderByDescending(x => x.Score))
                {
                    if (pk.Any(p => Overlap(p, c) > 0.15)) continue;
                    if (pk.Any(p => same(words(p), words(c)) > 0.6)) continue;
                    pk.Add(c);
                    if (pk.Count >= 8) break;
                }
                pk = pk.OrderBy(c => c.Top).ToList();
                for (int i = 0; i < pk.Count; i++) pk[i].Label = "패널 " + (i + 1);
                return pk;
            };
            var picked = panels(blocks, true);
            // 쓸 만한 패널이 3장이 안 되면: 여백 없이 길게 이어진 구간을 글자 사이 빈 곳에서 더 잘라 다시 찾음
            // (멜라토닌처럼 배경색이 이어져 단락 사이 여백이 없는 상세페이지). 이때는 "시너지 부원료" 같은 특징 목록 패널도 씀
            Func<List<Candidate>, int> goodCount = ps => ps.Count(c => !c.Package && c.Score >= 6);
            if (goodCount(picked) < 3)
            {
                var more = panels(SplitTall(img, blocks, lines), false);
                if (goodCount(more) > goodCount(picked)) picked = more;
            }

            var table = TableCandidate(lines, w, h, blocks);
            // 표가 너무 작거나(한두 줄만 잘림) 비어 있으면 쓰지 않음
            if (table != null)
            {
                if (!FitTable(table, lines, w, brands, package, seed))
                {                    // 영양정보는 꼭 한 장 있어야 함: 자르기가 안 되면 폭 전체로 (브랜드가 표 안에 있을 때만 뺌)
                    var tl = lines.Where(l => l.Y >= table.Top && l.Y + l.H <= table.Bottom).ToList();
                    if (table.Label == "영양정보" && tl.Count >= 5 && !tl.Any(l => TableBrand(l.Text, brands))) { table.Left = 0; table.Right = 0; }
                    else if (table.Label == "영양정보" && NutritionColumn(table, tl, w, brands)) { }
                    else table = null;
                }
            }
            if (table != null) picked.Add(table);

            // 제품정보 표 (내용량·원료명 …, 아래 회사 안내 줄은 빼고) — 내 스토어도 영양정보와 함께 넣은 경우가 있음
            var infoStart = lines.FirstOrDefault(l => InfoRow.IsMatch(Head(l.Text)) && l.Y > h * 0.3 && (table == null || l.Y + l.H < table.Top || l.Y > table.Bottom));
            if (infoStart != null)
            {
                var stop = lines.FirstOrDefault(l => l.Y > infoStart.Y && (CompanyRow.IsMatch(l.Text) || Bad.IsMatch(l.Text) || (table != null && l.Y >= table.Top && infoStart.Y < table.Top)));
                int top = Math.Max(0, infoStart.Y - 25), bottom = stop != null ? stop.Y - 6 : Math.Min(h, infoStart.Y + (int)(w * 1.4));
                if (bottom - top > w * 1.6) bottom = top + (int)(w * 1.6);
                var info2 = new Candidate { Label = "제품정보 표", Top = top, Bottom = bottom, IsTable = true, Score = 90 };
                if (bottom - top >= w * 0.2 && FitTable(info2, lines, w, brands, package, seed) && (table == null || Overlap(table, info2) < 0.2)) picked.Add(info2);
            }
            return picked;
        }

        // 폭의 1.45배보다 긴 덩어리를 글자 줄 사이 빈 곳(120px 이상, 목록 줄 사이는 자르지 않음)에서 나눔. 빈 곳 안에서 가장 고른(무늬 없는) 줄에서 자름
        public static List<int[]> SplitTall(Bitmap img, List<int[]> blocks, List<OcrLine> lines)
        {
            int w = img.Width;
            var res = new List<int[]>();
            foreach (var b in blocks)
            {
                if (b[1] - b[0] <= w * MaxAspect) { res.Add(b); continue; }
                var inside = lines.Where(l => l.Y >= b[0] && l.Y + l.H <= b[1]).OrderBy(l => l.Y).ToList();
                var cuts = new List<int>();
                int end = b[0];
                foreach (var l in inside)
                {
                    if (l.Y - end >= 120 && end > b[0]) cuts.Add(QuietRow(img, end + 10, l.Y - 10));
                    end = Math.Max(end, l.Y + l.H);
                }
                int start = b[0], first = 1;
                foreach (int c in cuts.Concat(new[] { b[1] }))
                {
                    if (c - start >= 30) { res.Add(new[] { start, c, b[2], first == 1 ? b[3] : 0 }); first = 0; }
                    start = c;
                }
            }
            return res;
        }

        static int QuietRow(Bitmap img, int a, int b)
        {
            if (b <= a) return (a + b) / 2;
            int w = img.Width, best = (a + b) / 2; double bestStd = double.MaxValue;
            var data = img.LockBits(new Rectangle(0, a, w, b - a), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[data.Stride];
                for (int y = 0; y < b - a; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                    double sum = 0, sq = 0; int n = 0;
                    for (int x = 0; x < w; x += 4) { int v = row[x * 3] + row[x * 3 + 1] + row[x * 3 + 2]; sum += v; sq += (double)v * v; n++; }
                    double mean = sum / n, sd = Math.Sqrt(Math.Max(0, sq / n - mean * mean));
                    // 고른 줄이 여럿이면 빈 곳 가운데에 가까운 쪽
                    sd += Math.Abs(y - (b - a) / 2.0) * 0.01;
                    if (sd < bestStd) { bestStd = sd; best = a + y; }
                }
            }
            finally { img.UnlockBits(data); }
            return best;
        }

        // 표: 글씨 있는 폭만 잘라 크게. 옆에 붙은 상품 사진·브랜드 글씨는 빼고, 뺄 수 없으면(표 안에 브랜드) 쓰지 않음
        // 표 안의 브랜드 줄: 원재료·함량 줄에 원료 이름으로 나온 것(뮤코다당단백(소연골/인도산))은 브랜드로 안 봄
        static readonly Regex IngredientLine = new Regex(@"원재료|원료명|함량|[(（][^()（）]{0,12}[가-힣]{1,5}산\s*[)）]");
        static bool TableBrand(string text, List<string> brands) { return HasBrand(text, brands) && !IngredientLine.IsMatch(text ?? ""); }

        // 제품정보 글과 영양정보 상자가 나란히 있는 표 (왼쪽 제품명·원재료 / 오른쪽 영양·기능정보): 영양정보 상자 쪽 세로 띠만 자름
        static readonly Regex NutritionRow = new Regex(@"영양|기능정보|섭취량|열량|kcal|탄수화물|단백질|지방|나트|당류|콜레스테롤|기준치|%|mg|㎎|μg|㎍", RegexOptions.IgnoreCase);
        static bool NutritionColumn(Candidate t, List<OcrLine> tl, int w, List<string> brands)
        {
            var rows = tl.Where(l => NutritionRow.IsMatch(l.Text) && Letters(l.Text).Length <= 18).ToList();
            if (rows.Count < 4) return false;
            var head = rows.FirstOrDefault(l => Regex.IsMatch(Letters(l.Text), "영양.{0,3}정보|영양성분"));
            // 영양정보 제목이 있는 쪽 절반 (왼쪽 / 오른쪽)
            bool right = head != null ? head.X + head.W / 2 > w / 2 : rows.Count(l => l.X > w / 2) > rows.Count / 2;
            var col = rows.Where(l => right ? l.X >= w * 0.4 : l.X + l.W <= w * 0.6).ToList();
            if (col.Count < 4) return false;
            int L = Math.Max(0, col.Min(l => l.X) - 30), R = Math.Min(w, col.Max(l => l.X + l.W) + 30);
            if (R - L < w * 0.3) return false;
            if (tl.Any(l => l.X >= L - 5 && l.X + l.W <= R + 5 && TableBrand(l.Text, brands))) return false;
            t.Left = L; t.Right = R;
            return true;
        }

        static bool FitTable(Candidate t, List<OcrLine> lines, int w, List<string> brands, List<string> package, string seed)
        {
            var tl = lines.Where(l => l.Y >= t.Top && l.Y + l.H <= t.Bottom).ToList();
            var bad = tl.Where(l => TableBrand(l.Text, brands) || IsPackageLine(l.Text, package, seed)).ToList();
            var ok = tl.Except(bad).ToList();
            if (ok.Count < 5 || t.Bottom - t.Top < w * 0.2) return false;
            t.Left = Math.Max(0, ok.Min(l => l.X) - 30);
            t.Right = Math.Min(w, ok.Max(l => l.X + l.W) + 30);
            // 브랜드·상자 글씨가 표 오른쪽(또는 왼쪽)에 따로 있으면 그쪽을 잘라냄
            foreach (var b in bad)
            {
                if (b.X >= t.Right - 40) continue;                        // 이미 밖
                if (b.X + b.W <= t.Left + 40) continue;
                int cut = b.X - 10;
                if (cut > t.Left + w * 0.45 && ok.All(l => l.X + l.W <= cut + 15)) t.Right = cut;
                else return false;                                        // 표 한가운데에 브랜드 (제품명 칸 등)
            }
            if (t.Right - t.Left < w * 0.45) return false;
            return true;
        }

        // 패널 위쪽 40% 아래에서 새 단락이 시작되는지: POINT/CHECK 표시, 또는 위에 본문이 있은 뒤 나오는 큰 제목 글씨
        static bool NextSectionInside(List<OcrLine> inside, int top, int bottom, int w)
        {
            if (inside.Count == 0) return false;
            int ph = bottom - top;
            var hs = inside.Select(l => l.H).OrderBy(x => x).ToList();
            // 큰 제목: 이 패널 본문 글씨보다 1.3배 이상 (폭 860 기준 38px 이상)
            double big = Math.Max(w * 0.044, hs[hs.Count / 2] * 1.3);
            foreach (var l in inside)
            {
                if (l.Y - top <= ph * 0.4) continue;
                if (SectionMark.IsMatch(l.Text.Trim()) || Regex.IsMatch(Letters(l.Text), @"^(point|check|cheok|chenk)\d{0,2}$")) return true;
                // 위에 본문 글(두 줄 이상)이 먼저 있고 그 뒤에 나오는 큰 제목 → 다음 단락 (그림 아래 제목은 같은 단락)
                if (l.H >= big && Letters(l.Text).Length >= 4 && !Regex.IsMatch(l.Text, @"\d")
                    && inside.Count(u => u.Y + u.H < l.Y - 30 && u.H < big && Letters(u.Text).Length >= 4) >= 2) return true;
            }
            return false;
        }

        // 패널 바로 위에 이 단락의 제목이 있는데 패널이 그 아래에서 시작하면 (제목이 잘림)
        static bool TitleAbove(List<OcrLine> lines, Candidate c)
        {
            var above = lines.Where(l => l.Y + l.H <= c.Top && l.Y + l.H > c.Top - 200).OrderByDescending(l => l.Y).FirstOrDefault();
            if (above == null || above.H < 30) return false;
            var first = lines.Where(l => l.Y >= c.Top && l.Y + l.H <= c.Bottom).OrderBy(l => l.Y).FirstOrDefault();
            return first != null && first.H < above.H * 0.8;
        }

        static double Overlap(Candidate a, Candidate b)
        {
            int inter = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            return inter <= 0 ? 0 : inter / (double)Math.Min(a.Bottom - a.Top, b.Bottom - b.Top);
        }

        public static double ScorePanel(Candidate c, List<OcrLine> lines, int w, int h)
        {
            var inside = lines.Where(l => l.Y >= c.Top && l.Y + l.H <= c.Bottom).ToList();
            if (inside.Any(l => Bad.IsMatch(l.Text))) return -100;
            if (c.Top < h * 0.03) return -100;                              // 맨 위 로고·안내 띠
            double s = 0;
            s += Math.Min(10, inside.Count(l => Good.IsMatch(l.Text)) * 1.5);  // 특징·함량·인증 같은 말
            int n = inside.Count;
            if (n >= 3 && n <= 25) s += 4; else if (n > 35) s -= 4;            // 설명이 너무 빽빽하면 감점
            if (n == 0) s -= 6;                                                // 글자가 없는 사진만 있는 구간
            if (inside.Any(l => l.H >= 30)) s += 3;                            // 큰 제목이 있는 패널
            // 기존 추가이미지는 짧은 문구가 여러 줄인 정리 패널(아이콘 목록, 함량 문구)이 많음
            s += Math.Min(8, inside.Count(l => l.Text.Length >= 2 && l.Text.Length <= 30) * 0.6);
            // 글자가 차지하는 높이가 너무 적으면 사진 위주 패널
            double textCover = inside.Sum(l => (double)l.H) / Math.Max(1, c.Bottom - c.Top);
            if (textCover < 0.08) s -= 5;
            // 아이콘 2열 정리표: 왼쪽·오른쪽에 짧은 문구가 같은 높이로 나란히 있음 (기존 추가이미지에 많음)
            int pairs = inside.Count(l => l.X + l.W < w * 0.55 && l.Text.Length <= 25 &&
                                          inside.Any(r => r.X > w * 0.45 && Math.Abs(r.Y - l.Y) < 12 && r.Text.Length <= 25));
            if (pairs >= 4) s += 6; else if (pairs >= 2) s += 3;
            // 글자 없는 큰 사진 영역(패널 높이의 45% 이상)이 있으면 제품 사진 위주라 감점
            var ys = new List<int> { c.Top };
            ys.AddRange(inside.Select(l => l.Y).OrderBy(y => y));
            ys.Add(c.Bottom);
            int gap = 0;
            for (int i = 1; i < ys.Count; i++) gap = Math.Max(gap, ys[i] - ys[i - 1]);
            if (gap > (c.Bottom - c.Top) * 0.45) s -= 6;
            // 다음 단락 제목(POINT 02, CHECK 03 …)이 패널 아래쪽에 걸리면 감점, 맨 위에 있으면 가점
            int ph = c.Bottom - c.Top;
            if (inside.Any(l => SectionMark.IsMatch(l.Text) && l.Y - c.Top > ph * 0.6)) s -= 9;
            if (inside.Any(l => SectionMark.IsMatch(l.Text) && l.Y - c.Top < ph * 0.2)) s += 2;
            if (TitleAbove(lines, c)) s -= 6;                                  // 단락 제목이 패널 위로 잘려 나감
            double aspect = (c.Bottom - c.Top) / (double)w;
            s -= Math.Abs(aspect - 1.15) * 4;                                  // 기존 추가이미지는 대부분 폭의 1~1.4배
            // 패널 경계가 글자 줄을 자르면 감점
            if (lines.Any(l => l.Y < c.Top && l.Y + l.H > c.Top) || lines.Any(l => l.Y < c.Bottom && l.Y + l.H > c.Bottom)) s -= 5;
            return s;
        }

        // 마지막 장: 영양정보 표 (없으면 제품정보 표). 높이가 너무 길면 영양정보만
        static Candidate TableCandidate(List<OcrLine> lines, int w, int h, List<int[]> blocks)
        {
            // 기존 추가이미지: 영양정보가 있으면 영양정보 표만, 없으면 맨 끝 제품정보 표
            var nut = lines.LastOrDefault(l => Nutrition.IsMatch(l.Text) && l.Y > h * 0.3);
            var infoHead = lines.LastOrDefault(l => InfoTable.IsMatch(l.Text) && l.Y > h * 0.3);
            if (nut == null && infoHead == null) return null;

            var head = nut ?? infoHead;
            int top = Math.Max(0, head.Y - 30);
            var end = lines.FirstOrDefault(l => l.Y > head.Y + 60 && TableEnd.IsMatch(l.Text));
            int bottom = end != null ? (Regex.IsMatch(end.Text, "기준|^※|\\(%\\)|000") ? end.Y + end.H + 25 : end.Y - 25) : Math.Min(h, head.Y + w);
            if (bottom - top > w * 2) bottom = top + (int)(w * 2);
            // 표 아래 다음 구간(색 띠 등)이 붙지 않게, 끝 줄 바로 위의 여백에서 자름
            if (end != null)
            {
                var gapAbove = blocks.Where(b => b[1] > head.Y + 60 && b[1] <= bottom).Select(b => b[1] + 8).DefaultIfEmpty(bottom).Max();
                bottom = Math.Min(bottom, gapAbove);
            }
            return new Candidate { Label = nut != null ? "영양정보" : "제품정보 표", Top = top, Bottom = bottom, IsTable = true, Score = 100 };
        }

        // 후보 패널을 1000×1000 흰 바탕 가운데에 비율 그대로 (기존 추가이미지와 같은 방식)
        public static Bitmap Render(Bitmap img, Candidate c)
        {
            var src = c.Right > c.Left ? new Rectangle(c.Left, c.Top, c.Right - c.Left, Math.Min(c.Bottom, img.Height) - c.Top)
                                       : new Rectangle(0, c.Top, img.Width, Math.Min(c.Bottom, img.Height) - c.Top);
            float scale = Math.Min(Size / (float)src.Width, Size / (float)src.Height);
            int dw = (int)Math.Round(src.Width * scale), dh = (int)Math.Round(src.Height * scale);
            var bmp = new Bitmap(Size, Size, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, new Rectangle((Size - dw) / 2, (Size - dh) / 2, dw, dh), src, GraphicsUnit.Pixel);
            }
            return bmp;
        }

        // 기본 선택: 점수 높은 특징 패널 3장(위에서부터 순서대로) + 영양정보/제품정보 표 1장
        public static List<Candidate> DefaultPick(List<Candidate> all)
        {
            // 점수가 낮은 패널(사진 위주·내용 없음)은 억지로 채우지 않음. 내 스토어도 추가이미지 3~5장
            var good = all.Where(c => !c.IsTable && c.Score >= 6).ToList();
            if (good.Count < 2) good = all.Where(c => !c.IsTable && !c.Package && c.Score > -50).ToList();
            if (good.Count < 2) good = all.Where(c => !c.IsTable && c.Score > -50).ToList();
            var pick = good.OrderByDescending(c => c.Score).Take(3).OrderBy(c => c.Top).ToList();
            // 마지막: 영양정보 표 (+ 패널이 모자라면 제품정보 표도) — 모두 4장까지
            var tables = all.Where(c => c.IsTable).OrderBy(c => c.Label == "제품정보 표" ? 0 : 1).ToList();
            var nut = tables.FirstOrDefault(c => c.Label == "영양정보") ?? tables.FirstOrDefault();
            var info = tables.FirstOrDefault(c => c != nut);
            if (info != null && pick.Count + (nut != null ? 1 : 0) < 4) pick.Add(info);
            if (nut != null) pick.Add(nut);
            return pick.Take(4).ToList();
        }
    }
}
