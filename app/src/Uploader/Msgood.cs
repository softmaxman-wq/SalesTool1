using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ProductUploader
{
    public class MsgoodProduct
    {
        public string Name;
        public string Code;       // msgood4u 상품코드 (예: 22266)
        public string Brand;
        public string DetailHtml; // 상세 이미지 <img> 태그들
        public string Notice;     // "[판매가 절대 준수]" 같은 판매 조건 안내
    }

    // msgood4u 상품 페이지는 로그인 없이도 상품명·코드·상세 이미지가 보이므로 그대로 읽음
    public static class Msgood
    {
        public static MsgoodProduct Fetch(string url)
        {
            if (!Regex.IsMatch(url ?? "", @"^https?://(www\.)?msgood4u\.com/", RegexOptions.IgnoreCase))
                throw new Exception("msgood4u.com 상품 주소를 넣어 주세요.");
            string html;
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                html = wc.DownloadString(url);
            }

            var p = new MsgoodProduct();
            p.Code = Grab(html, @"<div class=""title"">상품코드</div>\s*<div class=""cards""><span[^>]*>([^<]+)</span>");
            foreach (Match m in Regex.Matches(html, @"class=['""]txt_brandName['""][^>]*>(.*?)</span>", RegexOptions.Singleline))
                if ((p.Brand = Text(m.Groups[1].Value)) != null) break;

            // 상품명은 prd_title 안의 t2, 그 위의 파란 글씨(출고일, [판매가 절대 준수] 등)는 판매 조건 안내
            string box = Grab(html, @"<div class=""prd_title"">(.*?)<div class=""t3"">") ?? "";
            box = Regex.Replace(box, @"<!--.*?-->", "", RegexOptions.Singleline);
            p.Name = Text(Grab(box, @"<div class=""t2"">(.*?)</div>"));
            var notes = new List<string>();
            foreach (Match m in Regex.Matches(box, @"<span class='f_blue'>(.*?)</span>", RegexOptions.Singleline))
            {
                string t = Text(m.Groups[1].Value);
                if (t != null) notes.Add(t);
            }
            p.Notice = notes.Count > 0 ? string.Join("  ", notes) : null;

            // 상세 이미지: <div class="detailImg innerContent"> 안의 주석이 아닌 <img>
            string detail = Grab(html, @"<div class=""detailImg innerContent"">(.*?)</div>");
            if (detail != null)
            {
                detail = Regex.Replace(detail, @"<!--.*?-->", "", RegexOptions.Singleline);
                var imgs = new List<string>();
                foreach (Match m in Regex.Matches(detail, @"<img[^>]+src=""([^""]+)""[^>]*>", RegexOptions.IgnoreCase))
                    imgs.Add("<img src=\"" + m.Groups[1].Value + "\">");
                p.DetailHtml = string.Join("\r\n", imgs);
            }
            if (string.IsNullOrEmpty(p.DetailHtml) && p.Code != null)
                p.DetailHtml = "<img src=\"http://mskorea3118.cafe24.com/" + p.Code + ".jpg\">";
            return p;
        }

        static string Grab(string s, string pattern)
        {
            var m = Regex.Match(s, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        static string Text(string html)
        {
            if (html == null) return null;
            string t = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return t.Length == 0 ? null : t;
        }
    }
}
