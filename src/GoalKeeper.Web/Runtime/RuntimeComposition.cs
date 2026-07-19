using GoalKeeper.Application;
using GoalKeeper.Application.Monitoring;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Application.Runtime;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure;
using GoalKeeper.Infrastructure.Perception;
using GoalKeeper.Infrastructure.Reasoning;
using GoalKeeper.Infrastructure.Recovery;
using GoalKeeper.Web.Operations;
using GoalKeeper.Web.Presentation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Web.Runtime;

public sealed class SessionRuntimeSchedulingOptions
{
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan ReasoningFreshnessLimit { get; set; } = TimeSpan.FromMinutes(1);
}

public static class RuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddGoalKeeperRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddConfiguredProviders(services, configuration);

        services.AddOptions<SessionRuntimeSchedulingOptions>()
            .Bind(configuration.GetSection("GoalKeeper:Runtime"))
            .Validate(
                static options =>
                    options.TickInterval > TimeSpan.Zero &&
                    options.ReasoningFreshnessLimit > TimeSpan.Zero,
                "Runtime scheduling intervals must be positive.")
            .ValidateOnStart();
        services.AddOptions<SessionRuntimeUiOptions>()
            .Bind(configuration.GetSection("GoalKeeper:SessionUi"))
            .Validate(
                static options =>
                    options.CameraDeviceIndex >= 0 &&
                    options.CameraWarmupFrameCount >= 0 &&
                    options.CameraJpegQuality is >= 1 and <= 100 &&
                    options.CaptureCadence > TimeSpan.Zero &&
                    options.ObservationFreshnessLimit > TimeSpan.Zero &&
                    options.TechnicalGracePeriod >= TimeSpan.Zero,
                "Session UI camera and monitoring values are invalid.")
            .ValidateOnStart();

        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton<IGoalKeeperRepository, EfGoalKeeperRepository>();
        services.AddScoped<SetupWorkflow>();
        services.AddSingleton<ISnapshotArtifactStore>(
            static provider => provider.GetRequiredService<SessionArtifactStore>());
        services.TryAddSingleton<IMonitoringDelay, SystemMonitoringDelay>();

        services.TryAddSingleton<IPerceptionPort, UnconfiguredPerceptionPort>();
        services.TryAddSingleton<IReasoningPort, UnconfiguredReasoningPort>();
        services.TryAddSingleton<IRecoveryPort, UnconfiguredRecoveryPort>();

        services.AddSingleton<RuntimeControllerEventRelay>();
        services.AddSingleton<ICameraTechnicalEventSink>(
            static provider => provider.GetRequiredService<RuntimeControllerEventRelay>());
        services.AddSingleton<IMonitoringObservationSink>(
            static provider => provider.GetRequiredService<RuntimeControllerEventRelay>());
        services.AddSingleton<IMonitoringHealthEventSink>(
            static provider => provider.GetRequiredService<RuntimeControllerEventRelay>());
        services.TryAddSingleton<ICameraFactory, OpenCvCameraFactory>();

        services.AddSingleton<PreflightFrameAcquirer>();
        services.AddSingleton<PreflightOrchestrator>();
        services.AddSingleton<MonitoringPipeline>();
        services.AddSingleton(static provider =>
        {
            var options = provider
                .GetRequiredService<IOptions<SessionRuntimeSchedulingOptions>>()
                .Value;
            return new DurableReasoningOrchestrator(
                provider.GetRequiredService<IGoalKeeperRepository>(),
                provider.GetRequiredService<IReasoningPort>(),
                provider.GetRequiredService<IClock>(),
                options.ReasoningFreshnessLimit);
        });
        services.AddSingleton(static provider =>
        {
            var controller = ActivatorUtilities.CreateInstance<SessionRuntimeController>(
                provider);
            provider.GetRequiredService<RuntimeControllerEventRelay>()
                .Attach(controller);
            return controller;
        });
        services.AddSingleton<ISessionRuntimePresentation, SessionRuntimePresentation>();
        services.AddScoped<PostSessionPresentation>();

        services.AddSingleton<SessionRuntimeWorkerRegistry>();
        services.AddSingleton<ISessionRuntimeWorkerRegistry>(
            static provider => provider.GetRequiredService<SessionRuntimeWorkerRegistry>());
        services.AddSingleton<ISessionRuntimeWorkerCoordinator, RegistryRuntimeWorkerCoordinator>();
        services.AddScoped<ISessionRuntimeScheduler, SystemSessionRuntimeScheduler>();
        services.AddScoped<ISessionRuntimeWorker, ControllerSessionRuntimeWorker>();
        services.AddHostedService<SessionRuntimeHostedService>();

        return services;
    }

    private static void AddConfiguredProviders(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var configuredMode = configuration[
            $"{GoalKeeperOperationalOptions.SectionName}:Providers:Mode"];
        if (!Enum.TryParse<GoalKeeperProviderMode>(
                configuredMode,
                ignoreCase: true,
                out var mode) ||
            mode != GoalKeeperProviderMode.Hosted)
        {
            return;
        }

        services.AddOpenAiPerception(configuration);
        services.AddOpenAiReasoning(configuration);
        services.AddOpenAiVoiceRecovery(configuration);
    }
}

