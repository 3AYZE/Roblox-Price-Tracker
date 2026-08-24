using System.Windows;
using System.Windows.Media.Imaging;
using RobloxPriceTracker.Core;

namespace RobloxPriceTracker.Gui;

public partial class AddItemWindow : Window
{
    private readonly AppServices _services;
    private readonly bool _editing;
    private MarketObservation? _resolvedObservation;
    private ItemKey? _resolvedKey;

    public AddItemWindow(AppServices services, string? initialItem = null, long? initialTarget = null)
    {
        _services = services;
        _editing = !string.IsNullOrWhiteSpace(initialItem);
        InitializeComponent();

        if (_editing)
        {
            DialogTitleText.Text = "Edit target price";
            DialogSubtitleText.Text = "Refresh the current marketplace state, then update the alert threshold.";
            SaveButton.Content = "SAVE TARGET";
            ItemInputTextBox.Text = initialItem;
            ItemInputTextBox.IsReadOnly = true;
            VerifyButton.Content = "REFRESH";
        }

        if (initialTarget is > 0)
        {
            TargetPriceTextBox.Text = initialTarget.Value.ToString();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_editing)
        {
            await ResolveItemAsync();
        }
        else
        {
            ItemInputTextBox.Focus();
        }
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e) => await ResolveItemAsync();

    private void ItemInputTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_editing)
        {
            return;
        }

        _resolvedObservation = null;
        _resolvedKey = null;
        ResolvedItemPanel.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = false;
        HideError();
    }

    private async Task<bool> ResolveItemAsync()
    {
        HideError();
        if (!ItemInputParser.TryParseAsset(ItemInputTextBox.Text, out var key, out var parseError))
        {
            ShowError(parseError ?? "Enter a valid Roblox asset ID or catalog URL.");
            return false;
        }

        SetBusy(true, "Checking Roblox...");
        try
        {
            var fetch = await _services.Provider.FetchAsync(new[] { key }, CancellationToken.None);
            if (fetch.Failure is not null)
            {
                ShowError($"Roblox lookup failed: {fetch.Failure.Message}");
                return false;
            }

            if (!fetch.Observations.TryGetValue(key, out var observation) || observation.Status == MarketStatus.MissingFromResponse)
            {
                ShowError("Roblox did not return this asset. It may be invalid, deleted, moderated, or temporarily unavailable.");
                return false;
            }

            if (!observation.IsResaleCapable || observation.Status == MarketStatus.Unsupported)
            {
                ShowError("This asset is not confirmed as a resellable Limited/collectible, so it cannot be tracked safely.");
                return false;
            }

            _resolvedObservation = observation;
            _resolvedKey = key;
            ResolvedNameText.Text = observation.Name ?? key.ToString();
            ResolvedAssetIdText.Text = $"Asset ID {key.Id}";
            ResolvedPriceText.Text = observation.LowestResalePrice is > 0
                ? $"Current lowest reseller: {observation.LowestResalePrice:N0} R$"
                : $"Marketplace status: {FormatMarketStatus(observation.Status)}";
            SetResolvedBadge(observation.Status);
            ResolvedItemPanel.Visibility = Visibility.Visible;
            SaveButton.IsEnabled = true;

            var thumbnailUrl = await _services.ThumbnailService.GetAssetThumbnailUrlAsync(key.Id);
            if (!string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                try
                {
                    ResolvedThumbnail.Source = new BitmapImage(new Uri(thumbnailUrl));
                    ResolvedThumbnailFallback.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    ResolvedThumbnail.Source = null;
                    ResolvedThumbnailFallback.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ResolvedThumbnail.Source = null;
                ResolvedThumbnailFallback.Visibility = Visibility.Visible;
            }

            return true;
        }
        catch (Exception ex)
        {
            _services.Logger.Error(ex.ToString());
            ShowError(ex.Message);
            return false;
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        HideError();
        if (!ItemInputParser.TryParseAsset(ItemInputTextBox.Text, out var key, out var parseError))
        {
            ShowError(parseError ?? "Enter a valid Roblox asset ID or catalog URL.");
            return;
        }

        if (_resolvedObservation is null || _resolvedKey != key)
        {
            if (!await ResolveItemAsync())
            {
                return;
            }
        }

        var normalizedTarget = TargetPriceTextBox.Text.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        if (!long.TryParse(normalizedTarget, out var target))
        {
            ShowError("Target price must be a whole number of Robux.");
            return;
        }

        try
        {
            PriceValidation.ThrowIfInvalidTarget(target);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return;
        }

        var observation = _resolvedObservation!;
        SetBusy(true, "Saving watchlist...");
        try
        {
            await _services.Repository.AddOrUpdateTrackedAssetAsync(observation, target);
            var snapshot = await _services.Repository.LoadSnapshotAsync(key) ?? throw new InvalidOperationException("The saved item could not be reloaded.");
            var sequence = await _services.Repository.GetMaxPollSequenceAsync() + 1;
            var decision = _services.AlertEngine.Evaluate(snapshot, observation, sequence, TimeProvider.System.GetUtcNow());
            var committed = await _services.Repository.CommitDecisionAsync(decision, sequence);
            if (!committed)
            {
                throw new InvalidOperationException("The item changed while it was being saved. Try again.");
            }

            await _services.NotificationDispatcher.DispatchPendingAsync();
            _services.Logger.Info($"GUI {(_editing ? "updated" : "added")} {key} with target {target:N0} R$.");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _services.Logger.Error(ex.ToString());
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void SetResolvedBadge(MarketStatus status)
    {
        switch (status)
        {
            case MarketStatus.Available:
                ResolvedStatusText.Text = "Verified";
                SetBadgeColor(13, 40, 27, 45, 216, 129);
                break;
            case MarketStatus.NoResellers:
                ResolvedStatusText.Text = "No sellers";
                SetBadgeColor(51, 37, 21, 255, 184, 107);
                break;
            case MarketStatus.OffSale:
                ResolvedStatusText.Text = "Off sale";
                SetBadgeColor(35, 40, 51, 170, 178, 191);
                break;
            default:
                ResolvedStatusText.Text = "Verified";
                SetBadgeColor(31, 41, 54, 154, 165, 180);
                break;
        }
    }

    private void SetBadgeColor(byte br, byte bg, byte bb, byte fr, byte fg, byte fb)
    {
        ResolvedStatusBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(br, bg, bb));
        ResolvedStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(fr, fg, fb));
    }

    private void SetBusy(bool busy, string message)
    {
        VerifyButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy && _resolvedObservation is not null;
        ItemInputTextBox.IsEnabled = !busy && !_editing;
        TargetPriceTextBox.IsEnabled = !busy;
        BusyText.Text = message;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Text = string.Empty;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private static string FormatMarketStatus(MarketStatus status) => status switch
    {
        MarketStatus.Available => "Available",
        MarketStatus.NoResellers => "No sellers",
        MarketStatus.OffSale => "Off sale",
        MarketStatus.InvalidPrice => "Price issue",
        _ => status.ToString()
    };

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
