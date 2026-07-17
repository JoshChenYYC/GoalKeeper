using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
}
