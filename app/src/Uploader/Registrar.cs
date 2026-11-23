using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ProductUploader
{
    public class CoupangOption
    {
        public int Count;        // 1~6개
        public long Price;
        public string ImageFile; // 이 옵션의 대표이미지
    }

    public class NaverItem
    {
        public string Name;
        public string ImageFile;
        public long Price;
        public string SellerCode;
        public string Amount;    // 고시정보 용량 (예: 60정 / 120정)
        public int Packs = 1;    // 1개 / 2개 상품 (제품용량 = 1개월분 / 2개월분 계산)
        public string BrandName, MakerName;   // 네이버 브랜드·제조사 칸 (상품명에 못 넣은 키워드)
        public List<string> ExtraImages = new List<string>(); // 추가이미지 (최대 9장)
    }

    public class ProductInput
    {
        public string Code, Brand, DetailHtml;
        public int Stock = 300;
        public string Volume;    // 1개 용량 (예: 60정)
        public string SourceName; // msgood 상품명 (구매 옵션: 중량·용량·정 수를 읽음, 예: 50gx12포 → 600g)
        public NaverItem Naver1, Naver2;
        public string CoupangName;
        public List<CoupangOption> CoupangOptions = new List<CoupangOption>();
        public string NaverCategory, CoupangCategory; // 비우면 틀 상품 카테고리
        public bool Test;        // true: 네이버 판매중지(전시중지), 쿠팡 승인요청 안 함
        public bool CoupangApprove;
        public ProductFacts Facts; // 상세 이미지에서 읽은 정보 (없으면 상품속성·검색필터 비움)
        public List<string> SearchTags = new List<string>();  // 쿠팡 검색어 (최대 20개, 각 20자 이내)
        public List<string> ExtraImages = new List<string>(); // 쿠팡 옵션마다 대표이미지 뒤에 붙일 추가이미지
        public bool HealthFunctional; // 건강기능식품(인증마크 있음)이면 true → 고시정보 건강기능식품, 아니면 가공식품
    }

    // 틀 상품의 설정(배송, 반품, 고시정보 등)을 그대로 쓰고 상품마다 바뀌는 칸만 바꿔서 등록
    public class Registrar
    {
        readonly NaverClient naver;
        readonly CoupangClient coupang;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        Dictionary<string, object> naverTemplate, coupangTemplate;
        public string NaverTemplateName, CoupangTemplateName;

        public Registrar(NaverClient naver, CoupangClient coupang)
        {
            this.naver = naver;
            this.coupang = coupang;
        }

        public void LoadTemplates(string naverChannelNo, string coupangProductId)
        {
            if (naver != null && !string.IsNullOrWhiteSpace(naverChannelNo))
            {
                naverTemplate = Obj(json.DeserializeObject(naver.Get("/v2/products/channel-products/" + naverChannelNo.Trim())));
                NaverTemplateName = (string)Obj(naverTemplate["originProduct"])["name"];
            }
            if (coupang != null && !string.IsNullOrWhiteSpace(coupangProductId))
            {
                var res = Obj(json.DeserializeObject(coupang.Send("GET", "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products/" + coupangProductId.Trim(), null)));
                coupangTemplate = Obj(res["data"]);
                CoupangTemplateName = (string)coupangTemplate["displayProductName"];
            }
        }

        public bool HasNaverTemplate { get { return naverTemplate != null; } }
        public bool HasCoupangTemplate { get { return coupangTemplate != null; } }
        public string NaverTemplateCategory { get { return naverTemplate == null ? null : Convert.ToString(Obj(naverTemplate["originProduct"])["leafCategoryId"]); } }
        public string CoupangTemplateCategory { get { return coupangTemplate == null ? null : Convert.ToString(coupangTemplate["displayCategoryCode"]); } }

        // ── 네이버
        public string RegisterNaver(ProductInput p, NaverItem item, Action<string> log)
        {
            if (naverTemplate == null) throw new Exception("네이버 틀 상품을 먼저 불러오세요.");
            log("네이버 대표이미지 올리는 중...");
            var upl = naver.UploadImages(new[] { item.ImageFile }.Concat(item.ExtraImages.Take(9)).ToList());
            string imageUrl = upl[0];

            var origin = Obj(Clone(naverTemplate["originProduct"]));
            origin["name"] = item.Name;
            origin["statusType"] = "SALE";
            origin["salePrice"] = item.Price;
            origin["stockQuantity"] = p.Stock;
            origin["detailContent"] = NaverDetail(p.DetailHtml, log);
            origin["images"] = new Dictionary<string, object> { { "representativeImage", new Dictionary<string, object> { { "url", imageUrl } } } };
            if (upl.Count > 1)
                ((Dictionary<string, object>)origin["images"])["optionalImages"] = upl.Skip(1).Select(u => (object)new Dictionary<string, object> { { "url", u } }).ToArray();
            if (!string.IsNullOrWhiteSpace(p.NaverCategory)) origin["leafCategoryId"] = p.NaverCategory.Trim();
            origin.Remove("customerBenefit"); // 틀 상품의 할인이 따라오지 않게 하고 리뷰 포인트만 아래에서 다시 넣음
            var benefit = Obj(Obj(naverTemplate["originProduct"]).ContainsKey("customerBenefit") ? Obj(naverTemplate["originProduct"])["customerBenefit"] : null);
            if (benefit != null && benefit.ContainsKey("reviewPointPolicy"))
                origin["customerBenefit"] = new Dictionary<string, object> { { "reviewPointPolicy", Clone(benefit["reviewPointPolicy"]) } };

            var attr = Obj(origin["detailAttribute"]);
            attr.Remove("productAttributes"); // 틀 상품의 속성값은 쓰지 않고, 상세 이미지에서 읽은 정보로 다시 채움
            if (p.Facts != null)
            {
                log("네이버 상품속성 채우는 중...");
                var pa = NaverAttributes(Convert.ToString(origin["leafCategoryId"]), p.Facts, Math.Max(1, item.Packs), p.HealthFunctional, log);
                if (pa.Count > 0) attr["productAttributes"] = pa;
            }
            attr["sellerCodeInfo"] = new Dictionary<string, object> { { "sellerManagementCode", item.SellerCode ?? "" } };
            // 상품 주요정보: 브랜드는 자체제작 상품, 제조사는 비움
            attr["itselfProductionProductYn"] = true;
            var search = new Dictionary<string, object> { { "catalogMatchingYn", false } };
            if (!string.IsNullOrWhiteSpace(item.BrandName)) search["brandName"] = item.BrandName.Trim();
            if (!string.IsNullOrWhiteSpace(item.MakerName)) search["manufacturerName"] = item.MakerName.Trim();
            attr["naverShoppingSearchInfo"] = search;
            if (attr.ContainsKey("seoInfo"))
            {
                var seo = Obj(attr["seoInfo"]);
                seo["pageTitle"] = item.Name;
                seo["metaDescription"] = item.Name;
            }
            // 상품정보제공고시: 건강기능식품 마크가 없으면 가공식품으로
            if (!p.HealthFunctional) attr["productInfoProvidedNotice"] = GeneralFoodNotice(item.Name);
            else if (Convert.ToString(Obj(attr["productInfoProvidedNotice"])["productInfoProvidedNoticeType"]) != "DIET_FOOD") attr["productInfoProvidedNotice"] = DietFoodNotice(item.Name);
            SetNoticeAmount(attr, item.Amount);

            var channel = new Dictionary<string, object>
            {
                { "naverShoppingRegistration", true },
                { "channelProductDisplayStatusType", "ON" },   // 전시상태는 항상 전시중 (사용자 요청)
                { "storeKeepExclusiveProduct", false }
            };
            // 노출채널 공지사항: 틀 상품에 걸어 둔 공지(기타·협력업체 안내사항)를 그대로 연결
            var tChannel = Obj(naverTemplate["smartstoreChannelProduct"]);
            if (tChannel != null && tChannel.ContainsKey("bbsSeq") && tChannel["bbsSeq"] != null) channel["bbsSeq"] = tChannel["bbsSeq"];
            var body = new Dictionary<string, object> { { "originProduct", origin }, { "smartstoreChannelProduct", channel } };

            log("네이버 상품 등록 중... (" + item.Name + ")");
            var res = Obj(json.DeserializeObject(naver.Post("/v2/products", json.Serialize(body))));
            return "원상품번호 " + res["originProductNo"] + " / 스마트스토어 상품번호 " + res["smartstoreChannelProductNo"];
        }

        const string SeeDetail = "상품상세참조";

        static Dictionary<string, object> NoticeBase()
        {
            return new Dictionary<string, object>
            {
                { "returnCostReason", "0" }, { "noRefundReason", "0" }, { "qualityAssuranceStandard", "0" },
                { "compensationProcedure", "0" }, { "troubleShootingContents", "0" }
            };
        }

        // 가공식품 (기존 스토어 상품의 가공식품 고시와 같은 칸)
        static Dictionary<string, object> GeneralFoodNotice(string name)
        {
            var d = NoticeBase();
            d["productName"] = name;
            foreach (string k in new[] { "foodType", "producer", "location", "packDateText", "consumptionDateText", "weight", "amount", "ingredients", "nutritionFacts", "consumerSafetyCaution", "customerServicePhoneNumber" })
                d[k] = SeeDetail;
            d["geneticallyModified"] = false;
            d["importDeclarationCheck"] = false;
            return new Dictionary<string, object> { { "productInfoProvidedNoticeType", "GENERAL_FOOD" }, { "generalFood", d } };
        }

        // 건강기능식품 (틀 상품의 건강기능식품 고시와 같은 칸)
        static Dictionary<string, object> DietFoodNotice(string name)
        {
            var d = NoticeBase();
            d["productName"] = name;
            foreach (string k in new[] { "producer", "location", "expirationDateText", "consumptionDateText", "storageMethod", "weight", "amount", "ingredients", "nutritionFacts", "specification", "cautionAndSideEffect", "nonMedicinalUsesMessage", "consumerSafetyCaution", "customerServicePhoneNumber" })
                d[k] = SeeDetail;
            d["geneticallyModified"] = false;
            d["importDeclarationCheck"] = false;
            return new Dictionary<string, object> { { "productInfoProvidedNoticeType", "DIET_FOOD" }, { "dietFood", d } };
        }

        static void SetNoticeAmount(Dictionary<string, object> attr, string amount)
        {
            if (string.IsNullOrWhiteSpace(amount) || !attr.ContainsKey("productInfoProvidedNotice")) return;
            foreach (var v in Obj(attr["productInfoProvidedNotice"]).Values)
            {
                var d = v as Dictionary<string, object>;
                if (d == null) continue;
                foreach (string k in new[] { "amount", "weight", "capacity", "volume" })
                    if (d.ContainsKey(k)) { d[k] = amount; return; }
            }
        }

        // ── 쿠팡
        public string RegisterCoupang(ProductInput p, string imageUrlForNaverUpload, Action<string> log)
        {
            if (coupangTemplate == null) throw new Exception("쿠팡 틀 상품을 먼저 불러오세요.");
            if (p.CoupangOptions.Count == 0) throw new Exception("쿠팡 옵션을 하나 이상 체크하세요.");
            var t = coupangTemplate;

            // 쿠팡은 이미지 주소를 받아 가므로, 이미지를 네이버 이미지 서버에 올려 공개 주소를 만듦
            log("쿠팡 옵션 이미지 올리는 중...");
            var files = p.CoupangOptions.Select(o => o.ImageFile).Concat(p.ExtraImages).Distinct().ToList();
            var urls = naver.UploadImages(files);
            var urlOf = files.Zip(urls, (f, u) => new { f, u }).ToDictionary(x => x.f, x => x.u);

            var body = new Dictionary<string, object>();
            foreach (string k in new[] { "displayCategoryCode", "deliveryMethod", "deliveryCompanyCode", "deliveryChargeType", "deliveryCharge",
                                         "freeShipOverAmount", "deliveryChargeOnReturn", "remoteAreaDeliverable", "unionDeliveryType",
                                         "returnCenterCode", "returnChargeName", "companyContactNumber", "returnZipCode", "returnAddress",
                                         "returnAddressDetail", "returnCharge", "outboundShippingPlaceCode", "vendorUserId",
                                         "afterServiceInformation", "afterServiceContactNumber", "extraInfoMessage", "bundleInfo" })
                if (t.ContainsKey(k) && t[k] != null) body[k] = Clone(t[k]);
            if (!string.IsNullOrWhiteSpace(p.CoupangCategory)) body["displayCategoryCode"] = long.Parse(p.CoupangCategory.Trim());
            body["vendorId"] = coupang.Vendor;
            body["sellerProductName"] = p.Code ?? p.CoupangName;
            body["displayProductName"] = p.CoupangName;
            body["generalProductName"] = p.CoupangName;
            body["productGroup"] = "";
            // 브랜드는 항상 자체브랜드 (윙에서 자체브랜드로 등록한 상품은 API 에서 GENERIC 으로 저장됨)
            body["brand"] = "GENERIC";
            body["manufacture"] = "GENERIC";
            // 판매기간 설정 안 함 = 오늘부터 2099-12-31 까지 (윙에서 등록한 기존 상품과 같은 값)
            body["saleStartedAt"] = DateTime.Today.ToString("yyyy-MM-dd'T'00:00:00");
            body["saleEndedAt"] = "2099-12-31T00:00:00";
            body["requested"] = p.CoupangApprove;

            log("쿠팡 카테고리 필수 항목 확인 중...");
            var meta = CoupangMeta(Convert.ToString(body["displayCategoryCode"]));
            var mandatory = MandatoryAttributes(meta);

            var tItems = ((IEnumerable)t["items"]).Cast<object>().Select(Obj).ToList();
            var items = new List<object>();
            for (int i = 0; i < p.CoupangOptions.Count; i++)
            {
                var o = p.CoupangOptions[i];
                var ti = tItems[Math.Min(i, tItems.Count - 1)];
                var item = new Dictionary<string, object>();
                foreach (string k in new[] { "offerCondition", "offerDescription", "outboundShippingTimeDay", "maximumBuyForPerson", "maximumBuyForPersonPeriod",
                                             "unitCount", "adultOnly", "taxType", "parallelImported", "overseasPurchased", "pccNeeded",
                                             "emptyBarcode", "emptyBarcodeReason", "notices", "certifications" })
                    if (ti.ContainsKey(k) && ti[k] != null) item[k] = Clone(ti[k]);
                // 출고소요일: 항상 당일출고, 오후 12시 마감
                item["outboundShippingTimeDay"] = 1;
                item["sameDayShipping"] = new Dictionary<string, object> { { "active", true }, { "cutOffTimeHour", 12 }, { "cutOffTimeMinute", 0 }, { "cutOffTimeZone", "KR" } };
                item["itemName"] = (string.IsNullOrWhiteSpace(p.Volume) ? "" : p.Volume.Trim() + " ") + o.Count + "개";
                item["originalPrice"] = o.Price;
                item["salePrice"] = o.Price;
                item["maximumBuyCount"] = p.Stock;
                item["barcode"] = "";
                item["modelNo"] = "";
                item["externalVendorSku"] = p.Code ?? "";
                item["searchTags"] = p.SearchTags.Where(tag => !string.IsNullOrWhiteSpace(tag) && tag.Length <= 20).Take(20).Cast<object>().ToArray();
                var imgs = new List<object> { new Dictionary<string, object> { { "imageOrder", 0 }, { "imageType", "REPRESENTATION" }, { "vendorPath", urlOf[o.ImageFile] } } };
                foreach (var ex in p.ExtraImages.Take(9))
                    imgs.Add(new Dictionary<string, object> { { "imageOrder", imgs.Count }, { "imageType", "DETAIL" }, { "vendorPath", urlOf[ex] } });
                item["images"] = imgs.ToArray();
                // 상품고시정보: 건강기능식품 마크가 없으면 가공식품. 카테고리가 요구하는 항목을 모두 "상품 상세페이지 참조"로
                item["notices"] = CoupangNotices(meta.Notices, p.HealthFunctional);
                item["certifications"] = new object[] { new Dictionary<string, object> {
                    { "certificationType", p.HealthFunctional ? "PRESENTED_IN_DETAIL_PAGE" : "NOT_REQUIRED" }, { "certificationCode", "" } } };
                item["contents"] = new object[] { new Dictionary<string, object> {
                    { "contentsType", "HTML" },
                    { "contentDetails", new object[] { new Dictionary<string, object> { { "content", Regex.Replace(p.DetailHtml ?? "", "src=([\"'])http://", "src=$1https://", RegexOptions.IgnoreCase) }, { "detailType", "TEXT" } } } } } };

                // 속성: 틀 상품의 속성값(유산균 종류 등)은 상품마다 달라서 가져오지 않음.
                // 카테고리 필수 구매옵션 = 개당 캡슐/정·중량·용량 중 하나 + 수량 → 용량 단위를 보고 채움
                var attrs = new List<object>();
                foreach (var a in PurchaseAttributes(p.Volume, p.SourceName, o.Count, mandatory))
                    attrs.Add(a);
                if (p.Facts != null)
                    foreach (var a in FilterAttributes(meta, p.Facts, o.Count))
                        attrs.Add(a);
                item["attributes"] = attrs;
                items.Add(item);
            }
            body["items"] = items;

            log("쿠팡 상품 등록 중... (" + p.CoupangName + ")");
            var res = Obj(json.DeserializeObject(coupang.Send("POST", "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products", json.Serialize(body))));
            if (Convert.ToString(res["code"]) != "SUCCESS") throw new Exception("쿠팡 등록 실패: " + res["message"]);
            return "등록상품 ID " + res["data"] + ((bool)body["requested"] ? " (승인요청 함)" : " (임시저장 - 윙에서 승인요청 필요)");
        }

        // 쿠팡 카테고리 속성 목록 (필수 구매옵션 + 검색필터) 과 고시정보 항목
        class Meta : List<Dictionary<string, object>>
        {
            public List<Dictionary<string, object>> Notices = new List<Dictionary<string, object>>();
        }

        Meta CoupangMeta(string categoryCode)
        {
            var res = Obj(json.DeserializeObject(coupang.Send("GET", "/v2/providers/seller_api/apis/api/v1/marketplace/meta/category-related-metas/display-category-codes/" + categoryCode, null)));
            var data = Obj(res["data"]);
            var meta = new Meta();
            meta.AddRange(((IEnumerable)data["attributes"]).Cast<object>().Select(Obj));
            if (data.ContainsKey("noticeCategories") && data["noticeCategories"] != null)
                meta.Notices.AddRange(((IEnumerable)data["noticeCategories"]).Cast<object>().Select(Obj));
            return meta;
        }

        static object[] CoupangNotices(List<Dictionary<string, object>> noticeCategories, bool healthFunctional)
        {
            string want = healthFunctional ? "건강기능식품" : "가공식품";
            var cat = noticeCategories.FirstOrDefault(n => Convert.ToString(n["noticeCategoryName"]) == want);
            if (cat == null) throw new Exception("이 쿠팡 카테고리에는 '" + want + "' 고시정보가 없습니다. 카테고리를 확인하세요.");
            return ((IEnumerable)cat["noticeCategoryDetailNames"]).Cast<object>().Select(Obj)
                .Select(d => (object)new Dictionary<string, object>
                {
                    { "noticeCategoryName", want },
                    { "noticeCategoryDetailName", d["noticeCategoryDetailName"] },
                    { "content", "상품 상세페이지 참조" }
                }).ToArray();
        }

        // ── 카테고리 찾기
        List<Dictionary<string, object>> naverCategories;

        // 상품명에 들어 있는 단어와 이름이 같은 네이버 식품 카테고리 (예: "...젖산마그네슘..." → 식품>건강식품>영양제>마그네슘)
        public Tuple<string, string> SuggestNaverCategory(string productName)
        {
            if (naverCategories == null)
                naverCategories = ((IEnumerable)json.DeserializeObject(naver.Get("/v1/categories"))).Cast<object>().Select(Obj).ToList();
            string name = Regex.Replace(productName ?? "", @"\s+", "");
            var best = naverCategories
                .Where(c => Equals(c["last"], true) && Convert.ToString(c["wholeCategoryName"]).StartsWith("식품>"))
                .Select(c => new { c, leaf = Convert.ToString(c["wholeCategoryName"]).Split('>').Last() })
                .Where(x => x.leaf.Length >= 2 && !x.leaf.Contains("기타") && name.IndexOf(x.leaf.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(x => Convert.ToString(x.c["wholeCategoryName"]).Contains("건강식품") ? 1 : 0)
                .ThenByDescending(x => x.leaf.Length)
                .FirstOrDefault();
            return best == null ? null : Tuple.Create(Convert.ToString(best.c["id"]), Convert.ToString(best.c["wholeCategoryName"]));
        }

        // 네이버 쇼핑 상품의 카테고리 경로(식품>건강식품>홍삼>홍삼액) → 스마트스토어 카테고리 번호
        public Tuple<string, string> NaverCategoryByPath(string path)
        {
            if (naverCategories == null)
                naverCategories = ((IEnumerable)json.DeserializeObject(naver.Get("/v1/categories"))).Cast<object>().Select(Obj).ToList();
            string want = Regex.Replace(path ?? "", @"\s+", "");
            var c = naverCategories.FirstOrDefault(x => Equals(x["last"], true) && Regex.Replace(Convert.ToString(x["wholeCategoryName"]), @"\s+", "") == want);
            return c == null ? null : Tuple.Create(Convert.ToString(c["id"]), Convert.ToString(c["wholeCategoryName"]));
        }

        public string NaverCategoryName(string id)
        {
            if (naverCategories == null)
                naverCategories = ((IEnumerable)json.DeserializeObject(naver.Get("/v1/categories"))).Cast<object>().Select(Obj).ToList();
            var c = naverCategories.FirstOrDefault(x => Convert.ToString(x["id"]) == id.Trim());
            return c == null ? null : Convert.ToString(c["wholeCategoryName"]);
        }

        // 쿠팡 카테고리 추천 API (상품명으로 예측)
        public Tuple<string, string> SuggestCoupangCategory(string productName)
        {
            var res = Obj(json.DeserializeObject(coupang.Send("POST", "/v2/providers/openapi/apis/api/v1/categorization/predict", json.Serialize(new Dictionary<string, object> { { "productName", productName } }))));
            var d = Obj(res["data"]);
            if (d == null || Convert.ToString(d["autoCategorizationPredictionResultType"]) != "SUCCESS") return null;
            return Tuple.Create(Convert.ToString(d["predictedCategoryId"]), Convert.ToString(d["predictedCategoryName"]));
        }

        static List<string> Strings(Dictionary<string, object> a, string key)
        {
            object v;
            return a.TryGetValue(key, out v) && v is IEnumerable && !(v is string)
                ? ((IEnumerable)v).Cast<object>().Select(x => x is Dictionary<string, object> ? Convert.ToString(((Dictionary<string, object>)x)["inputValue"]) : Convert.ToString(x)).ToList()
                : new List<string>();
        }

        // 필수 구매옵션 이름 → 쓸 수 있는 단위 목록 (예: 개당 캡슐/정 → 정, 회분)
        static Dictionary<string, List<string>> MandatoryAttributes(List<Dictionary<string, object>> meta)
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var a in meta)
                if (Convert.ToString(a["required"]) == "MANDATORY")
                {
                    var units = Strings(a, "usableUnits");
                    if (units.Count == 0) units.Add(Convert.ToString(a["basicUnit"]));
                    map[Convert.ToString(a["attributeTypeName"])] = units;
                }
            return map;
        }

        // 검색필터(선택 속성): 상세 이미지에서 읽은 정보로 채울 수 있는 것만, 보기 목록에 있는 값으로
        static IEnumerable<object> FilterAttributes(List<Dictionary<string, object>> meta, ProductFacts f, int packs)
        {
            var list = new List<object>();
            foreach (var a in meta)
            {
                if (Convert.ToString(a["required"]) == "MANDATORY") continue;
                string name = Convert.ToString(a["attributeTypeName"]);
                var values = Strings(a, "inputValues");
                var units = Strings(a, "usableUnits");
                string basic = Convert.ToString(a.ContainsKey("basicUnit") ? a["basicUnit"] : "");
                Func<string, string> pick = want => want != null && (values.Count == 0 || values.Contains(want)) ? want : null;
                Func<double, string, string> num = (n, unit) =>
                {
                    if (n <= 0) return null;
                    string u = units.FirstOrDefault(x => x == unit) ?? (basic == unit ? unit : null);
                    return u == null ? null : n.ToString(CultureInfo.InvariantCulture) + u;
                };
                string val = null;
                switch (name)
                {
                    case "건강식품 타입": val = f.ProductType(values, true); break;
                    case "건강식품 사용대상": val = new[] { "성인남녀공용", "성인", "범용" }.FirstOrDefault(values.Contains); break;
                    case "총 캡슐/정 수량": if (f.Unit == "정" || f.Unit == "캡슐" || f.Unit == "알") val = num(f.Count * packs, "개"); break;
                    case "총 중량(g)": val = num(f.Weight * packs, "g"); break;
                    case "총 용량": val = num(f.VolumeMl * packs, "ml"); break;
                    case "보관방식": val = pick(f.Storage); break;
                    case "건강식품 성분": if (f.Nutrients.Count > 0) val = pick(f.Nutrients.Count >= 2 ? "복합건강식품" : "단일건강식품"); break;
                    case "건강기능식품": if (f.HealthFunctional) val = pick("인증 있음"); break;
                    case "비건 여부": case "유기농/친환경 등": val = pick("해당없음"); break;
                }
                if (val != null)
                    list.Add(new Dictionary<string, object> { { "attributeTypeName", name }, { "attributeValueName", val } });
            }
            return list;
        }

        // 이미 등록한 네이버 상품 고치기: 전시중으로, 상세 이미지를 네이버 서버로, 상품속성 다시 채움, 상품명 바꿈(주면)
        public string FixNaverProduct(string originNo, string name, ProductFacts f, int packs, bool healthFunctional, Action<string> log)
        {
            var body = Obj(json.DeserializeObject(naver.Get("/v2/products/origin-products/" + originNo)));
            var origin = Obj(body["originProduct"]);
            if (!string.IsNullOrWhiteSpace(name)) origin["name"] = name;
            origin["detailContent"] = NaverDetail(Convert.ToString(origin["detailContent"]), log);
            var attr = Obj(origin["detailAttribute"]);
            if (f != null)
            {
                var pa = NaverAttributes(Convert.ToString(origin["leafCategoryId"]), f, packs, healthFunctional, log);
                if (pa.Count > 0) attr["productAttributes"] = pa;
            }
            if (attr.ContainsKey("seoInfo") && !string.IsNullOrWhiteSpace(name)) { var seo = Obj(attr["seoInfo"]); seo["pageTitle"] = name; seo["metaDescription"] = name; }
            if (body.ContainsKey("smartstoreChannelProduct") && body["smartstoreChannelProduct"] != null)
                Obj(body["smartstoreChannelProduct"])["channelProductDisplayStatusType"] = "ON";
            naver.Put("/v2/products/origin-products/" + originNo, json.Serialize(body));
            // 확인: 다시 읽어서 전시상태·상세 이미지 주소
            string after = naver.Get("/v2/products/origin-products/" + originNo);
            return Regex.Match(after, "channelProductDisplayStatusType\"\\s*:\\s*\"(\\w+)").Groups[1].Value + " / 상세 " + (after.Contains("pstatic.net") && !Regex.IsMatch(after, "detailContent\"\\s*:\\s*\"[^\"]*http://") ? "네이버 이미지" : "확인 필요")
                   + " / 속성 " + Regex.Matches(after, "attributeValueSeq").Count + "개";
        }

        // 상세 이미지: msgood 이미지 주소(http://…cafe24.com)는 휴대폰 스마트스토어 앱·미리보기에서 깨짐(보안 연결 아님)
        // → 네이버 이미지 서버에 올려 그 주소로 바꿈. 못 올리면 https 주소로라도 바꿈
        string NaverDetail(string html, Action<string> log)
        {
            if (string.IsNullOrEmpty(html)) return html;
            var srcs = Regex.Matches(html, "<img[^>]+src=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            foreach (var src in srcs)
            {
                if (src.Contains("pstatic.net")) continue;
                string url = Regex.Replace(src, "^http://", "https://", RegexOptions.IgnoreCase);
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "nd_" + Guid.NewGuid().ToString("N") + ".jpg");
                    using (var wc = new System.Net.WebClient()) wc.DownloadFile(url, tmp);
                    try
                    {
                        if (new FileInfo(tmp).Length > 9 * 1024 * 1024) throw new Exception("10MB 넘음");
                        var up = naver.UploadImages(new List<string> { tmp });
                        if (up.Count > 0 && !string.IsNullOrEmpty(up[0])) { html = html.Replace(src, up[0]); log("상세 이미지를 네이버에 올림"); continue; }
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                }
                catch (Exception ex) { log("상세 이미지 네이버 업로드 실패 (https 주소로 넣음): " + ex.Message); }
                html = html.Replace(src, url);
            }
            return html;
        }

        // ── 네이버 상품속성
        readonly Dictionary<string, Tuple<List<Dictionary<string, object>>, List<Dictionary<string, object>>>> naverAttrCache = new Dictionary<string, Tuple<List<Dictionary<string, object>>, List<Dictionary<string, object>>>>();

        List<object> NaverAttributes(string categoryId, ProductFacts f, int packs, bool healthFunctional, Action<string> log)
        {
            Tuple<List<Dictionary<string, object>>, List<Dictionary<string, object>>> c;
            if (!naverAttrCache.TryGetValue(categoryId, out c))
            {
                var attrs = ((IEnumerable)json.DeserializeObject(naver.Get("/v1/product-attributes/attributes?categoryId=" + categoryId))).Cast<object>().Select(Obj).ToList();
                var vals = ((IEnumerable)json.DeserializeObject(naver.Get("/v1/product-attributes/attribute-values?categoryId=" + categoryId))).Cast<object>().Select(Obj).ToList();
                naverAttrCache[categoryId] = c = Tuple.Create(attrs, vals);
            }
            var result = new List<object>();
            foreach (var a in c.Item1)
            {
                string type = Convert.ToString(a["attributeClassificationType"]);
                object seq = a["attributeSeq"];
                // 범위형(성분 함량: 로르산 115mg) — 상세의 "로르산 115mg" 을 읽어 그 값이 든 구간에 넣음
                if (type == "RANGE")
                {
                    double amt = f.Amount(Convert.ToString(a["attributeName"]));
                    if (amt < 0) continue;
                    Func<Dictionary<string, object>, string, double> num = (v, k) => { double d; return v.ContainsKey(k) && double.TryParse(Convert.ToString(v[k]), NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : double.NaN; };
                    var band = c.Item2.Where(v => Equals(Convert.ToString(v["attributeSeq"]), Convert.ToString(seq)))
                                      .FirstOrDefault(v => (double.IsNaN(num(v, "minAttributeValue")) || num(v, "minAttributeValue") <= amt) && (double.IsNaN(num(v, "maxAttributeValue")) || amt <= num(v, "maxAttributeValue")));
                    if (band == null) continue;
                    string unit = Convert.ToString(a.ContainsKey("representativeUnitCode") ? a["representativeUnitCode"] : "");
                    var e = new Dictionary<string, object> { { "attributeSeq", seq }, { "attributeValueSeq", band["attributeValueSeq"] }, { "attributeRealValue", amt.ToString("0.##", CultureInfo.InvariantCulture) } };
                    if (unit.Length > 0) e["attributeRealValueUnitCode"] = unit;
                    result.Add(e);
                    continue;
                }
                if (type != "SINGLE_SELECT" && type != "MULTI_SELECT") continue;
                var choices = c.Item2.Where(v => Equals(Convert.ToString(v["attributeSeq"]), Convert.ToString(seq))).ToList();
                var names = choices.Select(v => Convert.ToString(v["minAttributeValue"])).ToList();
                var wanted = new List<string>();
                switch (Convert.ToString(a["attributeName"]))
                {
                    // 환 상품(구운토마토환 "1일 2회, 20~25환씩"): 1회 1스푼으로 봄 → 1일 2스푼 (사용자 기준)
                    case "1일 총 섭취량":
                        if (f.Unit == "환" && f.TimesPerDay > 0) wanted.Add(f.TimesPerDay + "스푼");
                        else if (f.Daily > 0) wanted.Add(f.Daily + f.Unit);
                        break;
                    // 개수가 애매한 환 상품(g 로만 적힘)·건강환/정 카테고리: 1개 = 1개월분, 2개 = 2개월분 (사용자 기준)
                    case "제품용량":
                        { string dur = f.Duration(packs, names); if (dur == null && (f.Unit == "환" || categoryId == "50001899")) dur = packs + "개월분"; wanted.Add(dur); }
                        break;
                    case "제품타입": wanted.Add(f.ProductType(names, false)); break;
                    case "섭취횟수": wanted.Add(f.TimesPerDay == 1 ? "하루 한 번" : f.TimesPerDay == 2 ? "하루 두 번" : f.TimesPerDay == 3 ? "하루 세 번" : null); break;
                    case "섭취방법": wanted.Add(f.Method); break;
                    case "섭취대상": wanted.Add(f.Target(names)); break;
                    case "주원료": wanted.Add("기타"); break;   // 항상 기타 (사용자 기준)
                    case "보관방법": wanted.Add(f.Storage); break;
                    case "포장형태": wanted.Add(f.Package); break;
                    // 기능성·식약처고시 원료는 건강기능식품만 표시 가능 → 일반(가공)식품은 "해당 없음"
                    // 상세에서 인정 기능성을 못 찾으면 추측해서 넣지 않고 비워 둠 (틀린 기능성 표시는 광고 위반)
                    case "주요 기능성": wanted.Add(healthFunctional ? f.MainFunction(names) : "해당 없음"); break;
                    case "영양소 원료명(식약처고시)": if (healthFunctional) wanted.AddRange(f.Nutrients); else wanted.Add("해당 없음"); break;
                }
                foreach (string w in wanted.Where(x => x != null).Distinct())
                {
                    var hit = choices.FirstOrDefault(v => Convert.ToString(v["minAttributeValue"]) == w);
                    if (hit == null) continue;
                    result.Add(new Dictionary<string, object> { { "attributeSeq", seq }, { "attributeValueSeq", hit["attributeValueSeq"] } });
                    if (type == "SINGLE_SELECT") break;
                }
            }
            log("네이버 상품속성 " + result.Count + "개 채움");
            return result;
        }

        // 용량 "60정" → 개당 캡슐/정 60정, "500g" → 개당 중량 500g, "1L" → 개당 용량 1L, 그리고 수량 n개
        // 쿠팡 필수 구매 옵션: 카테고리가 요구하는 것(개당 중량 / 개당 용량 / 개당 캡슐/정 / 수량)을 상품명·용량에서 찾아 채움
        //  예: 오트밀 카테고리는 "개당 중량 또는 개당 용량" 중 하나 + 수량 → 50gx12포 → 개당 중량 600g
        static IEnumerable<object> PurchaseAttributes(string volume, string sourceName, int count, Dictionary<string, List<string>> mandatory)
        {
            var list = new List<object>();
            string text = (volume ?? "") + " " + (sourceName ?? "");
            Func<string, string, double> total = (pat, unitPat) =>
            {
                var mm = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*(" + unitPat + @")(?:\s*[xX×*]\s*(\d+))?", RegexOptions.IgnoreCase);
                if (!mm.Success) return 0;
                double v = double.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture);
                string u = mm.Groups[2].Value.ToLowerInvariant();
                if (u == "kg" || u == "l") v *= 1000;
                if (mm.Groups[3].Success) v *= double.Parse(mm.Groups[3].Value, CultureInfo.InvariantCulture);
                return v;
            };
            Func<string, List<string>, string, string> unitOf = (name, units, want) => units.FirstOrDefault(x => string.Equals(x, want, StringComparison.OrdinalIgnoreCase)) ?? units.FirstOrDefault() ?? want;
            List<string> us;
            bool done = false;
            if (mandatory.TryGetValue("개당 중량", out us)) { double g = total("", "kg|g(?!r)"); if (g > 0) { list.Add(Attr("개당 중량", g.ToString("0.##", CultureInfo.InvariantCulture) + unitOf("개당 중량", us, "g"))); done = true; } }
            if (!done && mandatory.TryGetValue("개당 용량", out us)) { double ml = total("", "ml|l"); if (ml > 0) { list.Add(Attr("개당 용량", ml.ToString("0.##", CultureInfo.InvariantCulture) + unitOf("개당 용량", us, "ml"))); done = true; } }
            if (mandatory.TryGetValue("개당 캡슐/정", out us) && !list.Cast<Dictionary<string, object>>().Any(a => (string)a["attributeTypeName"] == "개당 캡슐/정"))
            {
                var cm = Regex.Match(text, @"(\d+)\s*(정|캡슐|알|포|회분)");
                if (cm.Success) { string want = cm.Groups[2].Value == "포" || cm.Groups[2].Value == "회분" ? "회분" : "정"; list.Add(Attr("개당 캡슐/정", cm.Groups[1].Value + unitOf("개당 캡슐/정", us, want))); done = true; }
            }
            if (list.Count > 0)
            {
                if (mandatory.ContainsKey("수량") || mandatory.Count == 0) list.Add(Attr("수량", count + "개"));
                return list;
            }
            var m = Regex.Match(volume ?? "", @"([\d.]+)\s*(정|캡슐|알|포|회분|kg|g|ml|mL|l|L)");
            if (m.Success)
            {
                string u = m.Groups[2].Value;
                string name = (u == "g" || u == "kg") ? "개당 중량"
                            : (u.ToLowerInvariant() == "ml" || u.ToLowerInvariant() == "l") ? "개당 용량"
                            : "개당 캡슐/정";
                List<string> units;
                if (mandatory.TryGetValue(name, out units))
                {
                    string want = name == "개당 캡슐/정" ? (u == "포" || u == "회분" ? "회분" : "정") : name == "개당 용량" ? (u.ToLowerInvariant() == "l" ? "L" : "ml") : u;
                    string unit = units.FirstOrDefault(x => string.Equals(x, want, StringComparison.OrdinalIgnoreCase)) ?? units.FirstOrDefault() ?? "";
                    list.Add(Attr(name, m.Groups[1].Value + unit));
                }
            }
            if (mandatory.ContainsKey("수량") || mandatory.Count == 0) list.Add(Attr("수량", count + "개"));
            return list;
        }

        static Dictionary<string, object> Attr(string name, string value)
        {
            return new Dictionary<string, object> { { "attributeTypeName", name }, { "attributeValueName", value }, { "exposed", "EXPOSED" } };
        }

        // ── 테스트로 만든 상품 지우기
        // 쿠팡 상품조회에서 보이는 검색어(searchTags): 임시저장된 상품을 다시 읽어 옵션마다 비어 있으면 넣고 저장
        // 돌려주는 값: 저장된 검색어 개수
        public int EnsureCoupangSearchTags(string sellerProductId, IList<string> tags, Action<string> log)
        {
            var want = tags.Where(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 20).Distinct().Take(20).ToList();
            if (want.Count == 0) return 0;
            string path = "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products/" + sellerProductId;
            var data = Obj(Obj(json.DeserializeObject(coupang.Send("GET", path, null)))["data"]);
            var items = ((IEnumerable)data["items"]).Cast<Dictionary<string, object>>().ToList();
            Func<Dictionary<string, object>, int> count = it => it.ContainsKey("searchTags") && it["searchTags"] is IEnumerable ? ((IEnumerable)it["searchTags"]).Cast<object>().Count() : 0;
            if (items.All(it => count(it) > 0)) return items.Min(count);
            log("쿠팡 상품조회에 검색어 넣는 중...");
            foreach (var it in items) it["searchTags"] = want.Cast<object>().ToArray();
            // 조회 전용 칸은 빼고 그대로 수정 요청
            foreach (var k in new[] { "trackingId", "statusName", "mdId", "mdName", "confirmationToken", "roleCode", "status" }) data.Remove(k);
            // 막 임시저장한 상품은 쿠팡이 처리 중이라 "현재 수정할 수 없습니다"가 나올 수 있음 → 20초씩 기다렸다 다시 (최대 약 3분)
            for (int attempt = 1; ; attempt++)
            {
                try { coupang.Send("PUT", "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products", json.Serialize(data)); break; }
                catch (ApiException ex)
                {
                    if (attempt >= 9 || !(ex.Body ?? "").Contains("수정할 수 없습니다")) throw;
                    log("쿠팡이 상품을 처리 중이라 20초 뒤 다시 검색어를 넣습니다... (" + attempt + "/8)");
                    System.Threading.Thread.Sleep(20000);
                }
            }
            var check = Obj(Obj(json.DeserializeObject(coupang.Send("GET", path, null)))["data"]);
            return ((IEnumerable)check["items"]).Cast<Dictionary<string, object>>().Min(count);
        }

        public void DeleteNaver(string originProductNo) { naver.Delete("/v2/products/origin-products/" + originProductNo); }
        public void DeleteCoupang(string sellerProductId) { coupang.Send("DELETE", "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products/" + sellerProductId, null); }

        static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }

        object Clone(object o)
        {
            return o == null ? null : json.DeserializeObject(json.Serialize(o));
        }
    }
}
