using GoalKeeper.Web.Operations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Integration.Tests;

public sealed class OperationalConfigurationTests
{
    [Fact]
    public void Hosted_web_host_fails_during_startup_without_a_key()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(
                    "GoalKeeper:DataRoot",
                    AbsoluteDataRoot());
                builder.UseSetting(
                    "GoalKeeper:Providers:Mode",
                    "Hosted");
                builder.UseSetting(
                    "GoalKeeper:Providers:OpenAI:ApiKey",
                    string.Empty);
                builder.ConfigureLogging(logging => logging.ClearProviders());
            });

        var exception = Assert.Throws<OptionsValidationException>(
            factory.CreateClient);

        Assert.Contains(
            "GoalKeeper:Providers:OpenAI:ApiKey",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_mode_validates_without_provider_credentials()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["GoalKeeper:DataRoot"] = AbsoluteDataRoot(),
            ["GoalKeeper:Providers:Mode"] = "Disabled",
            ["GoalKeeper:Providers:OpenAI:BaseUrl"] = "not-used"
        });

        var options = provider
            .GetRequiredService<IOptions<GoalKeeperOperationalOptions>>()
            .Value;

        Assert.Equal(GoalKeeperProviderMode.Disabled, options.Providers.Mode);
        Assert.Empty(options.Providers.OpenAI.ApiKey);
    }

    [Fact]
    public void Hosted_mode_accepts_only_the_documented_stack()
    {
        using var provider = Provider(HostedConfiguration());

        var options = provider
            .GetRequiredService<IOptions<GoalKeeperOperationalOptions>>()
            .Value;

        Assert.Equal(GoalKeeperProviderMode.Hosted, options.Providers.Mode);
        Assert.Equal("gpt-5.6-luna", options.Providers.Perception.Model);
        Assert.Equal("gpt-5.6-luna", options.Providers.Reasoning.Model);
        Assert.Equal(
            "gpt-5.6-terra",
            options.Providers.Recovery.ConversationModel);
    }

    [Theory]
    [InlineData(
        "GoalKeeper:Providers:OpenAI:ApiKey",
        "",
        "GoalKeeper:Providers:OpenAI:ApiKey")]
    [InlineData(
        "GoalKeeper:Providers:OpenAI:BaseUrl",
        "http://unsafe.example/v1",
        "GoalKeeper:Providers:OpenAI:BaseUrl")]
    [InlineData(
        "GoalKeeper:Providers:Perception:Model",
        "PRIVATE_MODEL_CANARY",
        "GoalKeeper:Providers:Perception:Model")]
    [InlineData(
        "GoalKeeper:Providers:Recovery:Voice",
        "PRIVATE_VOICE_CANARY",
        "GoalKeeper:Providers:Recovery:Voice")]
    public void Invalid_hosted_configuration_fails_without_echoing_values(
        string key,
        string value,
        string expectedKey)
    {
        var values = HostedConfiguration();
        values[key] = value;
        using var provider = Provider(values);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider
                .GetRequiredService<IOptions<GoalKeeperOperationalOptions>>()
                .Value);

        Assert.Contains(expectedKey, exception.Message, StringComparison.Ordinal);
        if (value.Length != 0)
        {
            Assert.DoesNotContain(
                value,
                exception.Message,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            "sk-test-private",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Relative_data_root_fails_before_runtime_services_are_used()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["GoalKeeper:DataRoot"] = "relative-data",
            ["GoalKeeper:Providers:Mode"] = "Disabled"
        });

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider
                .GetRequiredService<IOptions<GoalKeeperOperationalOptions>>()
                .Value);

        Assert.Contains(
            "GoalKeeper:DataRoot",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Relative_data_root_is_rejected_before_directory_creation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GoalKeeper:DataRoot"] = "relative-data"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GoalKeeperOperationsServiceCollectionExtensions.ResolveDataRoot(
                configuration));

        Assert.Contains(
            "GoalKeeper:DataRoot",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Operational_logger_redacts_payload_shaped_values()
    {
        const string secret = "sk-PRIVATE-KEY-CANARY";
        const string image = "data:image/jpeg;base64,PRIVATE_IMAGE_CANARY";
        const string audio = "RIFF_PRIVATE_AUDIO_CANARY";
        const string transcript = "I said PRIVATE_TRANSCRIPT_CANARY";
        var sink = new CapturingLogger<GoalKeeperOperationalLogger>();
        var log = new GoalKeeperOperationalLogger(sink);

        log.TechnicalBoundaryEvent(
            image,
            $"{audio} {transcript}",
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            secret);

        var message = Assert.Single(sink.Messages);
        Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(image, message, StringComparison.Ordinal);
        Assert.DoesNotContain(audio, message, StringComparison.Ordinal);
        Assert.DoesNotContain(transcript, message, StringComparison.Ordinal);
        Assert.Equal(3, Count(message, "redacted"));
    }

    private static ServiceProvider Provider(
        IDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGoalKeeperOperations(configuration);
        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }

    private static Dictionary<string, string?> HostedConfiguration() =>
        new()
        {
            ["GoalKeeper:DataRoot"] = AbsoluteDataRoot(),
            ["GoalKeeper:Providers:Mode"] = "Hosted",
            ["GoalKeeper:Providers:OpenAI:ApiKey"] = "sk-test-private",
            ["GoalKeeper:Providers:OpenAI:BaseUrl"] =
                "https://api.openai.com/v1",
            ["GoalKeeper:Providers:Perception:Model"] = "gpt-5.6-luna",
            ["GoalKeeper:Providers:Perception:ImageDetail"] = "low",
            ["GoalKeeper:Providers:Reasoning:Model"] = "gpt-5.6-luna",
            ["GoalKeeper:Providers:Reasoning:Effort"] = "medium",
            ["GoalKeeper:Providers:Recovery:ConversationModel"] =
                "gpt-5.6-terra",
            ["GoalKeeper:Providers:Recovery:ReasoningEffort"] = "low",
            ["GoalKeeper:Providers:Recovery:TranscriptionModel"] =
                "gpt-4o-transcribe",
            ["GoalKeeper:Providers:Recovery:SpeechModel"] = "tts-1",
            ["GoalKeeper:Providers:Recovery:Voice"] = "coral"
        };

    private static string AbsoluteDataRoot() =>
        Path.Combine(Path.GetTempPath(), "GoalKeeper", "operations-test");

    private static int Count(string value, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(
                   token,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }

        return count;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
