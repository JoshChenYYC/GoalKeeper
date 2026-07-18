using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace GoalKeeper.Web.Runtime;

public interface ISessionRuntimeWorker
{
    Task RunAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}

public interface ISessionRuntimeWorkerRegistry
{
    Guid? ActiveSessionId { get; }

    SessionRuntimeWorkerHandle Start(Guid sessionId);

    bool Cancel(Guid sessionId);
}

public sealed class SessionRuntimeWorkerHandle
{
    internal SessionRuntimeWorkerHandle(
        Guid sessionId,
        Task completion)
    {
        SessionId = sessionId;
        Completion = completion;
    }

    public Guid SessionId { get; }

    public Task Completion { get; }
}

public sealed class SessionRuntimeWorkerRegistry : ISessionRuntimeWorkerRegistry
{
    private readonly object _sync = new();
    private readonly Channel<SessionRuntimeWorkerLease> _requests =
        Channel.CreateUnbounded<SessionRuntimeWorkerLease>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    private SessionRuntimeWorkerLease? _current;

    public Guid? ActiveSessionId
    {
        get
        {
            lock (_sync)
            {
                return _current?.SessionId;
            }
        }
    }

    public SessionRuntimeWorkerHandle Start(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A runtime worker requires a Focus Session identifier.",
                nameof(sessionId));
        }

        var lease = new SessionRuntimeWorkerLease(sessionId);
        lock (_sync)
        {
            if (_current is not null)
            {
                lease.Dispose();
                throw new InvalidOperationException(
                    "A Focus Session runtime worker is already pending or running.");
            }

            _current = lease;
            if (!_requests.Writer.TryWrite(lease))
            {
                _current = null;
                lease.Dispose();
                throw new InvalidOperationException(
                    "The runtime worker scheduler is unavailable.");
            }
        }

        return new(sessionId, lease.Completion);
    }

    public bool Cancel(Guid sessionId)
    {
        SessionRuntimeWorkerLease? lease;
        lock (_sync)
        {
            lease = _current;
            if (lease is null || lease.SessionId != sessionId)
            {
                return false;
            }
        }

        lease.RequestCancellation();
        return true;
    }

    public async Task WaitUntilAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        Task? completion;
        lock (_sync)
        {
            completion = _current?.Completion;
        }

        if (completion is null)
        {
            return;
        }

        try
        {
            await completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A cancelled worker still released the registry slot.
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A failed worker also releases the slot; the hosted service logs the failure.
        }
    }

    internal ValueTask<SessionRuntimeWorkerLease> WaitForStartAsync(
        CancellationToken cancellationToken) =>
        _requests.Reader.ReadAsync(cancellationToken);

    internal void Complete(
        SessionRuntimeWorkerLease lease,
        Exception? failure,
        bool cancelled)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_current, lease))
            {
                return;
            }

            _current = null;
        }

        lease.Complete(failure, cancelled);
        lease.Dispose();
    }

    internal void AbortCurrent()
    {
        SessionRuntimeWorkerLease? lease;
        lock (_sync)
        {
            lease = _current;
            _current = null;
        }

        if (lease is null)
        {
            return;
        }

        lease.RequestCancellation();
        lease.Complete(null, cancelled: true);
        lease.Dispose();
    }
}

internal sealed class SessionRuntimeWorkerLease(Guid sessionId) : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid SessionId { get; } = sessionId;

    public CancellationToken CancellationToken => _cancellation.Token;

    public Task Completion => _completion.Task;

    public void RequestCancellation()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race with a cancellation request.
        }
    }

    public void Complete(Exception? failure, bool cancelled)
    {
        if (failure is not null)
        {
            _completion.TrySetException(failure);
        }
        else if (cancelled)
        {
            _completion.TrySetCanceled();
        }
        else
        {
            _completion.TrySetResult();
        }
    }

    public void Dispose() => _cancellation.Dispose();
}

public sealed class SessionRuntimeHostedService(
    IServiceScopeFactory scopeFactory,
    SessionRuntimeWorkerRegistry registry,
    ILogger<SessionRuntimeHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Guid, Exception?> LogWorkerFailure =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(1, nameof(LogWorkerFailure)),
            "Focus Session runtime worker {SessionId} failed.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var lease = await registry.WaitForStartAsync(stoppingToken);
                await RunWorkerAsync(lease, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown is expected.
        }
        finally
        {
            registry.AbortCurrent();
        }
    }

    private async Task RunWorkerAsync(
        SessionRuntimeWorkerLease lease,
        CancellationToken stoppingToken)
    {
        Exception? failure = null;
        var cancelled = false;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            lease.CancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var worker = scope.ServiceProvider.GetRequiredService<ISessionRuntimeWorker>();
            await worker.RunAsync(lease.SessionId, linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            failure = exception;
            LogWorkerFailure(logger, lease.SessionId, exception);
        }
        finally
        {
            registry.Complete(lease, failure, cancelled);
        }
    }
}
