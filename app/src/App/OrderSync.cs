using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using ProductUploader;

namespace SalesTool
{
    // 스토어 결제완료 주문 → msgood4u 상품대량발주 (적립금 결제) → 스토어에 발주확인 / 송장 입력
    public class StoreOrder
    {
        public string Market, Account;            // 네이버 / 쿠팡, 계정 1·2·3
        public string Key;                        // 중복 방지용: 네이버 상품주문번호 / 쿠팡 배송번호:옵션ID
        public string OrderId;                    // 스토어 주문번호 (msgood 원장주문번호로 넣음)
        public string ProductOrderId;             // 네이버 상품주문번호
        public string ShipmentBoxId, VendorItemId;// 쿠팡
        public string Code;                       // msgood 상품번호
        public int Qty;                           // msgood 발주 수량 (묶음 수를 곱한 것)
        public string Orderer, Receiver, Zip, Addr, Tel1, Tel2, Memo, ProductName;
        public string Problem;                    // 발주하지 못하는 이유 (상품번호를 모름 등)
        public override string ToString() { return Market + " #" + Account + " " + OrderId + " [" + (Code ?? "?") + " x" + Qty + "] " + Receiver + (Problem != null ? " (" + Problem + ")" : ""); }
    }

    public class OrderDone
    {
        public string Market, Account, Key, OrderId, ProductOrderId, ShipmentBoxId, VendorItemId, Code, Orderer, Receiver, OrderedAt, Tracking, Courier, TrackedAt;
        public int Qty;
    }

    public class OrderRecord
    {
        public Dictionary<string, OrderDone> Done = new Dictionary<string, OrderDone>();   // 발주한 주문 (Key → 기록)
        public Dictionary<string, string> Pending = new Dictionary<string, string>();      // 발주를 보냈지만 결과를 확인 못 한 주문 → 자동으로 다시 보내지 않음
        public string LastShortage;                                                       // 마지막 적립금 부족 안내
    }

    public static class OrderSync
    {
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public static string RecordPath { get { return Path.Combine(Store.Dir, "orders.json"); } }
        static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        static IEnumerable<Dictionary<string, object>> Arr(object o) { return (o as IEnumerable ?? new object[0]).OfType<Dictionary<string, object>>(); }
        static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null; }

        public static OrderRecord LoadRecord() { return Store.Load<OrderRecord>(RecordPath); }
        public static void SaveRecord(OrderRecord r) { Store.Save(RecordPath, r); }

        // 판매자관리코드(22021x2) → 상품번호 22021, 묶음 2
        static void CodeAndMult(string s, out string code, out int mult)
        {
            code = StockSync.CodeOf(s); mult = 1;
            var m = Regex.Match((s ?? "").Trim(), @"x([0-9]+)$", RegexOptions.IgnoreCase);
            if (m.Success) mult = Math.Max(1, int.Parse(m.Groups[1].Value));
        }

        // 쿠팡 옵션 이름에서 묶음 수: "60정 3개" → 3, "18g 40개입 5개" → 5, "60회분 3세트" → 3, "1개 120정" → 1
        static int CoupangMult(string itemName)
        {
            var ms = Regex.Matches(itemName ?? "", @"([0-9]+)\s*(개|세트|set)(?!입)", RegexOptions.IgnoreCase);
            return ms.Count > 0 ? Math.Max(1, int.Parse(ms[ms.Count - 1].Groups[1].Value)) : 1;
        }

        // 전화번호에 - 넣기 (msgood 양식: 010-0000-0000)
        public static string Tel(string t)
        {
            string d = Regex.Replace(t ?? "", "[^0-9]", "");
            if (d.Length == 0) return "";
            if ((t ?? "").Contains("-")) return t.Trim();
            if (d.StartsWith("02")) return d.Length == 9 ? d.Substring(0, 2) + "-" + d.Substring(2, 3) + "-" + d.Substring(5) : d.Substring(0, 2) + "-" + d.Substring(2, Math.Max(0, d.Length - 6)) + "-" + d.Substring(Math.Max(2, d.Length - 4));
            if (d.Length == 11) return d.Substring(0, 3) + "-" + d.Substring(3, 4) + "-" + d.Substring(7);
            if (d.Length == 12) return d.Substring(0, 4) + "-" + d.Substring(4, 4) + "-" + d.Substring(8);
            if (d.Length == 10) return d.Substring(0, 3) + "-" + d.Substring(3, 3) + "-" + d.Substring(6);
            return d;
        }

