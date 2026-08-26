namespace RobloxPriceTracker.Infrastructure;

public sealed record UgcResaleScoringInput(
    int OriginalPrice,
    long? TotalSupply,
    long PurchaseCount,
    long? UnitsAvailable,
    double VelocityPerMinute,
    double Acceleration,
    long FavoriteCount,
    int ObservationCount,
    double? RecentAveragePrice = null,
    long? LowestResalePrice = null,
    int ObservedResellers = 0,
    bool ResellerBookTruncated = false,
    int ListingsWithin10Pct = 0,
    int ListingsWithin20Pct = 0,
    double SalesLast7d = 0,
    double SalesLast30d = 0,
    double? MedianTop10 = null,
    bool HasLiveResaleMarket = false);

public sealed record UgcResaleScore(
    double ResalePotential,
    double DemandScore,
    double LiquidityScore,
    double ProfitabilityScore,
    double ScarcityScore,
    double MarketStabilityScore,
    double RiskScore,
    double Confidence,
    long BreakEvenResale,
    long BearResale,
    long BaseResale,
    long BullResale,
    double BearNetRoi,
    double BaseNetRoi,
    double BullNetRoi,
    string LiquidityLabel,
    string Recommendation,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Risks);

/// <summary>
/// Pure scoring model for paid community-created Limiteds. Resale scenarios are gross
/// listing prices; net ROI uses Roblox's current 50% community-Limited reseller share.
/// A fast sellout can raise demand confidence but cannot override a weak net-resale case.
/// </summary>
public static class UgcResaleScoring
{
    public const double CommunityLimitedResellerShare = 0.50d;

