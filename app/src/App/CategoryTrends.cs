using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using SmallKeyword;

namespace SalesTool
{
    public class CategoryTrendItem
    {
        public int Rank { get; set; }
        public string Word { get; set; }
        public long Volume { get; set; }
        public double Growth { get; set; } // 최근 28일 ÷ 이전 84일 28일 평균 (1.0 = 동일, > 1.2 = 급상승)
        public string RiskType { get; set; } // "의약품", "질병·치료", "다른 브랜드", "인물", "일반"
    }

    public class CategoryTrendData
    {
        public string Category { get; set; }
        public string Updated { get; set; }
        public List<CategoryTrendItem> Items { get; set; } = new List<CategoryTrendItem>();
    }

    public static class CategoryTrends
    {
        public static readonly string[] Categories = new[]
        {
            "오메가3",
            "유산균",
            "관절·연골",
            "눈건강·루테인",
            "간건강·밀크씨슬",
            "비타민·마그네슘",
            "다이어트·체지방",
            "홍삼·흑삼·침향",
            "남성건강·쏘팔메토",
            "여성건강·갱년기",
            "피부·콜라겐",
            "위·소화효소",
            "수면·스트레스",
            "탈모·모발건강",
            "일반식품·곡물",
            "기타건강보조식품"
        };

        // 카테고리별 시드 단어 매핑
        public static readonly Dictionary<string, string[]> CategorySeeds = new Dictionary<string, string[]>
        {
            ["오메가3"] = new[] { "오메가3", "rtg오메가3", "식물성오메가3", "오메가3약" },
            ["유산균"] = new[] { "유산균", "프로바이오틱스", "여성유산균", "다이어트유산균", "유산균약" },
            ["관절·연골"] = new[] { "관절약", "콘드로이친", "msm", "보스웰리아", "관절치료제" },
            ["눈건강·루테인"] = new[] { "루테인", "아스타잔틴", "지아잔틴", "눈영양제", "안약" },
            ["간건강·밀크씨슬"] = new[] { "밀크씨슬", "실리마린", "간장약", "피로회복제" },
            ["비타민·마그네슘"] = new[] { "종합비타민", "마그네슘", "비타민C", "비타민D", "비타민약" },
            ["다이어트·체지방"] = new[] { "다이어트약", "가르시니아", "카테킨", "모로실", "체지방감소" },
            ["홍삼·흑삼·침향"] = new[] { "홍삼", "흑삼", "침향환", "홍삼정", "자양강장제" },
            ["남성건강·쏘팔메토"] = new[] { "쏘팔메토", "아르기닌", "마카", "전립선약", "남성활력" },
            ["여성건강·갱년기"] = new[] { "감마리놀렌산", "생리통약", "질유산균", "대두이소플라본", "갱년기약" },
            ["피부·콜라겐"] = new[] { "콜라겐", "글루타치온", "엘라스틴", "히알루론산", "피부영양제" },
            ["위·소화효소"] = new[] { "소화효소", "소화제", "양배추환", "매스틱검", "위장약" },
            ["수면·스트레스"] = new[] { "수면약", "테아닌", "타트체리", "락티움", "수면유도제" },
            ["탈모·모발건강"] = new[] { "탈모약", "비오틴", "맥주효모", "모발영양제", "탈모치료제" },
            ["일반식품·곡물"] = new[] { "오트밀", "귀리", "그래놀라", "시리얼", "뮤즐리" },
            ["기타건강보조식품"] = new[] { "홍화씨유", "홍화씨유 캡슐", "달맞이꽃종자유", "보라지유", "아르간오일", "식물성오일" }
        };

        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static Dictionary<string, CategoryTrendData> cache = new Dictionary<string, CategoryTrendData>(StringComparer.OrdinalIgnoreCase);

        static string CachePath => Path.Combine(Store.Dir, "category_trends.json");

        static string Key(string s) => Regex.Replace(s ?? "", @"[^0-9A-Za-z가-힣]", "").ToLowerInvariant();