        // 스토어의 새 결제완료 주문 (아직 발주 안 한 것)
        public static List<StoreOrder> FetchNew(AppSettings set, Action<string> log, List<string> problems)
        {
            var rec = LoadRecord();
            var list = new List<StoreOrder>();
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
                        for (int d = 3; d >= 0; d--)
                        {
                            var from = DateTime.Now.AddDays(-d - 1);
                            var to = d == 0 ? DateTime.Now.AddMinutes(-1) : from.AddDays(1).AddSeconds(-1);
                            string q = "/v1/pay-order/seller/product-orders/last-changed-statuses?lastChangedFrom=" + Uri.EscapeDataString(from.ToString("yyyy-MM-dd'T'HH:mm:ss.fff+09:00"))
                                     + "&lastChangedTo=" + Uri.EscapeDataString(to.ToString("yyyy-MM-dd'T'HH:mm:ss.fff+09:00"));
                            foreach (Match m in Regex.Matches(n.Get(q), "\"productOrderId\"\\s*:\\s*\"([0-9]+)\"")) ids.Add(m.Groups[1].Value);
                            System.Threading.Thread.Sleep(250);
                        }
                        ids = ids.Distinct().Where(id => !rec.Done.ContainsKey("N:" + id) && !rec.Pending.ContainsKey("N:" + id)).ToList();
                        for (int i = 0; i < ids.Count; i += 300)
                        {
                            string body = "{\"productOrderIds\":[" + string.Join(",", ids.Skip(i).Take(300).Select(x => "\"" + x + "\"")) + "]}";
                            var res = Obj(json.DeserializeObject(n.Post("/v1/pay-order/seller/product-orders/query", body)));
                            foreach (var o in Arr(res["data"]))
                            {
                                var po = Obj(o.ContainsKey("productOrder") ? o["productOrder"] : null);
                                var od = Obj(o.ContainsKey("order") ? o["order"] : null);
                                if (po == null) continue;
                                if (S(po, "productOrderStatus") != "PAYED") continue;                 // 결제완료만
                                if (S(po, "placeOrderStatus") == "OK") continue;                      // 이미 발주확인한 것
                                var addr = Obj(po.ContainsKey("shippingAddress") ? po["shippingAddress"] : null);
                                string code; int mult; CodeAndMult(S(po, "sellerProductCode"), out code, out mult);
                                int qty; int.TryParse(S(po, "quantity"), out qty);
                                var so = new StoreOrder
                                {
                                    Market = "네이버", Account = a.Id, ProductOrderId = S(po, "productOrderId"), Key = "N:" + S(po, "productOrderId"),
                                    OrderId = S(od, "orderId") ?? S(po, "productOrderId"), Code = code, Qty = Math.Max(1, qty) * mult,
                                    Orderer = S(od, "ordererName"), Receiver = S(addr, "name"), Zip = S(addr, "zipCode"),
                                    Addr = ((S(addr, "baseAddress") ?? "") + " " + (S(addr, "detailedAddress") ?? "")).Trim(),
                                    Tel1 = Tel(S(addr, "tel1")), Tel2 = Tel(S(addr, "tel2")), Memo = S(po, "shippingMemo"), ProductName = S(po, "productName")
                                };
                                if (code == null) so.Problem = "판매자관리코드에 msgood 상품번호가 없음";
                                else if (string.IsNullOrEmpty(so.Addr) || string.IsNullOrEmpty(so.Receiver)) so.Problem = "받는 사람·주소 없음";
                                list.Add(so);
                            }
                        }
                    }
                    catch (Exception ex) { problems.Add("#" + a.Id + " 네이버 주문: " + ex.Message); }
                }
                if (c.HasCoupang)
                {
                    try
                    {
                        var cp = new CoupangClient(c);
                        string token = "";
                        for (int p = 0; p < 20; p++)
                        {
                            string q = "/v2/providers/openapi/apis/api/v4/vendors/" + cp.Vendor + "/ordersheets?createdAtFrom=" + DateTime.Now.AddDays(-7).ToString("yyyy-MM-dd")
                                     + "&createdAtTo=" + DateTime.Now.ToString("yyyy-MM-dd") + "&status=ACCEPT&maxPerPage=50" + (token != "" ? "&nextToken=" + token : "");
                            var res = Obj(json.DeserializeObject(cp.Send("GET", q, null)));
                            foreach (var o in Arr(res["data"]))
                            {
                                var rcv = Obj(o.ContainsKey("receiver") ? o["receiver"] : null);
                                var ord = Obj(o.ContainsKey("orderer") ? o["orderer"] : null);
                                foreach (var it in Arr(o.ContainsKey("orderItems") ? o["orderItems"] : null))
                                {
                                    string key = "C:" + S(o, "shipmentBoxId") + ":" + S(it, "vendorItemId");
                                    if (rec.Done.ContainsKey(key) || rec.Pending.ContainsKey(key)) continue;
                                    string code = StockSync.CodeOf(S(it, "sellerProductName")) ?? StockSync.CodeOf(S(it, "externalVendorSkuCode"));
                                    int cnt; int.TryParse(S(it, "shippingCount"), out cnt);
                                    var so = new StoreOrder
                                    {
                                        Market = "쿠팡", Account = a.Id, Key = key, OrderId = S(o, "orderId"), ShipmentBoxId = S(o, "shipmentBoxId"), VendorItemId = S(it, "vendorItemId"),
                                        Code = code, Qty = Math.Max(1, cnt) * CoupangMult(S(it, "vendorItemName")),
                                        Orderer = S(ord, "name"), Receiver = S(rcv, "name"), Zip = S(rcv, "postCode"),
                                        Addr = ((S(rcv, "addr1") ?? "") + " " + (S(rcv, "addr2") ?? "")).Trim(),
                                        Tel1 = Tel(S(rcv, "safeNumber") ?? S(rcv, "receiverNumber")), Tel2 = Tel(S(rcv, "receiverNumber")), Memo = S(o, "parcelPrintMessage"),
                                        ProductName = S(it, "sellerProductName") + " " + S(it, "vendorItemName")
                                    };
                                    if (so.Tel2 == so.Tel1) so.Tel2 = "";
                                    if (code == null) so.Problem = "쿠팡 등록상품명에 msgood 상품번호가 없음";
                                    else if (string.IsNullOrEmpty(so.Addr) || string.IsNullOrEmpty(so.Receiver)) so.Problem = "받는 사람·주소 없음";
                                    list.Add(so);
                                }
                            }
                            token = S(res, "nextToken") ?? "";
                            if (token == "") break;
                            System.Threading.Thread.Sleep(250);
                        }
                    }
                    catch (Exception ex) { problems.Add("#" + a.Id + " 쿠팡 주문: " + ex.Message); }
                }
            }
            return list;
        }

        // msgood4u 상품대량발주 엑셀 (HTML 표 형식 .xls: msgood 가 읽는 형식)
        public static string BuildExcel(List<StoreOrder> orders)
        {
            Func<string, string> h = s => System.Net.WebUtility.HtmlEncode(s ?? "");
            var hdr = new[] { "SEQ", "주문자", "우편번호", "주소", "연락처1", "연락처2", "배송메세지", "상품코드", "상품명", "수량", "옵션1", "옵션1수량", "옵션2", "옵션2수량", "묶음배송", "원장주문번호" };
            var sb = new StringBuilder("<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\"></head><body><table>");
            sb.Append("<tr>" + string.Join("", hdr.Select(x => "<td>" + h(x) + "</td>")) + "</tr>");
            // 같은 받는 사람·주소가 둘 이상이면 묶음배송
            var groups = orders.GroupBy(o => (o.Receiver ?? "") + "|" + (o.Addr ?? "")).ToDictionary(g => g.Key, g => g.Count());
            int seq = 1;
            foreach (var o in orders)
            {
                string who = string.IsNullOrEmpty(o.Orderer) || o.Orderer == o.Receiver ? o.Receiver : o.Orderer + "(" + o.Receiver + ")";
                string bundle = groups[(o.Receiver ?? "") + "|" + (o.Addr ?? "")] > 1 ? "Y" : "";
                var cells = new[] { (seq++).ToString(), who, o.Zip, o.Addr, o.Tel1, o.Tel2, o.Memo, o.Code, "", o.Qty.ToString(), "", "", "", "", bundle, o.OrderId };
                sb.Append("<tr>" + string.Join("", cells.Select(x => "<td>" + h(x) + "</td>")) + "</tr>");
            }
            sb.Append("</table></body></html>");
            return sb.ToString();
        }

        // 발주 뒤 스토어에 알림: 네이버 발주확인, 쿠팡 상품준비중
        public static void ConfirmInStores(List<StoreOrder> orders, Action<string> log)
        {
            foreach (var g in orders.GroupBy(o => o.Market + ":" + o.Account))
            {
                try
                {
                    var c = Accounts.Config(g.First().Account);
                    if (g.First().Market == "네이버")
                    {
                        var ids = g.Select(o => o.ProductOrderId).Distinct().ToList();
                        new NaverClient(c).Post("/v1/pay-order/seller/product-orders/confirm", "{\"productOrderIds\":[" + string.Join(",", ids.Select(x => "\"" + x + "\"")) + "]}");
                        log("네이버 #" + g.First().Account + " 발주확인 " + ids.Count + "건");
                    }
                    else
                    {
                        var cp = new CoupangClient(c);
                        var boxes = g.Select(o => o.ShipmentBoxId).Distinct().ToList();
                        cp.Send("PUT", "/v2/providers/openapi/apis/api/v4/vendors/" + cp.Vendor + "/ordersheets/acknowledgement",
                            "{\"vendorId\":\"" + cp.Vendor + "\",\"shipmentBoxIds\":[" + string.Join(",", boxes) + "]}");
                        log("쿠팡 #" + g.First().Account + " 상품준비중 처리 " + boxes.Count + "건");
                    }
                }
                catch (Exception ex) { log("스토어 발주확인 오류 (" + g.Key + "): " + ex.Message); }
            }
        }

        // ── 송장
        // 택배사 이름 → 네이버·쿠팡 택배사 코드
        public static void Courier(string text, string fallback, out string naver, out string coupang, out string name)
        {
            string t = (text ?? "") + " " + (fallback ?? "");
            var map = new[]
            {
                new[] { "대한통운|CJ|씨제이", "CJGLS", "CJGLS", "CJ대한통운" }, new[] { "한진", "HANJIN", "HANJIN", "한진택배" }, new[] { "롯데", "HYUNDAI", "HYUNDAI", "롯데택배" },
                new[] { "우체국", "EPOST", "EPOST", "우체국택배" }, new[] { "로젠", "KGB", "KGB", "로젠택배" }, new[] { "경동", "KDEXP", "KDEXP", "경동택배" },
                new[] { "대신", "DAESIN", "DAESIN", "대신택배" }, new[] { "일양", "ILYANG", "ILYANG", "일양로지스" }, new[] { "합동", "HDEXP", "HDEXP", "합동택배" },
                new[] { "CU|편의점", "CVSNET", "CVSNET", "CU편의점택배" },
            };
            foreach (var m in map)
                if (Regex.IsMatch(t, m[0], RegexOptions.IgnoreCase)) { naver = m[1]; coupang = m[2]; name = m[3]; return; }
            naver = coupang = name = null;
        }

        public static string SendTracking(OrderDone d, string courierText, string defaultCourier, string number)
        {
            string nv, cp, nm; Courier(courierText, defaultCourier, out nv, out cp, out nm);
            if (nv == null) return "택배사를 알 수 없음 (" + courierText + ")";
            var c = Accounts.Config(d.Account);
            if (d.Market == "네이버")
            {
                string body = "{\"dispatchProductOrders\":[{\"productOrderId\":\"" + d.ProductOrderId + "\",\"deliveryMethod\":\"DELIVERY\",\"deliveryCompanyCode\":\"" + nv
                            + "\",\"trackingNumber\":\"" + number + "\",\"dispatchDate\":\"" + DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff+09:00") + "\"}]}";
                new NaverClient(c).Post("/v1/pay-order/seller/product-orders/dispatch", body);
            }
            else
            {
                var client = new CoupangClient(c);
                string body = "{\"vendorId\":\"" + client.Vendor + "\",\"orderSheetInvoiceApplyDtos\":[{\"shipmentBoxId\":" + d.ShipmentBoxId + ",\"orderId\":" + d.OrderId + ",\"vendorItemId\":" + d.VendorItemId
                            + ",\"deliveryCompanyCode\":\"" + cp + "\",\"invoiceNumber\":\"" + number + "\",\"splitShipping\":false,\"preSplitShipped\":false,\"estimatedShippingDate\":\"\"}]}";
                client.Send("POST", "/v2/providers/openapi/apis/api/v4/vendors/" + client.Vendor + "/orders/invoices", body);
            }
            d.Tracking = number; d.Courier = nm; d.TrackedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            return null;
        }
    }
}