public sealed class RegistryRuntimeWorkerCoordinator(
    SessionRuntimeWorkerRegistry registry) : ISessionRuntimeWorkerCoordinator
{
    public Task WaitUntilAvailableAsync(
        CancellationToken cancellationToken = default) =>
        registry.WaitUntilAvailableAsync(cancellationToken);

    public void Start(Guid sessionId)
    {
        if (registry.ActiveSessionId == sessionId)
        {
            return;
        }

        var handle = registry.Start(sessionId);
        _ = handle.Completion.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Cancel(Guid sessionId) => registry.Cancel(sessionId);
}

public sealed class SystemSessionRuntimeScheduler(
    IOptions<SessionRuntimeSchedulingOptions> options) : ISessionRuntimeScheduler
{
    private readonly TimeSpan _tickInterval = options.Value.TickInterval;

    public Task WaitForNextTickAsync(
        CancellationToken cancellationToken = default) =>
        Task.Delay(_tickInterval, cancellationToken);
}

public sealed class ControllerSessionRuntimeWorker(
    SessionRuntimeController controller,
    ISessionRuntimeScheduler scheduler) : ISessionRuntimeWorker
{
    public async Task RunAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var status = await controller.GetStatusAsync(cancellationToken);
        if (status.ControllerState != SessionRuntimeControllerState.Running ||
            status.SessionId != sessionId)
        {
            throw new InvalidOperationException(
                "The requested worker does not match the active Focus Session.");
        }

        await controller.RunSchedulerAsync(scheduler, cancellationToken);
    }
}

public sealed class RuntimeControllerEventRelay(
    GoalKeeperOperationalLogger? operationalLogger = null) :
    ICameraTechnicalEventSink,
    IMonitoringObservationSink,
    IMonitoringHealthEventSink
{
    private SessionRuntimeController? _controller;

    public void Attach(SessionRuntimeController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var existing = Interlocked.CompareExchange(
            ref _controller,
            controller,
            null);
        if (existing is not null && !ReferenceEquals(existing, controller))
        {
            throw new InvalidOperationException(
                "The runtime event relay is already attached.");
        }
    }

    public void Report(CameraTechnicalEvent technicalEvent)
    {
        operationalLogger?.TechnicalBoundaryEvent(
            "camera",
            SnakeCase(technicalEvent.Kind.ToString()),
            Controller.SessionId);
        Controller.Report(technicalEvent);
    }

    public Task PublishAsync(
        ReasoningEligibleObservation observation,
        CancellationToken cancellationToken = default) =>
        Controller.PublishAsync(observation, cancellationToken);

    public void Report(MonitoringHealthEvent healthEvent)
    {
        operationalLogger?.TechnicalBoundaryEvent(
            healthEvent.Source.ToString().ToLowerInvariant(),
            SnakeCase(healthEvent.Kind.ToString()),
            healthEvent.SessionId);
        Controller.Report(healthEvent);
    }

    private static string SnakeCase(string value) =>
        string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? $"_{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));

    private SessionRuntimeController Controller =>
        Volatile.Read(ref _controller) ??
        throw new InvalidOperationException(
            "The runtime event relay has not been attached.");
}

public sealed class UnconfiguredPerceptionPort : IPerceptionPort
{
    public Task<PerceptionResult> ObserveAsync(
        PerceptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PerceptionResult>(
            new PerceptionFailure(
                PerceptionFailureCategory.ProviderUnavailable,
                new(
                    "unconfigured",
                    "none",
                    "perception-v1",
                    ObservationSchemaVersions.V1,
                    TimeSpan.Zero,
                    $"unconfigured-{Guid.NewGuid():N}")));
    }
}

public sealed class UnconfiguredReasoningPort : IReasoningPort
{
    public Task<ReasoningResult> EvaluateAsync(
        ReasoningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ReasoningResult>(
            new ReasoningFailure(
                ReasoningFailureCategory.ProviderUnavailable,
                new(
                    "unconfigured",
                    "none",
                    "reasoning-v1",
                    ReasoningSchemaVersions.V1,
                    TimeSpan.Zero,
                    $"unconfigured-{Guid.NewGuid():N}")));
    }
}

public sealed class UnconfiguredRecoveryPort : IRecoveryPort
{
    public Task<RecoveryPortResult> ProposeAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<RecoveryPortResult>(
            new RecoveryFailureResponse(
                RecoveryFailureCategory.ProviderUnavailable,
                new(
                    "unconfigured",
                    "none",
                    "recovery-v1",
                    RecoverySchemaVersions.V1,
                    TimeSpan.Zero,
                    $"unconfigured-{Guid.NewGuid():N}")));
    }
}
