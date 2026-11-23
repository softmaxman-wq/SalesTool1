using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SalesTool;

namespace VerificationSuite
{
    class Program
    {
        static int passed = 0;
        static int failed = 0;

        static void Assert(bool condition, string testName, string details = "")
        {
            if (condition)
            {
                passed++;
                Console.WriteLine("  [PASS] " + testName + (string.IsNullOrEmpty(details) ? "" : " (" + details + ")"));
            }
            else
            {
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [FAIL] " + testName + (string.IsNullOrEmpty(details) ? "" : " - " + details));
                Console.ResetColor();
            }
        }

        static int Main(string[] args)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine(" Coupang Suggest & Keyword Subsystem Verification Suite (Milestone M1)");
            Console.WriteLine("================================================================================");

            try
            {
                TestSampleNutritionKeywords().GetAwaiter().GetResult();
                TestLengthBoundariesAndSanitization();
                TestCategoryMatchingExpansion();
                TestRiskKeywordDetectionAndRegexHardening();
                TestPayloadResilienceAndErrorHandling();
                TestCoupangSuggestBuildAndFallback().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[UNHANDLED EXCEPTION] " + ex.ToString());
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine("================================================================================");
            Console.WriteLine(" Verification Summary: Passed: " + passed + " | Failed: " + failed);
            Console.WriteLine("================================================================================");

            return failed == 0 ? 0 : 1;
        }

        static async Task TestSampleNutritionKeywords()
        {
            Console.WriteLine("\n[1] Testing Sample Nutrition Keywords (오트밀, 루테인, 유산균)...");

            string[] samples = { "오트밀", "루테인", "유산균" };
            var suggestEngine = new CoupangSuggest(null); // Offline/test mode (view == null)

            foreach (var seed in samples)
            {
                var list = await suggestEngine.Suggest(seed);
                Assert(list != null && list.Count > 0, "Suggest non-empty for seed: " + seed, "Count: " + (list != null ? list.Count : 0));
                Assert(list.All(k => k.Length >= 2 && k.Length <= 20), "All suggestions meet length boundary (2..20) for " + seed);
                Assert(list.All(k => k.Contains(seed)), "All suggestions contain seed: " + seed);
                Assert(list.Distinct(StringComparer.OrdinalIgnoreCase).Count() == list.Count, "All suggestions are deduplicated for " + seed);

                var fb = CoupangSuggest.FallbackSuggest(seed);
                Assert(fb.Count >= 5, "Fallback suggestions count >= 5 for " + seed, "Count: " + fb.Count);
                Assert(fb.Any(s => s.Contains("추천")), "Fallback contains generic modifier '추천' for " + seed);
                Assert(fb.Any(s => s.Contains("효능")), "Fallback contains generic modifier '효능' for " + seed);
            }
        }

        static void TestLengthBoundariesAndSanitization()
        {
            Console.WriteLine("\n[2] Testing Keyword Length Boundaries (2-20) and Sanitization...");

            Assert(!CoupangSuggest.ValidateKeyword(null), "Null keyword rejected");
            Assert(!CoupangSuggest.ValidateKeyword(""), "Empty keyword rejected");
            Assert(!CoupangSuggest.ValidateKeyword("   "), "Whitespace keyword rejected");
            Assert(!CoupangSuggest.ValidateKeyword("약"), "Single character '약' (len 1) rejected");
            Assert(!CoupangSuggest.ValidateKeyword("a"), "Single character 'a' (len 1) rejected");
            Assert(CoupangSuggest.ValidateKeyword("귀리"), "2-character keyword '귀리' accepted");
            Assert(CoupangSuggest.ValidateKeyword("오트밀 100% 압착 귀리"), "Valid 14-character keyword accepted");
            Assert(CoupangSuggest.ValidateKeyword("12345678901234567890"), "Boundary 20-character keyword accepted");
            Assert(!CoupangSuggest.ValidateKeyword("123456789012345678901"), "Boundary 21-character keyword rejected");
        }

