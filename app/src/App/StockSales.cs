using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using ProductUploader;

namespace SalesTool
{
    // 품절 연동: msgood4u 의 "품절/입고현황"(품절·단종 상품 목록)을 읽어, 내 스토어에서 같은 상품번호인 상품을 품절 처리 / 다시 들어오면 판매 재개
    //  상품번호: 네이버는 판매자관리코드(22021, 22021x2), 쿠팡은 등록상품명(22021)
    //  판매 재개는 이 프로그램이 품절 처리한 상품만 (직접 품절로 바꾼 상품은 건드리지 않음)
    public class StockChange
    {
        public string Account, Market, Code, Name, Action;   // Action: 품절 / 판매 재개
        public string OriginNo;                               // 네이버 원상품번호
        public string SellerProductId;                        // 쿠팡 등록상품ID
        public override string ToString() { return "#" + Account + " " + Market + " [" + Code + "] " + Action + " - " + Name; }
    }

    public class StockRecord
    {
        public Dictionary<string, string> Naver = new Dictionary<string, string>();     // "계정:원상품번호" → 상품번호 (이 프로그램이 품절 처리한 것)
        public Dictionary<string, string> Coupang = new Dictionary<string, string>();   // "계정:등록상품ID" → 상품번호
        public Dictionary<string, long> TodayStock = new Dictionary<string, long>();    // "계정:원상품번호" → 품절 처리 전 오늘출발 재고수량 (판매 재개 때 되돌림)
        public string LastRun;
    }

    public static class StockSync
    {
        const string Base = "https://www.msgood4u.com";
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public static string RecordPath { get { return Path.Combine(Store.Dir, "stock_sync.json"); } }

        static string Fetch(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                return Encoding.UTF8.GetString(wc.DownloadData(url));
            }
        }

        // msgood4u 품절·단종 상품번호 전체
        public static HashSet<string> SoldOut(Action<string> log)
        {
            var set = new HashSet<string>();
            string prevFirst = null;
            for (int p = 1; p <= 100; p++)
            {
                log("msgood4u 품절 목록 읽는 중... " + p + "쪽");
                string html = Fetch(Base + "/html/goods/goods_sellout.php?&unit_page=50&unit_sort=latestdate@desc&page=" + p);
                var items = Regex.Matches(html, @"상품코드\s*<span>([0-9-]+)</span>.*?<div class='tag-box'>(.*?)</div>", RegexOptions.Singleline);
                if (items.Count == 0 || items[0].Groups[1].Value == prevFirst) break;
                prevFirst = items[0].Groups[1].Value;
                foreach (Match m in items)
                    if (m.Groups[2].Value.Contains("icon_sellout") || m.Groups[2].Value.Contains("icon_danjong")) set.Add(m.Groups[1].Value);
                System.Threading.Thread.Sleep(300);
            }
            return set;
        }

