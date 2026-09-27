namespace RobloxPriceTracker.Gui;

public partial class MainWindow
{
    // Both former channels migrate to the fixed Latest release.
    private void EnsureUpdateChannelUi() { }
    private bool IsLatestUpdateChannel() => false;
    private string UpdateChannelLabel() => "Latest";
}
