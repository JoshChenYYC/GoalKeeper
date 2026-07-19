using GoalKeeper.Application.Recovery;
using GoalKeeper.Infrastructure.Recovery.Audio;
using GoalKeeper.Infrastructure.Recovery.Conversation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GoalKeeper.Infrastructure.Recovery;

public static class OpenAiVoiceRecoveryServiceCollectionExtensions
{
    private const string TranscriptionModelConfigurationKey =
        "GoalKeeper:Providers:Recovery:TranscriptionModel";
    private const string SpeechModelConfigurationKey =
        "GoalKeeper:Providers:Recovery:SpeechModel";
    private const string VoiceConfigurationKey =
        "GoalKeeper:Providers:Recovery:Voice";

    public static IServiceCollection AddOpenAiVoiceRecovery(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddOpenAiVoiceRecovery(
            conversation =>
            {
                conversation.ApiKey = configuration[
                    OpenAiRecoveryConversationOptions.ApiKeyConfigurationKey]
                    ?? string.Empty;
                conversation.BaseUrl = ReadBaseUrl(
                    configuration,
                    OpenAiRecoveryConversationOptions.BaseUrlConfigurationKey,
                    conversation.BaseUrl);
                conversation.Model = configuration[
                    OpenAiRecoveryConversationOptions.ModelConfigurationKey]
                    ?? conversation.Model;
                conversation.Effort = configuration[
                    OpenAiRecoveryConversationOptions.EffortConfigurationKey]
                    ?? conversation.Effort;
            },
            audio =>
            {
                audio.ApiKey = configuration[
                    OpenAiRecoveryAudioOptions.ApiKeyConfigurationKey]
                    ?? string.Empty;
                audio.BaseUrl = ReadBaseUrl(
                    configuration,
                    OpenAiRecoveryConversationOptions.BaseUrlConfigurationKey,
                    audio.BaseUrl);
                audio.TranscriptionModel =
                    configuration[TranscriptionModelConfigurationKey]
                    ?? audio.TranscriptionModel;
                audio.SpeechModel =
                    configuration[SpeechModelConfigurationKey]
                    ?? audio.SpeechModel;
                audio.Voice =
                    configuration[VoiceConfigurationKey]
                    ?? audio.Voice;
            });
    }

    public static IServiceCollection AddOpenAiVoiceRecovery(
        this IServiceCollection services,
        Action<OpenAiRecoveryConversationOptions> configureConversation,
        Action<OpenAiRecoveryAudioOptions> configureAudio)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureConversation);
        ArgumentNullException.ThrowIfNull(configureAudio);

        services
            .AddOptions<OpenAiRecoveryConversationOptions>()
            .Configure(configureConversation);
        services
            .AddOptions<OpenAiRecoveryAudioOptions>()
            .Configure(configureAudio);

        services.AddHttpClient(
            OpenAiRecoveryConversationAdapter.HttpClientName,
            static client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient(
            OpenAiSpeechInputAdapter.HttpClientName,
            static client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient(
            OpenAiSpeechOutputAdapter.HttpClientName,
            static client => client.Timeout = Timeout.InfiniteTimeSpan);

        services.AddSingleton<OpenAiRecoveryConversationAdapter>();
        services.AddSingleton<IRecoveryPort>(static provider =>
            provider.GetRequiredService<OpenAiRecoveryConversationAdapter>());

        services.AddSingleton<IRecoveryAudioCaptureDeviceFactory,
            NAudioCaptureDeviceFactory>();
        services.AddSingleton<IMicrophonePort, NAudioMicrophonePort>();
        services.AddSingleton<IRecoveryAudioPlaybackSink,
            NAudioRecoveryAudioPlaybackSink>();
        services.AddSingleton<OpenAiSpeechInputAdapter>();
        services.AddSingleton<ISpeechInputPort>(static provider =>
            provider.GetRequiredService<OpenAiSpeechInputAdapter>());
        services.AddSingleton<OpenAiSpeechOutputAdapter>();
        services.AddSingleton<ISpeechOutputPort>(static provider =>
            provider.GetRequiredService<OpenAiSpeechOutputAdapter>());
        services.AddSingleton(RecoveryAudioCaptureOptions.Default);
        services.AddSingleton<VoiceRecoveryAdapter>();
        services.AddSingleton<IVoiceRecoveryPort>(static provider =>
            provider.GetRequiredService<VoiceRecoveryAdapter>());

        return services;
    }

    private static Uri ReadBaseUrl(
        IConfiguration configuration,
        string key,
        Uri fallback)
    {
        var value = configuration[key];
        if (value is null)
        {
            return fallback;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration key '{key}' is invalid.");
        }

        return parsed;
    }
}