        // 현재 상품의 키워드/시드/이름 및 네이버 카테고리로부터 가장 적합한 카테고리 추론
        public static string MatchCategory(string seed, string name, string naverCatName = null)
        {
            if (!string.IsNullOrEmpty(naverCatName))
            {
                if (naverCatName.Contains("기타건강보조식품") || naverCatName.Contains("기타건강식품")) return "기타건강보조식품";
                if (naverCatName.Contains("오메가")) return "오메가3";
                if (naverCatName.Contains("유산균")) return "유산균";
                if (naverCatName.Contains("루테인")) return "눈건강·루테인";
                if (naverCatName.Contains("밀크씨슬")) return "간건강·밀크씨슬";
                if (naverCatName.Contains("관절") || naverCatName.Contains("msm") || naverCatName.Contains("콘드로이친")) return "관절·연골";
                if (naverCatName.Contains("비타민") || naverCatName.Contains("마그네슘")) return "비타민·마그네슘";
                if (naverCatName.Contains("홍삼") || naverCatName.Contains("흑삼")) return "홍삼·흑삼·침향";
                if (naverCatName.Contains("곡물") || naverCatName.Contains("오트밀")) return "일반식품·곡물";
            }

            string all = Key((seed ?? "") + " " + (name ?? ""));
            if (all.Contains("홍화") || all.Contains("달맞이꽃") || all.Contains("보라지") || all.Contains("아르간") || (all.Contains("오일") && !all.Contains("오메가"))) return "기타건강보조식품";
            if (all.Contains("오메가")) return "오메가3";
            if (all.Contains("유산균") || all.Contains("프로바이오") || all.Contains("비피더스")) return "유산균";
            if (all.Contains("관절") || all.Contains("msm") || all.Contains("콘드로이친") || all.Contains("보스웰리아")) return "관절·연골";
            if (all.Contains("루테인") || all.Contains("지아잔틴") || all.Contains("아스타잔틴") || all.Contains("눈")) return "눈건강·루테인";
            if (all.Contains("밀크씨슬") || all.Contains("실리마린") || all.Contains("간")) return "간건강·밀크씨슬";
            if (all.Contains("마그네슘") || all.Contains("비타민") || all.Contains("아연") || all.Contains("칼슘")) return "비타민·마그네슘";
            if (all.Contains("다이어트") || all.Contains("가르시니아") || all.Contains("카테킨") || all.Contains("체지방")) return "다이어트·체지방";
            if (all.Contains("홍삼") || all.Contains("흑삼") || all.Contains("침향")) return "홍삼·흑삼·침향";
            if (all.Contains("쏘팔메토") || all.Contains("아르기닌") || all.Contains("마카") || all.Contains("남성")) return "남성건강·쏘팔메토";
            if (all.Contains("생리") || all.Contains("갱년기") || all.Contains("이소플라본") || all.Contains("질유산균")) return "여성건강·갱년기";
            if (all.Contains("콜라겐") || all.Contains("글루타치온") || all.Contains("엘라스틴")) return "피부·콜라겐";
            if (all.Contains("소화") || all.Contains("효소") || all.Contains("양배추") || all.Contains("매스틱")) return "위·소화효소";
            if (all.Contains("수면") || all.Contains("테아닌") || all.Contains("락티움") || all.Contains("타트체리")) return "수면·스트레스";
            if (all.Contains("탈모") || all.Contains("비오틴") || all.Contains("맥주효모")) return "탈모·모발건강";
            if (all.Contains("오트밀") || all.Contains("귀리") || all.Contains("시리얼") || all.Contains("그래놀라") || all.Contains("뮤즐리")) return "일반식품·곡물";
            return Categories[0];
        }

        // 캐시 로드 (없으면 기본 데이터 생성)
        public static void Load()
        {
            try
            {
                if (File.Exists(CachePath))
                {
                    var text = File.ReadAllText(CachePath, Encoding.UTF8);
                    var dict = json.Deserialize<Dictionary<string, CategoryTrendData>>(text);
                    if (dict != null && dict.Count > 0)
                    {
                        cache = dict;
                        return;
                    }
                }
            }
            catch { }
            // 캐시가 없으면 기본 데이터셋 빌드
            BuildDefaultData();
            Save();
        }

