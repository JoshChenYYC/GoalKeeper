using GoalKeeper.Application;
using GoalKeeper.Application.Monitoring;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Application.Runtime;
using GoalKeeper.Infrastructure;
using GoalKeeper.Infrastructure.Perception;
using GoalKeeper.Infrastructure.Reasoning;
using GoalKeeper.Infrastructure.Recovery.Audio;
using GoalKeeper.Infrastructure.Recovery.Conversation;
using GoalKeeper.Web.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GoalKeeper.Integration.Tests;

public sealed class RuntimeProviderCompositionTests
{
    [Fact]
    public void Disabled_mode_keeps_hosted_adapters_out_of_the_running_host()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            using var factory = CreateFactory(dataRoot, "Disabled");
            var services = factory.Services;

            Assert.IsType<UnconfiguredPerceptionPort>(
                services.GetRequiredService<IPerceptionPort>());
            Assert.IsType<UnconfiguredReasoningPort>(
                services.GetRequiredService<IReasoningPort>());
            Assert.IsType<UnconfiguredRecoveryPort>(
                services.GetRequiredService<IRecoveryPort>());
            Assert.Null(services.GetService<IVoiceRecoveryPort>());
            Assert.IsType<OpenCvCameraFactory>(
                services.GetRequiredService<ICameraFactory>());
            Assert.NotNull(
                services.GetRequiredService<SessionRuntimeController>());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void Hosted_mode_connects_all_late_adapters_to_the_running_host()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            using var factory = CreateFactory(dataRoot, "Hosted");
            var services = factory.Services;

            Assert.IsType<OpenAiPerceptionAdapter>(
                services.GetRequiredService<IPerceptionPort>());
            Assert.IsType<OpenAiReasoningAdapter>(
                services.GetRequiredService<IReasoningPort>());
            Assert.IsType<OpenAiRecoveryConversationAdapter>(
                services.GetRequiredService<IRecoveryPort>());
            Assert.IsType<VoiceRecoveryAdapter>(
                services.GetRequiredService<IVoiceRecoveryPort>());
            Assert.IsType<OpenCvCameraFactory>(
                services.GetRequiredService<ICameraFactory>());
            Assert.NotNull(
                services.GetRequiredService<SessionRuntimeController>());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string dataRoot,
        string providerMode) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("GoalKeeper:DataRoot", dataRoot);
            builder.UseSetting("GoalKeeper:Providers:Mode", providerMode);
            builder.UseSetting(
                "GoalKeeper:Providers:OpenAI:ApiKey",
                "test-api-key");
            builder.UseSetting(
                "GoalKeeper:Providers:OpenAI:BaseUrl",
                "https://recorded.test/v1");
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });

    private static string CreateDataRoot()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            $"goalkeeper-runtime-providers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        return dataRoot;
    }
}
