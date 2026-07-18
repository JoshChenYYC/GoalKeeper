using GoalKeeper.Application;
using GoalKeeper.Application.Monitoring;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Application.Runtime;
using GoalKeeper.Web.Runtime;
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
            Assert.Contains("Deviation Profile", html);
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
}
