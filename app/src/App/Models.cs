using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace SalesTool
{
    // 키워드 한 줄
    public class KwRow
    {
        public string Word;
        public long Volume = -1;     // 월간검색량 (-1 = 모름)
        public long Total = -1;      // 가격비교 상품총수량 (-1 = 안 읽음)
        public int InPage;           // 1페이지 상품명 중 이 단어가 들어간 수
        public string Excluded;      // 제외 사유 (브랜드 / 인물 / 학습) — null 이면 사용 가능
        public bool Use = true;      // 상품명에 쓸지 (검토 화면 체크)
        public bool Mentioned;       // msgood 상품명·상세 페이지에 나온 말인지
        public bool FromDetail;      // 상세 페이지 글자에서 찾은 키워드
        public bool Typo;
        public bool FromBook;        // 사장님 검색어 사전(keyword_book.json)에서 온 말
        public bool BookMine;        // 사전에서 이 상품번호에 직접 고른 말
        public bool BookProven;      // 쿠팡 광고로 실제 주문이 난 검색어
        public int BookCount;        // 사전 몇 묶음에 나왔나
        public bool InName;
        public bool Function;        // 기능·효과를 말하는 말이 상세 페이지에 나옴 (혈행개선·콜레스테롤·월경전 …)          // msgood 상품명에 있는 말 (오메가3+비타민D 의 비타민D)
        public bool Core;            // 상세 페이지의 핵심 (상세에 주 키워드의 30% 이상 나온 말: 체지방·다이어트·장건강)            // 시작 단어의 오타 (젓산마그네숨): 사람들이 오타로도 검색하고 경쟁 상품이 적어서 상품명에 넣음
        public bool CatChecked;      // 네이버 쇼핑에서 이 단어로 검색해 카테고리를 확인했는지
        public double CatShare = -1; // 그 검색 결과 중 이 상품 카테고리(끝 이름 기준)인 비율 (-1 = 판단할 만큼 안 나옴)
        public bool OtherCategory { get { return CatChecked && CatShare >= 0 && CatShare < 0.2; } }
        public int DetailCount;      // 상세 페이지에 나온 횟수
        public string TagReason;     // 상품명에는 안 쓰고 [쿠팡 검색어]로 보내는 이유 (위험 단어 / 상품에 언급 없음 / 직접)
        public double Ratio { get { return Volume > 0 && Total >= 0 ? Total / (double)Volume : double.MaxValue; } }
        public bool Small(double maxRatio) { return Volume > 0 && Total >= 0 && Total < maxRatio * Volume; }
        // 최근 검색 흐름 (검색어 트렌드): 최근 28일 ÷ 그 전 석 달 평균, 작년 같은 때 대비 (-1 = 모름)
        public double Growth = -1, YoY = -1;
        // 급상승: 1.5배 이상 늘었고, 작년 이맘때보다도 많고(매년 오르는 계절 말 제외), 검색량 300 이상
        public bool Rising { get { return Growth >= 1.5 && (YoY < 0 || YoY >= 1.3) && Volume >= 300; } }
    }

    // 상품 하나의 작업 (jobs\상품번호\job.json)
    public class Job
    {
        public string Code;
        public string Status = "대기";        // 대기 / 진행 중 / 검토 대기 / 등록 완료 / 오류
        public string Step;                   // 진행 중일 때 단계 설명
        public string Pending;                // 대기 중인 일: null = 처음부터 만들기, "keywords" = 키워드·상품명·쿠팡 검색어 다시, "tags" = 쿠팡 검색어만 다시
        public int RulesVersion;              // 어느 규칙으로 만든 초안인지 (규칙이 바뀌면 프로그램이 알아서 다시 만듦)
        public string Error;
        public string Added, Registered;      // yyyy-MM-dd HH:mm

        // msgood4u
        public string Url, Name, Brand, Category, Notice, MainImageUrl;
        public List<string> DetailUrls = new List<string>();
        public long SalePrice, Wholesale, Shipping;
        public long Floor; public string FloorReason;
        // msgood 안내에 '쿠팡등록금지'가 있으면 쿠팡에는 올리지 않음 (네이버만)
        public bool CoupangBanned { get { return System.Text.RegularExpressions.Regex.IsMatch(Notice ?? "", @"쿠팡\s*(등록|판매)\s*금지"); } }
        public bool NeedPrice;                // msgood4u 로그인 없이 만들어서 도매가·가격이 아직 없음 (로그인 후 [가격 다시 계산])

        // 키워드·상품명
        public string Seed, SeedReason, OrderMode;
        public List<KwRow> Keywords = new List<KwRow>();
        public List<KwRow> Related = new List<KwRow>();
        public List<KwRow> Concern = new List<KwRow>();   // 고민·상황·대상 키워드 (눈이뻑뻑할때·아연많은음식·군대px영양제): 상품수·검색량 확인한 것
        public List<string> CatTitles = new List<string>();  // msgood 상품명으로 네이버 쇼핑을 검색했을 때 1페이지 상품명 (카테고리·시작 단어 고를 때)
        public string CatPath;                               // 그때 가장 많이 나온 카테고리 경로
        public string DetailMain;                            // 상세 페이지 주 키워드 (카테고리 검색어·시작 단어)
        public string TypeWord, TypeSuffix;                  // 상품 종류 단어(발효흑삼고)와 실제로 검색되는 뒷부분(흑삼고)
        public List<string> SuffixTitles = new List<string>(); // 뒷부분으로 검색한 1페이지 상품명   // 검색광고 연관 키워드 (시작 단어가 들어간 것) — 상품명이 짧을 때 채움
        public Dictionary<string, string> Names = new Dictionary<string, string>();       // 검토 화면에서 고친 최종
        public Dictionary<string, string> DraftNames = new Dictionary<string, string>();  // 프로그램이 처음 만든 것 (학습 비교용)
        public string DraftSeed;
        public List<string> CoupangTags = new List<string>();  // 쿠팡 검색어 (#1 샵 기본)
        public List<string> CoupangTags1 = new List<string>(); // #1 샵: 질병/치료 위험 키워드 우선
        public List<string> CoupangTags2 = new List<string>(); // #2 샵: 인물 키워드 우선
        public List<string> CoupangTags3 = new List<string>(); // #3 샵: 증가배수 급상승 키워드 우선
        public List<string> DiseaseWords = new List<string>(); // 질병·치료 위험 검색어 후보 목록
        public List<string> PersonWords = new List<string>();  // 인물 위험 검색어 후보 목록
        public List<string> RisingWords = new List<string>();  // 급상승 증가배수 검색어 후보 목록
        public List<string> DraftTags = new List<string>();
        public List<string> RiskTags = new List<string>();     // 위험 검색어 후보 "검색어	이유" (인물·다른 브랜드·질병·의약품): 검토 화면에서 직접 골라야 들어감
        // 상품명 단어의 상품총수량·월간검색량 (네이버 쇼핑 전체 기준, 단어 순서 정할 때 씀)
        public Dictionary<string, long> WordTotals = new Dictionary<string, long>();
        public List<string> NameWords = new List<string>();   // 상품명에 쓸 수 있는 키워드 전체 (순서대로) → 못 들어간 것은 네이버 브랜드·제조사 칸
        public Dictionary<string, long> WordVolumes = new Dictionary<string, long>();

        // 가격 (index = 개수, 0 은 안 씀)
        public long[] Prices = new long[7];
        public long[] FormulaPrices = new long[7];

        // 상세·등록 정보
        public string Volume, DetailHtml, OcrText;
        public bool Health;
        public string HealthNote;     // 건강기능식품 판단 근거·확인 필요 (검토 화면에 표시)
        public string RepText;        // 대표 이미지(msgood 상품 사진) 글자 인식 결과 (포장의 건강기능식품 표시 찾기)
        public string RepMismatch;    // msgood 대표 사진이 다른 상품일 때 그 사진의 상품 이름 (천명환) → 상세 사진으로 대표이미지
        public List<int[]> EraseBoxes = new List<int[]>();   // 글자 인식이 못 읽는 작은 브랜드 글씨(둥근 배지의 휜 글씨) 직접 지울 곳: msgood 대표 사진 기준 x,y,w,h
        public string NaverCat, NaverCatName, CoupangCat, CoupangCatName;
        public string DraftNaverCat, DraftCoupangCat;
        public List<string> Results = new List<string>();
        public Dictionary<string, string> Registered_Ids = new Dictionary<string, string>(); // "n1_1" → 원상품번호, "c1" → 쿠팡 상품ID

        public string Title { get { return Name ?? ("상품 " + Code); } }
    }

    public class AccountPlan
    {
        public string Id;                 // "1", "2", "3"
        public bool Use;
        public string BusinessId = "";    // 사업자명 또는 사업자번호
        public string NameStyle = "기본"; // "기본", "브랜드 앞", "효능 앞", "함량 앞"
        public string NaverTemplate = "", CoupangTemplate = "";
        public string NaverCounts = "1,2";
        public string CoupangCounts = "1,3,5";
        public string Suffix { get { return Id == "1" ? "" : "_" + Id; } }
        public List<int> Counts(string s) { return (s ?? "").Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(x => { int n; return int.TryParse(x, out n) ? n : 0; }).Where(n => n >= 1 && n <= 6).Distinct().ToList(); }
    }

    public class AppSettings
    {
        public string OrderMode = Orders.Volume;
        public double MaxRatio = 2;
        public int DailyLimit = 20;
        public bool NaverPaused = false;    // (안 씀) 네이버는 항상 전시중으로 등록
        public bool CoupangApprove = false; // 쿠팡: 승인요청까지 (아니면 임시저장)
        public double NormalRate = 1.6;
        public int MaxReuse = 3;            // 다른 상품과 겹쳐 써도 되는 키워드: 최대 몇 개 상품까지 (상품명·쿠팡 검색어)
        public long ReuseMaxTotal = 3000;   //  …그리고 상품총수량이 이보다 적고 배율 조건에 맞는 말만 (1페이지에 오를 만한 말)
        public long TagMinVolume = 1000;    // 쿠팡 검색어에 넣을 최소 월간검색량 (브랜드·위험 단어·상품에 없는 말)
        public int Stock = 300;
        public bool AutoStock = false;
        public bool AutoAdKw = true;     // 새 쿠팡 광고 보고서가 들어오면 광고 키워드 목록을 자동으로 만듦
        public bool AutoOrder = true;
        public bool NotifyDraft = false;    // 초안 완성 알림 전송 여부 (체크 해제 시 초안 알림을 끄고 발주·품절 알림만 전송)
        public bool MobileOn = true;        // 휴대폰 앱 서버
        public int MobilePort = 8765;
        public string MobileKey;            // 휴대폰 앱 연결 키 (처음 켤 때 만듦)       // (자동 발주) 1·2·3차 마감 5분 전 발주(09:25·10:55·13:55), 송장은 19:00 (MainForm.OrderSlots)
        public string DefaultCourier = "CJ대한통운";   // msgood 송장에 택배사 이름이 없을 때      // 6시간마다 msgood4u 품절 목록을 보고 스토어 품절·판매 재개를 자동으로
        public string GithubRepo = "";              // GitHub 저장소 (owner/repo) 자동 업데이트용
        public bool AutoUpdateCheck = true;         // 시작 시 자동 업데이트 확인
        public bool AutoAiImage = false;            // 대기열 상품 등록 시 AI 3D 대표이미지 자동 생성
        public string SharedFolder = "";            // 공유 키워드 기록 폴더 경로 (예: 구글 드라이브 폴더)
        public List<AccountPlan> Accounts = new List<AccountPlan>
        {
            new AccountPlan { Id = "1", Use = true,  NaverCounts = "1,2", CoupangCounts = "1,3,5" },
            new AccountPlan { Id = "2", Use = false, NaverCounts = "1,2", CoupangCounts = "2,4,6" },
            new AccountPlan { Id = "3", Use = false, NaverCounts = "1,2", CoupangCounts = "1,2,3" },
        };
    }

    public static class Orders
    {
        public const string Volume = "검색량 순", Ratio = "배율 순", Total = "상품총수량 적은 순", Growth = "상승 순";
        public static readonly string[] All = { Volume, Ratio, Total, Growth };
    }

    // 검토 화면에서 고친 내용을 쌓아 다음 상품에 반영
    public class Learned
    {
        public Dictionary<string, int> Excluded = new Dictionary<string, int>();    // 상품명에서 뺀 단어 → 횟수
        public Dictionary<string, int> Preferred = new Dictionary<string, int>();   // 직접 넣은 단어 → 횟수
        public Dictionary<string, string> SeedFor = new Dictionary<string, string>(); // msgood 상품명 단어 → 고른 시작 단어
        public Dictionary<string, string> NaverCatFor = new Dictionary<string, string>();   // msgood 카테고리 → 네이버 카테고리
        public Dictionary<string, string> CoupangCatFor = new Dictionary<string, string>(); // msgood 카테고리 → 쿠팡 카테고리
        public Dictionary<string, int> Brands = new Dictionary<string, int>();    // 브랜드라고 알려 준 단어 → 상품명에는 안 쓰고 검색량이 많으면 쿠팡 검색어로
        public Dictionary<string, int> TagOnly = new Dictionary<string, int>();   // 상품명 대신 쿠팡 검색어로 옮긴 단어 → 횟수
        public List<string> History = new List<string>();
        public Dictionary<string, int> SoldWords = new Dictionary<string, int>();   // 팔린 상품(최근 30일)의 상품명 단어 → 판매 개수

        public int SoldScore(string w) { int n; return (SoldWords != null && SoldWords.TryGetValue(w.ToLowerInvariant(), out n) ? n : 0) + SalesBookImport.NameWordScore(w); }
        public bool IsBrand(string w) { int n; return Brands.TryGetValue(w.ToLowerInvariant(), out n) && n >= 1; }
        public bool IsTagOnly(string w) { int n; return TagOnly.TryGetValue(w.ToLowerInvariant(), out n) && n >= 1; }
        public bool IsExcluded(string w) { int n; return Excluded.TryGetValue(w.ToLowerInvariant(), out n) && n >= 1; }
        public bool IsPreferred(string w) { int n; return Preferred.TryGetValue(w.ToLowerInvariant(), out n) && n >= 1; }
    }

    // json 파일 저장·읽기
    public static class Store
    {
        public static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        public static string JobsDir { get { return Path.Combine(Dir, "jobs"); } }
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static T Load<T>(string path) where T : new()
        {
            try { if (File.Exists(path)) return json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
            catch { }
            return new T();
        }

        public static void Save(string path, object o)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Pretty(json.Serialize(o)), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static string SettingsPath { get { return Path.Combine(Dir, "settings.json"); } }
        public static string LearnPath { get { return Path.Combine(Dir, "learned.json"); } }
        public static string JobDir(string code) { return Path.Combine(JobsDir, code); }
        public static string JobPath(string code) { return Path.Combine(JobDir(code), "job.json"); }

        public static AppSettings LoadSettings()
        {
            var s = Load<AppSettings>(SettingsPath);
            // 계정 칸이 모자라면 채움
            foreach (var id in new[] { "1", "2", "3" })
                if (!s.Accounts.Any(a => a.Id == id)) s.Accounts.Add(new AppSettings().Accounts.First(a => a.Id == id));
            s.Accounts = s.Accounts.Where(a => a.Id == "1" || a.Id == "2" || a.Id == "3").GroupBy(a => a.Id).Select(g => g.First()).OrderBy(a => a.Id).ToList();
            if (!Orders.All.Contains(s.OrderMode)) s.OrderMode = Orders.Volume;
            return s;
        }

        public static List<Job> LoadJobs()
        {
            var list = new List<Job>();
            if (!Directory.Exists(JobsDir)) return list;
            foreach (var d in Directory.GetDirectories(JobsDir))
            {
                var f = Path.Combine(d, "job.json");
                if (!File.Exists(f)) continue;
                var j = Load<Job>(f);
                if (j.Code == null) continue;
                if (j.Status == "진행 중") j.Status = "대기"; // 프로그램을 껐다 켜면 처음부터 다시
                list.Add(j);
            }
            return list.OrderBy(j => j.Added).ToList();
        }

        public static void SaveJob(Job j) { Save(JobPath(j.Code), j); }

        // 사람이 메모장으로 봐도 읽히게 줄바꿈
        static string Pretty(string s)
        {
            var sb = new StringBuilder(); int ind = 0; bool str = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' && (i == 0 || s[i - 1] != '\\')) str = !str;
                if (str) { sb.Append(c); continue; }
                if (c == '{' || c == '[') { sb.Append(c); sb.Append("\r\n"); sb.Append(' ', ++ind * 2); }
                else if (c == '}' || c == ']') { sb.Append("\r\n"); sb.Append(' ', --ind * 2); sb.Append(c); }
                else if (c == ',') { sb.Append(",\r\n"); sb.Append(' ', ind * 2); }
                else if (c == ':') sb.Append(": ");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
