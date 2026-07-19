using GoalKeeper.Application;
using GoalKeeper.Application.Monitoring;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Application.Runtime;
using GoalKeeper.Web.Runtime;
using GoalKeeper.Web.Presentation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            Assert.Contains("<h1>Goals</h1>", html);
            Assert.Contains("Focus profile", html);
            Assert.True(File.Exists(Path.Combine(dataRoot, "goalkeeper.db")));
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
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
            Assert.Contains("Return to goals", liveHtml);
            Assert.True(preflightResponse.IsSuccessStatusCode, preflightHtml);
            Assert.Contains("The camera check could not open.", preflightHtml);
            Assert.Contains("Try again", preflightHtml);
            Assert.True(readyResponse.IsSuccessStatusCode, readyHtml);
            Assert.Contains("This setup is no longer ready.", readyHtml);
            Assert.Contains("Return to goals", readyHtml);
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
            Assert.Contains("Return to goals", reviewHtml);
            Assert.True(historyResponse.IsSuccessStatusCode, historyHtml);
            Assert.Contains("This Goal could not be found.", historyHtml);
            Assert.Contains("Return to goals", historyHtml);
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
            Assert.Contains("Session setup isn’t ready.", html);
            Assert.Contains(
                "Create a Deviation Profile before preparing a session.",
                html);
            Assert.Contains("Create Focus profile", html);
            Assert.Contains("Return to goals", html);
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
