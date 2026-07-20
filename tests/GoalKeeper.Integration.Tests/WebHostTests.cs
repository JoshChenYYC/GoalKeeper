using GoalKeeper.Application;
using GoalKeeper.Application.Monitoring;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Application.Runtime;
using GoalKeeper.Domain;
using GoalKeeper.Web.Runtime;
using GoalKeeper.Web.Presentation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Integration.Tests;

public sealed class WebHostTests
{
    [Fact]
    public async Task Blazor_host_applies_migrations_and_renders_setup_navigation()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"goalkeeper-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("GoalKeeper:DataRoot", dataRoot);
                builder.ConfigureLogging(logging => logging.ClearProviders());
            });
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/");
            var html = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, html);
            Assert.Contains(">Home</h1>", html);
            Assert.Contains("Accountability rules", html);
            Assert.Contains("Settings", html);
            Assert.True(File.Exists(Path.Combine(dataRoot, "goalkeeper.db")));
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_route_exposes_speech_model_voice_and_disclosure()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            $"goalkeeper-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("GoalKeeper:DataRoot", dataRoot);
                    builder.ConfigureLogging(logging =>
                        logging.ClearProviders());
                });
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/settings");
            var html = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, html);
            Assert.Contains(">Settings</h1>", html);
            Assert.Contains("Speech model", html);
            Assert.Contains("GPT-4o mini TTS", html);
            Assert.Contains("TTS-1 HD", html);
            Assert.Contains("Voice", html);
            Assert.Contains("AI-generated", html);
            Assert.Contains("Save voice settings", html);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_starts_and_resolves_complete_runtime_composition_without_credentials()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            $"goalkeeper-runtime-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("GoalKeeper:DataRoot", dataRoot);
                builder.ConfigureLogging(logging => logging.ClearProviders());
            });
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/");
            var controller = factory.Services.GetRequiredService<SessionRuntimeController>();
            var repository = factory.Services.GetRequiredService<IGoalKeeperRepository>();
            var concreteRegistry =
                factory.Services.GetRequiredService<SessionRuntimeWorkerRegistry>();
            var registry =
                factory.Services.GetRequiredService<ISessionRuntimeWorkerRegistry>();
            var coordinator =
                factory.Services.GetRequiredService<ISessionRuntimeWorkerCoordinator>();
            await using var scope = factory.Services.CreateAsyncScope();

            Assert.True(response.IsSuccessStatusCode);
            Assert.NotNull(controller);
            Assert.NotNull(repository);
            Assert.Same(concreteRegistry, registry);
            Assert.IsType<RegistryRuntimeWorkerCoordinator>(coordinator);
            Assert.IsType<UnconfiguredPerceptionPort>(
                factory.Services.GetRequiredService<IPerceptionPort>());
            Assert.IsType<UnconfiguredReasoningPort>(
                factory.Services.GetRequiredService<IReasoningPort>());
            Assert.IsType<UnconfiguredRecoveryPort>(
                factory.Services.GetRequiredService<IRecoveryPort>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISessionRuntimeWorker>());
            Assert.IsType<SystemSessionRuntimeScheduler>(
                scope.ServiceProvider.GetRequiredService<ISessionRuntimeScheduler>());
            Assert.NotNull(factory.Services.GetRequiredService<MonitoringPipeline>());
            Assert.NotNull(factory.Services.GetRequiredService<ISessionRuntimePresentation>());
            Assert.Equal(
                TimeSpan.FromSeconds(3),
                factory.Services
                    .GetRequiredService<IOptions<SessionRuntimeUiOptions>>()
                    .Value
                    .CaptureCadence);
            Assert.Null(concreteRegistry.ActiveSessionId);
            Assert.Equal(
                SessionRuntimeControllerState.Idle,
                (await controller.GetStatusAsync()).ControllerState);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Restart_does_not_orphan_a_persisted_nonterminal_focus_session()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            $"goalkeeper-restart-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            Guid sessionId;
            using (var firstHost = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                   {
                       builder.UseSetting("GoalKeeper:DataRoot", dataRoot);
                       builder.ConfigureLogging(logging => logging.ClearProviders());
                   }))
            {
                using var client = firstHost.CreateClient();
                _ = await client.GetAsync("/");
                await using var scope = firstHost.Services.CreateAsyncScope();
                var workflow = scope.ServiceProvider.GetRequiredService<SetupWorkflow>();
                var repository = scope.ServiceProvider.GetRequiredService<IGoalKeeperRepository>();
                var clock = scope.ServiceProvider.GetRequiredService<IClock>();
                await workflow.SaveProfileAsync(
                    "Default",
                    [new("Phone", VisualObservability.Observable)]);
                var goal = await workflow.CreateGoalAsync("Survives restart", null);
                var setup = await workflow.ConfirmAsync(await workflow.PrepareAsync(goal.Id));
                var domainGoal = Goal.Rehydrate(
                    goal.Id,
                    goal.Title,
                    goal.Description,
                    goal.Status,
                    goal.CreatedAtUtc,
                    goal.CompletedAtUtc);
                var contract = SessionContract.Rehydrate(
                    setup.Contract.Id,
                    new GoalSnapshot(
                        setup.Contract.GoalId,
                        setup.Contract.GoalTitle,
                        setup.Contract.GoalDescription),
                    setup.Contract.TargetFocusDuration,
                    setup.Contract.ScheduledBreaks.Select(value =>
                        ScheduledBreak.Create(value.ActiveFocusOffset, value.Duration)),
                    new DeviationProfileSnapshot(
                        setup.Contract.DeviationProfileId,
                        setup.Contract.DeviationProfileName,
                        setup.Contract.Deviations.Select(value =>
                            new DeviationSnapshot(
                                value.Id,
                                value.Description,
                                value.Observability)).ToArray()),
                    setup.Contract.ReasoningMode,
                    setup.Contract.Sensitivity,
                    setup.Contract.ConfirmedAtUtc);
                var session = FocusSession.Start(domainGoal, contract, true, clock);
                var persisted = await repository.StartSessionAsync(
                    setup.Id,
                    setup.Version,
                    session.CreateSnapshot());
                sessionId = persisted.Id;
                Assert.Equal(FocusSessionState.Focusing, persisted.State);
            }

            using var restartedHost = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("GoalKeeper:DataRoot", dataRoot);
                builder.ConfigureLogging(logging => logging.ClearProviders());
            });
            using var restartedClient = restartedHost.CreateClient();
            _ = await restartedClient.GetAsync("/");
            var restartedController =
                restartedHost.Services.GetRequiredService<SessionRuntimeController>();
            var restartedRepository =
                restartedHost.Services.GetRequiredService<IGoalKeeperRepository>();
            var status = await restartedController.GetStatusAsync();
            var persistedAfterRestart = await restartedRepository.GetSessionAsync(sessionId);
            Assert.NotNull(persistedAfterRestart);
            Assert.Equal(FocusSessionState.EndedEarly, persistedAfterRestart.State);
            Assert.Equal(
                EndedEarlyReason.ApplicationInterrupted,
                persistedAfterRestart.Runtime.EndedEarlyReason);
            Assert.Null(await restartedRepository.GetActiveSessionAsync());
            Assert.Equal(SessionRuntimeControllerState.Idle, status.ControllerState);
            Assert.Null(status.SessionId);
            Assert.Equal(
                GoalStatus.Active,
                (await restartedRepository.GetGoalAsync(persistedAfterRestart.GoalId))!.Status);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Live_and_preflight_routes_render_actionable_disconnected_states()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            $"goalkeeper-live-ui-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("GoalKeeper:DataRoot", dataRoot);
                builder.ConfigureLogging(logging => logging.ClearProviders());
            });
            using var client = factory.CreateClient();

            var liveResponse = await client.GetAsync($"/sessions/{Guid.NewGuid()}/live");
            var liveHtml = await liveResponse.Content.ReadAsStringAsync();
            var preflightResponse = await client.GetAsync($"/sessions/{Guid.NewGuid()}/preflight");
            var preflightHtml = await preflightResponse.Content.ReadAsStringAsync();
            var readyResponse = await client.GetAsync($"/sessions/{Guid.NewGuid()}/ready");
            var readyHtml = await readyResponse.Content.ReadAsStringAsync();

            Assert.True(liveResponse.IsSuccessStatusCode, liveHtml);
            Assert.Contains("This live session isn’t connected.", liveHtml);
            Assert.Contains("Return Home", liveHtml);
            Assert.True(preflightResponse.IsSuccessStatusCode, preflightHtml);
            Assert.Contains("The camera check could not open.", preflightHtml);
            Assert.Contains("Try again", preflightHtml);
            Assert.True(readyResponse.IsSuccessStatusCode, readyHtml);
            Assert.Contains("This setup is no longer ready.", readyHtml);
            Assert.Contains("Return Home", readyHtml);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Review_and_history_routes_render_safe_missing_record_states()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            $"goalkeeper-post-session-ui-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("GoalKeeper:DataRoot", dataRoot);
                builder.ConfigureLogging(logging => logging.ClearProviders());
            });
            using var client = factory.CreateClient();

            var reviewResponse = await client.GetAsync($"/sessions/{Guid.NewGuid()}/review");
            var reviewHtml = await reviewResponse.Content.ReadAsStringAsync();
            var historyResponse = await client.GetAsync($"/goals/{Guid.NewGuid()}/history");
            var historyHtml = await historyResponse.Content.ReadAsStringAsync();

            Assert.True(reviewResponse.IsSuccessStatusCode, reviewHtml);
            Assert.Contains("This Focus Session could not be reviewed.", reviewHtml);
            Assert.Contains("Return Home", reviewHtml);
            Assert.True(historyResponse.IsSuccessStatusCode, historyHtml);
            Assert.Contains("This Goal could not be found.", historyHtml);
            Assert.Contains("Return Home", historyHtml);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Session_setup_without_a_profile_renders_an_actionable_state()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            $"goalkeeper-setup-without-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("GoalKeeper:DataRoot", dataRoot);
                builder.ConfigureLogging(logging => logging.ClearProviders());
            });
            using var client = factory.CreateClient();
            await using var scope = factory.Services.CreateAsyncScope();
            var workflow = scope.ServiceProvider.GetRequiredService<SetupWorkflow>();
            var goal = await workflow.CreateGoalAsync("Profile prerequisite", null);

            var response = await client.GetAsync($"/sessions/setup/{goal.Id}");
            var html = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, html);
            Assert.Contains("This focus session isn’t ready.", html);
            Assert.Contains(
                "Define accountability rules before preparing a session.",
                html);
            Assert.Contains("Define accountability rules", html);
            Assert.Contains("Return Home", html);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }
}
