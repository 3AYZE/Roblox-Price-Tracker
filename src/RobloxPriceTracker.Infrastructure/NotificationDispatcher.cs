namespace RobloxPriceTracker.Infrastructure;

public interface INotificationSink
{
    Task SendAsync(string title, string body, CancellationToken cancellationToken);
}

public sealed class ConsoleNotificationSink : INotificationSink
{
    public Task SendAsync(string title, string body, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"=== ALERT: {title} ===");
        Console.WriteLine(body);
        Console.WriteLine("===============================");
        Console.WriteLine();
        return Task.CompletedTask;
    }
}

public sealed class NotificationDispatcher
{
    private readonly JsonFileRepository _repository;
    private readonly INotificationSink _sink;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);

    public NotificationDispatcher(JsonFileRepository repository, INotificationSink sink, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _sink = sink;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = await _repository.GetPendingNotificationsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var delivered = 0;
            foreach (var notification in pending)
            {
                try
                {
                    await _sink.SendAsync(notification.Title, notification.Body, cancellationToken).ConfigureAwait(false);
                    await _repository.MarkNotificationDeliveredAsync(notification.Id, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    delivered++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    await _repository.MarkNotificationAttemptFailedAsync(notification.Id, cancellationToken).ConfigureAwait(false);
                }
            }
            return delivered;
        }
        finally
        {
            _dispatchGate.Release();
        }
    }
}