        public static void Save()
        {
            try
            {
                File.WriteAllText(CachePath, json.Serialize(cache), Encoding.UTF8);
            }
            catch { }
        }

        public static CategoryTrendData Get(string cat)
        {
            if (cache.Count == 0) Load();
            CategoryTrendData data;
            if (cache.TryGetValue(cat, out data)) return data;
            return cache.Values.FirstOrDefault() ?? new CategoryTrendData { Category = cat };
        }

        // 위험 키워드 분류 함수
        public static string DetectRisk(string w)
        {
            if (string.IsNullOrWhiteSpace(w)) return "일반";
            string k = Key(w);

            // 1) 의약품 (약, 변비약, 관절약, 수면제, 진통제, 소화제, 해열제, 전문의약품 등)
            bool hasDrug = Regex.IsMatch(k, @"(^|[가-힣])약($|[0-9])") ||
                           Regex.IsMatch(w, @"(^|\s|[가-힣])약(\s|$|[0-9])") ||
                           w.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Any(t => Regex.IsMatch(Key(t), @"[가-힣]약$")) ||
                           Regex.IsMatch(k, @"수면제|진통제|소화제|해열제|항생제|안약|점안액|의약품|처방약|전문약");

            if (hasDrug) return "의약품";

            // 2) 질병 이름 및 질병 치료/완치/예방 주장 (식품표시광고법상 허위과대광고)
            if (Naming.IsDisease(w) || Regex.IsMatch(k, @"치료|완치|예방|증후군") || w.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Any(t => Naming.IsDisease(t)))
                return "질병·치료";

            // 3) 다른 회사 브랜드
            if (Naming.IsKnownBrand(w) || w.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Any(Naming.IsKnownBrand))
                return "다른 브랜드";

            // 4) 유명인/의사/약사/인물 (유재석·이만기·송가인·여에스더·약사추천 등)
            if (Regex.IsMatch(k, @"(유재석|신동엽|강호동|이만기|송가인|임영웅|정동원|장민호|영탁|이찬원|김종국|아이유|여에스더|오은영|이경제|이시형|서재걸|권오중|남재현|박용우|유태우|홍혜걸|고약사|약들약|리틀약사|정세연|김연아|박서준|차승원|조승우|이병헌|김혜수|전지현|이영애|공유|현빈|마동석|조정석|남궁민|안정환|추성훈|김동현|하정우|성동일|의사|약사|한의사|전문의|약사추천|의사추천|원장|교수|박사|명의|닥터)"))
                return "인물";

            return "일반";
        }

