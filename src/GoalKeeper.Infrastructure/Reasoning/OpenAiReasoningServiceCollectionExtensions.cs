using GoalKeeper.Application.Reasoning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GoalKeeper.Infrastructure.Reasoning;

public static class OpenAiReasoningServiceCollectionExtensions
{
    public const string HttpClientName = "GoalKeeper.OpenAI.Reasoning";

    public static IServiceCollection AddOpenAiReasoning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddOpenAiReasoning(options =>
        {
            options.ApiKey = configuration[OpenAiReasoningOptions.ApiKeyConfigurationKey]
                ?? string.Empty;

            var baseUrl = configuration[OpenAiReasoningOptions.BaseUrlConfigurationKey];
            if (baseUrl is not null)
            {
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl))
                {
                    throw new InvalidOperationException(
                        $"Configuration key '{OpenAiReasoningOptions.BaseUrlConfigurationKey}' is invalid.");
                }

                options.BaseUrl = parsedBaseUrl;
            }

            options.Model = configuration[OpenAiReasoningOptions.ModelConfigurationKey]
                ?? options.Model;
            options.Effort = configuration[OpenAiReasoningOptions.EffortConfigurationKey]
                ?? options.Effort;
        });
    }

    public static IServiceCollection AddOpenAiReasoning(
        this IServiceCollection services,
        Action<OpenAiReasoningOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<OpenAiReasoningOptions>().Configure(configure);
        services.AddHttpClient(HttpClientName, static client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<OpenAiReasoningAdapter>();
        services.AddSingleton<IReasoningPort>(static provider =>
            provider.GetRequiredService<OpenAiReasoningAdapter>());

        return services;
    }
}
