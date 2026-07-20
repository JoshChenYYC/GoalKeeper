using GoalKeeper.Application;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Tests;

public sealed class SpeechSettingsWorkflowTests
{
    [Fact]
    public async Task Supported_model_and_voice_are_saved()
    {
        var store = new RecordingStore();
        var workflow = new SpeechSettingsWorkflow(store);

        var saved = await workflow.SaveAsync(
            "gpt-4o-mini-tts",
            "cedar");

        Assert.Equal("gpt-4o-mini-tts", saved.SpeechModel);
        Assert.Equal("cedar", saved.Voice);
        Assert.Equal(saved, store.Settings);
    }

    [Theory]
    [InlineData("tts-1", "marin")]
    [InlineData("tts-1-hd", "verse")]
    [InlineData("unknown-model", "coral")]
    [InlineData("gpt-4o-mini-tts", "unknown-voice")]
    public async Task Unsupported_model_and_voice_combinations_are_rejected(
        string model,
        string voice)
    {
        var store = new RecordingStore();
        var workflow = new SpeechSettingsWorkflow(store);

        var exception = await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => workflow.SaveAsync(model, voice));

        Assert.Contains("supported", exception.Message);
        Assert.Null(store.Settings);
    }

    [Fact]
    public void Legacy_models_offer_only_their_supported_voices()
    {
        var legacyVoices = SpeechSettingsCatalog.VoicesFor("tts-1");
        var newestVoices = SpeechSettingsCatalog.VoicesFor(
            "gpt-4o-mini-tts");

        Assert.Equal(9, legacyVoices.Count);
        Assert.DoesNotContain(legacyVoices, x => x.Id == "marin");
        Assert.Equal(13, newestVoices.Count);
        Assert.Contains(newestVoices, x => x.Id == "marin");
        Assert.Contains(newestVoices, x => x.Id == "cedar");
    }

    private sealed class RecordingStore : ISpeechSettingsStore
    {
        public SpeechSettingsView? Settings { get; private set; }

        public Task<SpeechSettingsView> GetAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings ?? SpeechSettingsView.Default);

        public Task SaveAsync(
            SpeechSettingsView settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Settings = settings;
            return Task.CompletedTask;
        }
    }
}
