namespace RobloxPriceTracker.Gui;

// Old channel preferences now point at the same permanent, digest-verified EXE.
public static class GitHubUpdateServiceMainLatestExtensions
{
    public static Task<UpdateCheckResult> CheckMainLatestAsync(
        this GitHubUpdateService service,
        CancellationToken cancellationToken = default) => service.CheckAsync(cancellationToken);
}
