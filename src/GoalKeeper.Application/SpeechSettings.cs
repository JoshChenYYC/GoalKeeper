using GoalKeeper.Domain;

namespace GoalKeeper.Application;

public sealed record SpeechSettingsView(string SpeechModel, string Voice)
{
    public static SpeechSettingsView Default { get; } = new("tts-1", "coral");
}

public sealed record SpeechModelOption(
    string Id,
    string DisplayName,
    string Description);

public sealed record SpeechVoiceOption(string Id, string DisplayName);

public static class SpeechSettingsCatalog
{
    private static readonly SpeechModelOption[] SpeechModelOptions =
    [
        new(
            "gpt-4o-mini-tts",
            "GPT-4o mini TTS",
            "Newest and most reliable"),
        new("tts-1", "TTS-1", "Lower latency"),
        new("tts-1-hd", "TTS-1 HD", "Higher quality")
    ];

    private static readonly SpeechVoiceOption[] AllVoiceOptions =
    [
        Voice("alloy"),
        Voice("ash"),
        Voice("ballad"),
        Voice("coral"),
        Voice("echo"),
        Voice("fable"),
        Voice("nova"),
        Voice("onyx"),
        Voice("sage"),
        Voice("shimmer"),
        Voice("verse"),
        Voice("marin"),
        Voice("cedar")
    ];

    private static readonly HashSet<string> LegacyVoiceIds = new(
        [
            "alloy",
            "ash",
            "coral",
            "echo",
            "fable",
            "onyx",
            "nova",
            "sage",
            "shimmer"
        ],
        StringComparer.Ordinal);

    public static IReadOnlyList<SpeechModelOption> Models => SpeechModelOptions;

    public static IReadOnlyList<SpeechVoiceOption> VoicesFor(string speechModel) =>
        string.Equals(speechModel, "gpt-4o-mini-tts", StringComparison.Ordinal)
            ? AllVoiceOptions
            : IsLegacyModel(speechModel)
                ? AllVoiceOptions.Where(x => LegacyVoiceIds.Contains(x.Id)).ToArray()
                : [];

    public static bool IsSupported(string speechModel, string voice) =>
        SpeechModelOptions.Any(x =>
            string.Equals(x.Id, speechModel, StringComparison.Ordinal)) &&
        VoicesFor(speechModel).Any(x =>
            string.Equals(x.Id, voice, StringComparison.Ordinal));

    private static bool IsLegacyModel(string speechModel) =>
        speechModel is "tts-1" or "tts-1-hd";

    private static SpeechVoiceOption Voice(string id) =>
        new(id, $"{char.ToUpperInvariant(id[0])}{id[1..]}");
}

public interface ISpeechSettingsProvider
{
    Task<SpeechSettingsView> GetAsync(
        CancellationToken cancellationToken = default);
}

public interface ISpeechSettingsStore : ISpeechSettingsProvider
{
    Task SaveAsync(
        SpeechSettingsView settings,
        CancellationToken cancellationToken = default);
}

public sealed class SpeechSettingsWorkflow(ISpeechSettingsStore store)
{
    public Task<SpeechSettingsView> GetAsync(
        CancellationToken cancellationToken = default) =>
        store.GetAsync(cancellationToken);

    public async Task<SpeechSettingsView> SaveAsync(
        string speechModel,
        string voice,
        CancellationToken cancellationToken = default)
    {
        speechModel = Required(speechModel, "Speech model");
        voice = Required(voice, "Voice");
        if (!SpeechSettingsCatalog.IsSupported(speechModel, voice))
        {
            throw new DomainRuleViolationException(
                "Choose a voice supported by the selected speech model.");
        }

        var settings = new SpeechSettingsView(speechModel, voice);
        await store.SaveAsync(settings, cancellationToken);
        return settings;
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainRuleViolationException($"{name} is required.")
            : value.Trim();
}
