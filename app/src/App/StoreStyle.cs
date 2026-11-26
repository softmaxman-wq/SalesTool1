using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SalesTool
{
    // 스토어 판매 기록(팝스스토어 2023~2026 주문 1,934건)에서 잘 팔린 상품명 틀로 네이버 상품명을 다시 짬 (지금은 유산균 상품만)
    //  잘 팔린 이름: "뇌유산균 루테리 락티스 균 공복유산균 프로바이오텍", "공복 혈당 유산균 Lactobacillus Plantarum"
    //  1) 용도를 좁힌 조합어(혈당유산균·뇌유산균·초등학생유산균)  2) 실제 균 이름(라벨에 있는 것만)  3) 영어 학명 1개
    //  4) 대상·상황 말  5) 변형 표기(프로바이오텍)  — 큰 검색어(장에좋은유산균·프로바이오틱스유산균) 나열·CFU·유해균 같은 말은 안 씀
    //  용량·개수·균수는 넣지 않음 (잘 팔린 이름에 거의 없음). "유산균"이 든 단어는 2개까지 (네이버 반복 경고)
    public static class StoreStyle
    {
        static string Key(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z가-힣]", "").ToLowerInvariant(); }

        // 상품명에 넣지 않는 말 (뜻 없는 말·잘린 말·큰 검색어 나열)
        public static readonly HashSet<string> Junk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "CFU", "유해균", "유래", "밸런스", "프로바이오틱", "프로", "식물성", "혼합유산균", "유산균생균", "캡슐유산균", "프로바이오유산균",
          "프로바이오틱스유산균", "유산균프로바이오틱스", "장에좋은유산균", "대장에좋은유산균", "장유산균", "유산균100억", "보장균수", "프리바이오틱", "프리바이오", "비피더스", "비피도" };

        // 균 이름: 라벨·상품명에 나온 말 → 상품명에 쓸 한글, 영어 학명
        static readonly string[][] Strains =
        {
            new[] { "람노서스|람노수스", "람노서스", "Lactobacillus Rhamnosus" },
            new[] { "가세리", "가세리", "Lactobacillus Gasseri" },
            new[] { "루테리", "루테리", "Lactobacillus Reuteri" },
            new[] { "플란타룸|플란타럼|플란타", "플란타룸", "Lactobacillus Plantarum" },
            new[] { "애시도필루스|아시도필루스|에시도필루스", "애시도필루스", "Lactobacillus Acidophilus" },
            new[] { "파라카제이|카제이", "카제이", "Lactobacillus Paracasei" },
            new[] { "퍼멘텀", "퍼멘텀", "Lactobacillus Fermentum" },
            new[] { "살리바리우스", "살리바리우스", "Lactobacillus Salivarius" },
            new[] { "불가리쿠스", "불가리쿠스", "Lactobacillus Bulgaricus" },
            new[] { "락티스", "락티스", "Bifidobacterium Lactis" },
            new[] { "롱검|롱굼", "롱검", "Bifidobacterium Longum" },
            new[] { "브레브|브레베", "브레브", "Bifidobacterium Breve" },
            new[] { "비피덤", "비피덤", "Bifidobacterium Bifidum" },
            new[] { "써모필러스|서모필러스", "써모필러스", "Streptococcus Thermophilus" },
            new[] { "엔테로코커스|페시움|페슘", "엔테로코커스", "Enterococcus Faecium" },
        };

        // 용도·대상: msgood 상품명에 있는 말 → 조합어 (상품명에서만 봄. 상세의 "어린이 손이 닿지 않는 곳" 같은 문구는 안 봄)
        static readonly string[][] Purposes =
        {
            new[] { "다이어트|슬림|체지방|가르시니아", "다이어트유산균", "뱃살" },
            new[] { "혈당|당케어|바나바", "혈당유산균", "식후" },
            new[] { "기억력|인지력|PS|두뇌", "뇌유산균", "수험생" },
            new[] { "갱년기", "갱년기유산균", "중년" },
            new[] { "우먼|여성", "여성유산균", "" },
            new[] { "키즈|어린이|아이", "어린이유산균", "초등학생" },
            new[] { "임산부|임신", "임산부유산균", "임신준비" },
            new[] { "위케어|위건강", "위유산균", "속편한" },
            new[] { "면역", "면역유산균", "" },
            new[] { "배변|쾌변", "쾌변유산균", "" },
            new[] { "김치", "김치유산균", "" },
            new[] { "모유", "모유유산균", "" },
            new[] { "차전자피", "차전자피유산균", "식이섬유" },
            new[] { "질유산균|질건강", "질유산균", "" },
            new[] { "구강|잇몸", "구강유산균", "입냄새" },
            new[] { "장용성", "장용성유산균", "" },
        };

        // 함께 든 영양소: msgood 상품명에 있을 때만
        static readonly string[][] Nutrients =
        {
            new[] { "아연", "아연" }, new[] { "셀렌|셀레늄", "셀레늄" }, new[] { @"비타민\s*D", "비타민D" }, new[] { @"비타민\s*C", "비타민C" },
            new[] { "칼슘", "칼슘" }, new[] { "마그네슘", "마그네슘" }, new[] { @"오메가\s*3", "오메가3" }, new[] { "콜라겐", "콜라겐" },
            new[] { "식이섬유", "식이섬유" },
        };
        static readonly string[][] PreFoods =
        {
            new[] { "프락토올리고당|FOS", "프락토올리고당" }, new[] { "갈락토올리고당|GOS", "갈락토올리고당" }, new[] { "이눌린", "이눌린" }, new[] { "치커리", "치커리" },
        };

        public static bool Applies(Job job) { return Pipeline.ForcedCatPath(job) != null; }

        // 혼자 쓰면 뜻이 약한 말 (사전 조합어 대장유산균에서 떼어 낸 대장·소장·대사, 글자 인식이 잘못 읽은 간장)
        public static readonly HashSet<string> Weak = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "대장", "소장", "대사", "간장", "당", "멀티", "입안", "예민한장", "뇌복합물", "장", "위", "질", "뇌" };

        // 유산균 상품에서 혼자 쓰면 다른 카테고리(다이어트·위 건강·혈당·두뇌 영양제)로 잡히는 말: 체지방감소·헬리코박터·바나바·지방분해제
        //  → 조합어(체지방감소유산균·헬리코박터유산균)로만 쓰고, 조합어가 안 되면 뺌. 라벨에 적힌 영양소(아연·셀레늄·프락토올리고당)는 그대로
        static readonly Regex DriftRe = new Regex("(체지방|지방|칼로리|탄수화물|다이어트|뱃살|술배|살빠|버닝|컷팅|커트|헬리코박터|헤리코박터|위건강|위점막|혈당|당케어|바나바|가르시니아|가르니시아|가르시아나|갱년기|여성호르몬|전두엽|두뇌|뇌건강|기억력|인지력|감초|글라브리딘|차전자피)");
        public static bool Drift(string w)
        {
            string k = Key(w);
            if (k.Length < 2 || Regex.IsMatch(k, "유산균|바이오틱")) return false;
            if (k.Length >= 3 && k.EndsWith("제")) return true;   // 지방분해제·위영양제·가르시니아보조제: 다른 상품 종류
            return DriftRe.IsMatch(k);
        }

        // body: 기존 규칙이 고른 상품명 단어(순서대로) — 자리 채움에만 씀
        public static void Apply(Job job, List<string> body, Func<string, bool> nameOk)
        {
            if (!Applies(job) || job.Names == null) return;
            string name = job.Name ?? "", ocr = job.OcrText ?? "", both = name + " " + ocr;
            string nameKey = Key(name);

            var purpose = new List<string>(); var persona = new List<string>();
            foreach (var p in Purposes)
                if (Regex.IsMatch(name, p[0], RegexOptions.IgnoreCase)) { if (!purpose.Contains(p[1])) purpose.Add(p[1]); if (p[2] != "" && !persona.Contains(p[2])) persona.Add(p[2]); }
            if (Regex.IsMatch(ocr, "공복") && !persona.Contains("공복")) persona.Add("공복");
            persona = persona.Where(x => !Drift(x)).ToList();
            // 묶음 상품(30포x2박스): 한 통짜리와 이름이 같지 않게 묶음 수를 넣음
            var boxM = Regex.Match(name, @"[xX×]\s*(\d+)\s*(박스|개|세트|통|병)");
            string boxWord = boxM.Success && int.Parse(boxM.Groups[1].Value) >= 2 ? boxM.Groups[1].Value + boxM.Groups[2].Value : null;
            if (Regex.IsMatch(name, @"\d+억")) { var m = Regex.Match(name, @"(\d+)억"); purpose.Add(m.Groups[1].Value + "억유산균"); }   // 상품명에 적힌 균수만

            bool pre = Regex.IsMatch(name, "프리바이오틱스") && !Regex.IsMatch(name, "프로바이오틱스|유산균");   // 유산균 먹이(프리바이오틱스) 상품
            // 상품명에 적힌 그 밖의 특징 (사실 그대로): 19종 → 19종유산균, 함께 든 영양소, 생유산균
            var extra = new List<string>();
            var jm = Regex.Match(name, @"(\d+)\s*종");
            if (jm.Success && int.Parse(jm.Groups[1].Value) >= 4) { string w = jm.Groups[1].Value + "종유산균"; if (purpose.Count == 0) purpose.Add(w); else purpose.Insert(1, w); }
            foreach (var n in Nutrients)
                if (Regex.IsMatch(name, n[0], RegexOptions.IgnoreCase) && !extra.Contains(n[1])) extra.Add(n[1]);
            foreach (var n in PreFoods)   // 유산균 먹이 성분은 라벨 글자에서도 봄
                if ((pre || n[1] == "프락토올리고당") && Regex.IsMatch(both, n[0]) && !extra.Contains(n[1])) extra.Add(n[1]);   // 유산균 상품엔 프락토올리고당만 (치커리 같은 말은 먹이 상품에만)
            if (Regex.IsMatch(name, "생유산균")) extra.Add("생유산균");

            var strainKo = new List<string>(); var strainEn = new List<string>();
            foreach (var s in Strains)
                if (Regex.IsMatch(both, s[0])) { strainKo.Add(s[1]); strainEn.Add(s[2]); }
            bool bifido = Regex.IsMatch(both, "비피더스|비피도");
            bool syn = Regex.IsMatch(both, "신바이오틱스");

            // 변형 표기: 손님이 실제로 치는 말 (프로바이오텍·신바이오텍·유산 균)
            var typo = new List<string>();
            if (pre) { typo.Add("프리바이오텍"); typo.Add("유산균먹이"); }
            else { typo.Add("프로바이오텍"); if (syn) typo.Add("신바이오텍"); }

            // 자리 채움: 기존 규칙이 고른 단어 중 뜻 없는 말·큰 검색어 나열·숫자 아닌 것
            var fill = (body ?? new List<string>()).Where(w => !Junk.Contains(w) && !Regex.IsMatch(w, @"\d") && Key(w).Length >= 2 && !Naming.IsInfoQuery(w) && !Naming.IsDisease(w) && !Regex.IsMatch(w, "고르는법|추천|순위|효능|부작용|먹는법|먹는시간") && !Weak.Contains(w) && !Drift(w) && (nameOk == null || nameOk(w)))
                                                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // 사장님 사전 말(공복유산균·저녁유산균·장용성100억유산균): 네이버는 "유산균"을 떼고 붙은 말만 (저녁 식전 공복 혈당 유산균 — 사장님 상품명 방식, 반복 경고도 피함)
            var bookSet = new HashSet<string>((job.Keywords ?? new List<KwRow>()).Where(r => r.FromBook && r.Excluded == null).Select(r => Key(r.Word)));
            var bookMods = new List<string>();
            foreach (var w in (body ?? new List<string>()).Where(w => bookSet.Contains(Key(w)) && (nameOk == null || nameOk(w))))
            {
                string k = Key(w);
                string mod = k.Contains("유산균") && k != "유산균" ? w.Replace("유산균", "").Replace(" ", "") : w;
                if (Key(mod).Length < 1 || Junk.Contains(mod) || Weak.Contains(mod) || Regex.IsMatch(Key(mod), "^(프로바이오|프로바이오틱스|프로바이오틱|생|유산균)$")) continue;
                if (Drift(mod)) { if (Key(w) == Key(mod)) continue; mod = w; }   // 체지방감소 → 체지방감소유산균 (유산균 없는 말이면 뺌)
                if (!bookMods.Any(x => Key(x) == Key(mod))) bookMods.Add(mod);
            }

            string seedWord = pre ? "프리바이오틱스" : (Regex.IsMatch(name, "프로바이오틱스") && !Regex.IsMatch(name, "유산균") ? "프로바이오틱스" : "유산균");
            string bifWord = bifido ? "비피더스균" : null;

            var used = new HashSet<string>();
            int codeRot = (job.Code ?? "").Sum(ch => (int)ch) % 5;
            var accounts = job.Names.Keys.Where(k => Regex.IsMatch(k, @"^n\d+_\d+$")).OrderBy(k => k).ToList();
            foreach (var k in accounts)
            {
                var m = Regex.Match(k, @"^n(\d+)_(\d+)$");
                int a = int.Parse(m.Groups[1].Value), v = int.Parse(m.Groups[2].Value);
                int rot = (a - 1) * 2 + (v - 1) + codeRot;   // 사업자·상품명마다 다른 조합 (상품마다도 시작을 달리해 비슷한 상품끼리 이름이 같지 않게)
                var parts = new List<string>();
                Func<List<string>, int, List<string>> R = (l, r) => l.Count == 0 ? l : l.Skip(r % l.Count).Concat(l.Take(r % l.Count)).ToList();
                var P = R(purpose, rot); var S = R(strainKo, rot); var E = R(strainEn, rot); var T = R(typo, rot); var Pe = R(persona, rot);

                // 1) 용도 조합어 (가장 앞) — 없으면 시작 단어
                if (P.Count > 0) parts.Add(P[0]); else parts.Add(seedWord);
                // 묶음 수(2박스·2개)는 이름에 넣지 않음 (사용자 요청, v1.7.4)
                // 사장님 사전 말을 바로 뒤에 4개까지 (계정마다 순서를 돌림)
                foreach (var bm in R(bookMods.Take(6).ToList(), rot).Where(x => !Key(parts[0]).Contains(Key(x))).Take(4).ToList())   // 앞 조합어에 든 말(혈당유산균의 혈당)은 빼고
                    if (!parts.Any(x => Key(x) == Key(bm))) parts.Add(bm);
                // 사업자마다 다른 짜임: 1=균 이름 중심, 2=대상·상황 중심, 3=영어·변형 표기 중심
                if (a % 3 == 1) { parts.AddRange(S.Take(2)); if (bifWord != null) parts.Add(bifWord); parts.AddRange(Pe.Take(1)); if (P.Count > 1) parts.Add(P[1]); }
                else if (a % 3 == 2) { parts.AddRange(Pe.Take(2)); if (P.Count > 1) parts.Add(P[1]); parts.AddRange(S.Take(1)); parts.AddRange(E.Take(1)); }
                else { parts.AddRange(E.Take(1)); parts.AddRange(T.Take(v == 1 ? 1 : 0)); parts.AddRange(S.Skip(1).Take(1).DefaultIfEmpty(S.FirstOrDefault()).Where(x => x != null)); parts.AddRange(Pe.Take(1)); if (bifWord != null) parts.Add(bifWord); }
                if (!parts.Any(w => Key(w) == Key(seedWord)) && parts.Count(w => Key(w).Contains("유산균")) < 2) parts.Add(seedWord);

                // 2) 상품명에 적힌 특징을 붙임 (34자까지), 그래도 30자 안 되면 기존 단어로 채움 (45자까지)
                foreach (var w in R(extra, rot))
                {
                    if (Len(parts) >= 34) break;
                    if (Len(parts) + 1 + w.Length > 45) continue;
                    parts.Add(w);
                }
                foreach (var w in R(fill, rot))
                {
                    if (Len(parts) >= 30) break;
                    if (Len(parts) + 1 + w.Length > 45) continue;
                    parts.Add(w);
                }
                // 같은 말 정리: 겹치는 단어, "유산균" 든 단어 2개까지 (네이버 반복 경고)
                var outW = new List<string>();
                foreach (var w in parts.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    if (outW.Any(o => Key(o) == Key(w))) continue;
                    if (Key(w).Contains("유산균") && outW.Count(o => Key(o).Contains("유산균")) >= 2) continue;
                    if (Len(outW) + 1 + w.Length > 50) continue;
                    if (Drift(w) || Weak.Contains(w)) continue;
                    outW.Add(w);
                }
                // 같은 사업자의 두 번째 상품명이나 앞 사업자 상품명과 똑같으면: 첫 단어 뒤 순서를 뒤집고, 그래도 같으면 남은 단어를 더함
                string res = string.Join(" ", outW);
                if (used.Contains(res) && outW.Count > 2)
                {
                    var tail = outW.Skip(1).ToList();
                    foreach (var cand in new[] { Enumerable.Reverse(tail).ToList() }.Concat(Enumerable.Range(1, tail.Count - 1).Select(r => tail.Skip(r).Concat(tail.Take(r)).ToList()))
                                                                               .Concat(Enumerable.Range(1, tail.Count - 1).Select(r => Enumerable.Reverse(tail.Skip(r).Concat(tail.Take(r)).ToList()).ToList())))
                    {
                        string t = string.Join(" ", outW.Take(1).Concat(cand));
                        if (!used.Contains(t)) { outW = outW.Take(1).Concat(cand).ToList(); res = t; break; }
                    }
                }
                if (used.Contains(res)) foreach (var w in fill.Concat(persona))
                    { if (outW.Any(o => Key(o) == Key(w)) || Len(outW) + 1 + w.Length > 50 || (Key(w).Contains("유산균") && outW.Count(o => Key(o).Contains("유산균")) >= 2)) continue; outW.Add(w); res = string.Join(" ", outW); if (!used.Contains(res)) break; }
                used.Add(res);
                job.Names[k] = res;
            }
            // 쿠팡 상품명은 그대로 두되 뜻 없는 말만 뺌
            foreach (var k in job.Names.Keys.Where(k => Regex.IsMatch(k, @"^c\d+$")).ToList())
                job.Names[k] = string.Join(" ", Regex.Split(job.Names[k] ?? "", @"\s+").Where(w => w.Length > 0 && !(Junk.Contains(w) && !Regex.IsMatch(w, "유산균"))));
        }

        static int Len(List<string> ws) { return string.Join(" ", ws).Length; }
    }
}
