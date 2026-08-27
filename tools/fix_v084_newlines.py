from pathlib import Path

p = Path("src/RobloxPriceTracker.Gui/MainWindow.TerminalUi.cs")
text = p.read_text(encoding="utf-8")
bad = '''        if (_hunterInspectorForecast is not null) _hunterInspectorForecast.Text = $"FLOOR {item.CurrentResaleText} · RAP {item.RapText} · SELLERS {item.ResellersText}
SALES {item.SalesText} · BREAK-EVEN {item.BreakEvenText}
MODEL {item.ForecastText} · BASE {item.BaseValueText} · NET ROI {item.NetRoiText}";'''
good = '''        if (_hunterInspectorForecast is not null) _hunterInspectorForecast.Text = $"FLOOR {item.CurrentResaleText} · RAP {item.RapText} · SELLERS {item.ResellersText}\\nSALES {item.SalesText} · BREAK-EVEN {item.BreakEvenText}\\nMODEL {item.ForecastText} · BASE {item.BaseValueText} · NET ROI {item.NetRoiText}";'''
if text.count(bad) != 1:
    raise SystemExit(f"expected one generated multiline inspector string, found {text.count(bad)}")
p.write_text(text.replace(bad, good, 1), encoding="utf-8")
print("fixed generated C# inspector newlines")
