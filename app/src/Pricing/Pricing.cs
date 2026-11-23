using System;
using System.Collections.Generic;

namespace PriceSetter
{
    public class PriceRow
    {
        public int Count;           // 개수
        public long UnitCost;       // 원가(1개)
        public double Normal;       // 정상가
        public double Discount;     // 할인액 (할인가 - 정상가)
        public double Sale;         // 할인가
        public double PerUnit;      // 객단가
        public double TotalCost;    // 원가 합계 (원가 × 개수 + 배송비)
        public double Profit;       // 순익
        public double ProfitRate;   // 순익률
    }

    // 250407_R3_계산식.xlsx 의 식 그대로
    public static class Pricing
    {
        // 정상가(1개) = (원가 + 배송비) × 정상가배수
        // 할인가: 1개 = 정상가, n개 = TRUNC((원가 + 배송비) × 배수 × n, -3) - 100
        public static PriceRow Calc(long cost, long shipping, double normalRate, int count, double rate)
        {
            var r = new PriceRow { Count = count, UnitCost = cost };
            // 소수 오차로 천 원 단위 내림이 틀어지지 않게 decimal 로 계산
            decimal baseCost = cost + shipping;
            decimal normal1 = baseCost * (decimal)normalRate;
            r.Normal = (double)(normal1 * count);
            r.Sale = count == 1 ? (double)normal1 : (double)(Math.Truncate(baseCost * (decimal)rate * count / 1000m) * 1000m - 100m);
            r.Discount = r.Sale - r.Normal;
            r.PerUnit = r.Sale / count;
            r.TotalCost = (double)cost * count + shipping;
            r.Profit = r.Sale - r.TotalCost;
            r.ProfitRate = r.Sale == 0 ? 0 : r.Profit / r.Sale;
            return r;
        }

        // 엑셀에 있던 개수별 배수 (1~6개)
        public static readonly double[] DefaultRates = { 1.6, 1.52, 1.48, 1.435, 1.39, 1.37 };
        public static readonly bool[] DefaultOn = { true, true, true, true, true, true };
    }
}