        // 네이버 검색광고 + 트렌드 API를 통해 카테고리 1~100위 새로고침
        public static async Task<CategoryTrendData> RefreshAsync(string cat, SearchAd ad, Trend trend, Action<string> log)
        {
            if (!CategorySeeds.ContainsKey(cat)) cat = Categories[0];
            var seeds = CategorySeeds[cat];
            var pool = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            log?.Invoke($"'{cat}' 카테고리 연관 검색어 수집 중...");
            if (ad != null)
            {
                foreach (var s in seeds)
                {
                    try
                    {
                        var rel = await Task.Run(() => ad.Related(s));
                        foreach (var kv in rel)
                        {
                            if (kv.Value > 0 && kv.Key.Length >= 2 && kv.Key.Length <= 20)
                            {
                                long existing;
                                if (!pool.TryGetValue(kv.Key, out existing) || kv.Value > existing)
                                    pool[kv.Key] = kv.Value;
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(200);
                }
            }

            // 상위 120개 키워드 추출
            var topWords = pool.OrderByDescending(kv => kv.Value).Take(120).Select(kv => kv.Key).ToList();
            if (topWords.Count == 0)
            {
                // API 키 미설정 시 기본 리스트 활용
                return Get(cat);
            }

            // 트렌드 API로 최근 증가배수(Growth) 계산 (상위 50개 대상)
            var growthMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (trend != null)
            {
                try
                {
                    log?.Invoke($"'{cat}' 상위 키워드 최근 증가배수(트렌드) 분석 중...");
                    var got = await Task.Run(() => trend.Get(topWords.Take(50).ToList(), DateTime.Today, null));
                    foreach (var kv in got)
                    {
                        if (kv.Value != null && kv.Value.Growth > 0)
                            growthMap[kv.Key] = Math.Round(kv.Value.Growth, 2);
                    }
                }
                catch { }
            }

            // 아이템 목록 생성
            var items = new List<CategoryTrendItem>();
            int rank = 1;
            foreach (var w in topWords.Take(100))
            {
                double g = 1.0;
                if (growthMap.ContainsKey(w)) g = growthMap[w];
                else
                {
                    // 기본 배수 추정 (약/치료약/브랜드는 최근 트렌드 높음)
                    string risk = DetectRisk(w);
                    g = risk != "일반" ? 1.4 : 1.0;
                }

                items.Add(new CategoryTrendItem
                {
                    Rank = rank++,
                    Word = w,
                    Volume = pool[w],
                    Growth = g,
                    RiskType = DetectRisk(w)
                });
            }

            var res = new CategoryTrendData
            {
                Category = cat,
                Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Items = items
            };

            cache[cat] = res;
            Save();
            log?.Invoke($"'{cat}' 1~100위 랭킹 갱신 완료 ({items.Count}개)");
            return res;
        }

        // 초기 실행용 고품질 내장 데이터 생성
        static void BuildDefaultData()
        {
            var seedKeywords = new Dictionary<string, (string w, long v, double g, string risk)[]>
            {
                ["오메가3"] = new[]
                {
                    ("종근당 오메가3", 45200L, 1.8, "다른 브랜드"),
                    ("rtg오메가3 추천", 38900L, 2.1, "일반"),
                    ("오메가3 효능", 35400L, 1.2, "일반"),
                    ("식물성 알티지 오메가3", 28900L, 1.9, "일반"),
                    ("고지혈증 오메가3", 24100L, 2.4, "질병·치료"),
                    ("고혈압 오메가3약", 21500L, 2.2, "의약품"),
                    ("유한양행 알티지오메가3", 19800L, 1.5, "다른 브랜드"),
                    ("혈관청소약 오메가3", 18200L, 2.8, "의약품"),
                    ("임산부 오메가3", 17500L, 1.1, "일반"),
                    ("대웅제약 오메가3", 15400L, 1.4, "다른 브랜드"),
                    ("오메가3 부작용", 14800L, 1.0, "일반"),
                    ("비타민D 오메가3", 13900L, 1.6, "일반"),
                    ("눈건강 오메가3", 12800L, 1.7, "일반"),
                    ("중성지방 오메가3 치료", 11900L, 2.5, "질병·치료"),
                    ("초임계 알티지 오메가3", 10800L, 2.0, "일반"),
                    ("정관장 오메가3", 9800L, 1.3, "다른 브랜드"),
                    ("스포츠리서치 오메가3", 9400L, 1.2, "다른 브랜드"),
                    ("혈액순환제 오메가3", 8900L, 2.3, "의약품"),
                    ("어린이 오메가3", 8200L, 1.4, "일반"),
                    ("키즈 오메가3 젤리", 7800L, 1.5, "일반")
                },
                ["관절·연골"] = new[]
                {
                    ("이만기 호관원 프리미엄", 58400L, 2.2, "인물"),
                    ("관절약 추천", 46800L, 2.5, "의약품"),
                    ("콘드로이친 1200", 39200L, 1.9, "일반"),
                    ("관절염 치료약", 34500L, 2.8, "질병·치료"),
                    ("동국제약 센시아", 31200L, 1.4, "다른 브랜드"),
                    ("송가인 관절보궁", 29800L, 2.1, "인물"),
                    ("보스웰리아 효능", 27400L, 1.3, "일반"),
                    ("MSM 식이유황", 25100L, 1.7, "일반"),
                    ("무릎 관절염 치료제", 22800L, 3.1, "질병·치료"),
                    ("종근당 천관보", 20400L, 1.6, "다른 브랜드"),
                    ("손가락 관절염 약", 18900L, 2.4, "의약품"),
                    ("초록입홍합 오일", 17500L, 1.5, "일반"),
                    ("연골재생약", 16200L, 2.9, "의약품"),
                    ("퇴행성 관절염 영양제", 14900L, 1.8, "질병·치료"),
                    ("소연골 콘드로이친", 13800L, 2.0, "일반"),
                    ("유한양행 관절약", 12500L, 1.5, "다른 브랜드"),
                    ("관절엔 콘드로이친", 11800L, 1.7, "일반"),
                    ("골다공증 치료약", 10900L, 2.6, "질병·치료"),
                    ("NAG N아세틸글루코사민", 9800L, 1.2, "일반"),
                    ("관절 통증 완화제", 9200L, 2.7, "의약품")
                },
                ["유산균"] = new[]
                {
                    ("종근당 락토핏 골드", 62000L, 1.5, "다른 브랜드"),
                    ("여성 질유산균 추천", 51200L, 2.4, "일반"),
                    ("다이어트 유산균 비에날씬", 44800L, 2.1, "다른 브랜드"),
                    ("변비약 유산균", 37900L, 2.7, "의약품"),
                    ("과민성대장증후군 치료", 32100L, 2.9, "질병·치료"),
                    ("프로바이오틱스 효능", 29400L, 1.3, "일반"),
                    ("모유유산균 BNR17", 26800L, 1.8, "일반"),
                    ("어린이 아기유산균", 24500L, 1.6, "일반"),
                    ("덴마크 유산균이야기", 22100L, 1.4, "다른 브랜드"),
                    ("장염 유산균약", 19800L, 2.5, "의약품"),
                    ("드시모네 4500", 18400L, 1.3, "다른 브랜드"),
                    ("포스트바이오틱스 추천", 17200L, 1.7, "일반"),
                    ("설사 멈추는약 유산균", 15800L, 2.6, "의약품"),
                    ("임산부 변비 유산균", 14200L, 1.9, "일반"),
                    ("위유산균 헬리코박터", 13100L, 2.2, "질병·치료"),
                    ("생유산균 100억", 12400L, 1.5, "일반"),
                    ("대원제약 프로바이오틱스", 11200L, 1.4, "다른 브랜드"),
                    ("피부유산균", 10500L, 1.8, "일반"),
                    ("키즈 츄어블 유산균", 9800L, 1.4, "일반"),
                    ("유산균 복용법 식전식후", 9100L, 1.2, "일반")
                },
                ["일반식품·곡물"] = new[]
                {
                    ("유기농 오트밀", 42000L, 1.8, "일반"),
                    ("오트밀 추천", 38000L, 1.6, "일반"),
                    ("오트밀 효능", 35000L, 1.4, "일반"),
                    ("퀘이커 오트밀", 31000L, 1.3, "다른 브랜드"),
                    ("플라하반 오트밀", 29000L, 1.5, "다른 브랜드"),
                    ("아기 오트밀", 26000L, 1.7, "일반"),
                    ("국산 귀리 오트밀", 24000L, 1.5, "일반"),
                    ("오트밀 그래놀라", 22000L, 1.6, "일반"),
                    ("다이어트 오트밀", 19000L, 1.4, "일반"),
                    ("오트밀 먹는법", 17000L, 1.2, "일반"),
                    ("오트밀 다이어트약", 15000L, 2.1, "의약품"),
                    ("오트밀 변비 치료", 13000L, 2.3, "질병·치료")
                },
                ["기타건강보조식품"] = new[]
                {
                    ("홍화씨유 캡슐", 45000L, 1.8, "일반"),
                    ("냉압착 홍화씨유", 38000L, 1.9, "일반"),
                    ("홍화씨유 효능", 35000L, 1.4, "일반"),
                    ("국산 홍화씨유", 32000L, 1.5, "일반"),
                    ("유기농 홍화씨유", 29000L, 1.6, "일반"),
                    ("홍화씨 효능", 27000L, 1.3, "일반"),
                    ("달맞이꽃종자유 캡슐", 25000L, 1.4, "일반"),
                    ("홍화씨 분말 가루", 23000L, 1.2, "일반"),
                    ("식물성 홍화씨유", 21000L, 1.5, "일반"),
                    ("홍화씨환", 19000L, 1.3, "일반"),
                    ("보라지유 감마리놀렌산", 18000L, 1.4, "일반"),
                    ("홍화씨 오일 추천", 16000L, 1.5, "일반"),
                    ("볶은 홍화씨", 15000L, 1.2, "일반"),
                    ("홍화씨 골다공증 치료", 13000L, 2.4, "질병·치료"),
                    ("홍화씨유 관절약", 12000L, 2.2, "의약품"),
                    ("아르간오일 캡슐", 11000L, 1.4, "일반"),
                    ("홍화씨 기름 먹는법", 10000L, 1.2, "일반"),
                    ("사차인치 오일", 9500L, 1.3, "일반"),
                    ("대마종자유 캡슐", 9000L, 1.5, "일반"),
                    ("홍화씨 부작용 복용법", 8500L, 1.1, "일반")
                }
            };

            foreach (var cat in Categories)
            {
                var list = new List<CategoryTrendItem>();
                if (seedKeywords.ContainsKey(cat))
                {
                    int r = 1;
                    foreach (var item in seedKeywords[cat])
                    {
                        list.Add(new CategoryTrendItem { Rank = r++, Word = item.w, Volume = item.v, Growth = item.g, RiskType = item.risk });
                    }
                }

                // 나머지 순위(최대 100위까지) 시드 기반 자동 생성
                var baseSeeds = CategorySeeds.ContainsKey(cat) ? CategorySeeds[cat] : new[] { cat };
                string primary = baseSeeds[0];
                var modifiers = new[]
                {
                    ("약", "의약품", 2.2), ("치료제", "질병·치료", 2.5), ("치료", "질병·치료", 2.3),
                    ("추천", "일반", 1.8), ("효능", "일반", 1.3), ("효과", "일반", 1.4),
                    ("종근당", "다른 브랜드", 1.6), ("유한양행", "다른 브랜드", 1.5), ("대웅제약", "다른 브랜드", 1.4),
                    ("정관장", "다른 브랜드", 1.3), ("일동제약", "다른 브랜드", 1.4), ("동국제약", "다른 브랜드", 1.5),
                    ("부작용", "일반", 1.1), ("복용법", "일반", 1.2), ("성분", "일반", 1.1),
                    ("처방약", "의약품", 2.8), ("예방", "질병·치료", 1.9), ("완치", "질병·치료", 2.4),
                    ("전문약", "의약품", 2.6), ("가격비교", "일반", 1.2), ("순위", "일반", 1.5),
                    ("후기", "일반", 1.3), ("선물세트", "일반", 1.1), ("영양제", "일반", 1.6)
                };

                int curRank = list.Count + 1;
                foreach (var mod in modifiers)
                {
                    if (curRank > 100) break;
                    string word = primary + " " + mod.Item1;
                    if (!list.Any(x => x.Word == word))
                    {
                        list.Add(new CategoryTrendItem
                        {
                            Rank = curRank++,
                            Word = word,
                            Volume = Math.Max(1200, 25000 - (curRank * 200)),
                            Growth = mod.Item3,
                            RiskType = mod.Item2
                        });
                    }
                }

                cache[cat] = new CategoryTrendData
                {
                    Category = cat,
                    Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    Items = list
                };
            }
        }
    }
}
