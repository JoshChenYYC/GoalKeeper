using GoalKeeper.Application.Recovery;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure;
using GoalKeeper.Infrastructure.Recovery;
using GoalKeeper.Infrastructure.Recovery.Audio;
using GoalKeeper.Infrastructure.Recovery.Conversation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Integration.Tests;

public sealed class HostedVoiceRecoveryRegistrationTests
{
    [Fact]
    public void Configuration_registration_is_feature_local_and_resolvable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GoalKeeper:Providers:OpenAI:ApiKey"] = "test-key",
                ["GoalKeeper:Providers:OpenAI:BaseUrl"] =
                    "https://recorded.test/v1",
                ["GoalKeeper:Providers:Recovery:ConversationModel"] =
                    "gpt-5.6-terra",
                ["GoalKeeper:Providers:Recovery:ReasoningEffort"] = "low",
                ["GoalKeeper:Providers:Recovery:TranscriptionModel"] =
                    "gpt-4o-transcribe",
                ["GoalKeeper:Providers:Recovery:SpeechModel"] = "tts-1",
                ["GoalKeeper:Providers:Recovery:Voice"] = "coral"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();

        services.AddOpenAiVoiceRecovery(configuration);

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        Assert.IsType<OpenAiRecoveryConversationAdapter>(
            provider.GetRequiredService<IRecoveryPort>());
        Assert.IsType<VoiceRecoveryAdapter>(
            provider.GetRequiredService<IVoiceRecoveryPort>());
        Assert.IsType<NAudioMicrophonePort>(
            provider.GetRequiredService<IMicrophonePort>());
        Assert.IsType<OpenAiSpeechInputAdapter>(
            provider.GetRequiredService<ISpeechInputPort>());
        Assert.IsType<OpenAiSpeechOutputAdapter>(
            provider.GetRequiredService<ISpeechOutputPort>());

        var conversation = provider
            .GetRequiredService<IOptions<OpenAiRecoveryConversationOptions>>()
            .Value;
        var audio = provider
            .GetRequiredService<IOptions<OpenAiRecoveryAudioOptions>>()
            .Value;
        Assert.Equal("test-key", conversation.ApiKey);
        Assert.Equal(new Uri("https://recorded.test/v1"), conversation.BaseUrl);
        Assert.Equal("gpt-5.6-terra", conversation.Model);
        Assert.Equal("low", conversation.Effort);
        Assert.Equal("test-key", audio.ApiKey);
        Assert.Equal(new Uri("https://recorded.test/v1"), audio.BaseUrl);
        Assert.Equal("gpt-4o-transcribe", audio.TranscriptionModel);
        Assert.Equal("tts-1", audio.SpeechModel);
        Assert.Equal("coral", audio.Voice);
    }

    [Fact]
    public void Registration_uses_infinite_transport_timeouts()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        services.AddOpenAiVoiceRecovery(
            options => options.ApiKey = "test-key",
            options => options.ApiKey = "test-key");

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var conversation = factory.CreateClient(
            OpenAiRecoveryConversationAdapter.HttpClientName);
        using var speechInput = factory.CreateClient(
            OpenAiSpeechInputAdapter.HttpClientName);
        using var speechOutput = factory.CreateClient(
            OpenAiSpeechOutputAdapter.HttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, conversation.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, speechInput.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, speechOutput.Timeout);
    }

    [Fact]
    public void Invalid_base_url_is_rejected_without_exposing_the_api_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GoalKeeper:Providers:OpenAI:ApiKey"] = "secret-test-key",
                ["GoalKeeper:Providers:OpenAI:BaseUrl"] = "not-a-url"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddOpenAiVoiceRecovery(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider
                .GetRequiredService<
                    IOptions<OpenAiRecoveryConversationOptions>>()
                .Value);

        Assert.Contains(
            "GoalKeeper:Providers:OpenAI:BaseUrl",
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "secret-test-key",
            exception.Message,
            StringComparison.Ordinal);
    }
}