        // 판매자관리코드·쿠팡 등록상품명에서 msgood 상품번호 (22021x2 → 22021, 0842-5 → 0842-5)
        public static string CodeOf(string s)
        {
            var m = Regex.Match((s ?? "").Trim(), @"^([0-9]+(?:-[0-9]+)?)(?:x[0-9]+)?$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        static IEnumerable<Dictionary<string, object>> Arr(object o) { return (o as IEnumerable ?? new object[0]).OfType<Dictionary<string, object>>(); }
        static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null; }

        // 바꿀 것 목록 (아직 바꾸지 않음)
        public static List<StockChange> Plan(AppSettings set, Action<string> log)
        {
            var sold = SoldOut(log);
            log("msgood4u 품절·단종 " + sold.Count + "개");
            var rec = Store.Load<StockRecord>(RecordPath) ?? new StockRecord();
            var list = new List<StockChange>();
            foreach (var a in set.Accounts)
            {
                MarketConfig c;
                try { c = Accounts.Config(a.Id); } catch { continue; }
                if (c == null) continue;
                if (c.HasNaver)
                {
                    log("#" + a.Id + " 네이버 상품 목록 읽는 중...");
                    var n = new NaverClient(c);
                    for (int p = 1; p <= 40; p++)
                    {
                        var res = Obj(json.DeserializeObject(n.Post("/v1/products/search", "{\"page\":" + p + ",\"size\":500,\"orderType\":\"REG_DATE\"}")));
                        var contents = Arr(res["contents"]).ToList();
                        foreach (var ct in contents)
                        {
                            string origin = S(ct, "originProductNo");
                            var ch = Arr(ct["channelProducts"]).FirstOrDefault();
                            if (ch == null) continue;
                            string code = CodeOf(S(ch, "sellerManagementCode"));
                            if (code == null) continue;
                            string status = S(ch, "statusType");
                            bool isSold = sold.Contains(code);
                            if (isSold && status == "SALE")
                                list.Add(new StockChange { Account = a.Id, Market = "네이버", Code = code, Name = S(ch, "name"), Action = "품절", OriginNo = origin });
                            else if (!isSold && (status == "OUTOFSTOCK" || status == "SUSPENSION") && rec.Naver.ContainsKey(a.Id + ":" + origin))
                                list.Add(new StockChange { Account = a.Id, Market = "네이버", Code = code, Name = S(ch, "name"), Action = "판매 재개", OriginNo = origin });
                        }
                        if (contents.Count == 0 || Convert.ToBoolean(res.ContainsKey("last") ? res["last"] : true)) break;
                    }
                }
                if (c.HasCoupang)
                {
                    log("#" + a.Id + " 쿠팡 상품 목록 읽는 중...");
                    var cp = new CoupangClient(c);
                    string token = "";
                    for (int p = 1; p <= 100; p++)
                    {
                        var res = Obj(json.DeserializeObject(cp.Send("GET", "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products?vendorId=" + cp.Vendor + "&maxPerPage=100" + (token != "" ? "&nextToken=" + token : ""), null)));
                        foreach (var d in Arr(res["data"]))
                        {
                            if (S(d, "statusName") != "승인완료") continue;
                            string code = CodeOf(S(d, "sellerProductName"));
                            if (code == null) continue;
                            string id = S(d, "sellerProductId");
                            bool isSold = sold.Contains(code), mine = rec.Coupang.ContainsKey(a.Id + ":" + id);
                            if (isSold && !mine)
                                list.Add(new StockChange { Account = a.Id, Market = "쿠팡", Code = code, Name = S(d, "sellerProductName"), Action = "품절", SellerProductId = id });
                            else if (!isSold && mine)
                                list.Add(new StockChange { Account = a.Id, Market = "쿠팡", Code = code, Name = S(d, "sellerProductName"), Action = "판매 재개", SellerProductId = id });
                        }
                        token = S(res, "nextToken") ?? "";
                        if (token == "") break;
                        System.Threading.Thread.Sleep(300);
                    }
                }
            }
            return list;
        }

        static void SetToday(object node, string key, bool soldOut, StockRecord rec, int stock)
        {
            var d = node as Dictionary<string, object>;
            if (d != null)
            {
                if (d.ContainsKey("todayStockQuantity"))
                {
                    long cur; long.TryParse(Convert.ToString(d["todayStockQuantity"]), out cur);
                    if (soldOut) { if (cur > 0) rec.TodayStock[key] = cur; d["todayStockQuantity"] = 0; }
                    else { long old; if (rec.TodayStock.TryGetValue(key, out old)) { d["todayStockQuantity"] = Math.Min(old, stock); rec.TodayStock.Remove(key); } }
                }
                foreach (var v in d.Values.ToList()) SetToday(v, key, soldOut, rec, stock);
            }
            else if (node is IEnumerable && !(node is string)) foreach (var v in (IEnumerable)node) SetToday(v, key, soldOut, rec, stock);
        }

        // 실제로 바꿈 (네이버: 판매중지 / 판매중, 쿠팡: 옵션별 판매중지 / 판매재개)
        public static void Apply(List<StockChange> changes, AppSettings set, Action<string> log)
        {
            var rec = Store.Load<StockRecord>(RecordPath) ?? new StockRecord();
            foreach (var ch in changes)
            {
                try
                {
                    var c = Accounts.Config(ch.Account);
                    if (ch.Market == "네이버")
                    {
                        var n = new NaverClient(c);
                        var body = Obj(json.DeserializeObject(n.Get("/v2/products/origin-products/" + ch.OriginNo)));
                        var origin = Obj(body["originProduct"]);
                        // 품절: 판매중지 (오늘출발 상품은 재고 0으로 둘 수 없어서 재고는 그대로 둠) / 판매 재개: 판매중
                        origin["statusType"] = ch.Action == "품절" ? "SUSPENSION" : "SALE";
                        long st; long.TryParse(Convert.ToString(origin.ContainsKey("stockQuantity") ? origin["stockQuantity"] : 0), out st);
                        if (ch.Action != "품절" && st <= 0) origin["stockQuantity"] = set.Stock;
                        n.Put("/v2/products/origin-products/" + ch.OriginNo, json.Serialize(body));
                        string key = ch.Account + ":" + ch.OriginNo;
                        if (ch.Action == "품절") rec.Naver[key] = ch.Code; else rec.Naver.Remove(key);
                    }
                    else
                    {
                        var cp = new CoupangClient(c);
                        var res = Obj(json.DeserializeObject(cp.Send("GET", "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products/" + ch.SellerProductId, null)));
                        foreach (var item in Arr(Obj(res["data"])["items"]))
                        {
                            string vid = S(item, "vendorItemId");
                            if (string.IsNullOrEmpty(vid)) continue;
                            cp.Send("PUT", "/v2/providers/seller_api/apis/api/v1/marketplace/vendor-items/" + vid + "/sales/" + (ch.Action == "품절" ? "stop" : "resume"), null);
                            System.Threading.Thread.Sleep(200);
                        }
                        string key = ch.Account + ":" + ch.SellerProductId;
                        if (ch.Action == "품절") rec.Coupang[key] = ch.Code; else rec.Coupang.Remove(key);
                    }
                    log("✔ " + ch);
                }
                catch (Exception ex) { log("✖ " + ch + " : " + ex.Message); }
                System.Threading.Thread.Sleep(300);
            }
            rec.LastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            Store.Save(RecordPath, rec);
        }
    }

