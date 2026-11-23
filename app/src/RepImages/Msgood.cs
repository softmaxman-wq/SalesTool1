using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RepImages
{
    // msgood4u: 상품번호(예: 22266)로 상품을 찾고 상세 이미지를 내려받음 (로그인 없이 보이는 정보)
    public static class Msgood
    {
        const string Base = "https://www.msgood4u.com";

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
            return wc;
        }

        // 상품목록 페이지 검색(etc_sword)은 상품번호로 찾아짐
        public static string FindUrl(string codeOrUrl)
        {
            string s = (codeOrUrl ?? "").Trim();
            if (Regex.IsMatch(s, @"^https?://", RegexOptions.IgnoreCase)) return s;
            if (!Regex.IsMatch(s, @"^[0-9]+(-[0-9]+)?$")) throw new Exception("msgood4u 상품번호(예: 22266)나 상품 주소를 넣어 주세요.");
            string html;
            using (var wc = Client())
                html = Encoding.UTF8.GetString(wc.UploadValues(Base + "/html/goods/goods_list.php", new NameValueCollection { { "etc_sword", s } }));
            // 검색은 번호가 들어간 상품을 모두 보여 줌 (1864 → 21864·1864): 상품번호가 똑같은 상품만
            Match m = null;
            foreach (var block in Regex.Split(html, @"<li class=['""]line_5['""]>").Skip(1))
            {
                var code = Regex.Match(block, @"class=['""]code['""].*?<span>\s*([\w-]+)\s*</span>", RegexOptions.Singleline);
                if (!code.Success || code.Groups[1].Value != s) continue;
                m = Regex.Match(block, @"goods_detail\.php\?g_code=([A-Za-z0-9]+)[^'""]*");
                if (m.Success) break;
            }
            if (m == null || !m.Success) throw new Exception("msgood4u 에서 상품번호 " + s + " 를 찾지 못했습니다.");
            return Base + "/html/goods/" + WebUtility.HtmlDecode(m.Value);
        }

        // 상품 대표 사진 (상품 페이지 위쪽 사진. 아래 "함께 구매한 상품" 사진들보다 먼저 나옴)
        public static string MainImageUrl(string productUrl, out string name)
        {
            string brand;
            return MainImageUrl(productUrl, out name, out brand);
        }

        // 상품명·브랜드(라벨에서 브랜드를 찾아 지우는 데 씀)도 함께
        public static string MainImageUrl(string productUrl, out string name, out string brand)
        {
            string html;
            using (var wc = Client()) html = wc.DownloadString(productUrl);
            html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
            var t = Regex.Match(html, @"<div class=""t2"">(.*?)</div>", RegexOptions.Singleline);
            name = t.Success ? WebUtility.HtmlDecode(Regex.Replace(t.Groups[1].Value, "<[^>]+>", "")).Trim() : null;
            brand = null;
            foreach (Match bm in Regex.Matches(html, @"class=['""]txt_brandName['""][^>]*>(.*?)</span>", RegexOptions.Singleline))
            {
                string v = WebUtility.HtmlDecode(Regex.Replace(bm.Groups[1].Value, "<[^>]+>", "")).Trim();
                if (v.Length > 0) { brand = v; break; }
            }
            // 브랜드 칸이 비었거나("-") 키워드 목록이 들어 있으면 쓰지 않음 (상품명 첫 단어는 브랜드가 아닐 때가 많아 쓰지 않음)
            if (brand == "-" || (brand != null && brand.Contains(","))) brand = null;
            var m = Regex.Match(html, @"(?:src|data-src)=[""']([^""']*/files/goods/[^""']+\.(?:jpg|jpeg|png|gif))[""']", RegexOptions.IgnoreCase);
            if (!m.Success) throw new Exception("msgood4u 상품 사진을 찾지 못했습니다.");
            string u = m.Groups[1].Value;
            return u.StartsWith("http") ? u : Base + (u.StartsWith("/") ? "" : "/") + u;
        }

        public static Bitmap DownloadImage(string url)
        {
            using (var wc = Client())
            using (var ms = new MemoryStream(wc.DownloadData(url)))
            using (var im = Image.FromStream(ms))
                return new Bitmap(im);
        }

        // 상세 영역의 이미지 주소들 (주석 처리된 이미지는 뺌)
        public static List<string> DetailImageUrls(string productUrl)
        {
            string html;
            using (var wc = Client()) html = wc.DownloadString(productUrl);
            var m = Regex.Match(html, @"<div class=""detailImg innerContent"">(.*?)</div>", RegexOptions.Singleline);
            var urls = new List<string>();
            if (m.Success)
            {
                string body = Regex.Replace(m.Groups[1].Value, @"<!--.*?-->", "", RegexOptions.Singleline);
                foreach (Match im in Regex.Matches(body, @"<img[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase))
                    if (!urls.Contains(im.Groups[1].Value)) urls.Add(im.Groups[1].Value);
            }
            if (urls.Count == 0) throw new Exception("상세 이미지를 찾지 못했습니다.");
            return urls;
        }

        // 상세 이미지가 여러 장이면 세로로 이어 붙여 한 장으로 (폭은 가장 넓은 것 기준)
        public static Bitmap DownloadDetail(List<string> urls, Action<string> log)
        {
            var parts = new List<Bitmap>();
            try
            {
                using (var wc = Client())
                    for (int i = 0; i < urls.Count; i++)
                    {
                        log("상세 이미지 받는 중... (" + (i + 1) + "/" + urls.Count + ")");
                        using (var ms = new MemoryStream(wc.DownloadData(urls[i])))
                        using (var im = Image.FromStream(ms))
                            parts.Add(new Bitmap(im));
                    }
                int w = 0, h = 0;
                foreach (var p in parts) w = Math.Max(w, p.Width);
                foreach (var p in parts) h += (int)Math.Round(p.Height * (w / (double)p.Width));
                var all = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(all))
                {
                    g.Clear(Color.White);
                    int y = 0;
                    foreach (var p in parts)
                    {
                        int ph = (int)Math.Round(p.Height * (w / (double)p.Width));
                        g.DrawImage(p, new Rectangle(0, y, w, ph));
                        y += ph;
                    }
                }
                return all;
            }
            finally { foreach (var p in parts) p.Dispose(); }
        }
    }
}
