using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SalesTool
{
    public static class Naming
    {
        // 상품명 꾸밈말 (시작 단어로 쓰지 않음)
        static readonly HashSet<string> Decor = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "베스트", "프리미엄", "플러스", "맥스", "골드", "슈퍼", "리얼", "더", "오리지널", "뉴", "new", "best", "premium", "plus", "max", "gold",
            "1+1", "대용량", "고함량", "순수", "100%", "국내산", "국산", "정품", "본사", "공식", "신제품", "리뉴얼", "special", "스페셜", "데일리", "daily"
        };

        // 숫자·용량·개수 표현 (60정, 600mg, 1+1, 3개 등)
        static readonly Regex NotKeyword = new Regex(@"^[\d.,+xX×*/\-]+(mg|g|kg|ml|mL|l|L|정|개|개입|포|캡슐|입|통|박스|병|봉|일분|개월분|개월|회분|매|ea|EA|p|P|x|팩|세트|박|구|샷|회|알|잔|환)?[\d+xX]*$");

        // 단위 (mg, ml, kcal …) 는 키워드가 아님
        static readonly Regex UnitOnly = new Regex(@"^(\d*)(mg|g|kg|ml|l|mcg|ug|iu|kcal|cal|cc|ea|p|pcs|정|포|개|개입|캡슐|병|박스|입|매|ml당|g당|일|회|번|알|스푼|개월분|일분|세트|봉|팩|통)$", RegexOptions.IgnoreCase);

        // 누구나 아는 건강식품·식품 브랜드/회사 (네이버 브랜드 목록에 안 보여도 뺌)
        public static readonly string[] KnownBrands =
        {
            "정관장", "한국인삼공사", "종근당", "종근당건강", "녹십자", "녹십자웰빙", "GC녹십자", "일양약품", "유한양행", "동국제약", "대웅제약", "대웅생명과학", "한미약품", "광동", "광동제약",
            "일동제약", "일동후디스", "보령", "안국건강", "고려은단", "뉴트리원", "뉴트리코어", "닥터린", "세노비스", "센트룸", "솔가", "나우푸드", "GNC", "애터미", "뉴스킨", "암웨이", "뉴트리라이트",
            "코스트코", "커클랜드", "이마트", "노브랜드", "피코크", "쿠팡", "곰곰", "탐사", "한삼인", "천호식품", "김정문알로에", "풀무원", "CJ", "비비고", "오뚜기", "동원", "청정원", "대상",
            "농심", "롯데", "매일유업", "남양", "빙그레", "정식품", "에버콜라겐", "뉴트리디데이", "함소아", "바이오가이아", "락토핏", "프롬바이오", "내츄럴플러스", "오쏘몰", "얼라이브",
            "네이처메이드", "라이프익스텐션", "닥터스베스트", "오플닷컴", "아이허브", "브랜뉴데이", "한손한끼", "에스더포뮬러", "덴마크", "서울우유", "동아제약", "삼진제약", "경남제약",
            "휴온스", "JW중외제약", "유한", "애터미", "에버", "셀트리온", "콜마", "한국야쿠르트", "hy", "비타민하우스", "GNM자연의품격", "자연의품격", "뉴트리케이", "메디트리", "팔레오", "지엠팜", "헬로네이처", "마켓컬리", "여에스더", "닥터에스더", "에스더몰"
        };

        // 회사 이름 모양 (…제약, …양행, …웰빙, …닷컴 등)
        static readonly Regex CompanyLike = new Regex(@"(제약|약품|양행|웰빙|헬스케어|생명과학|바이오텍|닷컴|코리아|팜|푸드|식품|유업|홀딩스|글로벌|인터내셔널|몰)$");

        // 회사 이름처럼 끝나도 보통 말인 것
        static readonly string[] NotCompany = { "건강식품", "기능식품", "가공식품", "발효식품", "수입식품", "자연식품", "유기농식품", "일반식품", "즉석식품", "다이어트식품", "슈퍼푸드", "홍삼식품", "영양식품", "간편식품", "어린이식품" };

        public static bool IsKnownBrand(string w)
        {
            string k = Key(w);
            if (k.Length < 2) return false;
            if (KnownBrands.Any(b => { string bk = Key(b); return bk.Length >= 2 && (k == bk || k.Contains(bk)); })) return true;
            return CompanyLike.IsMatch(k) && k.Length >= 4 && !NotCompany.Any(n => k.EndsWith(Key(n)));
        }

        // 판매 문구 (상품을 나타내는 말이 아님)
        static readonly HashSet<string> SalesWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "네이버", "쿠팡", "단독", "공식", "정품", "특가", "할인", "무료배송", "증정", "이벤트", "세일", "최저가", "인기", "추천", "베스트", "신상", "당일발송", "당일출고",
            "발휘", "국내", "본사", "직영", "골라담기", "골라담는", "냉장고", "정성", "담은", "담아", "함유", "가득", "엄선", "엄선한", "듬뿍", "그대로", "선물하기", "프리미엄", "고급", "명품", "인기상품", "필수템", "꿀템", "판매", "구매", "리뷰", "후기", "사은품", "행사", "기획", "한정", "대박", "핫딜", "best", "new", "sale", "hot", "단계", "1단계", "2단계", "3단계"
        };

        // 꾸밈말·주장하는 말(유기농·국산·프리미엄 …)인지: 주 키워드로 고르지 않음
        public static bool IsClaimOrDecor(string w)
        {
            string k = Key(w);
            if (Decor.Contains(w) || SalesWords.Contains(w) || FormWords.Contains(w)) return true;
            // 성분 이름(오메가3·비타민D·마그네슘)은 주 키워드가 될 수 있음. 유기농·국산·대용량·나라 이름 같은 주장하는 말만 뺌
            string[] attr = { "유기농", "유기가공", "오가닉", "국산", "국내산", "무농약", "무설탕", "무가당", "슈가프리", "저당", "저칼로리", "비건", "글루텐프리", "무첨가",
                              "대용량", "고함량", "제로", "선물세트", "선물용", "선물", "여성", "여성용", "남성", "남성용", "호주", "쿠바", "미국", "캐나다", "독일", "일본",
                              "뉴질랜드", "이탈리아", "노르웨이", "페루", "프랑스", "스페인", "중국", "인도", "미국산", "호주산", "캐나다산" };
            return attr.Any(x => Key(x) == k);
        }

        public static bool IsRisky(string w) { string k = Key(w); return Risky.Any(r => k.Contains(r)); }

        // 편의점 CU나 무의미한 2글자 쪼개기 방지용 금지어 목록 및 영양성분/화학분석 수치어 배제
        // 주의: '식물성캡슐', '냉압착', '홍화씨오일' 등 정상 복합 키워드는 살리고 오직 무의미한 조각 단어 단독어만 배제
        public static readonly HashSet<string> BannedFragments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "씨유", "자유", "화씨", "화씨유", "물성",
            "올레산", "지방산", "불포화지방산", "포화지방산", "리놀산", "리놀레산", "홍화씨앗", "산가", "과산화물가", "콜레스테롤"
        };

        public static bool IsCandidate(string w)
        {
            string c = SmallKeyword.SearchAd.Clean(w);
            if (c.Length < 2) return false;
            if (BannedFragments.Contains(c) || BannedFragments.Contains(w)) return false;
            if (IsMedicine(w)) return false;
            if (UnitOnly.IsMatch(c) || SalesWords.Contains(c)) return false;
            // 인증 표시(해썹·HACCP·GMP·식약처인증)는 상품 이름이 아님
            if (Regex.IsMatch(c, "^(해썹|햅썹|haccp|gmp|cgmp|식약처|식약처인증|.{0,6}인증)$", RegexOptions.IgnoreCase)) return false;
            if (NotKeyword.IsMatch(w) || NotKeyword.IsMatch(c)) return false;
            if (Regex.IsMatch(c, @"\d+(mg|g|ml|정|포|캡슐|개|환|kcal)", RegexOptions.IgnoreCase)) return false;
            if (!Regex.IsMatch(c, "[가-힣A-Za-z]")) return false;
            return true;
        }

        static string Key(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z가-힣]", "").ToLowerInvariant(); }

        static readonly Regex Measure = new Regex(@"^[\d.,+xX×]*|[\d.,+xX×]+\s*(mg|g|kg|ml|l|iu|mcg|정|포|캡슐|개입|개|환|병|박스|입|매|일분|개월분|통|봉)?([xX×]\d+)?$", RegexOptions.IgnoreCase);

        // msgood 상품명에서 시작 단어 후보 (Item1 = 상품명 단어 그대로, Item2 = 붙어 있는 말의 앞·뒤 부분)
        //  - 브랜드·숫자·용량·꾸밈말은 뺌 ("마시는마그네슘14포" → "마시는마그네슘")
        //  - msgood 에 브랜드가 없으면 첫 단어는 대개 브랜드라서 뺌 (다른 후보가 있을 때)
        public static Tuple<List<string>, List<string>> SeedCandidates(string name, string brand)
        {
            string bk = Key(brand);
            var full = new List<string>();
            var digitTail = new Dictionary<string, string>();   // 오메가3 처럼 끝에 숫자 하나가 붙은 이름
            int firstFrom = -1;
            // 띄어쓰기뿐 아니라 + & , / 괄호로도 나눔 (캐나다네이처오메가3+비타민D2000IU → 캐나다네이처오메가3 / 비타민D2000IU)
            var tokens = Regex.Split(name ?? "", @"[\s\[\]\(\)/,+&]+").Where(t => t.Length > 0).ToList();
            for (int i = 0; i < tokens.Count; i++)
            {
                // 한글(+영문 한 글자, 예: 비타민D) 앞부분만: 오트밀6종세트50gx12포 → 오트밀, 비타민D2000IU → 비타민D, 오메가3 → 오메가(+3)
                // (와사비잎추출물S플러스 의 S 는 뒤 말 "S플러스"의 일부라 붙이지 않음)
                var lead = Regex.Match(tokens[i], @"^([가-힣]{2,}(?:[A-Za-z](?![A-Za-z가-힣]))?)(\d(?!\d))?");
                string w = SmallKeyword.SearchAd.Clean(lead.Success ? lead.Groups[1].Value : Measure.Replace(tokens[i], ""));
                if (w.Length < 2 || Decor.Contains(w) || !IsCandidate(w)) continue;
                if (bk.Length >= 2 && (Key(w) == bk || Key(w).Contains(bk) || bk.Contains(Key(w)))) continue;
                if (IsKnownBrand(w)) continue;
                if (!Regex.IsMatch(w, "[가-힣]")) continue;  // 시작 단어는 한글
                if (full.Contains(w)) continue;
                full.Add(w);
                if (i == 0) firstFrom = full.Count - 1;
                if (lead.Success && lead.Groups[2].Success) digitTail[w] = lead.Groups[2].Value;
            }
            // msgood 에 브랜드가 없으면 첫 단어는 대개 만든 곳 이름이라 뺌 (단 다른 후보가 있을 때만: 멜라뉴잇 400mgx60정 은 그대로)
            // (캐나다네이처오메가3 처럼 6자 이상 붙은 첫 단어는 회사+상품이라 두고, 뒤쪽 상품 부분(오메가3)을 씀)
            if (bk.Length < 2 && firstFrom == 0 && full.Count > 1 && Regex.Match(full[0], "^[가-힣]+").Value.Length < 6) full.RemoveAt(0);
            var parts = new List<string>();
            foreach (var w in full)
            {
                string h = Regex.Match(w, "^[가-힣]+").Value;   // 한글 부분
                if (h.Length < 3) continue;
                // 앞·뒤·가운데 부분 모두 (발효흑삼고 → 발효, 흑삼, 흑삼고, 삼고 …)
                for (int len = 2; len <= h.Length; len++)
                    for (int st = 0; st + len <= h.Length; st++)
                    {
                        string part = h.Substring(st, len);
                        if (part == w) continue;
                        if (BannedFragments.Contains(part)) continue;
                        if (!full.Contains(part) && !parts.Contains(part) && !Decor.Contains(part)) parts.Add(part);
                        string dt;   // 끝부분이면 숫자도 붙인 것 (캐나다네이처오메가3 → 오메가3)
                        if (st + len == h.Length && h == w && digitTail.TryGetValue(w, out dt) && !parts.Contains(part + dt)) parts.Add(part + dt);
                    }
            }
            return Tuple.Create(full, parts);
        }

        // 네이버 상품명: 같은 말(토마토)이 든 단어가 3개 이상이면 앞의 2개만 남김 (시작 단어가 든 단어는 꼭 남김)
        // 네이버 검색 최적화 검사가 "유의어 포함 반복된 단어가 다수"로 경고하는 것을 미리 막음
        public static string LimitRepeats(string name, string seed)
        {
            var words = Regex.Split(name ?? "", @"\s+").Where(w => w.Length > 0).ToList();
            string sk = Key(seed);
            for (bool changed = true; changed; )
            {
                changed = false;
                foreach (var root in words.Select(Key).Where(r => r.Length >= 2).Distinct().OrderBy(r => r.Length).ToList())
                {
                    var with = words.Where(w => Key(w).Contains(root)).ToList();
                    if (with.Count <= 2) continue;
                    // 남길 2개: 시작 단어 그 자체 → 그다음 앞쪽부터
                    var keep = with.Where(w => Key(w) == sk).Concat(with).Distinct().Take(2).ToList();
                    words = words.Where(w => !with.Contains(w) || keep.Contains(w)).ToList();
                    changed = true; break;
                }
            }
            return string.Join(" ", words);
        }

        // 카테고리 찾을 검색어: 상품명 맨 끝 단어(대개 상품 종류: 발효흑삼고, 젖산마그네슘, 오트밀)와 그 뒷부분(흑삼고)
        // 상품 종류 단어: 보통 상품명 맨 끝 단어. 단 "오메가3+비타민D" 처럼 + 로 묶였으면 + 앞이 주 상품
        public static string MainTypeWord(string name, string brand)
        {
            string n = name ?? "";
            int plus = n.IndexOf('+');
            if (plus > 0)
            {
                var left = SeedCandidates(n.Substring(0, plus), brand).Item1;
                if (left.Count > 0) return left[left.Count - 1];
            }
            var full = SeedCandidates(n, brand).Item1;
            // 맨 끝이 용도·꾸밈 말(다이어트·건강·선물세트)이면 그 앞 단어가 상품 종류 (쾌변포유 차전자피 다이어트 → 차전자피)
            while (full.Count > 1 && PurposeTail.Contains(Key(full[full.Count - 1]))) full.RemoveAt(full.Count - 1);
            return full.Count > 0 ? full[full.Count - 1] : null;
        }
        static readonly HashSet<string> PurposeTail = new HashSet<string> { "다이어트", "다이어트식품", "건강", "건강식품", "선물", "선물세트", "세트", "기획", "기획세트", "이벤트", "대용량", "슬림" };

        public static List<string> TypeQueries(string name, string brand)
        {
            var list = new List<string>();
            string last = MainTypeWord(name, brand);
            if (last == null) return list;
            list.Add(last);
            return list;
        }

        // 상품 종류 단어의 뒷부분 후보 (발효흑삼고 → 효흑삼고, 흑삼고, 삼고 …). 실제로 검색되는 말인지는 검색량으로 고름
        public static List<string> TypeSuffixes(string type)
        {
            var list = new List<string>();
            if (type == null || !Regex.IsMatch(type, "^[가-힣]+$")) return list;
            for (int k = type.Length - 1; k >= 3; k--) list.Add(type.Substring(type.Length - k));
            return list;
        }

        // msgood 상품명에서 브랜드·용량을 뺀 핵심 (네이버 쇼핑에서 카테고리를 찾을 때 검색어)
        public static string CoreName(string name, string brand)
        {
            var sc = SeedCandidates(name, brand);
            return string.Join(" ", sc.Item1);
        }

        // 자주 틀리는 글자 (젖↔젓, 슘↔숨, 네↔내 …)
        static readonly string[][] TypoPairs =
        {
            new[] { "젖", "젓" }, new[] { "슘", "숨" }, new[] { "네", "내" }, new[] { "애", "에" }, new[] { "웨", "외" }, new[] { "왜", "외" }, new[] { "궤", "괘" },
            new[] { "됬", "됐" }, new[] { "쥬", "주" }, new[] { "츄", "추" }, new[] { "쉐", "셰" }, new[] { "쏘", "소" }, new[] { "빠", "바" }, new[] { "띠", "티" },
            new[] { "샷", "샸" }, new[] { "콜", "꼴" }, new[] { "렌", "랜" }, new[] { "멘", "맨" }, new[] { "센", "샌" }, new[] { "벤", "밴" }, new[] { "펩", "팹" }
        };

        // 오타인지: 같은 길이, 다른 글자 1~2개가 모두 흔히 틀리는 짝(젖/젓)이거나 받침만 다른 글자(슘/슝)
        public static bool IsTypoOf(string word, string seed)
        {
            string w = Key(word), s = Key(seed);
            if (w.Length != s.Length || w == s || w.Length < 3) return false;
            int diffs = 0;
            for (int i = 0; i < w.Length; i++)
            {
                if (w[i] == s[i]) continue;
                diffs++;
                bool pair = TypoPairs.Any(p => (p[0] == w[i].ToString() && p[1] == s[i].ToString()) || (p[1] == w[i].ToString() && p[0] == s[i].ToString()))
                            || VowelSwaps(s[i]).Contains(w[i]);
                bool sameStart = w[i] >= '가' && w[i] <= '힣' && s[i] >= '가' && s[i] <= '힣' && (w[i] - '가') / 28 == (s[i] - '가') / 28; // 초성·중성 같고 받침만 다름
                if (!pair && !sameStart) return false;
            }
            return diffs >= 1 && diffs <= 2;
        }

        // 시작 단어의 흔한 오타 (한두 글자 바꾼 것). 검색량은 따로 확인
        // 모음만 바꾼 글자 (ㅔ↔ㅐ, ㅖ↔ㅔ, ㅒ↔ㅖ): 사람들이 가장 많이 틀리는 것
        static IEnumerable<char> VowelSwaps(char c)
        {
            if (c < '가' || c > '힣') yield break;
            int code = c - '가', cho = code / 588, jung = (code % 588) / 28, jong = code % 28;
            // 중성 번호: ㅐ=1, ㅒ=3, ㅔ=5, ㅖ=7
            int[][] swaps = { new[] { 1, 5 }, new[] { 5, 1 }, new[] { 7, 5 }, new[] { 5, 7 }, new[] { 3, 7 }, new[] { 7, 3 } };
            foreach (var sw in swaps) if (jung == sw[0]) yield return (char)('가' + cho * 588 + sw[1] * 28 + jong);
        }

        public static List<string> TypoVariants(string seed)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrEmpty(seed)) return new List<string>();
            var one = new List<string>();
            for (int i = 0; i < seed.Length; i++)
            {
                foreach (var p in TypoPairs)
                    for (int k = 0; k < 2; k++)
                        if (seed[i].ToString() == p[k]) one.Add(seed.Substring(0, i) + p[1 - k] + seed.Substring(i + 1));
                foreach (var v in VowelSwaps(seed[i])) one.Add(seed.Substring(0, i) + v + seed.Substring(i + 1));
            }
            foreach (var o in one) { set.Add(o); for (int i = 0; i < o.Length; i++) foreach (var p in TypoPairs) for (int k = 0; k < 2; k++) if (o[i].ToString() == p[k]) set.Add(o.Substring(0, i) + p[1 - k] + o.Substring(i + 1)); }
            set.Remove(seed);
            return set.Take(30).ToList();
        }

        // 상품명에 넣을 키워드의 최소 월간검색량 (PC + 모바일)
        public const long MinVolume = 20;
        public const long RestMinVolume = 100;   // 비율이 나빠 자리 채우기로 들어가는 말의 최소 월 검색량

        // 상세 페이지 글자에 그 말이 나온 횟수 (글자 인식이 흑을 혹으로 자주 읽어서 둘 다 셈)
        // 상세 페이지에 영어·성분 이름으로만 적힌 경우 (Omega-3, EPA·DHA → 오메가3)
        static readonly string[][] Aliases =
        {
            new[] { "omega3", "오메가3" }, new[] { "omega", "오메가" }, new[] { "epa", "오메가3" }, new[] { "dha", "오메가3" }, new[] { "vitamin", "비타민" },
            new[] { "lutein", "루테인" }, new[] { "collagen", "콜라겐" }, new[] { "magnesium", "마그네슘" }, new[] { "probiotics", "프로바이오틱스" },
            new[] { "zinc", "아연" }, new[] { "calcium", "칼슘" }, new[] { "melatonin", "멜라토닌" }, new[] { "policosanol", "폴리코사놀" }, new[] { "selenium", "셀렌" }
        };

        public static int Occ(string ocr, string word)
        {
            string t = Key(ocr), w = Key(word);
            foreach (var al in Aliases) t = t.Replace(al[0], al[1]);
            if (w.Length < 2 || t.Length == 0) return 0;
            int n = 0;
            foreach (var v in new[] { w, w.Replace("흑", "혹") }.Distinct())
                for (int i = t.IndexOf(v, StringComparison.Ordinal); i >= 0; i = t.IndexOf(v, i + v.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        // 상세 페이지에 나오는 일반적인 말 (키워드 아님): 표시사항·알레르기·안내 문구
        static readonly HashSet<string> DetailStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "건강기능식품", "기능성", "영양성분", "영양정보", "기준치", "영양성분기준치", "원재료", "원재료명", "섭취", "섭취량", "섭취방법", "제품", "제품명", "함유", "원료", "부원료", "인증",
            "식약처", "식품의약품안전처", "주의사항", "보관", "보관방법", "유통기한", "소비기한", "제조", "제조사", "제조원", "판매원", "유통전문판매원", "고객", "소비자", "분쟁", "교환", "반품",
            "공정거래위원회", "공정거래위원회고시", "알레르기", "대두", "난류", "땅콩", "새우", "돼지고기", "복숭아", "우유", "고등어", "토마토", "쇠고기", "닭고기", "오징어", "조개류",
            "호두", "메밀", "하루", "간편", "간편하게", "함께", "필요", "도움", "정상적인", "개선", "감소", "증가", "유지", "건강", "식품", "식품의", "유형", "품목보고", "번호", "내용량",
            "총내용량", "열량", "나트륨", "탄수화물", "당류", "지방", "트랜스지방", "포화지방", "단백질", "칼로리", "직사광선", "서늘한", "개봉", "가급적", "시일", "제품은",
            "올레산", "지방산", "불포화지방산", "리놀산", "리놀레산", "홍화씨앗", "씨앗", "종자", "산가", "과산화물가", "콜레스테롤",
            "이상", "이하", "대한", "통해", "위해", "위한", "가능", "경우", "사용", "확인", "있습니다", "있는", "합니다", "드시기", "바랍니다", "하십시오", "보관하십시오", "섭취하십시오",
            "관리", "과정", "고형분", "기능식품", "건강기능", "시설", "의거", "신고", "부정", "해결", "빠른", "하세요", "따라", "포장재질", "곡류가공품", "아황산류", "포만감", "가볍게",
            "충청북도", "충청북", "경기도", "서울시", "삼성면", "음성군", "주소", "고객센터", "상담", "문의", "택배", "배송", "출고", "정성", "품질", "엄격", "안심", "믿을", "선택",
            "제품명", "식품유형", "품목", "보고번호", "제조일", "제조일자", "원산지", "용량", "중량", "포장", "개별포장", "섭취시", "주의", "알레르기유발", "혼입", "가능성"
        };

        // 상세 페이지의 제조원·판매원·수입원 등에 적힌 회사 이름
        public static HashSet<string> CompanyWords(string ocr)
        {
            var set = new HashSet<string>();
            foreach (Match m in Regex.Matches(ocr ?? "", @"(제조원|판매원|제조사|수입원|수입사|유통전문판매원|제조업체|수입업체|판매업체|제조회사|판매처|브랜드)[^가-힣A-Za-z]{0,4}([^\n]{0,40})"))
                foreach (Match w in Regex.Matches(m.Groups[2].Value, @"[가-힣A-Za-z]{2,}"))
                {
                    string k = Key(w.Value);
                    if (k.Length >= 2 && !DetailStop.Contains(w.Value) && k != "주식회사" && k != "주" && !Countries.Contains(w.Value)) set.Add(k);
                }
            return set;
        }

        public static bool IsDetailStop(string w) { return DetailStop.Contains(w) || w == "원물" || w == "추출분말" || w == "추출물"; }

        // 원산지 표시 (인도산·국산·미국산): 원료 원산지라서 상품명 채우는 말로 안 씀
        public static bool IsOrigin(string w)
        {
            string k = Key(w);
            return k.EndsWith("산") && k.Length >= 2 && (Countries.Contains(k.Substring(0, k.Length - 1)) || k == "국산" || k == "국내산" || k == "수입산");
        }

        static readonly HashSet<string> Countries = new HashSet<string>
        {
            "캐나다", "미국", "호주", "뉴질랜드", "독일", "일본", "중국", "프랑스", "이탈리아", "스페인", "노르웨이", "덴마크", "페루", "칠레", "영국", "스위스", "네덜란드",
            "인도", "베트남", "태국", "필리핀", "인도네시아", "폴란드", "쿠바", "그리스", "터키", "한국", "대한민국", "국내", "국산", "수입", "수입산"
        };

        // 같은 것을 다르게 부르는 말 (상세에 하나만 있어도 나온 것으로 봄)
        static readonly string[][] Synonyms =
        {
            new[] { "피쉬오일", "피시오일", "어유", "정제어유", "연어유", "멸치유", "생선오일", "fishoil" },
            new[] { "오메가3", "omega3", "epa", "dha" }, new[] { "비타민d", "비타민d3", "vitamind" }, new[] { "연질캡슐", "소프트젤", "softgel" },
            new[] { "셀렌", "셀레늄", "selenium" }, new[] { "아연", "징크", "zinc" }, new[] { "면역", "면역력", "면역기능" }, new[] { "마그네슘", "magnesium" },
            new[] { "유산균", "프로바이오틱스", "probiotics" }, new[] { "콜라겐", "collagen" }, new[] { "루테인", "lutein" },
        };

        // 기능·효과를 말하는 말 (식약처 기능성 표현 등). 상세 페이지에서 바로 키워드로 가져올 수 있는 것은 이런 말뿐
        static readonly string[] FunctionTerms =
        {
            "체지방", "다이어트", "장건강", "배변", "혈행", "혈중", "콜레스테롤", "중성지방", "혈당", "혈압", "면역", "피부", "보습", "주름", "눈건강", "눈피로", "황반",
            "관절", "연골", "뼈건강", "골다공증", "간건강", "간기능", "기억력", "인지", "수면", "숙면", "피로", "활력", "에너지", "항산화", "갱년기", "월경", "생리",
            "전립선", "배뇨", "근력", "근육", "운동", "체력", "모발", "탈모", "손톱", "치아", "잇몸", "위건강", "소화", "장운동", "유산균증식", "유해균", "신경", "긴장",
            "스트레스", "집중", "성장", "키성장", "칼슘흡수", "정상적인", "식욕", "포만감", "디톡스", "붓기", "부기", "순환", "냉증", "목건강", "기관지", "호흡기"
        };

        public static bool IsFunctionWord(string w)
        {
            string k = Key(w);
            return (k.Length >= 3 ? FunctionTerms.Any(t => k.Contains(Key(t))) : FunctionTerms.Any(t => Key(t) == k)) && !DetailStop.Contains(w);
        }

        // 상세 페이지의 핵심인지: 상세에 시작 단어의 절반 이상 나온 말 (체지방·다이어트·장건강). 붙은 말은 앞 세 글자로도 봄 (체지방감소 → 체지방)
        public static bool IsCore(string ocr, string word, int seedOcc)
        {
            if (string.IsNullOrEmpty(ocr) || IsClaimOrDecor(word) || Key(word).Length < 2 || DetailStop.Contains(word)) return false;
            double t = Math.Max(3, seedOcc * 0.5);
            // 두 글자 말은 따로 쓰인 것만 셈 (귀리 60%·귀리는 O, 가르시니아 속의 가르 X)
            if (Key(word).Length == 2)
                return Regex.Matches(ocr, "(?<![가-힣])" + Regex.Escape(word) + "(?![가-힣]|(?:은|는|이|가|을|를|과|와|의|로|으로|에|도|만))").Count
                     + Regex.Matches(ocr, "(?<![가-힣])" + Regex.Escape(word) + "(?=은|는|이|가|을|를|과|와|의|로|으로|에|도|만)").Count >= t * 0.5;
            int occ = Occ(ocr, word);
            if (occ >= t) return true;
            string k = Key(word);
            return k.Length >= 4 && occ >= 3 && Occ(ocr, k.Substring(0, 3)) >= t;
        }

        // 상세 페이지 글자에서 키워드 후보: 따로 쓰인 말이 2번 이상 (뒤에 붙은 조사는 뗌)
        public static List<Tuple<string, int>> DetailWords(string ocr)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(ocr ?? "", @"[가-힣A-Za-z0-9]{2,12}"))
            {
                string w = Regex.Replace(m.Value, "(은|는|이|가|을|를|과|와|의|로|으로|에|에서|도|만|이며|이고|입니다)$", "");
                // 글자 인식이 흑을 혹으로 잘못 읽은 말(발효혹삼)은 뺌
                if (w.Contains("혹")) continue;
                if (w.Length < 2 || !Regex.IsMatch(w, "[가-힣]") || DetailStop.Contains(w) || !IsCandidate(w) || InfoWords.Any(x => Key(w).Contains(Key(x)))) continue;
                int c; counts.TryGetValue(w, out c); counts[w] = c + 1;
            }
            foreach (Match m in Regex.Matches(ocr ?? "", @"([가-힣]{2,6})\s*(개선|감소|건강|케어|증진|강화|유지|보호|완화)"))
            {
                string w = m.Groups[1].Value + m.Groups[2].Value;
                if (!IsFunctionWord(w)) continue;
                int c; counts.TryGetValue(w, out c); counts[w] = c + 1;
            }
            return counts.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).Take(60).Select(kv => Tuple.Create(kv.Key, kv.Value)).ToList();
        }

        // 쓸 키워드 순서: 조건(상품총수량 < 배율×검색량)에 맞는 것 먼저 (고른 기준대로), 그다음 1페이지에 2번 이상 나온 단어 (검색량 순)
        public static List<KwRow> Ordered(IEnumerable<KwRow> rows, string mode, double maxRatio, Learned learned)
        {
            // 카테고리 1페이지 상품명 중 2개 이상에 나온 단어만 (한 번만 나온 코스트코·편스토랑 같은 말은 뺌)
            // 그리고 이 상품의 상품명·상세에 따로 나온 말이거나, 1페이지 40개 중 10개 이상에 나온 말 (녹용처럼 몇 개 상품에만 있는 다른 원료는 뺌)
            // 상세 페이지에서 찾은 키워드(2번 이상, 검색량 300 이상)도 씀
            // 카테고리 확인을 한 초안이면: 이 상품 카테고리에서 검색되는 말만 (비타민D·가르시니아처럼 다른 카테고리 말은 뺌)
            bool strict = rows.Any(r => r.CatChecked);
            var usable = rows.Where(r => r.Excluded == null && r.TagReason == null && r.Volume >= MinVolume && IsCandidate(r.Word) && NameBlockReason(r) == null
                                         // 상세 페이지 검증: 이 상품 상세 페이지·상품명에 나온 말만 (6년근·보라지유처럼 상세에 없는 말은 안 씀)
                                         && (r.Typo || r.Mentioned)
                                         // 카테고리: 이 상품 카테고리에서 검색되는 말만. 검색해 보니 다른 카테고리(20% 미만)로 나오는 말은
                                         // 상세에 많이 나와도 안 씀 (유산균의 체지방·오트밀의 치아씨앗). msgood 상품명에 있는 말(슬림다이어트)만 예외
                                         && (r.Typo || r.InName || !r.OtherCategory)
                                         && (r.Typo || !strict || r.Core || r.InName || r.Function || (r.InPage >= 5 && r.Mentioned) || (r.CatChecked && r.CatShare >= 0.2) || (!r.CatChecked && !r.OtherCategory))
                                         // 카테고리 상품명에 2번 이상(두 글자 말은 3번 이상) 나온 말. 상세 안내 문구(관리·과정·고형분 …)는 안 씀
                                         && (r.Typo || r.Core || r.Function || (r.InPage >= 2 && (Key(r.Word).Length >= 3 || r.InPage >= 3))
                                             // 1페이지에 한 번만 나와도, 이 상품에 있는 말이고 검색하면 거의 이 카테고리만 나오는 말(Zinc)은 씀
                                             || (r.Mentioned && r.CatChecked && r.CatShare >= 0.8))
                                         && (r.Typo || !DetailStop.Contains(r.Word))
                                         // 상세 이미지 글자에서만 나온 말(1페이지 상품명·msgood 상품명에 없음)은 글자 인식이 잘못 읽은 말일 수 있음
                                         // (인지력 → 인지리). 실제로 많이 검색되고 파는 상품도 있는 말만
                                         && (r.Typo || r.InPage > 0 || r.InName || (r.Volume >= 100 && r.Total >= 300))).ToList();
            // 급상승 키워드(최근 검색이 확 늘어난 말)는 각 묶음 안에서 앞으로. "상승 순"이면 늘어난 정도 순
            Func<IEnumerable<KwRow>, IEnumerable<KwRow>> sort = xs =>
                mode == Orders.Growth ? xs.OrderByDescending(r => r.Rising).ThenByDescending(r => r.Volume >= 300 ? r.Growth : -1).ThenByDescending(r => r.Volume)
                : mode == Orders.Ratio ? xs.OrderByDescending(r => r.Rising).ThenBy(r => r.Ratio).ThenByDescending(r => r.Volume)
                : mode == Orders.Total ? xs.OrderByDescending(r => r.Rising).ThenBy(r => r.Total < 0 ? long.MaxValue : r.Total).ThenByDescending(r => r.Volume)
                : xs.OrderByDescending(r => r.Rising).ThenByDescending(r => r.Volume);
            var small = sort(usable.Where(r => r.Small(maxRatio))).ToList();
            // 나머지(비율이 나쁜 말): 검색량 100 이상만 (보장균수 30 같은 말로 자리를 채우지 않음). 시작 단어 묶음 핵심어·오타는 예외
            // "박사의"처럼 조사로 끝나는 말은 뺌
            var rest = usable.Where(r => !r.Small(maxRatio) && (r.Volume >= RestMinVolume || r.Core || r.Typo) && !Regex.IsMatch(r.Word, "[가-힣](의|을|를|에|와|과|로)$"))
                             .OrderByDescending(r => r.Rising).ThenByDescending(r => r.InPage >= 2 && r.FromDetail).ThenByDescending(r => r.Volume).ToList();
            // 시작 단어 오타(젓산마그네숨)는 맨 앞쪽에
            var typos = usable.Where(r => r.Typo).OrderByDescending(r => r.Volume).ToList();
            var all = typos.Concat(small.Concat(rest).Where(r => !r.Typo)).ToList();
            // 직접 넣었던 단어는 앞으로
            var pref = all.Where(r => learned.IsPreferred(r.Word)).ToList();
            // 팔린 상품의 상품명에 있던 단어는 그다음으로 (판매 실적 학습)
            var sold = all.Where(r => !pref.Contains(r) && !r.Typo && learned.SoldScore(r.Word) > 0).OrderByDescending(r => learned.SoldScore(r.Word)).ToList();
            var head = typos.Where(r => !pref.Contains(r)).ToList();
            return pref.Concat(head).Concat(sold.Where(r => !head.Contains(r))).Concat(all.Where(r => !pref.Contains(r) && !head.Contains(r) && !sold.Contains(r))).ToList();
        }

        // 상품명에 넣지 않는 말 (쿠팡 검색어로는 보냄): 정보를 찾는 검색어, 질병 이름, 검색하면 다른 카테고리가 주로 나오는 말
        static readonly Regex InfoQueryRe = new Regex("(방법|법$|만들기|만드는|만들|레시피|음식$|음식들|과일$|고르는|고르기|먹는|먹을때|복용|섭취법|효능|효과|부작용|추천|가격|후기|순위|차이|뜻$|하는법|좋은$|좋은음식|높이는|낮추는|할때$|일때$|날때$)");
        static readonly string[] DiseaseWords = { "변비", "설사", "pms", "월경전증후군", "생리통", "생리불순", "당뇨", "고혈압", "저혈압", "고지혈", "콜레스테롤높", "비만", "탈모", "불면증", "우울", "치매", "관절염", "골다공증", "암", "염증", "아토피", "비염", "역류성", "위염", "장염", "갑상선", "통풍", "빈혈", "감기", "독감", "질염", "방광염", "전립선", "갱년기장애", "치질", "수족냉증" };
        public static bool IsInfoQuery(string w) { return InfoQueryRe.IsMatch(Key(w)); }
        public static bool IsDisease(string w) { string k = Key(w); return DiseaseWords.Any(d => d == "암" ? Regex.IsMatch(k, "(^|[가-힣])암$") : k.Contains(d)); }
        public static string NameBlockReason(KwRow r)
        {
            if (r.Typo) return null;
            if (IsInfoQuery(r.Word)) return "정보성 검색어 (쿠팡 검색어로)";
            if (IsDisease(r.Word)) return "질병 이름 (상품명에 안 씀)";
            if (r.CatChecked && r.CatShare >= 0 && r.CatShare < 0.5) return "검색하면 다른 카테고리가 주로 나옴 (" + (int)Math.Round(r.CatShare * 100) + "%)";
            return null;
        }

        // 상품명에 쓰면 위험한 말 (영유아·임산부 대상으로 오인) → 쿠팡 검색어로만
        static readonly string[] Risky = { "아기", "유아", "영아", "영유아", "어린이", "키즈", "신생아", "임산부", "임신", "수유", "이유식", "천연" };

        // 쿠팡 검색어에도 넣지 않을 다른 용도 제품 (상세에 없으면): 질유산균·구강유산균 …
        public static readonly string[] OtherUse = { "질유산균", "여성유산균", "질건강", "구강유산균", "구강", "어린이유산균", "키즈유산균", "아기유산균", "유아유산균", "잇몸" };
        // (IsRisky: 위험 단어인지)

        // 상품에 적혀 있어야만 쓸 수 있는 말 (같은 뜻끼리 묶음). 상품명·상세에 없으면 쿠팡 검색어로만
        static readonly string[][] Claims =
        {
            new[] { "유기농", "유기가공", "오가닉", "organic" }, new[] { "국산", "국내산" }, new[] { "무농약" }, new[] { "무설탕", "무가당", "슈가프리" },
            new[] { "저당" }, new[] { "저칼로리", "로우칼로리" }, new[] { "비건" }, new[] { "글루텐프리" }, new[] { "무첨가" },
            new[] { "파우더", "가루", "분말" }, new[] { "액상" }, new[] { "마시는", "드링크", "음료", "쉐이크", "셰이크", "쉐이크", "셰이크" }, new[] { "스틱" }, new[] { "캡슐" }, new[] { "알약", "정제" }, new[] { "젤리" },
            new[] { "대용량" }, new[] { "고함량" }, new[] { "미국산" }, new[] { "호주산" }, new[] { "캐나다산" }, new[] { "핀란드산" }, new[] { "아일랜드산" },
            new[] { "제로" }, new[] { "단백질", "프로틴" },
            new[] { "식물성" }, new[] { "리포좀", "리포조말" }, new[] { "알티지", "rtg" }, new[] { "초임계" }, new[] { "트리플" }, new[] { "저분자" }, new[] { "액상" }, new[] { "발효" },
            new[] { "호주" }, new[] { "쿠바" }, new[] { "미국" }, new[] { "캐나다" }, new[] { "독일" }, new[] { "일본" }, new[] { "뉴질랜드" }, new[] { "이탈리아" },
            new[] { "노르웨이" }, new[] { "페루" }, new[] { "프랑스" }, new[] { "덴마크산" }, new[] { "스페인" }, new[] { "중국" }, new[] { "인도" },
            new[] { "선물세트", "선물용", "선물" }, new[] { "여성", "여성용", "여자" }, new[] { "남성", "남성용", "남자" },
            new[] { "포스트바이오틱스" }, new[] { "프리바이오틱스" }, new[] { "신바이오틱스" }, new[] { "생유산균" }, new[] { "모유유래" },
            new[] { "비타민a" }, new[] { "비타민b" }, new[] { "비타민c" }, new[] { "비타민d" }, new[] { "비타민e" }, new[] { "비타민k" }, new[] { "아연" }, new[] { "셀레늄", "셀렌" },
            new[] { "칼슘" }, new[] { "마그네슘" }, new[] { "철분" }, new[] { "엽산" }, new[] { "오메가3" }, new[] { "루테인" }, new[] { "코엔자임", "코큐텐" }, new[] { "콜라겐" },
            new[] { "히알루론산" }, new[] { "밀크씨슬" }, new[] { "프로폴리스" }, new[] { "녹용" }, new[] { "홍삼" }, new[] { "옥타코사놀" }, new[] { "쏘팔메토" }, new[] { "구강유산균", "구강" }, new[] { "질유산균", "여성유산균", "질건강", "여성질" }, new[] { "잇몸" }, new[] { "어린이유산균", "키즈유산균" }, new[] { "시리얼" }, new[] { "그래놀라" }, new[] { "포리지", "porridge" },
        };

        // 모양·형태를 나타내는 말 (상세 페이지에 그 형태라고 나와야 씀)
        static readonly string[] FormWords = { "파우더", "가루", "분말", "액상", "마시는", "드링크", "음료", "스틱", "캡슐", "알약", "정제", "젤리", "시리얼", "그래놀라", "포리지", "porridge" };

        // 상품명·상세 글자에 그 말이 따로 쓰였는지 (요거트분말·녹차가루처럼 다른 말에 붙은 것, 미국산·태국산 속의 '국산', (국내산) 같은 원료 원산지는 아님)
        static bool Mentioned(string text, string word)
        {
            string w = Regex.Escape(word);
            return Regex.IsMatch(text ?? "", "(?<![가-힣A-Za-z(])" + w, RegexOptions.IgnoreCase);
        }

        // 상품명에 못 쓰는 단어면 그 이유. name = msgood 상품명, detail = 상세 이미지 글자
        // 상품명·상세에 그 말이 따로 쓰였는지 (말차바나나·치아씨앗 속의 바나나·치아는 아님. 뒤에 조사는 붙어도 됨)
        // 영양정보 표에 나오는 이름: 표 속 "콜레스테롤 0g"은 이 상품의 주장이 아님 → 뒤에 숫자가 붙은 것은 세지 않음
        static readonly string[] NutritionLabels = { "콜레스테롤", "나트륨", "탄수화물", "당류", "지방", "포화지방", "트랜스지방", "단백질", "열량" };

        // 같은 뜻인지 (셀렌 = 셀레늄)
        public static bool SameMeaning(string a, string b)
        {
            string ka = Key(a), kb = Key(b);
            return ka == kb || Synonyms.Any(g => g.Contains(ka) && g.Contains(kb));
        }

        public static bool StrictMention(string name, string detail, string word)
        {
            if (NutritionLabels.Contains(word))
                return Regex.Matches(detail ?? "", Regex.Escape(word) + @"(?!\s*[0-9.]+\s*(g|mg|kcal|%))(?!\s*(" + string.Join("|", NutritionLabels) + "))").Count >= 2 || Key(name).Contains(Key(word));
            var syn = Synonyms.FirstOrDefault(g => g.Contains(Key(word)));
            if (syn != null && syn.Any(x => x != Key(word) && Key(detail).Contains(x))) return true;
            // 네 글자 이상 붙은 말은 띄어쓰기만 달라도 같은 말 (체지방감소 = "체지방 감소")
            if (Key(word).Length >= 4 && Key(detail).Contains(Key(word))) return true;
            string w = Regex.Escape(word);
            string pat = "(?<![가-힣A-Za-z0-9])" + w + "(?=$|[^가-힣A-Za-z]|은|는|이|가|을|를|과|와|의|로|으로|에|도|만|입니다|이며|이고)";
            return Regex.IsMatch(name ?? "", pat, RegexOptions.IgnoreCase) || Regex.IsMatch(detail ?? "", pat, RegexOptions.IgnoreCase)
                || Key(name).Contains(Key(word));
        }

        // 쿠팡 검색어 거르기: 이 상품과 형태·성분·수량·주장이 다른 검색어면 그 이유
        //  (아연 젤리·아기 아연 액상 / 비타민d 아연·마그네슘 멜라토닌 / 레몬생강즙 30포·솔가 아연 50mg / 유기농 베르가못)
        static readonly string[][] TagForms =
        {
            new[] { "젤리", "구미" }, new[] { "액상", "드롭", "드랍" }, new[] { "시럽" }, new[] { "스틱" }, new[] { "즙" }, new[] { "진액" },
            new[] { "파우더", "가루", "분말" }, new[] { "츄어블", "씹어먹는" }, new[] { "드링크", "음료", "마시는", "앰플" }, new[] { "스프레이" },
            new[] { "캡슐" }, new[] { "알약", "정제" }, new[] { "패치" }, new[] { "사탕", "캔디" },
        };
        static readonly string[][] TagExtra =
        {
            new[] { "다이어트" }, new[] { "락토페린" }, new[] { "아르기닌" }, new[] { "테아닌" }, new[] { "트립토판" }, new[] { "도라지유" }, new[] { "보라지유" },
            new[] { "비오틴" }, new[] { "크릴" }, new[] { "글루코사민" }, new[] { "msm" }, new[] { "키토산" }, new[] { "가르시니아" }, new[] { "카테킨" },
            new[] { "모로오렌지" }, new[] { "커큐민", "강황" }, new[] { "홍국" }, new[] { "나토키나제" }, new[] { "오메가369", "오메가6", "오메가9" }, new[] { "셀륨", "셀레늄", "셀렌" },
            new[] { "원액" }, new[] { "녹용" }, new[] { "윤옥고" }, new[] { "흑도라지" }, new[] { "비타민" }, new[] { "멀티비타민", "종합비타민" }, new[] { "마카" }, new[] { "아쉬와간다" },
        };
        static readonly string[] TagFreeClaims = { "시리얼", "그래놀라", "포리지", "porridge" };   // 오트밀 그래놀라처럼 사람들이 함께 찾는 말 (막지 않음)
        static readonly Regex CountUnit = new Regex(@"^(개|포|정|캡슐|병|박스|입|봉|팩|알|매|통|세트|box|ea)$");

        public static string TagFilterReason(string tag, string seed, string name, string detail)
        {
            string text = (name ?? "") + " " + (detail ?? "");
            string tk = Key(tag), sk = Key(seed), all = Key(text);
            if (IsMedicine(tag)) return "의약품 오해 표현";
            string rest = sk.Length > 0 ? tk.Replace(sk, " ") : tk;
            Func<string, bool> inSeed = c => sk.Contains(Key(c));
            // 따로 쓰인 말만 (산화마그네슘·요거트분말·녹차가루 속의 마그네슘·분말·가루는 이 상품의 성분·형태가 아님)
            Func<string, bool> said = c => Mentioned(text, c) || (Synonyms.FirstOrDefault(x => x.Contains(Key(c))) ?? new string[0]).Any(x => Mentioned(text, x));

            // 1. 형태 (상세·상품명에 그 형태가 나와야): 젤리·액상·시럽·스틱·진액 …
            foreach (var g in TagForms)
            {
                if (!g.Any(c => rest.Contains(Key(c)))) continue;
                if (g.Any(inSeed)) continue;
                bool ok = g[0].Length == 1 ? g.Any(c => Regex.IsMatch(text, "(?<![가-힣])" + Regex.Escape(c) + "(?![가-힣])") || (sk.EndsWith(c)))
                                           : g.Any(said);
                if (!ok) return "다른 형태";
            }
            // 2. 다른 성분·주장 (비타민d 아연·마그네슘 멜라토닌·유기농·리포좀·미국 …)
            foreach (var g in Claims.Concat(TagExtra))
            {
                if (g.Any(c => TagFreeClaims.Contains(c)) || g.Any(c => FormWords.Contains(c))) continue;
                if (!g.Any(c => rest.Contains(Key(c)))) continue;
                if (g.Any(inSeed)) continue;
                // "비타민"은 비타민d·비타민마을처럼 붙은 말이 따로 있으므로 단독으로 쓴 것만
                if (g[0] == "비타민" && !Regex.IsMatch(tag, "비타민(?![가-힣])", RegexOptions.IgnoreCase)) continue;
                if (!g.Any(said)) return "상품에 없는 말";
            }
            // 3. 숫자 (다른 수량·용량: 30포·50mg·폴리코사놀5·오메가369)
            var nameNums = new HashSet<string>(Regex.Matches(Key(name), @"\d+").Cast<Match>().Select(m => m.Value));
            foreach (Match m in Regex.Matches(rest, @"(\d+)([a-z가-힣]*)"))
            {
                string num = m.Groups[1].Value, unit = m.Groups[2].Value;
                string unit1 = Regex.Match(unit, @"^(mg|mcg|ug|iu|ml|kg|g|l|개|포|정|캡슐|병|박스|입|봉|팩|알|매|통|세트|종|억|만|배|일|년근|년)").Value;
                if (unit1.Length > 0)
                {
                    if (all.Contains(num + unit1)) continue;
                    if (CountUnit.IsMatch(unit1) && nameNums.Contains(num)) continue;
                    return "다른 수량·용량";
                }
                // 붙은 숫자 (폴리코사놀5, 유산균19, 트리플s 의 숫자 등): 상품명에 그 숫자가 있거나 상세에 바로 앞 말과 함께 있어야
                string before = Regex.Match(rest.Substring(0, m.Index), @"[a-z가-힣]+$").Value;
                if (nameNums.Contains(num) && (before.Length == 0 || all.Contains(before + num) || Key(name).Contains(num))) continue;
                if (before.Length > 0 && all.Contains(before + num)) continue;
                if (before.Length == 0 && sk.Length > 0 && all.Contains(sk + num)) continue;
                return "다른 수량·용량";
            }
            return null;
        }

        public static string TagReason(string word, string name, string detail, Learned learned)
        {
            string k = Key(word), text = (name ?? "") + " " + (detail ?? "");
            if (Risky.Any(r => k.Contains(r))) return "위험 단어";
            // 숫자 주장(100억, 1000억, 19종 …)은 상품명·상세에 그 숫자가 있어야
            var num = Regex.Match(k, @"\d+(억|만|천|종|가지|배)");
            if (num.Success && !Key(text).Contains(num.Value)) return "상품에 언급 없음";
            // 숫자가 들어간 말(3000IU·비타민D5000IU·폴리코사놀5)은 이 상품 이름·상세에 그 숫자가 있어야 (다른 용량 상품 이름이 섞이지 않게)
            if (Regex.IsMatch(k, @"\d") && !Key(text).Contains(k)) return "다른 용량";
            var group = Claims.FirstOrDefault(g => g.Any(c => k.Contains(Key(c))));
            if (group != null && !group.Any(c => Mentioned(text, c)))
                return group.Any(c => FormWords.Contains(c)) ? "상품 형태가 아님" : "상품에 언급 없음";
            // 여성·남성은 연구 인용("미국 여성 89명")처럼 한 번 지나가는 말이 아니라 2번 이상 나와야
            if (group != null && (group.Contains("여성") || group.Contains("남성")) && Regex.Matches(text, "(?<![가-힣])" + Regex.Escape(group[0]) + "(?!" + "[0-9]+명)").Count < 2)
                return "상품에 언급 없음";
            if (learned.IsTagOnly(word)) return "전에 쿠팡 검색어로 옮김";
            return null;
        }

        // 단어를 순서대로 이어 붙여 max 글자 이내로 (이미 들어간 단어에 포함되는 단어는 건너뜀)
        public static List<string> Fit(string seed, IEnumerable<string> words, int max)
        {
            var used = new List<string> { seed };
            int len = seed.Length;
            string sk = Key(seed);
            foreach (var w in words)
            {
                if (string.Equals(w, seed, StringComparison.OrdinalIgnoreCase)) continue;
                string joined = Key(string.Concat(used)), wk = Key(w);
                if (joined.Contains(wk)) continue;
                // 같은 말 반복 금지: 시작 단어가 든 말은 시작 단어 포함 3번까지 (포스파티딜세린 ×4 같은 도배는 네이버가 어뷰징으로 봄)
                if (sk.Length >= 2 && wk.Contains(sk) && used.Count(u => Key(u).Contains(sk)) >= 3) continue;
                // 이미 넣은 말에 단위만 붙인 말은 같은 말 (포스파티딜세린300 → 포스파티딜세린300MG)
                if (used.Any(u => { string uk = Key(u); return uk.Length >= 2 && wk.StartsWith(uk) && UnitOnly.IsMatch(wk.Substring(uk.Length)); })) continue;
                if (len + 1 + w.Length > max) continue;
                used.Add(w); len += 1 + w.Length;
            }
            return used;
        }

        static List<string> PairSwap(List<string> r)
        {
            var o = new List<string>(r);
            for (int i = 0; i + 1 < o.Count; i += 2) { var t = o[i]; o[i] = o[i + 1]; o[i + 1] = t; }
            return o;
        }

        static List<string> Rotate(List<string> r, int k)
        {
            if (r.Count == 0) return r;
            k %= r.Count;
            return r.Skip(k).Concat(r.Take(k)).ToList();
        }

        // 계정·스토어마다 단어 배열을 다르게: n{계정}_{개수}, c{계정}
        static readonly string[] NotFlavor = { "단", "쓴", "신", "짠", "감칠", "입", "손", "한", "본연의", "고소한", "달콤한", "새콤달콤한", "깔끔한", "다양한", "여러", "가지", "종류", "부드러운", "진한" };

        // 쿠팡 상품명 채우기: 상세 페이지에 2번 이상 나온 맛 이름(애플시나몬맛 …) + msgood 상품명의 구성·용량(6종 세트 50g x 12포)
        public static List<string> CoupangExtra(string name, string brand, string detail)
        {
            var list = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (Match m in Regex.Matches(detail ?? "", @"(?<![0-9A-Za-z가-힣])([가-힣]{2,8})\s?맛(?![가-힣])"))
            {
                string f = m.Groups[1].Value;
                if (NotFlavor.Any(n => f.EndsWith(n) || f == n)) continue;
                int c; counts.TryGetValue(f, out c); counts[f] = c + 1;
            }
            foreach (var kv in counts.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).Take(8)) list.Add(kv.Key + "맛");
            string bk = Key(brand);
            foreach (Match m in Regex.Matches(name ?? "", @"\d+\s*종|세트|\d+(?:\.\d+)?\s*(?:g|kg|ml|l|mg)\s*(?:[xX×]\s*\d+\s*(?:포|개|입|병|봉|팩|정|캡슐)?)?|\d+\s*(?:포|개입|입|병|봉|팩|정|캡슐|환)", RegexOptions.IgnoreCase))
                list.Add(Regex.Replace(m.Value, @"\s+", ""));
            return list.Where(w => Key(w) != bk && w.Length > 0).Distinct().ToList();
        }

        // 정보를 찾는 검색어(오트밀효능, 오트밀칼로리 …)는 상품명에 안 맞음
        static readonly string[] InfoWords = { "추천", "효능", "효과", "칼로리", "먹는법", "먹는방법", "만들기", "만드는법", "레시피", "부작용", "가격", "순위", "후기", "차이", "성분", "영양", "단백질함량", "뜻", "종류", "보관", "유통기한", "다이어트", "쿠팡", "코스트코", "이마트", "트레이더스", "판매처", "파는곳" };

        // 의약품으로 오해할 말 (판매금지 위험): 이완제·유도제·강화제·치료제 …  상품명·쿠팡 검색어 모두 안 씀
        static readonly string[] MedicineWords = { "이완제", "유도제", "강화제", "치료제", "치료", "수면제", "진통제", "소화제", "해열제", "안약", "점안액", "처방", "완치", "특효", "약국", "의약품", "진정제", "항생제", "주사",
            // 질병 이름처럼 보이는 말 (건강기능식품 상품명에 안 씀: PMS증후군·생리전증후군)
            "증후군" };
        public static bool IsMedicine(string w)
        {
            string k = Key(w);
            return MedicineWords.Any(m => k.Contains(m)) || (Regex.IsMatch(k, "[가-힣]약$") && !k.EndsWith("제약") && !k.EndsWith("생약") && !k.EndsWith("한약"));
        }

        // 고민·상황 키워드: 사람들이 증상·목적으로 찾는 말 (눈이뻑뻑할때·다리에쥐날때·아연많은음식·집중력높이는법)
        static readonly Regex ConcernRe = new Regex("(할때|할적|때먹는|날때|일때|법$|방법$|에좋은|좋은음식|많은음식|많은과일|음식$|높이기|높이는|낮추는|빼는|빠지는|떨림|뻑뻑|침침|저하|느낌|증상|원인|부족|피로|숙면|붓기|잘하는|만들기)");
        static readonly Regex InfoBadRe = new Regex("(효능|효과|부작용|가격|추천|후기|순위|차이|뜻|칼로리|성분|권장량|복용|섭취량|섭취법|구매|파는곳|판매처|쿠팡|코스트코|이마트|약국|다이소|올리브영)");
        // 대상 (청소년·수험생·군대px …). 어린이·아기·임산부는 위험 단어라 상품명에 안 씀, 남자·여자는 상세에 있어야 해서 여기 없음
        static readonly string[] Audience = { "청소년", "수험생", "고3", "학생", "중학생", "고등학생", "대학생", "직장인", "중년", "장년", "노인", "어르신", "부모님", "군인", "군대", "px", "피엑스", "성인", "10대", "20대", "30대", "40대", "50대", "60대", "70대" };

        // 상세에 나온 고민 + 영양제 (감마리놀렌산 상세의 "PMS"·"월경" → PMS영양제·생리영양제). '증후군'은 질병 이름이라 안 씀
        // 카테고리 확인을 따로 거치므로 여기서는 상세에 나온 말인지만 봄
        static readonly Regex NeedSuffix = new Regex("(영양제)$");
        public static bool IsMentionedNeed(string w, string ocr)
        {
            string k = Key(w);
            var m = NeedSuffix.Match(k);
            if (!m.Success || k.Length > 14 || InfoBadRe.IsMatch(k) || IsMedicine(w) || IsRisky(w)) return false;
            string rest = k.Substring(0, m.Index);
            if (rest.Length < 2) return false;
            string o = Key(ocr ?? "");
            return o.Contains(rest) || o.Contains(rest.Replace("생리", "월경")) || o.Contains(rest.Replace("월경", "생리"));
        }

        public static bool IsConcern(string w)
        {
            string k = Key(w);
            return ConcernRe.IsMatch(k) && !InfoBadRe.IsMatch(k) && !IsMedicine(w) && !IsRisky(w) && k.Length <= 14;
        }

        // 남는 부분이 대상 말로만 되어 있는지 (청소년아연 → 청소년, 군대px영양제 → 군대·px)
        public static bool IsAudienceRest(string rest)
        {
            string k = Key(rest);
            if (k.Length == 0) return false;
            var dp = new bool[k.Length + 1]; dp[0] = true;
            for (int i = 0; i < k.Length; i++)
                if (dp[i]) foreach (var a in Audience) if (i + a.Length <= k.Length && string.CompareOrdinal(k, i, a, 0, a.Length) == 0) dp[i + a.Length] = true;
            return dp[k.Length];
        }

        // 대상 + 영양제 (군대px영양제·수험생영양제·청소년영양제)
        public static bool IsAudienceSupplement(string w)
        {
            string k = Key(w);
            return k.EndsWith("영양제") && k.Length > 3 && IsAudienceRest(k.Substring(0, k.Length - 3)) && !IsRisky(w);
        }

        // 연관 검색어 중 시작 단어의 오타 (글자 하나·둘이 빠지거나 바뀐 것: 포스타딜세린·마그네시움·루데인·아스잔틴)
        //  바뀐 글자는 자음이나 모음 하나는 같아야 (마그네틱 같은 다른 말은 아님). 네이버 상품수로 한 번 더 확인함
        public static bool IsNearTypo(string word, string seed)
        {
            string w = Key(word), s = Key(seed);
            if (s.Length < 3 || w.Length < 3 || w == s || w.Contains(s) || s.Contains(w) && w.Length < s.Length - 1) return false;
            if (!Regex.IsMatch(w, "^[가-힣0-9a-z]+$") || Regex.Replace(w, "[^0-9]", "") != Regex.Replace(s, "[^0-9]", "")) return false;
            int maxD = s.Length >= 4 ? 2 : 1;
            var d = new int[w.Length + 1, s.Length + 1];
            for (int i = 0; i <= w.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= s.Length; j++) d[0, j] = j;
            for (int i = 1; i <= w.Length; i++)
                for (int j = 1; j <= s.Length; j++)
                {
                    int sub = w[i - 1] == s[j - 1] ? 0 : ShareJamo(w[i - 1], s[j - 1]) ? 1 : 9;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + sub);
                }
            // 앞 글자는 같거나 첫 자음이 같아야 (사람은 대개 앞은 맞게 씀: 멜라토닌 → 메라토닌)
            return d[w.Length, s.Length] >= 1 && d[w.Length, s.Length] <= maxD && (w[0] == s[0] || (w[0] >= '가' && s[0] >= '가' && (w[0] - '가') / 588 == (s[0] - '가') / 588));
        }

        static bool ShareJamo(char a, char b)
        {
            if (a < '가' || a > '힣' || b < '가' || b > '힣') return false;
            int x = a - '가', y = b - '가';
            int same = (x / 588 == y / 588 ? 1 : 0) + ((x % 588) / 28 == (y % 588) / 28 ? 1 : 0) + (x % 28 == y % 28 && x % 28 != 0 ? 1 : 0);
            return same >= 1;
        }

        public static bool GoodFiller(string word, string seed, string name, string detail, Learned learned)
        {
            if (IsMedicine(word)) return false;
            string k = Key(word);
            if (SalesWords.Any(s => s.Length >= 2 && k.Contains(Key(s)))) return false;   // 프리미엄유산균 등
            if (InfoWords.Any(w => k.Contains(Key(w)))) return false;
            if (learned.IsExcluded(word) || learned.IsBrand(word)) return false;
            if (TagReason(word, name, detail, learned) != null) return false;
            return k.Length <= 12;
        }

        // 상품명 단어 순서: 상품수가 적고 배율(상품수 ÷ 월간검색량)이 좋은 말을 앞에, 상품수가 아주 많은 큰 말(유산균·오메가3)은 뒤에
        //  0 = 상품수 10만 미만이고 배율 300 이하 (배율 좋은 순) / 1 = 상품수 모름 / 2 = 나머지 (상품수 10만 이상이거나 배율 나쁨, 배율 순) / 3 = 맛·용량 같은 덧붙임
        //  시작 단어의 오타(오매가3)는 시작 단어 바로 뒤
        public const long HugeTotal = 100000;
        public const double GoodRatio = 300;   // 배율이 이보다 나쁘면(면역기능: 상품 8.9만 ÷ 검색 25) 앞에 두지 않음
        public class WordStat { public long Total = -1, Volume = -1; public bool Typo, Known, Rising; }

        public static List<string> Arrange(List<string> ws, string seed, Func<string, WordStat> stat, bool byRank)
        {
            var st = ws.ToDictionary(w => w, w => stat(w) ?? new WordStat(), StringComparer.OrdinalIgnoreCase);
            var ss = stat(seed) ?? new WordStat();
            Func<WordStat, int> grp0 = s => !s.Known ? 3 : s.Total < 0 || s.Volume <= 0 ? 1 : s.Total < HugeTotal && s.Total <= GoodRatio * s.Volume ? 0 : 2;
            Func<WordStat, double> rk0 = s => s.Total < 0 || s.Volume <= 0 ? 1e12 : s.Total / (double)s.Volume;
            Func<string, int> grp = w => st[w].Typo ? grp0(ss) : grp0(st[w]);
            Func<string, double> rk = w => st[w].Typo ? rk0(ss) + 0.001 : rk0(st[w]);
            var idx = ws.Select((w, i) => new { w, i }).ToList();
            // 배율 좋은 말 중에서는 최근 급상승한 말을 먼저
            var o = byRank ? idx.OrderBy(x => grp(x.w)).ThenByDescending(x => grp(x.w) == 0 && st[x.w].Rising).ThenBy(x => grp(x.w) == 1 || grp(x.w) == 3 ? 0 : rk(x.w)).ThenBy(x => x.i)
                           : idx.OrderBy(x => grp(x.w)).ThenBy(x => x.i);
            var res = o.Select(x => x.w).ToList();
            // 맨 앞 단어는 무슨 상품인지 드러나야 함: 맨 앞이 시작 단어를 안 품은 말(항산화·면역기능)이면,
            // 배율 좋은 말 중 시작 단어가 든 말(아연셀레늄)을 맨 앞으로
            string sk = Key(seed);
            if (res.Count > 1 && sk.Length > 0 && !Key(res[0]).Contains(sk))
            {
                var front = res.FirstOrDefault(w => Key(w).Contains(sk) && Key(w) != sk && grp(w) <= 1);
                if (front != null) { res.Remove(front); res.Insert(0, front); }
            }
            return res;
        }

        // 상품명에 따로 있는 단어들을 이어 붙이기만 한 말인지 (유산균·프로바이오틱스가 있으면 프로바이오틱스유산균)
        //  네이버는 띄어 쓴 단어를 합쳐서도 찾아 주므로 이런 말은 같은 단어를 또 쓰는 셈 → 스토어 상품명에서는 빼고 다른 키워드를 넣음
        public static bool Segmentable(string word, IEnumerable<string> others)
        {
            string k = Key(word);
            if (k.Length < 4) return false;
            var parts = new HashSet<string>(others.Select(Key).Where(x => x.Length >= 2 && x != k));
            var dp = Enumerable.Repeat(-1, k.Length + 1).ToArray();
            dp[0] = 0;
            for (int i = 0; i < k.Length; i++)
            {
                if (dp[i] < 0) continue;
                foreach (var p in parts)
                    if (i + p.Length <= k.Length && string.CompareOrdinal(k, i, p, 0, p.Length) == 0)
                    {
                        int j = i + p.Length;
                        if (dp[j] < 0 || dp[j] > dp[i] + 1) dp[j] = dp[i] + 1;
                    }
            }
            return dp[k.Length] >= 2;
        }

        // 스토어 상품명용: 붙여 쓴 말을 빼고 그 자리를 다음 키워드로 채움
        public const int NaverMinLen = 20;
        static List<string> FitNaver(string seed, List<string> cands, int max)
        {
            var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> res = null;
            for (int t = 0; t < 6; t++)
            {
                res = Fit(seed, cands.Where(w => !drop.Contains(w)).ToList(), max);
                var cur = res;
                var comp = res.Skip(1).Where(w => Segmentable(w, cur.Where(x => !string.Equals(x, w, StringComparison.OrdinalIgnoreCase)))).ToList();
                if (comp.Count == 0) break;
                foreach (var c in comp) drop.Add(c);
            }
            // 그래도 30자가 안 되면 뺐던 붙여 쓴 말을 다시 넣어 30자를 채움 (짧은 상품명보다는 나음)
            int len = string.Join(" ", res).Length;
            foreach (var w in cands.Where(drop.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (len >= NaverMinLen) break;
                if (len + 1 + w.Length > max || res.Contains(w, StringComparer.OrdinalIgnoreCase)) continue;
                res.Add(w); len += 1 + w.Length;
            }
            return res;
        }

        // 상품명에 못 들어간 키워드 (네이버 브랜드·제조사 칸에 넣을 말): 상품명에 없고, 상품명 단어를 이어 붙인 말도 아닌 것
        public static List<string> Leftover(string name, IEnumerable<string> cands, int n)
        {
            var nameWords = Regex.Split(name ?? "", @"\s+").Where(w => w.Length > 0).ToList();
            string nk = Key(name);
            return (cands ?? new List<string>()).Where(w => Key(w).Length >= 2 && w.Length <= 20 && !nk.Contains(Key(w)) && !Segmentable(w, nameWords))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Take(n).ToList();
        }

        public static string ApplyStyle(string style, Job job, string seed, string name, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(style) || style == "기본" || job == null || string.IsNullOrWhiteSpace(name))
                return name;

            string prefix = null;
            if (style == "브랜드 앞" && !string.IsNullOrWhiteSpace(job.Brand))
            {
                prefix = job.Brand.Trim();
            }
            else if (style == "효능 앞")
            {
                var cw = (job.Concern ?? new List<KwRow>()).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Word));
                if (cw != null) prefix = cw.Word.Trim();
                else
                {
                    var eff = job.Keywords.FirstOrDefault(k => k.TagReason != null && k.TagReason.Contains("효능"));
                    if (eff != null) prefix = eff.Word.Trim();
                }
            }
            else if (style == "함량 앞")
            {
                if (!string.IsNullOrWhiteSpace(job.Volume)) prefix = job.Volume.Trim();
                else
                {
                    var m = Regex.Match(job.Name ?? "", @"\d+\s*(mg|g|ml|정|캡슐|포|환)");
                    if (m.Success) prefix = m.Value.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(prefix)) return name;

            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name;

            var parts = Regex.Split(name, @"\s+").Where(w => w.Length > 0 && !string.Equals(w, prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            parts.Insert(0, prefix);

            var joined = string.Join(" ", parts);
            while (joined.Length > maxLen && parts.Count > 1)
            {
                parts.RemoveAt(parts.Count - 1);
                joined = string.Join(" ", parts);
            }
            return joined;
        }

        public static Dictionary<string, string> BuildForAccounts(
            Job job,
            AppSettings set,
            List<string> words,
            List<string> filler = null,
            List<string> coupangExtra = null,
            Func<string, WordStat> stat = null)
        {
            var seed = job.Seed ?? "";
            Func<List<string>, bool, List<string>> arr = (ws, byRank) => stat == null ? ws : Arrange(ws, seed, stat, byRank);
            if (filler != null) words = words.Concat(filler.Where(f => !words.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();
            var cwords = coupangExtra == null ? words : words.Concat(coupangExtra.Where(f => !words.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();
            var d = new Dictionary<string, string>();

            if (coupangExtra != null && coupangExtra.Count > 0 && string.Join(" ", FitNaver(seed, words, 50)).Length < 20)
                words = words.Concat(coupangExtra.Where(f => !words.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();

            var usedShared = (set != null && !string.IsNullOrWhiteSpace(set.SharedFolder))
                ? SharedNames.GetUsedNames(set.SharedFolder, job.Code)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Func<IEnumerable<string>, string> join = xs => string.Join(" ", xs);
            const int CoupangLen = 50;

            var p1 = set != null && set.Accounts != null ? set.Accounts.FirstOrDefault(a => a.Id == "1") : null;
            var p2 = set != null && set.Accounts != null ? set.Accounts.FirstOrDefault(a => a.Id == "2") : null;
            var p3 = set != null && set.Accounts != null ? set.Accounts.FirstOrDefault(a => a.Id == "3") : null;

            // ── #1 계정: 기본 최적 순위 키워드 조합
            var fit50_1 = FitNaver(seed, words, 50);
            var r1 = fit50_1.Skip(1).ToList();
            var r0_1 = arr(fit50_1, true).Where(w => !string.Equals(w, seed, StringComparison.OrdinalIgnoreCase)).ToList();
            if (stat != null) r1 = r0_1;

            string n1_1 = join(arr(fit50_1, true));
            string n1_2 = join(arr(FitNaver(seed, new[] { seed }.Concat(PairSwap(r1)).Concat(words).ToList(), 50), false));
            var fit100_1 = Fit(seed, cwords, CoupangLen);
            string c1 = join(arr(fit100_1, true));

            if (p1 != null)
            {
                n1_1 = ApplyStyle(p1.NameStyle, job, seed, n1_1, 50);
                n1_2 = ApplyStyle(p1.NameStyle, job, seed, n1_2, 50);
                c1 = ApplyStyle(p1.NameStyle, job, seed, c1, CoupangLen);
            }
            d["n1_1"] = n1_1;
            d["n1_2"] = n1_2;
            d["c1"] = c1;

            var usedIn1 = new HashSet<string>(Regex.Split(n1_1 + " " + c1, @"\s+"), StringComparer.OrdinalIgnoreCase);

            // ── #2 계정: 계정 1이 쓴 강한 조합을 피하고 대안 유효 키워드 우선 배치
            var words2 = new List<string> { seed };
            var altWords2 = words.Where(w => !string.Equals(w, seed, StringComparison.OrdinalIgnoreCase)).ToList();
            var fresh2 = altWords2.Where(w => !usedIn1.Contains(w)).ToList();
            var overlapped2 = altWords2.Where(w => usedIn1.Contains(w)).ToList();
            words2.AddRange(fresh2);
            words2.AddRange(r1.Where((w, i) => i % 2 == 1));
            words2.AddRange(overlapped2);
            words2.AddRange(words);
            words2 = words2.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var fit50_2 = FitNaver(seed, words2, 50);
            var r2 = fit50_2.Skip(1).ToList();
            string n2_1 = join(arr(fit50_2, false));
            string n2_2 = join(arr(FitNaver(seed, new[] { seed }.Concat(Enumerable.Reverse(r2)).Concat(words2).ToList(), 50), false));
            var fit100_2 = Fit(seed, words2.Concat(cwords).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), CoupangLen);
            string c2 = join(arr(fit100_2, false));

            if (p2 != null)
            {
                n2_1 = ApplyStyle(p2.NameStyle, job, seed, n2_1, 50);
                n2_2 = ApplyStyle(p2.NameStyle, job, seed, n2_2, 50);
                c2 = ApplyStyle(p2.NameStyle, job, seed, c2, CoupangLen);
            }

            if (n2_1 == n1_1 || usedShared.Contains(n2_1)) n2_1 = join(arr(FitNaver(seed, words2.Skip(1).Concat(new[] { seed }).ToList(), 50), false));
            if (c2 == c1 || usedShared.Contains(c2)) c2 = join(arr(Fit(seed, PairSwap(r2).Concat(words2), CoupangLen), false));

            d["n2_1"] = n2_1;
            d["n2_2"] = n2_2;
            d["c2"] = c2;

            var usedIn2 = new HashSet<string>(Regex.Split(n2_1 + " " + c2, @"\s+"), StringComparer.OrdinalIgnoreCase);

            // ── #3 계정: 계정 1, 2 조합 모두 피하고 회전 및 3차 단어군 조합
            var words3 = new List<string> { seed };
            var altWords3 = words.Where(w => !string.Equals(w, seed, StringComparison.OrdinalIgnoreCase)).ToList();
            var fresh3 = altWords3.Where(w => !usedIn1.Contains(w) && !usedIn2.Contains(w)).ToList();
            words3.AddRange(fresh3);
            words3.AddRange(Rotate(altWords3, 2));
            words3.AddRange(words);
            words3 = words3.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var fit50_3 = FitNaver(seed, words3, 50);
            var r3 = fit50_3.Skip(1).ToList();
            string n3_1 = join(arr(fit50_3, false));
            string n3_2 = join(arr(FitNaver(seed, new[] { seed }.Concat(Rotate(r3, 1)).Concat(words3).ToList(), 50), false));
            var fit100_3 = Fit(seed, words3.Concat(cwords).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), CoupangLen);
            string c3 = join(arr(fit100_3, false));

            if (p3 != null)
            {
                n3_1 = ApplyStyle(p3.NameStyle, job, seed, n3_1, 50);
                n3_2 = ApplyStyle(p3.NameStyle, job, seed, n3_2, 50);
                c3 = ApplyStyle(p3.NameStyle, job, seed, c3, CoupangLen);
            }

            if (n3_1 == n1_1 || n3_1 == n2_1 || usedShared.Contains(n3_1)) n3_1 = join(arr(FitNaver(seed, Rotate(words3, 3), 50), false));
            if (c3 == c1 || c3 == c2 || usedShared.Contains(c3)) c3 = join(arr(Fit(seed, Rotate(r3, 2).Concat(words3), CoupangLen), false));

            d["n3_1"] = n3_1;
            d["n3_2"] = n3_2;
            d["c3"] = c3;

            return d;
        }

        public static Dictionary<string, string> Build(string seed, List<string> words, List<string> filler = null, List<string> coupangExtra = null, Func<string, WordStat> stat = null)
        {
            Func<List<string>, bool, List<string>> arr = (ws, byRank) => stat == null ? ws : Arrange(ws, seed, stat, byRank);
            // 상품명이 짧으면 연관 키워드로 채움 (스토어 40자 이상, 쿠팡 100자 가까이)
            if (filler != null) words = words.Concat(filler.Where(f => !words.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();
            var cwords = coupangExtra == null ? words : words.Concat(coupangExtra.Where(f => !words.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();
            var d = new Dictionary<string, string>();
            // 스토어 상품명이 20자가 안 되면 맛 이름 같은 덧붙임까지 써서 20자 이상으로
            if (coupangExtra != null && coupangExtra.Count > 0 && string.Join(" ", FitNaver(seed, words, 50)).Length < 20)
                words = words.Concat(coupangExtra.Where(f => !words.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();
            var fit50 = FitNaver(seed, words, 50);
            var r = fit50.Skip(1).ToList();
            Func<IEnumerable<string>, string> join = xs => string.Join(" ", xs);
            // 같은 단어 묶음 안에서 배열만 바꾸고, 남는 자리는 다음 단어로 채움
            Func<List<string>, string> naver = order => join(Fit(order[0], order.Skip(1).Concat(words), 50));
            d["n1_1"] = join(arr(fit50, true));
            naver = order => join(arr(FitNaver(order[0], order.Skip(1).Concat(words).ToList(), 50), false));
            // 다른 배열은 같은 무리(배율 좋은 말 / 큰 말) 안에서만 순서를 바꿈
            var r0 = arr(fit50, true).Where(w => !string.Equals(w, seed, StringComparison.OrdinalIgnoreCase)).ToList();
            if (stat != null) r = r0;
            d["n1_2"] = naver(new[] { seed }.Concat(PairSwap(r)).ToList());
            // #2 스토어 1개: 홀수 번째 단어 먼저, 짝수 번째 단어 나중
            d["n2_1"] = naver(new[] { seed }.Concat(r.Where((w, i) => i % 2 == 1)).Concat(r.Where((w, i) => i % 2 == 0)).ToList());
            d["n2_2"] = naver(new[] { seed }.Concat(Enumerable.Reverse(r)).ToList());
            d["n3_1"] = naver(new[] { seed }.Concat(Rotate(r, 1)).ToList());
            d["n3_2"] = naver(new[] { seed }.Concat(Rotate(r, 2)).ToList());
            // 쿠팡 상품명도 짧게 (키워드 나열은 쿠팡 정책상 불리 → 나머지 키워드는 쿠팡 검색어 20개로)
            const int CoupangLen = 50;
            var fit100 = Fit(seed, cwords, CoupangLen);
            var r100 = stat != null ? arr(fit100, true).Where(w => !string.Equals(w, seed, StringComparison.OrdinalIgnoreCase)).ToList() : fit100.Skip(1).ToList();
            d["c1"] = join(arr(fit100, true));
            d["c2"] = join(arr(Fit(seed, PairSwap(r100).Concat(cwords), CoupangLen), false));
            var extra = new HashSet<string>(coupangExtra ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            d["c3"] = join(arr(Fit(seed, Enumerable.Reverse(r100.Where(w => !extra.Contains(w)).ToList()).Concat(cwords), CoupangLen), false));
            return d;
        }

        public static string NameLabel(string key)
        {
            if (key.StartsWith("n")) return "#" + key[1] + " 스토어 " + key.Substring(3) + "개";
            return "#" + key[1] + " 쿠팡";
        }

        public static int MaxLen(string key) { return key.StartsWith("c") ? 100 : 50; }

        // 단어 단위 비교 (학습용)
        public static HashSet<string> Words(IEnumerable<string> names)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names) foreach (var w in Regex.Split(n ?? "", @"\s+")) if (w.Trim().Length > 0) set.Add(w.Trim());
            return set;
        }
    }
}
