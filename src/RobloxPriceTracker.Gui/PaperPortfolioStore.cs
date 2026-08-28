using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

namespace RobloxPriceTracker.Gui;

public sealed class PaperPortfolioStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
    private List<PaperPosition> _positions = new();
    private bool _initialized;

    public PaperPortfolioStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "paper-portfolio.json");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            _positions = await SafeJsonStore.LoadWithRecoveryAsync<List<PaperPosition>>(_path, Options, cancellationToken: cancellationToken).ConfigureAwait(false)
                         ?? new List<PaperPosition>();
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PaperPosition>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _positions.OrderByDescending(x => x.EntryAtUtc).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PaperPosition> AddAsync(UgcHunterItem item, int quantity = 1, CancellationToken cancellationToken = default)
    {
        if (quantity < 1 || quantity > 999) throw new ArgumentOutOfRangeException(nameof(quantity));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var position = new PaperPosition(
            Guid.NewGuid().ToString("N"),
            item.AssetId,
            item.Name,
            item.CreatorName,
            quantity,
            item.Price,
            DateTimeOffset.UtcNow,
            item.OpportunityScore,
            item.EntryScore,
            item.RiskScore,
            item.Confidence,
            item.BearValue,
            item.BaseValue,
            item.BullValue,
            null,
            null,
            null);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _positions.Add(position);
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
            return position;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateMarketAsync(string id, long? currentFloor, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _positions.FindIndex(x => x.Id == id);
            if (index < 0) return;
            var current = _positions[index];
            _positions[index] = current with
            {
                CurrentFloor = currentFloor,
                LastMarketAtUtc = observedAtUtc,
                FirstResaleAtUtc = current.FirstResaleAtUtc ?? (currentFloor is > 0 ? observedAtUtc : null)
            };
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _positions.RemoveAll(x => x.Id == id);
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task PersistLockedAsync(CancellationToken cancellationToken) =>
        SafeJsonStore.SaveWithBackupAsync(_path, _positions, Options, cancellationToken);
}

public sealed record PaperPosition(
    string Id,
    long AssetId,
    string Name,
    string CreatorName,
    int Quantity,
    long EntryPrice,
    DateTimeOffset EntryAtUtc,
    double OpportunityScore,
    double EntryScore,
    double RiskScore,
    double Confidence,
    int BearForecast,
    int BaseForecast,
    int BullForecast,
    long? CurrentFloor,
    DateTimeOffset? FirstResaleAtUtc,
    DateTimeOffset? LastMarketAtUtc);

public sealed class PaperPositionRow
{
    public PaperPosition Position { get; init; } = default!;
    public string Name => Position.Name;
    public string Creator => Position.CreatorName;
    public string Quantity => Position.Quantity.ToString("N0");
    public string Entry => $"{Position.EntryPrice:N0} R$";
    public string Floor => Position.CurrentFloor is > 0 ? $"{Position.CurrentFloor:N0} R$" : "WAITING";
    public string Value => Position.CurrentFloor is > 0
        ? $"{Position.CurrentFloor.Value * UgcResaleScoring.CommunityLimitedResellerShare * Position.Quantity:N0} R$"
        : "—";
    public string Cost => $"{Position.EntryPrice * Position.Quantity:N0} R$";
    public string ProfitLoss
    {
        get
        {
            if (Position.CurrentFloor is not > 0) return "—";
            var delta = (Position.CurrentFloor.Value * UgcResaleScoring.CommunityLimitedResellerShare - Position.EntryPrice) * Position.Quantity;
            return $"{delta:+#,##0;-#,##0;0} R$";
        }
    }
    public string Roi
    {
        get
        {
            if (Position.CurrentFloor is not > 0 || Position.EntryPrice <= 0) return "—";
            var roi = (Position.CurrentFloor.Value * UgcResaleScoring.CommunityLimitedResellerShare - Position.EntryPrice) / Position.EntryPrice;
            return $"{roi:+0.0%;-0.0%;0.0%}";
        }
    }
    public string Forecast => $"{Position.BearForecast:N0}–{Position.BullForecast:N0} R$";
    public string Scores => $"P{Position.OpportunityScore:0} · E{Position.EntryScore:0} · R{Position.RiskScore:0}";
    public string Entered => Position.EntryAtUtc.ToLocalTime().ToString("MMM d · h:mm tt");
    public Brush ProfitForeground => Position.CurrentFloor is not > 0 ? TerminalBrushes.Muted
        : Position.CurrentFloor.Value * UgcResaleScoring.CommunityLimitedResellerShare >= Position.EntryPrice
            ? TerminalBrushes.Green
            : TerminalBrushes.Red;
}

internal static class TerminalBrushes
{
    public static readonly Brush Green = new SolidColorBrush(Color.FromRgb(39, 211, 139));
    public static readonly Brush Red = new SolidColorBrush(Color.FromRgb(255, 93, 108));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(112, 126, 145));
}