    public static UgcResaleScore Evaluate(UgcResaleScoringInput input)
    {
        if (input.OriginalPrice <= 0) throw new ArgumentOutOfRangeException(nameof(input.OriginalPrice));

        var soldPct = input.TotalSupply is > 0
            ? Math.Clamp(input.PurchaseCount / (double)input.TotalSupply.Value, 0d, 1d)
            : 0d;

        var remainingPct = input.TotalSupply is > 0 && input.UnitsAvailable is { } remaining
            ? Math.Clamp(remaining / (double)input.TotalSupply.Value, 0d, 1d)
            : (double?)null;

        var absorptionPerHour = input.TotalSupply is > 0 && input.VelocityPerMinute > 0
            ? input.VelocityPerMinute * 60d / input.TotalSupply.Value
            : 0d;

        var velocityScore = Clamp(15d + Math.Log10(1d + Math.Max(0d, input.VelocityPerMinute)) * 24d + absorptionPerHour * 420d);
        if (input.Acceleration > 0) velocityScore = Clamp(velocityScore + Math.Min(18d, input.Acceleration * 12d));
        if (input.Acceleration < 0) velocityScore = Clamp(velocityScore - Math.Min(28d, -input.Acceleration * 20d));

        var favoritePerPurchase = input.PurchaseCount > 0 ? input.FavoriteCount / (double)input.PurchaseCount : 0d;
        var engagementScore = Clamp(28d + Math.Log10(1d + Math.Max(0L, input.FavoriteCount)) * 9d + Math.Min(24d, favoritePerPurchase * 9d));
        var demandScore = Clamp(velocityScore * 0.58d + engagementScore * 0.22d + soldPct * 100d * 0.20d);

        var scarcityScore = input.TotalSupply switch
        {
            <= 100 => 100d,
            <= 300 => 94d,
            <= 750 => 84d,
            <= 1500 => 70d,
            <= 2250 => 56d,
            <= 3000 => 44d,
            <= 5000 => 30d,
            null => 45d,
            _ => 20d
        };
        scarcityScore = Clamp(scarcityScore + soldPct * 10d);

        var capitalEfficiency = input.OriginalPrice switch
        {
            <= 75 => 98d,
            <= 100 => 94d,
            <= 150 => 86d,
            <= 250 => 74d,
            <= 500 => 58d,
            <= 1000 => 40d,
            _ => 24d
        };

        var modeledMultiplier = 1.05d
            + demandScore / 100d * 1.55d
            + scarcityScore / 100d * 0.75d
            + capitalEfficiency / 100d * 0.35d
            + Math.Clamp(input.Acceleration, 0d, 1.5d) * 0.18d;

        modeledMultiplier -= input.OriginalPrice switch
        {
            >= 2000 => 1.05d,
            >= 1000 => 0.82d,
            >= 500 => 0.46d,
            >= 250 => 0.22d,
            _ => 0d
        };
        if (input.Acceleration < 0) modeledMultiplier -= Math.Min(0.55d, -input.Acceleration * 0.22d);
        modeledMultiplier = Math.Clamp(modeledMultiplier, 1.05d, 4.75d);

        var modeledBase = input.OriginalPrice * modeledMultiplier;
        var marketAnchor = BuildMarketAnchor(input);
        var marketEvidenceStrength = MarketEvidenceStrength(input);
        var baseResale = marketAnchor is > 0
            ? modeledBase * (1d - marketEvidenceStrength) + marketAnchor.Value * marketEvidenceStrength
            : modeledBase;

        var breakEven = (long)Math.Ceiling(input.OriginalPrice / CommunityLimitedResellerShare);
        var baseGross = Math.Max(1L, (long)Math.Round(baseResale));
        var bearGross = Math.Max(1L, (long)Math.Round(baseGross * (input.HasLiveResaleMarket ? 0.74d : 0.68d)));
        var bullGross = Math.Max(baseGross, (long)Math.Round(baseGross * (1.30d + Math.Clamp(demandScore - 55d, 0d, 45d) / 100d * 0.35d)));

        var bearRoi = NetRoi(input.OriginalPrice, bearGross);
        var baseRoi = NetRoi(input.OriginalPrice, baseGross);
        var bullRoi = NetRoi(input.OriginalPrice, bullGross);
        var profitabilityScore = ProfitabilityScore(baseRoi, bearRoi);

        var liquidityScore = LiquidityScore(input, demandScore);
        var stabilityScore = MarketStabilityScore(input);

        var observationConfidence = Math.Min(32d, Math.Max(0, input.ObservationCount) * 5.5d);
        var confidence = 18d + observationConfidence;
        if (input.TotalSupply is > 0) confidence += 8d;
        if (input.VelocityPerMinute > 0) confidence += 7d;
        if (input.HasLiveResaleMarket) confidence += 14d;
        if (input.SalesLast7d >= 3) confidence += 8d;
        if (input.SalesLast30d >= 10) confidence += 7d;
        if (input.ObservedResellers >= 3) confidence += 5d;
        confidence = Clamp(confidence);
        if (!input.HasLiveResaleMarket) confidence = Math.Min(confidence, 72d);

        var highPriceRisk = input.OriginalPrice switch
        {
            <= 100 => 0d,
            <= 250 => 4d,
            <= 500 => 10d,
            <= 1000 => 18d,
            _ => 28d
        };
        var slowdownRisk = input.Acceleration < 0 ? Math.Min(24d, -input.Acceleration * 26d) : 0d;
        var liquidityRisk = (100d - liquidityScore) * 0.22d;
        var stabilityRisk = input.HasLiveResaleMarket ? (100d - stabilityScore) * 0.18d : 8d;
        var profitRisk = baseRoi < 0 ? Math.Min(32d, -baseRoi * 45d + 14d) : baseRoi < 0.15d ? 10d : 0d;
        var lowEvidenceRisk = (100d - confidence) * 0.16d;
        var risk = Clamp(10d + highPriceRisk + slowdownRisk + liquidityRisk + stabilityRisk + profitRisk + lowEvidenceRisk);

        var rawPotential = profitabilityScore * 0.35d
            + demandScore * 0.25d
            + liquidityScore * 0.15d
            + scarcityScore * 0.10d
            + stabilityScore * 0.10d
            + capitalEfficiency * 0.05d;
        var potential = Clamp(rawPotential - risk * 0.18d + confidence * 0.06d);

        // Profitability hard gates: a sellout signal cannot make an unprofitable resale a good entry.
        if (baseRoi < 0d) potential = Math.Min(potential, 35d);
        else if (baseRoi < 0.10d) potential = Math.Min(potential, 48d);
        if (input.OriginalPrice >= 1000 && !input.HasLiveResaleMarket) potential = Math.Min(potential, 50d);
        if (input.HasLiveResaleMarket && input.LowestResalePrice is > 0 && input.LowestResalePrice.Value < breakEven && input.SalesLast7d >= 2d)
            potential = Math.Min(potential, 35d);
        if (input.HasLiveResaleMarket && input.SalesLast30d < 2d && stabilityScore < 45d)
            potential = Math.Min(potential, 55d);

        var liquidityLabel = input.HasLiveResaleMarket
            ? liquidityScore switch
            {
                >= 78 => "STRONG",
                >= 60 => "HEALTHY",
                >= 42 => "THIN",
                _ when input.ObservedResellers >= 35 => "CROWDED",
                _ => "VERY THIN"
            }
            : "MODELED";

        var recommendation = Recommendation(potential, baseRoi, liquidityScore, confidence, risk, input);
        var reasons = BuildReasons(input, demandScore, scarcityScore, profitabilityScore, liquidityScore, baseRoi, breakEven);
        var risks = BuildRisks(input, risk, baseRoi, breakEven, stabilityScore, confidence);

        return new UgcResaleScore(
            potential,
            demandScore,
            liquidityScore,
            profitabilityScore,
            scarcityScore,
            stabilityScore,
            risk,
            confidence,
            breakEven,
            bearGross,
            baseGross,
            bullGross,
            bearRoi,
            baseRoi,
            bullRoi,
            liquidityLabel,
            recommendation,
            reasons,
            risks);
    }

