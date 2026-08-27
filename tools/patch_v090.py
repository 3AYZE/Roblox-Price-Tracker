from pathlib import Path


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected 1 match, found {count}")
    return text.replace(old, new, 1)

# Wire Analyzer navigation/page into the existing MainWindow lifecycle.
p = Path("src/RobloxPriceTracker.Gui/MainWindow.Part01.cs")
text = p.read_text(encoding="utf-8")
text = replace_once(
    text,
    "        ConfigureStockUi();\n        ConfigureTerminalUi();\n    }",
    "        ConfigureStockUi();\n        ConfigureTerminalUi();\n        ConfigureIntelligenceUi();\n    }",
    "constructor intelligence wiring",
)
text = replace_once(
    text,
    "        _hunterTimer?.Stop();\n        StopUpdateChecks();",
    "        _hunterTimer?.Stop();\n        _analyzerCts?.Cancel();\n        StopUpdateChecks();",
    "analyzer cancellation",
)
text = replace_once(
    text,
    "        SetTerminalPageVisibility(page);\n\n        (PageTitleText.Text, PageSubtitleText.Text) = page switch",
    "        SetTerminalPageVisibility(page);\n        SetIntelligencePageVisibility(page);\n\n        (PageTitleText.Text, PageSubtitleText.Text) = page switch",
    "analyzer page visibility",
)
text = replace_once(
    text,
    '            "Hunter" => ("UGC Hunter", "Live buyable Limited UGC scanner with velocity, scarcity, entry timing, risk, and resale scenarios."),',
    '            "Hunter" => ("UGC Hunter", "Live Limited UGC scanner with verified market evidence, resale economics, data quality, and entry timing."),\n            "Analyzer" => ("Analyzer", "Deep-dive any Roblox catalog item with direct market evidence, break-even economics, and creator track record."),',
    "analyzer title",
)
p.write_text(text, encoding="utf-8")

# Add evidence-quality/freshness fields without changing stored Hunter schemas.
p = Path("src/RobloxPriceTracker.Gui/UgcHunterServiceV080.cs")
text = p.read_text(encoding="utf-8")
old = '''    public bool PrimaryMarketVerified { get; init; }

    public double VelocityPerMinute'''
new = '''    public bool PrimaryMarketVerified { get; init; }

    public int DataQualityScore
    {
        get
        {
            var score = 0;
            if (PrimaryMarketVerified) score += 25;
            if (Price > 0) score += 10;
            if (TotalSupply is > 0) score += 10;
            if (UnitsAvailable is not null) score += 10;
            if (CurrentResaleFloor is > 0) score += 15;
            if (RecentAveragePrice is > 0) score += 10;
            if (ObservedResellers > 0) score += 10;
            if (SalesLast7d > 0 || SalesLast30d > 0) score += 10;
            return Math.Clamp(score, 0, 100);
        }
    }
    public string DataQualityLabel => DataQualityScore switch
    {
        >= 85 => "VERIFIED",
        >= 70 => "STRONG",
        >= 50 => "PARTIAL",
        _ => "LOW"
    };
    public string DataQualityText => $"{DataQualityScore}% {DataQualityLabel}";
    public string FreshnessText
    {
        get
        {
            var age = DateTimeOffset.UtcNow - ObservedAtUtc;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            return age switch
            {
                { TotalMinutes: < 2 } => "LIVE",
                { TotalMinutes: < 10 } => $"{Math.Max(1, age.TotalMinutes):0}m old",
                { TotalMinutes: < 60 } => $"{age.TotalMinutes:0}m old",
                _ => $"{age.TotalHours:0.0}h old"
            };
        }
    }
    public string EvidenceText
    {
        get
        {
            var evidence = new List<string>();
            evidence.Add(PrimaryMarketVerified ? "✓ Roblox primary market" : "— catalog fallback");
            evidence.Add(TotalSupply is > 0 && UnitsAvailable is not null ? "✓ supply" : "— supply missing");
            evidence.Add(CurrentResaleFloor is > 0 ? "✓ floor" : "— floor unavailable");
            evidence.Add(RecentAveragePrice is > 0 ? "✓ RAP" : "— RAP unavailable");
            evidence.Add(ObservedResellers > 0 ? "✓ reseller book" : "— reseller book unavailable");
            evidence.Add(SalesLast7d > 0 || SalesLast30d > 0 ? "✓ resale volume" : "— resale volume unavailable");
            return string.Join(" · ", evidence);
        }
    }

    public double VelocityPerMinute'''
text = replace_once(text, old, new, "hunter data quality properties")
p.write_text(text, encoding="utf-8")

# Register analyzer regressions.
p = Path("tests/RobloxPriceTracker.SelfTest/Program.Main.cs")
text = p.read_text(encoding="utf-8")
text = replace_once(
    text,
    '            ("White Heart Aura reference is unprofitable", TestWhiteHeartAuraReferenceIsUnprofitableAsync)\n',
    '            ("White Heart Aura reference is unprofitable", TestWhiteHeartAuraReferenceIsUnprofitableAsync),\n            ("UGC analyzer parses authoritative catalog detail", TestUgcAnalyzerCatalogParserAsync),\n            ("UGC analyzer grades complete market evidence", TestUgcAnalyzerDataQualityAsync),\n            ("Creator intelligence summarizes profitable track record", TestCreatorIntelligenceTrackRecordAsync)\n',
    "test registration",
)
p.write_text(text, encoding="utf-8")

# v0.9.0 application version.
p = Path("src/RobloxPriceTracker.Gui/RobloxPriceTracker.Gui.csproj")
text = p.read_text(encoding="utf-8")
text = replace_once(text, "<Version>0.8.4</Version>", "<Version>0.9.0</Version>", "version")
text = replace_once(text, "<AssemblyVersion>0.8.4.0</AssemblyVersion>", "<AssemblyVersion>0.9.0.0</AssemblyVersion>", "assembly version")
text = replace_once(text, "<FileVersion>0.8.4.0</FileVersion>", "<FileVersion>0.9.0.0</FileVersion>", "file version")
p.write_text(text, encoding="utf-8")

# Release notes.
p = Path("docs/CHANGELOG.md")
text = p.read_text(encoding="utf-8")
header = "# Roblox Price Tracker changelog\n\n"
entry = '''## v0.9.0
- Added a dedicated **Analyzer** workspace: paste any Roblox catalog URL or asset ID for a direct market deep-dive.
- Analyzer separates Roblox-observed primary price, sold/remaining supply, resale floor, RAP, seller depth, and resale volume from modeled economics.
- Added a weighted **Data Quality** grade with explicit verified/missing evidence and observation freshness instead of hiding incomplete inputs behind a score.
- Added Hunter DATA grades and evidence tooltips; the selected-drop inspector now shows freshness and source quality alongside supply.
- Added one-click **ANALYZE** from a Hunter row so scanner candidates can be promoted into deeper research without copying IDs manually.
- Added **Creator Intelligence** using a bounded sample of recent Limiteds: verified count, active/sold-out mix, average sell-through, profitable-floor rate after Roblox reseller proceeds, and median floor/cost multiple.
- Creator history is on-demand only and capped to a small sample so normal Hunter refreshes remain lightweight and avoid unnecessary Roblox requests.
- Added analyzer/catalog/data-quality/creator-track-record regressions and bumped the Lite Windows app to v0.9.0.

'''
if not text.startswith(header):
    raise SystemExit("changelog header not found")
text = header + entry + text[len(header):]
p.write_text(text, encoding="utf-8")

print("v0.9.0 intelligence integration patch applied")
