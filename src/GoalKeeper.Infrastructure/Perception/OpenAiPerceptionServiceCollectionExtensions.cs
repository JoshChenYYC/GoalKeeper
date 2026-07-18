using GoalKeeper.Application.Perception;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GoalKeeper.Infrastructure.Perception;

public static class OpenAiPerceptionServiceCollectionExtensions
{
    public static IServiceCollection AddOpenAiPerception(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddOpenAiPerception(options =>
        {
            options.ApiKey = configuration[OpenAiPerceptionOptions.ApiKeyConfigurationKey]
                ?? string.Empty;

            var baseUrl = configuration[OpenAiPerceptionOptions.BaseUrlConfigurationKey];
            if (baseUrl is not null)
            {
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl))
                {
                    throw new InvalidOperationException(
                        $"Configuration key '{OpenAiPerceptionOptions.BaseUrlConfigurationKey}' is invalid.");
                }

                options.BaseUrl = parsedBaseUrl;
            }

            options.Model = configuration[OpenAiPerceptionOptions.ModelConfigurationKey]
                ?? options.Model;
            options.ImageDetail =
                configuration[OpenAiPerceptionOptions.ImageDetailConfigurationKey]
                ?? options.ImageDetail;
        });
    }

    public static IServiceCollection AddOpenAiPerception(
        this IServiceCollection services,
        Action<OpenAiPerceptionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<OpenAiPerceptionOptions>().Configure(configure);
        services.AddHttpClient<OpenAiPerceptionAdapter>(static client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddTransient<IPerceptionPort>(static provider =>
            provider.GetRequiredService<OpenAiPerceptionAdapter>());

        return services;
    }
}