    private static double? BuildMarketAnchor(UgcResaleScoringInput input)
    {
        var floor = input.LowestResalePrice is > 0 ? (double?)input.LowestResalePrice.Value : null;
        var rap = input.RecentAveragePrice is > 0 ? input.RecentAveragePrice : null;
        var median = input.MedianTop10 is > 0 ? input.MedianTop10 : null;

        if (floor is null && rap is null && median is null) return null;
        if (floor is { } f && rap is { } r)
        {
            var upperRap = r * 1.30d;
            var conservativeFloor = Math.Min(f, upperRap);
            var book = median is { } m ? Math.Min(m, conservativeFloor * 1.25d) : conservativeFloor;
            return conservativeFloor * 0.55d + r * 0.30d + book * 0.15d;
        }
        if (floor is { } onlyFloor)
        {
            return median is { } m ? onlyFloor * 0.75d + Math.Min(m, onlyFloor * 1.25d) * 0.25d : onlyFloor;
        }
        return rap ?? median;
    }

    private static double MarketEvidenceStrength(UgcResaleScoringInput input)
    {
        if (!input.HasLiveResaleMarket) return 0d;
        var strength = 0.45d;
        if (input.SalesLast7d >= 3d) strength += 0.10d;
        if (input.SalesLast30d >= 10d) strength += 0.10d;
        if (input.ObservedResellers >= 3) strength += 0.05d;
        if (input.ObservedResellers >= 8) strength += 0.05d;
        return Math.Clamp(strength, 0.45d, 0.75d);
    }

    private static double LiquidityScore(UgcResaleScoringInput input, double demandScore)
    {
        if (!input.HasLiveResaleMarket)
            return Math.Min(55d, 18d + demandScore * 0.42d);

        var listings = Math.Max(1, input.ObservedResellers);
        var sales30 = Math.Max(input.SalesLast30d, input.SalesLast7d);
        var turnover = sales30 / listings;
        var score = 22d + Math.Log10(1d + Math.Max(0d, sales30)) * 23d + Math.Min(35d, turnover * 18d);
        if (input.ObservedResellers <= 1) score -= 18d;
        else if (input.ObservedResellers <= 2) score -= 10d;
        if ((input.ResellerBookTruncated || input.ObservedResellers >= 100) && sales30 < 40d) score -= 18d;
        else if (input.ObservedResellers >= 35 && sales30 < input.ObservedResellers * 0.5d) score -= 12d;
        if (input.ListingsWithin10Pct is >= 2 and <= 15) score += 6d;
        return Clamp(score);
    }

    private static double MarketStabilityScore(UgcResaleScoringInput input)
    {
        if (!input.HasLiveResaleMarket) return 48d;
        var score = 55d;
        if (input.LowestResalePrice is > 0 && input.RecentAveragePrice is > 0)
        {
            var divergence = Math.Abs(input.LowestResalePrice.Value - input.RecentAveragePrice.Value) / input.RecentAveragePrice.Value;
            score = divergence switch
            {
                <= 0.10d => 92d,
                <= 0.25d => 78d,
                <= 0.50d => 58d,
                _ => 30d
            };
        }
        if (input.LowestResalePrice is > 0 && input.MedianTop10 is > 0)
        {
            var bookSpread = Math.Abs(input.MedianTop10.Value - input.LowestResalePrice.Value) / input.LowestResalePrice.Value;
            score = (score + (bookSpread switch
            {
                <= 0.10d => 92d,
                <= 0.25d => 76d,
                <= 0.50d => 54d,
                _ => 28d
            })) / 2d;
        }
        if (input.ObservedResellers < 3) score = Math.Min(score, 45d);
        if (input.SalesLast30d < 3d) score = Math.Min(score, 42d);
        return Clamp(score);
    }

