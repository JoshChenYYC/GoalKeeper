using GoalKeeper.Application;
using GoalKeeper.Domain;
using GoalKeeper.Web.Presentation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GoalKeeper.Integration.Tests;

public sealed class GuidedHomeTests
{
    [Fact]
    public async Task Nothing_configured_starts_with_goal_creation()
    {
        await using var harness = await HomeHarness.CreateAsync();

        var html = await harness.HtmlAsync("/");

        Assert.Contains("Three steps. No guesswork.", html);
        Assert.Contains("0 of 3 ready", html);
        Assert.Contains("Create your first Goal", html);
        Assert.DoesNotContain("Start session", html);
    }

    [Fact]
    public async Task Goal_only_advances_to_accountability_rules()
    {
        Guid goalId = default;
        await using var harness = await HomeHarness.CreateAsync(async workflow =>
        {
            goalId = (await workflow.CreateGoalAsync("Pass the supplied OA", null)).Id;
        });

        var html = await harness.HtmlAsync($"/?goal={goalId:D}");

        Assert.Contains("Goal selected", html);
        Assert.Contains("Define accountability rules", html);
        Assert.Contains("1 of 3 ready", html);
        Assert.DoesNotContain($"/sessions/setup/{goalId}", html);
    }

    [Fact]
    public async Task Rules_only_keeps_goal_creation_active()
    {
        await using var harness = await HomeHarness.CreateAsync(async workflow =>
            await workflow.SaveProfileAsync(
                "Desk rules",
                [new("I use my phone", VisualObservability.Observable)]));

        var html = await harness.HtmlAsync("/");

        Assert.Contains("Create your first Goal", html);
        Assert.Contains("1 of 3 ready", html);
        Assert.Contains("Define accountability rules", html);
    }

    [Fact]
    public async Task Goal_and_rules_show_the_ready_dashboard()
    {
        Guid goalId = default;
        await using var harness = await HomeHarness.CreateAsync(async workflow =>
        {
            goalId = (await workflow.CreateGoalAsync("Write the report", null)).Id;
            await workflow.SaveProfileAsync(
                "Desk rules",
                [new("I use my phone", VisualObservability.Observable)]);
        });

        var html = await harness.HtmlAsync($"/?goal={goalId:D}");

        Assert.Contains("Ready when you are", html);
        Assert.Contains($"/sessions/setup/{goalId}", html);
        Assert.Contains("Start session", html);
        Assert.DoesNotContain("Three steps. No guesswork.", html);
    }

    [Fact]
    public async Task Rules_return_activates_the_start_session_step()
    {
        Guid goalId = default;
        await using var harness = await HomeHarness.CreateAsync(async workflow =>
        {
            goalId = (await workflow.CreateGoalAsync("Write the report", null)).Id;
            await workflow.SaveProfileAsync(
                "Desk rules",
                [new("I use my phone", VisualObservability.Observable)]);
        });

        var html = await harness.HtmlAsync(
            $"/?goal={goalId:D}&guide=true");

        Assert.Contains("Ready to focus", html);
        Assert.Contains("2 of 3 ready", html);
        Assert.Contains($"/sessions/setup/{goalId}", html);
    }

    [Fact]
    public async Task Invalid_selected_goal_falls_back_without_exposing_a_dead_end()
    {
        Guid goalId = default;
        await using var harness = await HomeHarness.CreateAsync(async workflow =>
        {
            goalId = (await workflow.CreateGoalAsync("Prepare the demo", null)).Id;
            await workflow.SaveProfileAsync(
                "Desk rules",
                [new("I leave the desk", VisualObservability.Observable)]);
        });

        var html = await harness.HtmlAsync($"/?goal={Guid.NewGuid():D}");

        Assert.Contains("Prepare the demo", html);
        Assert.Contains($"/sessions/setup/{goalId}", html);
        Assert.DoesNotContain("Goal not found", html);
    }

    [Theory]
    [InlineData("/?goal=11111111-1111-1111-1111-111111111111&guide=true", true)]
    [InlineData("/sessions/setup/11111111-1111-1111-1111-111111111111", true)]
    [InlineData("https://example.com", false)]
    [InlineData("//example.com", false)]
    [InlineData("/\\example.com", false)]
    [InlineData("", false)]
    public void Return_path_accepts_only_bounded_local_routes(
        string candidate,
        bool accepted)
    {
        Assert.Equal(
            accepted ? candidate : null,
            OnboardingReturnPath.Validate(candidate));
    }

    private sealed class HomeHarness : IAsyncDisposable
    {
        private readonly string _dataRoot;
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;

        private HomeHarness(
            string dataRoot,
            WebApplicationFactory<Program> factory,
            HttpClient client)
        {
            _dataRoot = dataRoot;
            _factory = factory;
            _client = client;
        }

        public static async Task<HomeHarness> CreateAsync(
            Func<SetupWorkflow, Task>? configure = null)
        {
            var dataRoot = Path.Combine(
                Path.GetTempPath(),
                $"goalkeeper-guided-home-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataRoot);
            var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("GoalKeeper:DataRoot", dataRoot);
                    builder.ConfigureLogging(logging => logging.ClearProviders());
                });
            var client = factory.CreateClient();
            _ = await client.GetAsync("/");
            if (configure is not null)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                await configure(
                    scope.ServiceProvider.GetRequiredService<SetupWorkflow>());
            }

            return new(dataRoot, factory, client);
        }

        public async Task<string> HtmlAsync(string path)
        {
            var response = await _client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, html);
            return html;
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            _factory.Dispose();
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
