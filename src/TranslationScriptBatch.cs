using System.Text.RegularExpressions;
using GameTranslateToolkit.ScriptTranslation;

namespace GameTranslateToolkit;

internal sealed class TranslationApiException(string message, bool retryable, TimeSpan retryAfter) : InvalidOperationException(message)
{
    public bool Retryable { get; } = retryable;
    public TimeSpan RetryAfter { get; } = retryAfter;
}

internal sealed record ScriptTranslatedLine(string Source, string Text, string ProviderId, string Model);

public sealed partial class TranslationService
{
    internal string ScriptConfigurationStamp(string gameId)
    {
        var data = _store.Snapshot();
        var game = data.Games.FirstOrDefault(g => g.Id == gameId) ?? throw new InvalidOperationException("找不到当前游戏。");
        return ScriptBatchPolicy.Stamp(data, game);
    }

    private (AppData Data, GameRecord Game) ScriptSnapshot(string gameId, string configuration)
    {
        var data = _store.Snapshot();
        var game = data.Games.FirstOrDefault(g => g.Id == gameId) ?? throw new InvalidOperationException("找不到当前游戏。");
        if (!data.Settings.Enabled || !game.Enabled || ScriptBatchPolicy.Stamp(data, game) != configuration)
            throw new InvalidOperationException("翻译配置已改变或已暂停，请重新确认剩余范围。");
        return (data, game);
    }

    // Exact per-provider cache keys include model, credentials, prompt, language and glossary.
    // Search every selected provider before scheduling a request, including after an interrupted save.
    internal IReadOnlyList<ScriptTranslatedLine> CachedScriptLines(string gameId, string configuration,
        IReadOnlyList<ScriptProviderSpec> providers, IReadOnlyList<string> sources)
    {
        var (data, _) = ScriptSnapshot(gameId, configuration);
        var result = new List<ScriptTranslatedLine>();
        foreach (var source in sources)
            foreach (var provider in providers)
            {
                var context = PrepareRequest(new TranslateInput { GameId = gameId, Text = source, ProviderId = provider.ProviderId },
                    true, false, false, out var cached, data, count: false);
                if (context != null) continue;
                result.Add(new(source, cached!.Text, provider.ProviderId, provider.Model)); break;
            }
        return result;
    }

    // One independently limited request. Failover is handled by the script scheduler, with no hidden retries.
    internal async Task<IReadOnlyList<ScriptTranslatedLine>> TranslateScriptBatchAsync(string gameId, string configuration,
        ScriptProviderSpec provider, IReadOnlyList<string> sources, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (sources.Count is < 1 or > 8 || sources.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 4096) ||
            sources.Distinct().Count() != sources.Count || sources.Sum(s => s.Length) > PrefetchCharacters)
            throw new ArgumentException("剧本请求需要 1–8 条完整对白、共不超过 8192 字符。");
        var (data, _) = ScriptSnapshot(gameId, configuration);
        var prepared = sources.Select(source => {
            var context = PrepareRequest(new TranslateInput { GameId = gameId, Text = source, ProviderId = provider.ProviderId },
                true, false, false, out var cached, data);
            return (Source: source, Context: context, Cached: cached);
        }).ToArray();
        var work = prepared.Where(p => p.Context != null).ToArray();
        var outputs = prepared.Where(p => p.Context == null).ToDictionary(p => p.Source, p => p.Cached!.Text, StringComparer.Ordinal);
        if (work.Length > 0)
        {
            var tag = "rika_" + Guid.NewGuid().ToString("N");
            var combined = string.Join("\n", work.Select((w, i) => $"<{tag}_{i}>{w.Source}</{tag}_{i}>"));
            var first = work[0].Context!;
            var context = first with { Input = new TranslateInput { GameId = gameId, ProviderId = provider.ProviderId, Text = combined },
                Glossary = work.SelectMany(w => w.Context!.Glossary).GroupBy(g => g.Source, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()).OrderBy(g => g.Source, StringComparer.Ordinal).Take(200).ToList() };
            var translated = await ExecuteAsync(context, ct, scriptBatch: true);
            var pattern = "\\A" + string.Join("\n", work.Select((_, i) => $"<{tag}_{i}>([\\s\\S]*?)</{tag}_{i}>")) + "\\z";
            var match = Regex.Match(translated.Text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) throw new InvalidOperationException("本批分句校验未通过，未保存为译文。");
            var values = work.Select((w, i) => ValidateManualTranslation(w.Source, match.Groups[i + 1].Value)).ToArray();
            if (values.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("本批译文不完整，未保存为译文。");
            // Commit all validated lines atomically before reporting success to the job checkpoint.
            await _store.UpdateAsync(latest => {
                var game = latest.Games.FirstOrDefault(g => g.Id == gameId);
                if (game == null || ScriptBatchPolicy.Stamp(latest, game) != configuration)
                    throw new InvalidOperationException("翻译配置已改变，未发布旧配置的译文。");
                foreach (var (item, index) in work.Select((w, i) => (w, i)))
                {
                    var c = item.Context!;
                    if (!latest.Translations.Any(t => !t.Manual && t.CacheKey == c.CacheKey))
                        latest.Translations.Add(new TranslationRecord { GameId = gameId, Source = item.Source, Translation = values[index],
                            From = c.From, To = c.To, ProviderId = provider.ProviderId, Model = provider.Model, CacheKey = c.CacheKey });
                }
            });
            foreach (var (item, index) in work.Select((w, i) => (w, i))) outputs[item.Source] = values[index];
            if (work.Length > 1) ChangeStats(s => s.Completed += work.Length - 1);
        }
        return sources.Select(s => new ScriptTranslatedLine(s, outputs[s], provider.ProviderId, provider.Model)).ToArray();
    }
}