    private static double ProfitabilityScore(double baseRoi, double bearRoi)
    {
        var score = baseRoi switch
        {
            <= -0.25d => 5d,
            <= 0d => 15d,
            <= 0.10d => 30d,
            <= 0.25d => 45d,
            <= 0.50d => 65d,
            <= 1.00d => 82d,
            <= 2.00d => 94d,
            _ => 100d
        };
        if (bearRoi < -0.25d) score -= 10d;
        return Clamp(score);
    }

    private static string Recommendation(double potential, double baseRoi, double liquidity, double confidence, double risk, UgcResaleScoringInput input)
    {
        if (baseRoi < 0d || risk >= 82d) return "AVOID";
        if (input.HasLiveResaleMarket && input.LowestResalePrice is > 0 && input.LowestResalePrice.Value < input.OriginalPrice / CommunityLimitedResellerShare && input.SalesLast7d >= 2d)
            return "AVOID";
        if (potential >= 82d && baseRoi >= 0.50d && liquidity >= 55d && confidence >= 55d) return "HIGH RESALE";
        if (potential >= 72d && baseRoi >= 0.30d && risk < 68d) return "STRONG";
        if (potential >= 58d && baseRoi >= 0.15d) return confidence < 50d ? "SPECULATIVE" : "WATCH";
        return "AVOID";
    }

    private static IReadOnlyList<string> BuildReasons(UgcResaleScoringInput input, double demand, double scarcity, double profitability, double liquidity, double baseRoi, long breakEven)
    {
        var reasons = new List<(double Score, string Text)>();
        if (profitability >= 65d) reasons.Add((profitability + 10d, $"Base case clears the {breakEven:N0} R$ break-even with {baseRoi:+0%;-0%;0%} net ROI"));
        if (input.HasLiveResaleMarket && input.LowestResalePrice is > 0 && input.LowestResalePrice.Value >= breakEven)
            reasons.Add((96d, $"Live resale floor already clears break-even at {input.LowestResalePrice.Value:N0} R$"));
        if (liquidity >= 60d) reasons.Add((liquidity, "Resale volume is healthy relative to competing listings"));
        if (demand >= 72d) reasons.Add((demand, "Original-stock demand and sell-through are strong"));
        if (scarcity >= 78d) reasons.Add((scarcity, "Supply is scarce within the paid UGC Limited range"));
        if (input.Acceleration >= 0.20d) reasons.Add((80d, "Sales velocity is still accelerating"));
        if (reasons.Count == 0) reasons.Add((50d, "Resale case is still developing; current signals are mixed"));
        return reasons.OrderByDescending(x => x.Score).Take(3).Select(x => x.Text).ToArray();
    }

    private static IReadOnlyList<string> BuildRisks(UgcResaleScoringInput input, double risk, double baseRoi, long breakEven, double stability, double confidence)
    {
        var risks = new List<string>();
        if (baseRoi < 0d) risks.Add($"Projected resale does not clear the {breakEven:N0} R$ break-even after the 50% reseller share");
        else if (baseRoi < 0.15d) risks.Add("Projected net margin is too thin for the holding-period and price risk");
        if (input.OriginalPrice >= 1000 && !input.HasLiveResaleMarket) risks.Add("High entry price has no live resale market evidence yet");
        if (input.Acceleration <= -0.30d) risks.Add("Original-stock sales velocity is slowing");
        if (input.HasLiveResaleMarket && input.SalesLast30d < 3d) risks.Add("Very low resale volume makes the displayed floor unreliable");
        if (input.ObservedResellers >= 35 && input.SalesLast30d < input.ObservedResellers * 0.5d) risks.Add("Reseller competition is heavy relative to recent sales");
        if (stability < 45d) risks.Add("Floor, RAP, or listing depth indicates an unstable resale price");
        if (confidence < 50d) risks.Add("Limited evidence lowers model confidence");
        if (risk >= 75d && risks.Count == 0) risks.Add("Combined market-risk signals are elevated");
        if (risks.Count == 0) risks.Add("No major quantitative resale risk signal detected yet");
        return risks.Take(3).ToArray();
    }

    private static double NetRoi(int originalPrice, long grossResale)
    {
        var netProceeds = grossResale * CommunityLimitedResellerShare;
        return (netProceeds - originalPrice) / originalPrice;
    }

    private static double Clamp(double value) => Math.Clamp(value, 0d, 100d);
}
