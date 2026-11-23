using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using RepImages;

namespace SalesTool
{
    public static class AiImageGen
    {
        static readonly Random rng = new Random();

        // 주요 영양제/건강식품 키워드 영문 매핑 사전
        static readonly Dictionary<string, string> KeywordEn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "콘드로이친", "Chondroitin Shark Cartilage" },
            { "홍화씨유", "Safflower Seed Oil CLA Softgels" },
            { "달맞이꽃", "Evening Primrose Seed Oil GLA" },
            { "보라지유", "Borage Seed Oil GLA Softgels" },
            { "상어", "Shark Cartilage Joint Care" },
            { "오메가3", "Omega-3 Fish Oil" },
            { "알티지", "rTG Omega-3" },
            { "루테인", "Lutein Zeaxanthin Eye Care" },
            { "지아잔틴", "Lutein Zeaxanthin" },
            { "유산균", "Probiotics Digestive Health" },
            { "락토바실러스", "Probiotics Lactobacillus" },
            { "밀크씨슬", "Milk Thistle Silymarin Liver" },
            { "실리마린", "Silymarin Liver Support" },
            { "글루타치온", "Glutathione Brightening Complex" },
            { "콜라겐", "Collagen Peptides" },
            { "마그네슘", "Magnesium Glycinate" },
            { "비타민", "Daily Multivitamin Complex" },
            { "비타민C", "Pure Vitamin C" },
            { "비타민D", "Vitamin D3" },
            { "쏘팔메토", "Saw Palmetto Men Prostate" },
            { "보스웰리아", "Boswellia Joint Support" },
            { "홍삼", "Korean Red Ginseng Extract" },
            { "침향", "Agarwood Traditional Herbal" },
            { "아르기닌", "L-Arginine Energy" },
            { "아연", "Zinc Mineral" },
            { "철분", "Iron Supplement" },
            { "칼슘", "Calcium Magnesium D" },
            { "엽산", "Folic Acid Folate" },
            { "코엔자임", "CoQ10 Heart Health" },
            { "프로폴리스", "Propolis Immune Booster" },
            { "다이어트", "Diet Fat Burner Garcinia" },
            { "가르시니아", "Garcinia Cambogia Extract" },
            { "카테킨", "Green Tea Catechin" },
            { "차전자피", "Psyllium Husk Fiber" },
            { "효소", "Digestive Multi Enzyme" },
            { "스피루리나", "Spirulina Superfood" },
            { "크릴오일", "Krill Oil Phospholipid" },
            { "타트체리", "Tart Cherry Melatonin" },
            { "테아닌", "L-Theanine Relaxation" },
            { "맥주효모", "Brewer Yeast Hair Care" },
            { "비오틴", "Biotin Hair and Nail" }
        };

        // 상품 정보로부터 AI 이미지 생성용 프롬프트 생성
        public static string BuildPrompt(Job job, out string topicKorean)
        {
            string name = job.Name ?? "";
            string seed = job.Seed ?? "";
            string cat = job.Category ?? "";

            // 핵심 키워드 매칭
            string matchedTopic = null;
            string matchedKo = "건강기능식품";

            foreach (var kv in KeywordEn)
            {
                if (name.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    seed.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cat.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchedTopic = kv.Value;
                    matchedKo = kv.Key;
                    break;
                }
            }

            if (string.IsNullOrEmpty(matchedTopic))
            {
                matchedTopic = !string.IsNullOrWhiteSpace(seed) ? seed : "Health Dietary Supplement";
                matchedKo = !string.IsNullOrWhiteSpace(seed) ? seed : "건강영양제";
            }

            topicKorean = matchedKo;

            // 영양제 병, 약통, 비타민 보틀 형태
            string containerType = "standing white cylindrical dietary supplement medicine bottle with white screw cap, realistic paper product label wrap";

            if (name.Contains("스틱") || name.Contains("포"))
            {
                containerType = "standing glossy dietary supplement stick box and single stick sachet, realistic product packaging";
            }
            else if (name.Contains("환"))
            {
                containerType = "standing traditional herbal supplement round jar with golden cap and label wrap";
            }
            else if (name.Contains("분말") || name.Contains("가루"))
            {
                containerType = "standing wide plastic supplement powder jar tub with screw lid";
            }
            else
            {
                // 캡슐, 정, 연질캡슐, 일반 영양제 병: 세워진 약통/보틀
                containerType = "standing upright cylindrical dietary supplement bottle with modern clean label and sealed white cap";
            }

            // 흰 배경 위에 똑바로 서 있는 실제 제품 용기 사진 (비누곽, 장난감 형태 원천 차단)
            return $"professional studio packshot photography of an upright standing {containerType}, realistic commercial retail packaging for {matchedTopic}, front eye-level view, isolated on pure solid white background, clean studio lighting, realistic commercial product shot, sharp focus, 8k";
        }

        // 무료 AI 이미지 생성 (Pollinations / Flux.1 Schnell 무제한 무료)
        public static async Task<Bitmap> GenerateFreeAsync(string prompt, Action<string> log)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            int seed = rng.Next(100000, 999999);
            string url = $"https://image.pollinations.ai/prompt/{Uri.EscapeDataString(prompt)}?width=1024&height=1024&model=flux&nologo=true&seed={seed}";

            log?.Invoke("무료 AI 엔진으로 3D 대표이미지 생성 중 (약 3~5초 소요)...");

            using (var client = new WebClient())
            {
                client.Headers["User-Agent"] = "SalesTool/1.1";
                byte[] data = await client.DownloadDataTaskAsync(new Uri(url));
                using (var ms = new MemoryStream(data))
                {
                    var bmp = new Bitmap(ms);
                    return new Bitmap(bmp); // 메모리 복사본 반환
                }
            }
        }

        // 생성되거나 다운받은 이미지를 해당 Job의 1~6통 및 네이버 대표이미지로 일괄 합성 덮어쓰기
        public static bool ApplyImageToJob(Job job, Bitmap newPhoto, Learned learned, Action<string> log)
        {
            if (job == null || newPhoto == null) return false;

            string dir = Store.JobDir(job.Code);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            var q = new EncoderParameters(1);
            q.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 93L);

            // 1. 원본 AI 사진 백업 저장
            string backupPath = Path.Combine(dir, "rep_source_ai.jpg");
            newPhoto.Save(backupPath, enc, q);

            // 2. 레이아웃 분석 및 합성 (1~6통)
            log?.Invoke("생성된 이미지에 1~6통 및 1+1 배지 합성 중...");
            var brandList = learned?.Brands != null ? learned.Brands.Keys.ToList() : new List<string>();
            var lay = Composer.Analyze(newPhoto, job.Brand, log ?? (m => { }), job.Name, brandList);
            lay.WhiteBg = true;

            // 네이버 스토어용: 상표 글자판 없이 1개짜리 + 1+1
            var plain = Composer.Plain(newPhoto);
            plain.WhiteBg = true;
            if (lay.Cropped) { plain.Content = lay.Content; plain.Cropped = true; }

            for (int n = 1; n <= 2; n++)
            {
                using (var bmp = Composer.Render(plain, null, n))
                {
                    bmp.Save(Path.Combine(dir, "naver_" + n + ".jpg"), enc, q);
                }
            }
            plain.Photo.Dispose();

            // 쿠팡/대표 1~6통: 글자판(job.Seed) + 수량 배지
            for (int n = 1; n <= 6; n++)
            {
                using (var bmp = Composer.Render(lay, job.Seed, n))
                {
                    bmp.Save(Path.Combine(dir, "rep_" + n + ".jpg"), enc, q);
                }
            }
            lay.Photo.Dispose();

            Store.SaveJob(job);
            log?.Invoke($"✔ [{job.Code}] 대표이미지(1~6통 및 네이버용)를 새 이미지로 합성 완료했습니다.");
            return true;
        }

        // 어사이드(Aside) AI 에이전트 일괄 생성용 배치 파일 내보내기 (JSON 및 TXT)
        public static string ExportAsideBatchTasks(IEnumerable<Job> jobs, Action<string> log)
        {
            var list = (jobs ?? new List<Job>()).ToList();
            if (list.Count == 0) return null;

            string outDir = Path.Combine(Store.Dir, "aside_batch");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            var tasks = new List<Dictionary<string, object>>();
            var txtLines = new List<string>();
            txtLines.Add("# [어사이드(Aside) 챗GPT 유료 계정 일괄 이미지 생성 작업 목록]");
            txtLines.Add("# Aside 브라우저 에이전트에게 '이 목록의 각 상품에 대해 ChatGPT에서 프롬프트로 이미지를 생성하고 save_path에 저장해줘'라고 지시하세요.\n");

            foreach (var j in list)
            {
                string topicKo;
                string p = BuildPrompt(j, out topicKo);
                string targetDir = Store.JobDir(j.Code);
                string targetPath = Path.Combine(targetDir, "rep_source_ai.jpg");

                var item = new Dictionary<string, object>
                {
                    { "code", j.Code },
                    { "name", j.Name },
                    { "topic", topicKo },
                    { "prompt", p },
                    { "save_path", targetPath }
                };
                tasks.Add(item);

                txtLines.Add($"[{j.Code}] {j.Name} ({topicKo})");
                txtLines.Add($"프롬프트: {p}");
                txtLines.Add($"저장경로: {targetPath}\n");
            }

            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string jsonPath = Path.Combine(outDir, "aside_batch_tasks.json");
            File.WriteAllText(jsonPath, serializer.Serialize(tasks), System.Text.Encoding.UTF8);

            string txtPath = Path.Combine(outDir, "aside_batch_prompts.txt");
            File.WriteAllLines(txtPath, txtLines, System.Text.Encoding.UTF8);

            log?.Invoke($"✔ 어사이드 배치 파일({list.Count}개 상품)을 생성했습니다: {jsonPath}");
            return jsonPath;
        }

        // 어사이드(Aside) AI 웹 브라우저를 챗GPT 페이지로 열고 맞춤 프롬프트 클립보드 복사
        public static void OpenAsideWithPrompt(Job job, Action<string> log)
        {
            string topicKo;
            string prompt = BuildPrompt(job, out topicKo);

            string asideExe = @"C:\Program Files\Aside\Application\Aside.exe";
            if (!File.Exists(asideExe))
            {
                string localAside = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Aside\Aside.exe");
                if (File.Exists(localAside)) asideExe = localAside;
            }

            Clipboard.SetText(prompt);
            log?.Invoke($"[{job.Code}] 어사이드/챗GPT용 프롬프트가 복사되었습니다! (붙여넣기: Ctrl + V)");

            try
            {
                if (File.Exists(asideExe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = asideExe,
                        Arguments = "\"https://chatgpt.com\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start("https://chatgpt.com");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("Aside 브라우저 열기 실패: " + ex.Message);
            }
        }

        // 클립보드에 복사된 이미지를 즉시 현재 상품의 1~6통 및 네이버 대표이미지로 합성 적용
        public static bool PasteFromClipboard(Job job, Learned learned, Action<string> log)
        {
            if (job == null) return false;
            if (!Clipboard.ContainsImage())
            {
                log?.Invoke("클립보드에 복사된 이미지가 없습니다. 챗GPT나 어사이드에서 이미지를 우클릭하여 '이미지 복사' 후 눌러주세요.");
                return false;
            }

            try
            {
                using (var img = Clipboard.GetImage())
                {
                    if (img == null) return false;
                    var bmp = new Bitmap(img);
                    return ApplyImageToJob(job, bmp, learned, log);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("클립보드 이미지 적용 실패: " + ex.Message);
                return false;
            }
        }

        // 호환용 레거시 메서드 (코파일럿 버튼 유지 호환)
        public static void OpenCopilotWithPrompt(Job job, Action<string> log)
        {
            OpenAsideWithPrompt(job, log);
        }
    }
}
