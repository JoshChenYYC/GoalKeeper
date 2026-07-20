using GoalKeeper.Application;
using Microsoft.EntityFrameworkCore;

namespace GoalKeeper.Infrastructure;

public sealed class EfSpeechSettingsStore(
    IDbContextFactory<GoalKeeperDbContext> factory) : ISpeechSettingsStore
{
    private const string SpeechModelKey = "SpeechModel";
    private const string VoiceKey = "SpeechVoice";

    public async Task<SpeechSettingsView> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var values = await db.ApplicationSettings
            .AsNoTracking()
            .Where(x => x.Key == SpeechModelKey || x.Key == VoiceKey)
            .ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);
        return new(
            values.GetValueOrDefault(
                SpeechModelKey,
                SpeechSettingsView.Default.SpeechModel),
            values.GetValueOrDefault(
                VoiceKey,
                SpeechSettingsView.Default.Voice));
    }

    public async Task SaveAsync(
        SpeechSettingsView settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var values = await db.ApplicationSettings
            .Where(x => x.Key == SpeechModelKey || x.Key == VoiceKey)
            .ToDictionaryAsync(x => x.Key, cancellationToken);
        Set(db, values, SpeechModelKey, settings.SpeechModel);
        Set(db, values, VoiceKey, settings.Voice);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Set(
        GoalKeeperDbContext db,
        Dictionary<string, ApplicationSettingEntity> values,
        string key,
        string value)
    {
        if (values.TryGetValue(key, out var entity))
        {
            entity.Value = value;
            return;
        }

        db.ApplicationSettings.Add(new() { Key = key, Value = value });
    }
}
