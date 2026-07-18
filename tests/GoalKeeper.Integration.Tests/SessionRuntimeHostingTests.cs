using System.Threading.Channels;
using GoalKeeper.Web.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoalKeeper.Integration.Tests;

public sealed class SessionRuntimeHostingTests
{
    [Fact]
    public async Task Registry_rejects_a_second_pending_or_running_worker()
    {
        await using var harness = await HostedServiceHarness.StartAsync();
        var sessionId = Guid.NewGuid();

        var first = harness.Registry.Start(sessionId);

        Assert.Equal(sessionId, harness.Registry.ActiveSessionId);
        Assert.Throws<InvalidOperationException>(() =>
            harness.Registry.Start(Guid.NewGuid()));
        await harness.Probe.WaitForStartAsync(sessionId);

        Assert.True(harness.Registry.Cancel(sessionId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => first.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await EventuallyAsync(() => harness.Registry.ActiveSessionId is null);
    }

    [Fact]
    public async Task Session_cancellation_reaches_worker_and_disposes_its_scope()
    {
        await using var harness = await HostedServiceHarness.StartAsync();
        var sessionId = Guid.NewGuid();
        var handle = harness.Registry.Start(sessionId);
        await harness.Probe.WaitForStartAsync(sessionId);

        Assert.False(harness.Registry.Cancel(Guid.NewGuid()));
        Assert.True(harness.Registry.Cancel(sessionId));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handle.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await EventuallyAsync(() =>
            harness.Probe.CancellationCount == 1 &&
            harness.Probe.AsyncDisposalCount == 1);
        Assert.Null(harness.Registry.ActiveSessionId);
    }

    [Fact]
    public async Task Worker_failure_clears_registry_and_allows_the_next_scoped_worker()
    {
        await using var harness = await HostedServiceHarness.StartAsync();
        harness.Probe.Mode = RuntimeWorkerMode.Fail;
        var failedSessionId = Guid.NewGuid();

        var failed = harness.Registry.Start(failedSessionId);
        await harness.Probe.WaitForStartAsync(failedSessionId);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failed.Completion.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("Scripted runtime worker failure.", exception.Message);
        await EventuallyAsync(() =>
            harness.Registry.ActiveSessionId is null &&
            harness.Probe.AsyncDisposalCount == 1);

        harness.Probe.Mode = RuntimeWorkerMode.Complete;
        var completedSessionId = Guid.NewGuid();
        var completed = harness.Registry.Start(completedSessionId);
        await harness.Probe.WaitForStartAsync(completedSessionId);
        await completed.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await EventuallyAsync(() =>
            harness.Registry.ActiveSessionId is null &&
            harness.Probe.AsyncDisposalCount == 2);
        Assert.Equal(2, harness.Probe.ConstructionCount);
    }

    [Fact]
    public async Task Host_shutdown_cancels_worker_disposes_scope_and_clears_registry()
    {
        await using var harness = await HostedServiceHarness.StartAsync();
        var sessionId = Guid.NewGuid();
        var handle = harness.Registry.Start(sessionId);
        await harness.Probe.WaitForStartAsync(sessionId);

        await harness.StopAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handle.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(harness.Registry.ActiveSessionId);
        Assert.Equal(1, harness.Probe.CancellationCount);
        Assert.Equal(1, harness.Probe.AsyncDisposalCount);
    }

    [Fact]
    public async Task Application_coordinator_routes_start_and_terminal_cancel_to_registry()
    {
        await using var harness = await HostedServiceHarness.StartAsync();
        var coordinator = new RegistryRuntimeWorkerCoordinator(harness.Registry);
        var sessionId = Guid.NewGuid();

        coordinator.Start(sessionId);
        await harness.Probe.WaitForStartAsync(sessionId);
        Assert.Equal(sessionId, harness.Registry.ActiveSessionId);

        coordinator.Cancel(sessionId);

        await EventuallyAsync(() =>
            harness.Registry.ActiveSessionId is null &&
            harness.Probe.CancellationCount == 1 &&
            harness.Probe.AsyncDisposalCount == 1);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(1);
        }

        Assert.Fail("The asynchronous condition was not reached within five seconds.");
    }

    private sealed class HostedServiceHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private bool _stopped;

        private HostedServiceHarness(
            ServiceProvider provider,
            RuntimeWorkerProbe probe,
            SessionRuntimeWorkerRegistry registry,
            SessionRuntimeHostedService service)
        {
            _provider = provider;
            Probe = probe;
            Registry = registry;
            Service = service;
        }

        public RuntimeWorkerProbe Probe { get; }

        public SessionRuntimeWorkerRegistry Registry { get; }

        private SessionRuntimeHostedService Service { get; }

        public static async Task<HostedServiceHarness> StartAsync()
        {
            var services = new ServiceCollection();
            services.AddSingleton<RuntimeWorkerProbe>();
            services.AddScoped<ISessionRuntimeWorker, RecordingRuntimeWorker>();
            var provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateScopes = true });
            var probe = provider.GetRequiredService<RuntimeWorkerProbe>();
            var registry = new SessionRuntimeWorkerRegistry();
            var service = new SessionRuntimeHostedService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                registry,
                NullLogger<SessionRuntimeHostedService>.Instance);
            var harness = new HostedServiceHarness(provider, probe, registry, service);
            await service.StartAsync(CancellationToken.None);
            return harness;
        }

        public async Task StopAsync()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Service.StopAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Service.Dispose();
            await _provider.DisposeAsync();
        }
    }

    private enum RuntimeWorkerMode
    {
        Block,
        Complete,
        Fail
    }

    private sealed class RuntimeWorkerProbe
    {
        private readonly Channel<Guid> _starts = Channel.CreateUnbounded<Guid>();
        private int _constructionCount;
        private int _cancellationCount;
        private int _asyncDisposalCount;
        private int _mode;

        public int ConstructionCount => Volatile.Read(ref _constructionCount);

        public int CancellationCount => Volatile.Read(ref _cancellationCount);

        public int AsyncDisposalCount => Volatile.Read(ref _asyncDisposalCount);

        public RuntimeWorkerMode Mode
        {
            get => (RuntimeWorkerMode)Volatile.Read(ref _mode);
            set => Volatile.Write(ref _mode, (int)value);
        }

        public void RecordConstruction() => Interlocked.Increment(ref _constructionCount);

        public void RecordStart(Guid sessionId) => _starts.Writer.TryWrite(sessionId);

        public void RecordCancellation() => Interlocked.Increment(ref _cancellationCount);

        public void RecordAsyncDisposal() => Interlocked.Increment(ref _asyncDisposalCount);

        public async Task WaitForStartAsync(Guid expectedSessionId)
        {
            var actual = await _starts.Reader.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expectedSessionId, actual);
        }
    }

    private sealed class RecordingRuntimeWorker : ISessionRuntimeWorker, IAsyncDisposable
    {
        private readonly RuntimeWorkerProbe _probe;

        public RecordingRuntimeWorker(RuntimeWorkerProbe probe)
        {
            _probe = probe;
            _probe.RecordConstruction();
        }

        public async Task RunAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            _probe.RecordStart(sessionId);
            switch (_probe.Mode)
            {
                case RuntimeWorkerMode.Complete:
                    return;
                case RuntimeWorkerMode.Fail:
                    throw new InvalidOperationException(
                        "Scripted runtime worker failure.");
                case RuntimeWorkerMode.Block:
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        _probe.RecordCancellation();
                        throw;
                    }

                    break;
                default:
                    throw new InvalidOperationException("Unknown runtime worker mode.");
            }
        }

        public ValueTask DisposeAsync()
        {
            _probe.RecordAsyncDisposal();
            return ValueTask.CompletedTask;
        }
    }
}