        static void TestCategoryMatchingExpansion()
        {
            Console.WriteLine("\n[3] Testing Category Matching Expansion (General food / Grain seeds)...");

            string catOatmeal = CategoryTrends.MatchCategory("오트밀", "유기농 압착 오트밀 1kg");
            Assert(catOatmeal == "일반식품·곡물", "Seed '오트밀' maps to '일반식품·곡물' (not '오메가3')", "Result: " + catOatmeal);

            string catOat = CategoryTrends.MatchCategory("귀리", "캐나다산 볶은 통귀리");
            Assert(catOat == "일반식품·곡물", "Seed '귀리' maps to '일반식품·곡물'", "Result: " + catOat);

            string catGranola = CategoryTrends.MatchCategory("그래놀라", "수제 그래놀라 시리얼");
            Assert(catGranola == "일반식품·곡물", "Seed '그래놀라' maps to '일반식품·곡물'", "Result: " + catGranola);

            string catLutein = CategoryTrends.MatchCategory("루테인", "루테인 지아잔틴 눈건강");
            Assert(catLutein == "눈건강·루테인", "Seed '루테인' maps to '눈건강·루테인'", "Result: " + catLutein);

            string catProbiotics = CategoryTrends.MatchCategory("유산균", "생유산균 100억 보장");
            Assert(catProbiotics == "유산균", "Seed '유산균' maps to '유산균'", "Result: " + catProbiotics);

            var foodData = CategoryTrends.Get("일반식품·곡물");
            Assert(foodData != null && foodData.Items.Count > 0, "CategoryTrends.Get('일반식품·곡물') returns populated trend data", "Items: " + (foodData != null ? foodData.Items.Count : 0));
        }

        static void TestRiskKeywordDetectionAndRegexHardening()
        {
            Console.WriteLine("\n[4] Testing Risk Keyword Detection and Multi-word Regex Hardening...");

            // Multi-word medicine keywords
            Assert(CategoryTrends.DetectRisk("오메가3 약 추천") == "의약품", "'오메가3 약 추천' detected as 의약품");
            Assert(CategoryTrends.DetectRisk("관절약 추천") == "의약품", "'관절약 추천' detected as 의약품");
            Assert(CategoryTrends.DetectRisk("유산균약") == "의약품", "'유산균약' detected as 의약품");
            Assert(CategoryTrends.DetectRisk("혈관청소약") == "의약품", "'혈관청소약' detected as 의약품");
            Assert(CategoryTrends.DetectRisk("변비약 유산균") == "의약품", "'변비약 유산균' detected as 의약품");

            // Disease and treatment claim keywords
            Assert(CategoryTrends.DetectRisk("당뇨 치료제") == "질병·치료", "'당뇨 치료제' detected as 질병·치료");
            Assert(CategoryTrends.DetectRisk("고지혈증 예방") == "질병·치료", "'고지혈증 예방' detected as 질병·치료");
            Assert(CategoryTrends.DetectRisk("관절염 완치") == "질병·치료", "'관절염 완치' detected as 질병·치료");
            Assert(CategoryTrends.DetectRisk("중성지방 치료") == "질병·치료", "'중성지방 치료' detected as 질병·치료");

            // Competitor brands
            Assert(CategoryTrends.DetectRisk("종근당 락토핏") == "다른 브랜드", "'종근당 락토핏' detected as 다른 브랜드");
            Assert(CategoryTrends.DetectRisk("정관장 홍삼") == "다른 브랜드", "'정관장 홍삼' detected as 다른 브랜드");
            Assert(CategoryTrends.DetectRisk("유한양행 비타민") == "다른 브랜드", "'유한양행 비타민' detected as 다른 브랜드");

            // Clean generic keywords
            Assert(CategoryTrends.DetectRisk("오트밀 효능") == "일반", "'오트밀 효능' detected as 일반");
            Assert(CategoryTrends.DetectRisk("루테인 추천") == "일반", "'루테인 추천' detected as 일반");
            Assert(CategoryTrends.DetectRisk("유산균 복용법") == "일반", "'유산균 복용법' detected as 일반");
            Assert(CategoryTrends.DetectRisk("오트밀 그래놀라") == "일반", "'오트밀 그래놀라' detected as 일반");
        }

