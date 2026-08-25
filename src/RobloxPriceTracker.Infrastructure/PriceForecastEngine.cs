namespace RobloxPriceTracker.Infrastructure;

public enum ForecastDirection
{
    StrongBearish,
    Bearish,
    Neutral,
    Bullish,
    StrongBullish
}

public sealed record PriceForecastResult(
    bool IsAvailable,
    string Status,
    long? NextPrice,
    long? FairValue,
    long? RangeLow,
    long? RangeHigh,
    double ConfidencePercent,
    ForecastDirection Direction,
    double VolatilityPercent,
    double? LiquidityScore,
    double SalesPerDay7d,
    double? TargetProbability1h,
    double? TargetProbability6h,
    double? TargetProbability24h,
    double? EstimatedHoursToTarget,
    int ObservationCount,
    double ExpectedIntervalHours,
    double TrendPerHour,
    bool SalesDataAvailable,
    string ModelVersion)
{
    public static PriceForecastResult Insufficient(string status, int observations) => new(
        false, status, null, null, null, null, 0, ForecastDirection.Neutral, 0, null, 0,
        null, null, null, null, observations, 0, 0, false, PriceForecastEngine.ModelVersion);
}

/// <summary>
/// Deterministic, dependency-free forecast model for the next observed lowest-reseller quote.
/// It intentionally reports a range and confidence instead of pretending a single price is certain.
/// Inputs combine local lowest-price history with Roblox RAP/daily volume aggregates when available.
/// </summary>
public sealed class PriceForecastEngine
{
    public const string ModelVersion = "robust-ema-v1";
    public const int MinimumObservations = 8;

    public PriceForecastResult Calculate(
        IEnumerable<JsonFileRepository.PriceHistoryEntry> history,
        RobloxResaleMarketData? resaleData,
        long? targetPrice,
        DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var valid = history
            .Where(x => x.Price is > 0)
            .OrderBy(x => x.ObservedAtUtc)
            .TakeLast(256)
            .ToArray();

        if (valid.Length < MinimumObservations)
        {
            return PriceForecastResult.Insufficient($"Need at least {MinimumObservations} valid price observations.", valid.Length);
        }

        var samples = valid.Select(x => new PriceSample(x.ObservedAtUtc, (double)x.Price!.Value)).ToArray();
        var current = samples[^1].Price;
        if (current <= 0)
        {
            return PriceForecastResult.Insufficient("Current reseller price is unavailable.", valid.Length);
        }

        var expectedIntervalHours = EstimateIntervalHours(samples);
        var shortEma = Ema(samples.TakeLast(Math.Min(12, samples.Length)).Select(x => x.Price), 5);
        var mediumEma = Ema(samples.TakeLast(Math.Min(32, samples.Length)).Select(x => x.Price), 14);
        var recentMedian = Median(samples.TakeLast(Math.Min(16, samples.Length)).Select(x => x.Price));
        var logSlopePerHour = EstimateLogSlopePerHour(samples.TakeLast(Math.Min(48, samples.Length)).ToArray());
        var regressionNext = current * Math.Exp(Clamp(logSlopePerHour * expectedIntervalHours, -0.18, 0.18));

        var hourlyVolatility = EstimateHourlyVolatility(samples.TakeLast(Math.Min(64, samples.Length)).ToArray());
        var nextVolatility = hourlyVolatility * Math.Sqrt(Math.Max(expectedIntervalHours, 1d / 60d));
        var volatilityPercent = nextVolatility * 100d;

        var salesBaseline = GetSalesBaseline(resaleData);
        var liquidityScore = CalculateLiquidityScore(resaleData, now);
        var salesWeight = salesBaseline is > 0
            ? 0.10 + 0.16 * ((liquidityScore ?? 20d) / 100d)
            : 0d;
        salesWeight = Clamp(salesWeight, 0, 0.26);

        var localCore = shortEma * 0.44 + mediumEma * 0.24 + recentMedian * 0.17 + regressionNext * 0.15;
        var next = salesBaseline is > 0
            ? localCore * (1d - salesWeight) + salesBaseline.Value * salesWeight
            : localCore;

        // Prevent one noisy signal from generating absurd next-tick predictions.
        var maxStep = Math.Max(0.04, Math.Min(0.22, nextVolatility * 3.0 + 0.04));
        next = Clamp(next, current * (1d - maxStep), current * (1d + maxStep));

        var fairValue = salesBaseline is > 0
            ? recentMedian * 0.30 + mediumEma * 0.25 + salesBaseline.Value * 0.45
            : recentMedian * 0.50 + mediumEma * 0.50;

        var spanHours = Math.Max(0, (samples[^1].AtUtc - samples[0].AtUtc).TotalHours);
        var observationScore = Math.Min(25d, Math.Max(0, valid.Length - MinimumObservations) * 1.25d);
        var spanScore = Math.Min(15d, spanHours / 48d * 15d);
        var salesScore = resaleData is { IsAvailable: true }
            ? 7d + (liquidityScore ?? 0d) * 0.15d
            : 0d;
        var volatilityPenalty = Math.Min(28d, volatilityPercent * 1.8d);
        var latestAgeHours = Math.Max(0, (now - samples[^1].AtUtc).TotalHours);
        var stalePenalty = latestAgeHours > Math.Max(1, expectedIntervalHours * 3d) ? 18d : 0d;
        var confidence = Clamp(34d + observationScore + spanScore + salesScore - volatilityPenalty - stalePenalty, 10d, 95d);

        var uncertaintyPct = Clamp(
            Math.Max(0.018, nextVolatility * 1.96) + (1d - confidence / 100d) * 0.065,
            0.02,
            0.38);
        var rangeLow = Math.Max(1, next * (1d - uncertaintyPct));
        var rangeHigh = next * (1d + uncertaintyPct);

        var movePct = (next - current) / current * 100d;
        var direction = movePct switch
        {
            <= -3.0 => ForecastDirection.StrongBearish,
            <= -0.75 => ForecastDirection.Bearish,
            >= 3.0 => ForecastDirection.StrongBullish,
            >= 0.75 => ForecastDirection.Bullish,
            _ => ForecastDirection.Neutral
        };

        // Convert the fitted log slope into an intuitive Robux/hour signal near the current price.
        var trendPerHour = current * (Math.Exp(logSlopePerHour) - 1d);
        var target1h = CalculateTargetProbability(current, fairValue, trendPerHour, hourlyVolatility, targetPrice, 1d, liquidityScore);
        var target6h = CalculateTargetProbability(current, fairValue, trendPerHour, hourlyVolatility, targetPrice, 6d, liquidityScore);
        var target24h = CalculateTargetProbability(current, fairValue, trendPerHour, hourlyVolatility, targetPrice, 24d, liquidityScore);
        var eta = EstimateHoursToTarget(current, fairValue, trendPerHour, targetPrice);

        return new PriceForecastResult(
            true,
            resaleData is { IsAvailable: true }
                ? "Forecast combines local reseller quotes with Roblox RAP and sale-volume aggregates."
                : "Forecast uses local reseller quote history; Roblox sale aggregates are unavailable for this item.",
            RoundPrice(next),
            RoundPrice(fairValue),
            RoundPrice(rangeLow),
            RoundPrice(rangeHigh),
            Math.Round(confidence, 0),
            direction,
            Math.Round(volatilityPercent, 2),
            liquidityScore is null ? null : Math.Round(liquidityScore.Value, 0),
            resaleData?.SalesPerDay7d ?? 0,
            target1h,
            target6h,
            target24h,
            eta,
            valid.Length,
            expectedIntervalHours,
            trendPerHour,
            resaleData is { IsAvailable: true },
            ModelVersion);
    }

