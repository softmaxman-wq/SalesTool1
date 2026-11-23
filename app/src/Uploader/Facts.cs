using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ProductUploader
{
    // 상세 이미지 글자(OCR)에서 뽑은 상품 정보. 네이버 상품속성 / 쿠팡 검색필터를 채우는 데 씀
    public class ProductFacts
    {
        public int Count;          // 1개(통)당 정/캡슐/포 수 (예: 60)
        public string Unit;        // 정, 캡슐, 포, 환, ml ...
        public double Weight;      // 1개당 총 중량 g (예: 36)
        public double VolumeMl;    // 1개당 총 용량 ml
        public int TimesPerDay;    // 1일 섭취 횟수
        public int PerTime;        // 1회 섭취량
        public string Method;      // 물과 함께 / 씹어서 / 물에 섞어서 / 바로 음용
        public string Storage;     // 실온보관 / 냉장보관 / 냉동보관
        public string Package;     // 네이버 포장형태: 용기형 / 스틱형 / 개별PTP형
        public bool HealthFunctional; // 건강기능식품 (인정 문구가 있을 때만)
        public List<string> Nutrients = new List<string>();
        public List<string> Functions = new List<string>();

        public int Daily { get { return TimesPerDay > 0 && PerTime > 0 ? TimesPerDay * PerTime : 0; } }

        public string Summary()
        {
            var s = new List<string>();
            if (Count > 0) s.Add("1개 " + Count + Unit + (Weight > 0 ? " (" + Weight + "g)" : VolumeMl > 0 ? " (" + VolumeMl + "ml)" : ""));
            if (Daily > 0) s.Add("1일 " + TimesPerDay + "회 × " + PerTime + Unit);
            if (Method != null) s.Add(Method);
            if (Storage != null) s.Add(Storage);
            if (HealthFunctional) s.Add("건강기능식품");
            if (Functions.Count > 0) s.Add("기능: " + string.Join(", ", Functions));
            if (Nutrients.Count > 0) s.Add("원료: " + string.Join(", ", Nutrients));
            return s.Count == 0 ? "(읽은 정보 없음)" : string.Join(" · ", s);
        }

        static readonly string[][] NutrientPatterns =
        {
            new[] { "비타민A", @"비타민\s*A(?![a-z])|레티놀" }, new[] { "비타민D", @"비타민\s*D(?![a-z])" }, new[] { "비타민E", @"비타민\s*E(?![a-z])|토코페롤" },
            new[] { "비타민K", @"비타민\s*K(?![a-z])" }, new[] { "비타민B1", @"비타민\s*B\s*[1lIⅠ](?!\d)|티아민" }, new[] { "비타민B2", @"비타민\s*B\s*2|리보플라빈" },
            new[] { "비타민B6", @"비타민\s*B\s*6|피리독신" }, new[] { "비타민B12", @"비타민\s*B\s*12|시아노코발라민" }, new[] { "비타민C", @"비타민\s*C(?![a-z])|아스코르브산" },
            new[] { "베타카로틴", @"베타카로틴" }, new[] { "나이아신", @"나이아신|니코틴산" }, new[] { "판토텐산", @"판토텐산" }, new[] { "엽산", @"엽산" },
            new[] { "비오틴", @"비오틴" }, new[] { "칼슘", @"(?<!판토텐[가-힣]{0,2}\s?)(?<!인산|규산|스테아린산)칼슘" }, new[] { "마그네슘", @"(?<!스테아린[가-힣]{0,2}\s?)마그네슘" }, new[] { "철", @"(?<![가-힣])철(?![가-힣])|철분|푸마르산제일철" },
            new[] { "아연", @"아연" }, new[] { "구리", @"구리(?!스)" }, new[] { "셀레늄(셀렌)", @"셀레늄|셀렌" }, new[] { "요오드", @"요오드" }, new[] { "망간", @"망간" },
            new[] { "몰리브덴", @"몰리브덴" }, new[] { "칼륨", @"칼륨" }, new[] { "크롬", @"크롬" }, new[] { "식이섬유", @"식이섬유|난소화성말토덱스트린|차전자피" },
        };

        static readonly string[][] FunctionPatterns =
        {
            new[] { "눈건강", @"눈\s*건강|루테인|지아잔틴" }, new[] { "면역력", @"면역" }, new[] { "간건강", @"간\s*건강|밀크씨슬|실리마린" },
            new[] { "피로회복", @"피로" }, new[] { "장건강", @"장\s*건강|유산균|프로바이오틱스|배변" }, new[] { "체지방 감소", @"체지방|가르시니아" },
            new[] { "혈행개선", @"혈행|오메가|EPA|DHA" }, new[] { "피부건강", @"피부|콜라겐|히알루론" }, new[] { "관절/뼈건강", @"관절|뼈|연골|MSM|글루코사민" },
            new[] { "갱년기", @"갱년기" }, new[] { "기억력 개선", @"기억력|인지" }, new[] { "긴장완화", @"긴장|스트레스" }, new[] { "수면질 개선", @"수면|숙면" },
            new[] { "혈당조절", @"혈당" }, new[] { "혈압조절", @"혈압" }, new[] { "항산화", @"항산화" }, new[] { "전립선", @"전립선|쏘팔메토" },
            new[] { "콜레스테롤", @"콜레스테롤" }, new[] { "성장발육", @"성장|발육" },
        };

        public string Name, Text;   // msgood 상품명, 상세 글자(한 줄로)

        // 주요 기능성: 상품명의 주원료로 정함 (파워쏘팔메토 → 전립선). 없으면 상세에서 찾은 첫 기능성
        public string MainFunction(IList<string> choices)
        {
            foreach (var p in FunctionPatterns)
                if (choices.Contains(p[0]) && Regex.IsMatch(Name ?? "", p[1], RegexOptions.IgnoreCase)) return p[0];
            return Functions.FirstOrDefault(choices.Contains);
        }

        // 섭취대상: 쏘팔메토는 성인 남성용 (섭취 시 주의사항 "성인 남성만 섭취하십시오") — 사용자 기준
        public string Target(IList<string> choices)
        {
            if (Regex.IsMatch((Name ?? "") + " " + (Text ?? ""), "쏘팔메토|소팔메토") && choices.Contains("성인남성")) return "성인남성";
            return new[] { "성인남녀", "성인남녀공용", "성인" }.FirstOrDefault(choices.Contains);
        }

        // 함량(범위형 속성: 로르산 115mg): 상세에서 "이름 숫자mg"
        public double Amount(string ingredient)
        {
            var m = Regex.Match(Text ?? "", Regex.Escape(ingredient) + @"\s*(\d+(?:\.\d+)?)\s*mg", RegexOptions.IgnoreCase);
            double v; return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        public static ProductFacts Extract(string ocr, string name, string volume)
        {
            var f = new ProductFacts();
            f.Name = name; f.Text = Regex.Replace((ocr ?? "") + " " + (name ?? ""), @"\s+", " ");
            string t = (ocr ?? "") + "\n" + (name ?? "");
            string flat = Regex.Replace(t, @"\s+", " ");

            // 내용량: "36 g (600 mg x 60정)"
            var m = Regex.Match(flat, @"(\d+(?:\.\d+)?)\s*(g|kg|ml|mL|L)\s*\(\s*[\d.]+\s*(?:mg|g|ml)\s*[xX×*]\s*(\d+)\s*(정|캡슐|포|환|알|병|봉|개)");
            if (m.Success)
            {
                double w = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string u = m.Groups[2].Value.ToLowerInvariant();
                if (u == "kg") f.Weight = w * 1000; else if (u == "g") f.Weight = w; else if (u == "l") f.VolumeMl = w * 1000; else f.VolumeMl = w;
                f.Count = int.Parse(m.Groups[3].Value);
                f.Unit = m.Groups[4].Value;
            }
            // 용량 칸(예: 60정)이 있으면 그걸 우선
            var v = Regex.Match(volume ?? "", @"(\d+)\s*(정|캡슐|포|환|알|ml|mL|g)");
            if (v.Success)
            {
                if (v.Groups[2].Value == "g") { if (f.Weight == 0) f.Weight = double.Parse(v.Groups[1].Value); }
                else if (v.Groups[2].Value.ToLowerInvariant() == "ml") { if (f.VolumeMl == 0) f.VolumeMl = double.Parse(v.Groups[1].Value); if (f.Unit == null) f.Unit = "ml"; }
                else { f.Count = int.Parse(v.Groups[1].Value); f.Unit = v.Groups[2].Value; }
            }

            // 섭취량: "1일 1회, 1회 2정"
            m = Regex.Match(flat, @"1\s*일\s*(\d+)\s*회\s*[,.]?\s*1\s*회\s*(\d+)\s*(정|캡슐|포|환|알|ml|mL|스푼|병)");
            if (m.Success) { f.TimesPerDay = int.Parse(m.Groups[1].Value); f.PerTime = int.Parse(m.Groups[2].Value); if (f.Unit == null) f.Unit = m.Groups[3].Value; }
            else
            {
                m = Regex.Match(flat, @"(?:1\s*일|하루)\s*(?:단\s*)?(\d+)\s*(정|캡슐|포|환|알)");
                if (m.Success) { f.TimesPerDay = 1; f.PerTime = int.Parse(m.Groups[1].Value); }
            }
            // 횟수만 적힌 경우 (구운토마토환: "1일 2회, 20~25환씩")
            if (f.TimesPerDay == 0) { m = Regex.Match(flat, @"(?:1\s*일|하루)\s*(\d)\s*회"); if (m.Success) f.TimesPerDay = int.Parse(m.Groups[1].Value); }
            // 상품 형태: 이름에 환·정·캡슐이 있으면 (용량이 g 로만 적힌 환 상품)
            if (f.Unit == null) { var um = Regex.Match(name ?? "", @"(환|캡슐|정)(?![가-힣])"); if (um.Success) f.Unit = um.Groups[1].Value; }
            // 포장형태: 포·스틱 = 스틱형, PTP = 개별PTP형, 정·캡슐·환·알 = 용기형(통)
            if (Regex.IsMatch(t, "PTP", RegexOptions.IgnoreCase)) f.Package = "개별PTP형";
            else if (f.Unit == "포" || Regex.IsMatch(name ?? "", @"\d+\s*포|스틱")) f.Package = "스틱형";
            else if (f.Unit == "정" || f.Unit == "캡슐" || f.Unit == "환" || f.Unit == "알") f.Package = "용기형";

            if (Regex.IsMatch(flat, @"씹어")) f.Method = "씹어서";
            else if (Regex.IsMatch(flat, @"물에\s*(타|섞|희석)")) f.Method = "물에 섞어서";
            else if (Regex.IsMatch(flat, @"물과\s*함께|물과함께")) f.Method = "물과 함께";
            else if (f.Unit == "ml" || Regex.IsMatch(flat, @"그대로\s*(드|섭취|음용)")) f.Method = "바로 음용";
            // 영양제(정·캡슐·환·알)는 대개 물과 함께 (사용자 기준)
            if (f.Method == null && (f.Unit == "정" || f.Unit == "캡슐" || f.Unit == "환" || f.Unit == "알")) f.Method = "물과 함께";

            if (Regex.IsMatch(flat, @"냉동\s*보관")) f.Storage = "냉동보관";
            else if (Regex.IsMatch(flat, @"냉장\s*보관")) f.Storage = "냉장보관";
            else if (Regex.IsMatch(flat, @"서늘한|실온|상온|직사광선|광선을|건냉소|건정소|건조한\s*곳")) f.Storage = "실온보관";

            f.HealthFunctional = Regex.IsMatch(flat, @"기능성\s*(내용|원료)|식약처\s*인정|식품의약품안전처\s*인정|건강기능식품\s*(인증|마크|도안)")
                                 && !Regex.IsMatch(flat, @"일반가공식품|일반\s*식품");

            foreach (var p in NutrientPatterns) if (Regex.IsMatch(flat, p[1])) f.Nutrients.Add(p[0]);
            foreach (var p in FunctionPatterns) if (Regex.IsMatch(flat, p[1])) f.Functions.Add(p[0]);
            return f;
        }

        // 섭취 일수 → "1개월분", "45일분" 등. 보기 목록에서 가장 알맞은 것
        public string Duration(int packs, IList<string> choices)
        {
            if (Count <= 0 || Daily <= 0) return null;
            int days = Count * packs / Daily;
            if (days <= 0) return null;
            if (days % 30 == 0 && choices.Contains(days / 30 + "개월분")) return days / 30 + "개월분";
            if (choices.Contains(days + "일분")) return days + "일분";
            int months = (int)Math.Round(days / 30.0);
            if (months >= 1 && choices.Contains(months + "개월분")) return months + "개월분";
            return null;
        }

        public string ProductType(IList<string> choices, bool coupang)
        {
            string u = Unit ?? "";
            string[] wants = u == "정" || u == "알" ? (coupang ? new[] { "정제", "태블릿" } : new[] { "정", "정제" })
                           : u == "캡슐" ? new[] { "캡슐", "소프트젤" }
                           : u == "환" ? (coupang ? new[] { "정제", "태블릿" } : new[] { "환" })
                           : u == "포" ? (coupang ? new[] { "가루", "사셰" } : new[] { "분말" })
                           : u.ToLowerInvariant() == "ml" ? (coupang ? new[] { "액체", "액" } : new[] { "액상" })
                           : new string[0];
            return wants.FirstOrDefault(choices.Contains);
        }
    }
}
