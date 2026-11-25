using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SalesTool
{
    // 쿠팡 상품명: 쿠팡샵 판매 기록(2021-12~2026-07 주문 788건)에서 잘 팔린 틀로 길게 짬
    //  잘 팔린 이름: 중간값 65자·12단어, 대상(남성·여성·학생·어르신) 47%, 나이대 나열(10대 20대 30대) 35%, 영어 이름 40%
    //  예) "청소년기 영양제 중학생 고등학생 청소년 10대 20대 수능 시험 멀티 비타민 필수 미네랄 정 3개월분 내신 학생 공부"
    //  변형 표기(일부러 틀린 글자)는 넣지 않음: 쿠팡에선 아이템위너 묶임을 피하려던 것이라 검색에 필요 없음
    //  코스트코·약국·질병 이름은 자동으로 넣지 않음 (상표·제재 위험)
    public static class CoupangStyle
    {
        const int Target = 65, Max = 90;
        static string Key(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z가-힣]", "").ToLowerInvariant(); }

        // 대상: msgood 상품명에 있는 말 → 대상·나이대 묶음
        static readonly string[][] Personas =
        {
            new[] { "키즈|어린이|아이|유아|초등", "어린이 아이 키즈 초등학생 유아 성장기" },
            new[] { "청소년|수험생|학생|틴", "청소년 중학생 고등학생 수험생 10대 20대 학생" },
            new[] { "임산부|임신|산모|출산", "임산부 임신 산모 출산후 여성 20대 30대 40대" },
            new[] { "갱년기|중년", "중년 갱년기 여성 남성 40대 50대 60대" },
            new[] { "실버|시니어|어르신|노인|부모님", "어르신 부모님 노인 60대 70대 아버지 어머니" },
            new[] { "우먼|여성|여자|레이디", "여성 여자 20대 30대 40대 50대" },
            new[] { "맨|남성|남자", "남성 남자 20대 30대 40대 50대" },
        };
        const string AdultAll = "남자 남성 여성 여자 20대 30대 40대 50대";
        const string Family = "어린이 아이 어른 어르신 온가족";

        // 영어 이름: msgood 상품명·시작 단어에 원료가 있을 때만
        static readonly string[][] English =
        {
            new[] { "유산균|프로바이오틱스", "Probiotics" }, new[] { "루테인", "Lutein" }, new[] { "지아잔틴", "Zeaxanthin" },
            new[] { @"비타민\s*B|비타민비", "VitaminB" }, new[] { @"비타민\s*C", "VitaminC" }, new[] { @"비타민\s*D", "VitaminD" },
            new[] { @"오메가\s*3", "Omega3" }, new[] { "마그네슘", "Magnesium" }, new[] { "아연", "Zinc" }, new[] { "칼슘", "Calcium" },
            new[] { "밀크씨슬|밀크시슬|실리마린", "MilkThistle" }, new[] { "콜라겐", "Collagen" }, new[] { "프로폴리스", "Propolis" },
            new[] { "글루타치온", "Glutathione" }, new[] { "비오틴", "Biotin" }, new[] { "코엔자임|코큐텐", "CoQ10" },
            new[] { "홍삼", "RedGinseng" }, new[] { "쏘팔메토|소팔메토", "SawPalmetto" }, new[] { "크릴", "KrillOil" },
            new[] { "엠에스엠|MSM|식이유황", "MSM" }, new[] { "멀티비타민|종합비타민", "Multivitamin" }, new[] { "철분", "Iron" },
            new[] { "엽산", "FolicAcid" }, new[] { "가르시니아", "Garcinia" }, new[] { "히알루론산", "HyaluronicAcid" },
            new[] { "보스웰리아", "Boswellia" }, new[] { "아르기닌", "Arginine" }, new[] { "글루코사민", "Glucosamine" },
        };

        static bool IsPet(Job job) { return Regex.IsMatch((job.Name ?? "") + " " + (job.CatPath ?? ""), "강아지|고양이|반려|애견|애묘|펫"); }

        // body: 기존 규칙이 고른 단어(순서대로)
        public static void Apply(Job job, List<string> body, Func<string, bool> nameOk)
        {
            if (job.Names == null) return;
            var cKeys = job.Names.Keys.Where(k => Regex.IsMatch(k, @"^c\d+$")).OrderBy(k => k).ToList();
            if (cKeys.Count == 0) return;
            string name = job.Name ?? "", both = name + " " + (job.OcrText ?? "");
            string cat = job.CatPath ?? "";

            // 변형 표기(쿠팡에선 안 씀): 검색어 표에서 오타로 표시된 말, 유산균 틀의 프로바이오텍·신바이오텍·프리바이오텍
            var typos = new HashSet<string>((job.Keywords ?? new List<KwRow>()).Where(k => k.Typo).Select(k => Key(k.Word)));
            foreach (var t in new[] { "프로바이오텍", "신바이오텍", "프리바이오텍" }) typos.Add(Key(t));
            Func<string, bool> ok = w =>
            {
                string k = Key(w);
                if (k.Length < 2 || typos.Contains(k)) return false;
                if (StoreStyle.Junk.Contains(w)) return false;
                if (Regex.IsMatch(w, "코스트코|약국|약사|의사|병원")) return false;
                var eok = Regex.Match(w, @"(\d+)억"); if (eok.Success && !Regex.IsMatch(name, eok.Groups[1].Value + @"\s*억")) return false;   // 상품에 없는 균수(900억 상품의 100억)
                if (Naming.IsInfoQuery(w) || Naming.IsDisease(w)) return false;
                if (Regex.IsMatch(w, "고르는법|추천|순위|효능|부작용|먹는법|먹는시간|가격|최저가")) return false;
                return nameOk == null || nameOk(w);
            };

            // 대상·나이대 묶음
            string persona = null;
            if (!IsPet(job))
            {
                foreach (var p in Personas) if (Regex.IsMatch(name, p[0], RegexOptions.IgnoreCase)) { persona = p[1]; break; }
                if (persona == null && cat.StartsWith("식품>건강식품")) persona = AdultAll;
                else if (persona == null && cat.StartsWith("식품")) persona = Family;
            }
            var pw = persona == null ? new List<string>() : persona.Split(' ').ToList();

            var eng = English.Where(e => Regex.IsMatch(name + " " + (job.Seed ?? ""), e[0], RegexOptions.IgnoreCase))   // 상품명·시작 단어에 있는 원료만 (상세 원료표의 부원료는 안 봄)
                .Select(e => e[1]).Distinct().ToList();
            var months = Regex.Match(name, @"(\d+)\s*개월");
            string period = months.Success ? months.Groups[1].Value + "개월분" : null;

            // 유산균 상품: 네이버 상품명의 용도 조합어·균 이름·영어 학명(다이어트유산균·뱃살·Lactobacillus Plantarum)을 먼저 씀
            var fromNaver = StoreStyle.Applies(job) ? job.Names.Where(kv => kv.Key.StartsWith("n")).OrderBy(kv => kv.Key).SelectMany(kv => Regex.Split(kv.Value ?? "", @"\s+")).ToList() : new List<string>();
            var bodyW = fromNaver.Concat(body ?? new List<string>()).Where(ok).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var used = new HashSet<string>();
            int i = 0;
            foreach (var k in cKeys)
            {
                // 지금 쿠팡 이름(변형 표기 뺌)을 앞에 두고, 영어 이름 → 기존 단어 → 대상·나이대 → 기간 순으로 채움. 계정마다 순서를 바꿈
                var head = Regex.Split(job.Names[k] ?? "", @"\s+").Where(w => w.Length > 0 && ok(w)).ToList();
                var rest = bodyW.Skip(i * 3 % Math.Max(1, bodyW.Count)).Concat(bodyW.Take(i * 3 % Math.Max(1, bodyW.Count))).ToList();
                var e = eng.Skip(i % Math.Max(1, eng.Count)).Concat(eng.Take(i % Math.Max(1, eng.Count))).ToList();
                var parts = new List<string>(head);
                Action<string> add = w => { if (w == null || parts.Any(x => Key(x) == Key(w))) return; if ((string.Join(" ", parts) + " " + w).Length > Max) return; parts.Add(w); };
                if (fromNaver.Count > 0 && ok(fromNaver[0])) add(fromNaver[0]);   // 용도 조합어(다이어트유산균·혈당유산균)는 모든 계정에
                if (i % 3 == 1) { foreach (var w in pw) add(w); }
                foreach (var w in e.Take(2)) add(w);
                foreach (var w in rest) { if (string.Join(" ", parts).Length >= Target - (i % 3 == 1 ? 0 : 20)) break; add(w); }
                if (i % 3 != 1) foreach (var w in pw) add(w);
                add(period);
                foreach (var w in rest) { if (string.Join(" ", parts).Length >= Target) break; add(w); }
                string res = string.Join(" ", parts);
                if (used.Contains(res) && parts.Count > 3) { var t = parts.Skip(1).ToList(); t = t.Skip(2).Concat(t.Take(2)).ToList(); res = string.Join(" ", parts.Take(1).Concat(t)); }
                used.Add(res);
                job.Names[k] = res;
                i++;
            }
        }
    }
}