    private static double? GetSalesBaseline(RobloxResaleMarketData? data)
    {
        if (data is not { IsAvailable: true }) return null;
        if (data.RecentAveragePrice is > 0) return data.RecentAveragePrice;

        var prices = data.PriceDataPoints.Where(x => x.Value > 0).OrderBy(x => x.Date).TakeLast(14).ToArray();
        if (prices.Length == 0) return null;

        var totalWeight = 0d;
        var weighted = 0d;
        for (var i = 0; i < prices.Length; i++)
        {
            var weight = i + 1d;
            totalWeight += weight;
            weighted += prices[i].Value * weight;
        }
        return totalWeight > 0 ? weighted / totalWeight : null;
    }

    private static double? CalculateLiquidityScore(RobloxResaleMarketData? data, DateTimeOffset now)
    {
        if (data is not { IsAvailable: true } || !data.HasSalesSeries) return null;
        var salesPerDay = Math.Max(0, data.SalesPerDay7d);
        var velocity = 100d * (1d - Math.Exp(-salesPerDay / 18d));
        var recencyFactor = data.LastVolumeDate is { } last
            ? Clamp(1d - Math.Max(0, (now - last).TotalDays - 1d) / 14d, 0.35, 1d)
            : 0.5;
        return Clamp(velocity * recencyFactor, 0, 100);
    }

    private static double CalculateTargetProbability(
        double current,
        double fairValue,
        double trendPerHour,
        double hourlyVolatility,
        long? targetPrice,
        double hours,
        double? liquidityScore)
    {
        if (targetPrice is not > 0) return double.NaN;
        var target = (double)targetPrice.Value;
        if (current <= target) return 100d;

        var trendContribution = Clamp(trendPerHour * hours, -current * 0.45, current * 0.45);
        var meanReversion = (fairValue - current) * (1d - Math.Exp(-hours / 24d)) * 0.40d;
        var mean = Math.Max(1, current + trendContribution + meanReversion);

        var baseSigmaPct = Math.Max(0.008, hourlyVolatility * Math.Sqrt(Math.Max(hours, 0.25)));
        if (liquidityScore is { } liquidity)
        {
            // Thin markets are harder to forecast, so widen the distribution.
            baseSigmaPct *= 1d + (100d - liquidity) / 160d;
        }
        else
        {
            baseSigmaPct *= 1.35d;
        }

        var sigma = Math.Max(current * 0.008, current * baseSigmaPct);
        var z = (target - mean) / sigma;
        var probability = NormalCdf(z) * 100d;
        return Math.Round(Clamp(probability, 0, 100), 0);
    }