        static void TestPayloadResilienceAndErrorHandling()
        {
            Console.WriteLine("\n[5] Testing Payload Resilience (Timeouts, HTML 403, Empty, Malformed JSON)...");

            // Empty & null
            var emptyRes = CoupangSuggest.ParseSuggestPayload("");
            Assert(emptyRes != null && emptyRes.Count == 0, "Empty payload returns empty list without exception");

            var nullRes = CoupangSuggest.ParseSuggestPayload(null);
            Assert(nullRes != null && nullRes.Count == 0, "Null payload returns empty list without exception");

            var literalNull = CoupangSuggest.ParseSuggestPayload("null");
            Assert(literalNull != null && literalNull.Count == 0, "Literal 'null' returns empty list without exception");

            // Akamai 403 Access Denied HTML payload
            string html403 = "<HTML><HEAD><TITLE>Access Denied</TITLE></HEAD><BODY><H1>Access Denied</H1>You don't have permission to access...</BODY></HTML>";
            var htmlRes = CoupangSuggest.ParseSuggestPayload(html403);
            Assert(htmlRes != null && htmlRes.Count == 0, "Akamai 403 HTML payload handled gracefully without exception");

            // Malformed JSON
            var malformedRes = CoupangSuggest.ParseSuggestPayload("{ invalid json [}");
            Assert(malformedRes != null && malformedRes.Count == 0, "Malformed JSON handled gracefully without exception");

            // JSON array with numbers or primitive items
            var numRes = CoupangSuggest.ParseSuggestPayload("[123, 456, 789]");
            Assert(numRes != null, "Numeric array handled without exception");

            // Real Coupang suggest response format with HTML entity
            string validJson = "[{\"keyword\":\"플라하반 오트밀\",\"travelKeyword\":false},{\"keyword\":\"오트밀 &amp; 귀리\",\"travelKeyword\":false},{\"keyword\":\"\",\"travelKeyword\":false},{\"keyword\":\"a\",\"travelKeyword\":false}]";
            var validRes = CoupangSuggest.ParseSuggestPayload(validJson);
            Assert(validRes.Count == 2, "Valid payload parsed and filtered correctly", "Count: " + validRes.Count);
            Assert(validRes.Contains("플라하반 오트밀"), "Keyword '플라하반 오트밀' present");
            Assert(validRes.Contains("오트밀 & 귀리"), "HTML entity '&amp;' properly decoded to '&'");
            Assert(!validRes.Contains("a"), "Single character 'a' filtered out by boundary check");
        }

        static async Task TestCoupangSuggestBuildAndFallback()
        {
            Console.WriteLine("\n[6] Testing CoupangSuggest.Build & Fallback Non-Empty Guarantee...");

            var engine = new CoupangSuggest(null); // Offline test mode
            var tagWords = new List<string> { "유기농", "국산", "아기" };
            var allWords = new List<string> { "오트밀", "귀리", "그래놀라", "유기농", "국산", "아기" };

            var tags = await engine.Build("오트밀", tagWords, allWords, null);
            Assert(tags != null && tags.Count > 0, "Build tags are never empty in offline/fallback mode", "Count: " + (tags != null ? tags.Count : 0));
            Assert(tags.Count <= 20, "Build tags count does not exceed 20 limit", "Count: " + tags.Count);
            Assert(tags.All(t => t.Length >= 2 && t.Length <= 20), "All build tags satisfy 2..20 length boundary");
            Assert(tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() == tags.Count, "Build tags are strictly deduplicated");

            // BatchSuggest test
            var queries = new List<string> { "오트밀", "루테인", "유산균" };
            var batch = await engine.BatchSuggest(queries);
            Assert(batch.Count == 3, "BatchSuggest returns entries for all queries", "Count: " + batch.Count);
            Assert(batch.All(kv => kv.Value != null && kv.Value.Count > 0), "All batch results contain non-empty suggestions");
        }
    }
}