    // 판매 실적: 최근 30일 주문을 상품번호별로 모아 보여 주고, 팔린 상품의 상품명 단어를 학습 (다음 상품명에서 먼저 고려)
    public class SaleRow { public string Code, Name; public int Count; public long Amount; public string Market; }

    public static class SalesStats
    {
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        static IEnumerable<Dictionary<string, object>> Arr(object o) { return (o as IEnumerable ?? new object[0]).OfType<Dictionary<string, object>>(); }
        static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null; }
        static long L(Dictionary<string, object> d, string k) { long x; return long.TryParse(S(d, k), out x) ? x : 0; }

        public static List<SaleRow> Load(AppSettings set, int days, Action<string> log, List<string> problems)
        {
            var rows = new Dictionary<string, SaleRow>();
            Action<string, string, string, int, long> add = (market, code, name, cnt, amt) =>
            {
                string key = market + ":" + (code ?? name);
                SaleRow r;
                if (!rows.TryGetValue(key, out r)) rows[key] = r = new SaleRow { Market = market, Code = code, Name = name };
                r.Count += cnt; r.Amount += amt;
                if (string.IsNullOrEmpty(r.Name)) r.Name = name;
            };
            foreach (var a in set.Accounts)
            {
                MarketConfig c;
                try { c = Accounts.Config(a.Id); } catch { continue; }
                if (c == null) continue;
                if (c.HasNaver)
                {
                    try
                    {
                        var n = new NaverClient(c);
                        var ids = new List<string>();
                        for (int d = days; d >= 1; d--)
                        {
                            log("#" + a.Id + " 네이버 주문 읽는 중... (" + (days - d + 1) + "/" + days + "일)");
                            var from = DateTime.Now.AddDays(-d);
                            string q = "/v1/pay-order/seller/product-orders/last-changed-statuses?lastChangedFrom=" + Uri.EscapeDataString(from.ToString("yyyy-MM-dd'T'HH:mm:ss.fff+09:00"))
                                     + "&lastChangedTo=" + Uri.EscapeDataString(from.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff+09:00"));
                            foreach (Match m in Regex.Matches(n.Get(q), "\"productOrderId\"\\s*:\\s*\"([0-9]+)\"")) ids.Add(m.Groups[1].Value);
                            System.Threading.Thread.Sleep(250);
                        }
                        ids = ids.Distinct().ToList();
                        for (int i = 0; i < ids.Count; i += 300)
                        {
                            string body = "{\"productOrderIds\":[" + string.Join(",", ids.Skip(i).Take(300).Select(x => "\"" + x + "\"")) + "]}";
                            var res = Obj(json.DeserializeObject(n.Post("/v1/pay-order/seller/product-orders/query", body)));
                            foreach (var o in Arr(res["data"]))
                            {
                                var po = Obj(o.ContainsKey("productOrder") ? o["productOrder"] : null);
                                if (po == null) continue;
                                string st = S(po, "productOrderStatus") ?? "";
                                if (st.Contains("CANCEL") || st.Contains("RETURN")) continue;
                                add("네이버", StockSync.CodeOf(S(po, "sellerProductCode")), S(po, "productName"), (int)L(po, "quantity"), L(po, "totalPaymentAmount"));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        problems.Add("#" + a.Id + " 네이버 주문: " + (ex.Message.Contains("403") ? "주문 조회 권한이 없습니다. 네이버 커머스API센터 → 애플리케이션 → API 권한에서 '주문' 조회를 켜 주세요." : ex.Message));
                    }
                }
                if (c.HasCoupang)
                {
                    try
                    {
                        var cp = new CoupangClient(c);
                        foreach (var st in new[] { "ACCEPT", "INSTRUCT", "DEPARTURE", "DELIVERING", "FINAL_DELIVERY", "NONE_TRACKING" })
                        {
                            log("#" + a.Id + " 쿠팡 주문 읽는 중... (" + st + ")");
                            string token = "";
                            for (int p = 0; p < 50; p++)
                            {
                                string q = "/v2/providers/openapi/apis/api/v4/vendors/" + cp.Vendor + "/ordersheets?createdAtFrom=" + DateTime.Now.AddDays(-Math.Min(days, 30)).ToString("yyyy-MM-dd")
                                         + "&createdAtTo=" + DateTime.Now.ToString("yyyy-MM-dd") + "&status=" + st + "&maxPerPage=50" + (token != "" ? "&nextToken=" + token : "");
                                var res = Obj(json.DeserializeObject(cp.Send("GET", q, null)));
                                foreach (var o in Arr(res["data"]))
                                    foreach (var it in Arr(o.ContainsKey("orderItems") ? o["orderItems"] : null))
                                        add("쿠팡", StockSync.CodeOf(S(it, "sellerProductName")), S(it, "sellerProductName") + " " + S(it, "vendorItemName"), (int)L(it, "shippingCount"), L(it, "orderPrice"));
                                token = S(res, "nextToken") ?? "";
                                if (token == "") break;
                                System.Threading.Thread.Sleep(250);
                            }
                        }
                    }
                    catch (Exception ex) { problems.Add("#" + a.Id + " 쿠팡 주문: " + ex.Message); }
                }
            }
            return rows.Values.OrderByDescending(r => r.Count).ToList();
        }

        // 팔린 상품의 상품명 단어를 학습 (상품번호로 판매도구 작업의 스토어 상품명을 찾고, 없으면 주문의 상품명)
        public static int Learn(List<SaleRow> rows, Learned learned)
        {
            learned.SoldWords.Clear();
            int n = 0;
            foreach (var r in rows.Where(r => r.Count > 0))
            {
                string name = r.Name ?? "";
                if (r.Code != null)
                {
                    var job = Store.Load<Job>(Store.JobPath(r.Code));
                    if (job != null && job.Names != null && job.Names.Count > 0) name = string.Join(" ", job.Names.Values);
                }
                foreach (var w in Regex.Split(name, @"\s+").Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length >= 2 && !Regex.IsMatch(x, @"^[0-9]")).Distinct())
                {
                    int c; learned.SoldWords.TryGetValue(w, out c); learned.SoldWords[w] = c + r.Count; n++;
                }
            }
            return n;
        }
    }
}