    private static double? EstimateHoursToTarget(double current, double fairValue, double trendPerHour, long? targetPrice)
    {
        if (targetPrice is not > 0) return null;
        var target = (double)targetPrice.Value;
        if (current <= target) return 0;
        if (trendPerHour < -0.01)
        {
            var hours = (current - target) / -trendPerHour;
            return hours is > 0 and <= 24d * 60d ? Math.Round(hours, 1) : null;
        }

        if (fairValue < target)
        {
            // If trend is temporarily flat but fair value sits below target, expose only a coarse estimate.
            var fraction = (current - target) / Math.Max(1, current - fairValue);
            return Math.Round(Clamp(24d * fraction, 2d, 168d), 1);
        }
        return null;
    }

    private static double EstimateIntervalHours(IReadOnlyList<PriceSample> samples)
    {
        var intervals = new List<double>();
        for (var i = Math.Max(1, samples.Count - 24); i < samples.Count; i++)
        {
            var hours = (samples[i].AtUtc - samples[i - 1].AtUtc).TotalHours;
            if (hours > 0 && hours <= 72) intervals.Add(hours);
        }
        return intervals.Count == 0 ? 1d : Clamp(Median(intervals), 1d / 60d, 24d);
    }

    private static double EstimateLogSlopePerHour(IReadOnlyList<PriceSample> samples)
    {
        if (samples.Count < 2) return 0;
        var origin = samples[0].AtUtc;
        var xs = samples.Select(x => (x.AtUtc - origin).TotalHours).ToArray();
        var ys = samples.Select(x => Math.Log(Math.Max(1, x.Price))).ToArray();
        var xMean = xs.Average();
        var yMean = ys.Average();
        var numerator = 0d;
        var denominator = 0d;
        for (var i = 0; i < xs.Length; i++)
        {
            var dx = xs[i] - xMean;
            numerator += dx * (ys[i] - yMean);
            denominator += dx * dx;
        }
        if (denominator <= 1e-9) return 0;
        return Clamp(numerator / denominator, -0.20, 0.20);
    }

    private static double EstimateHourlyVolatility(IReadOnlyList<PriceSample> samples)
    {
        if (samples.Count < 3) return 0.02;
        var normalizedReturns = new List<double>();
        for (var i = 1; i < samples.Count; i++)
        {
            var hours = Math.Max(1d / 60d, (samples[i].AtUtc - samples[i - 1].AtUtc).TotalHours);
            if (hours > 72) continue;
            var logReturn = Math.Log(Math.Max(1, samples[i].Price) / Math.Max(1, samples[i - 1].Price));
            normalizedReturns.Add(logReturn / Math.Sqrt(hours));
        }
        if (normalizedReturns.Count < 2) return 0.02;
        var mean = normalizedReturns.Average();
        var variance = normalizedReturns.Sum(x => Math.Pow(x - mean, 2)) / Math.Max(1, normalizedReturns.Count - 1);
        return Clamp(Math.Sqrt(Math.Max(0, variance)), 0.001, 0.30);
    }

    private static double Ema(IEnumerable<double> values, int period)
    {
        var data = values.ToArray();
        if (data.Length == 0) return 0;
        var alpha = 2d / (Math.Max(1, period) + 1d);
        var ema = data[0];
        for (var i = 1; i < data.Length; i++) ema = alpha * data[i] + (1d - alpha) * ema;
        return ema;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        if (sorted.Length == 0) return 0;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2d : sorted[mid];
    }

    private static long RoundPrice(double value) => Math.Max(1, (long)Math.Round(value, MidpointRounding.AwayFromZero));
    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

    // Abramowitz & Stegun approximation. Sufficient for UI probability estimates without extra dependencies.
    private static double NormalCdf(double x)
    {
        var sign = x < 0 ? -1d : 1d;
        x = Math.Abs(x) / Math.Sqrt(2d);
        var t = 1d / (1d + 0.3275911d * x);
        var y = 1d - (((((1.061405429d * t - 1.453152027d) * t) + 1.421413741d) * t - 0.284496736d) * t + 0.254829592d) * t * Math.Exp(-x * x);
        var erf = sign * y;
        return 0.5d * (1d + erf);
    }

    private sealed record PriceSample(DateTimeOffset AtUtc, double Price);
}
