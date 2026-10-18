using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit;

public sealed record PrefetchedLine(string Source, string Text);

public sealed partial class TranslationService
{
    public const int MaxPrefetchLines = 10;
    public const int PrefetchCharacters = 8192;
    private int _prefetchRequests;

    public static bool SupportsPrefetch(GameRecord game) => game.Engine is "RPG Maker MV" or "RPG Maker MZ" ||
        game.Engine == "NW.js" && game.Capability == "experimental-embedded";

    public string ConfigurationStamp(string gameId)
    {
        var data = _store.Snapshot();
        var game = data.Games.FirstOrDefault(g => g.Id == gameId) ?? throw new InvalidOperationException("找不到当前游戏。");
        return ConfigurationStamp(data, game);
    }

    private static string ConfigurationStamp(AppData data, GameRecord game)
    {
        var provider = data.Providers.FirstOrDefault(p => p.Id == First(game.ProviderId, data.Settings.ProviderId));
        // No credentials are returned, even indirectly; revision changes when a provider is edited.
        return Hash(JsonSerializer.Serialize(new {
            game.Id, gameEnabled = game.Enabled, globalEnabled = data.Settings.Enabled, data.Settings.PrefetchEnabled,
            prefetchLines = Math.Clamp(data.Settings.PrefetchLines, 1, MaxPrefetchLines),
            providerId = provider?.Id, provider?.Revision, provider?.Model,
            from = First(game.SourceLanguage, data.Settings.SourceLanguage), to = First(game.TargetLanguage, data.Settings.TargetLanguage),
            data.Settings.Prompt, game.Context,
            glossary = data.Glossary.Where(g => string.IsNullOrEmpty(g.GameId) || g.GameId == game.Id),
            manual = data.Translations.Where(t => t.Manual && SameGame(t.GameId, game.Id))
                .Select(t => new { t.Id, t.UpdatedAt, t.Source, t.Translation, t.From, t.To })
        }));
    }

    public async Task<IReadOnlyList<PrefetchedLine>> PrefetchAsync(string gameId, string configuration,
        IReadOnlyList<string> sources, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (sources.Count is < 1 or > MaxPrefetchLines || sources.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 4096) ||
            sources.Sum(s => s.Length) > PrefetchCharacters)
            throw new ArgumentException("预读每次最多 10 句、共 8192 字符。");
        var data = _store.Snapshot();
        var game = data.Games.FirstOrDefault(g => g.Id == gameId) ?? throw new InvalidOperationException("找不到当前游戏。");
        if (!SupportsPrefetch(game) || !data.Settings.PrefetchEnabled || !data.Settings.Enabled || !game.Enabled ||
            configuration != ConfigurationStamp(data, game)) return [];
        if (sources.Count > Math.Clamp(data.Settings.PrefetchLines, 1, MaxPrefetchLines))
            throw new ArgumentException("预读句数超过当前设置，请更新游戏适配后重试。");
        if (Interlocked.Increment(ref _prefetchRequests) > 4)
        { Interlocked.Decrement(ref _prefetchRequests); return []; }
        var waits = new List<Task<TranslationResult>>();
        var created = new List<PrefetchWork>();
        var priority = new PrefetchPriority();
        var unique = sources.Distinct(StringComparer.Ordinal).ToArray();
        try
        {
            // Resolve all lines before reserving flights so malformed configuration cannot leave unfinished work.
            var prepared = unique.Select(source => {
                var context = PrepareRequest(new TranslateInput { GameId = gameId, Text = source }, true, true, false,
                    out var cached, data);
                return (Context: context, Cached: cached);
            }).ToArray();
            lock (_flightLock)
            {
                foreach (var item in prepared)
                {
                    if (item.Context == null) { waits.Add(Task.FromResult(item.Cached!)); continue; }
                    var key = item.Context.CacheKey;
                    if (!_flights.TryGetValue(key, out var flight))
                    {
                        var completion = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                        flight = new Flight { Task = completion.Task, Speculative = true };
                        flight.Promote = () => {
                            priority.Demanded = true;
                            lock (_gateLock) { var signal = _gateChanged; _gateChanged = NewSignal(); signal.TrySetResult(); }
                        };
                        _flights.Add(key, flight);
                        created.Add(new(item.Context, flight.Cancellation.Token, completion));
                    }
                    flight.Waiters++;
                    waits.Add(AwaitFlightAsync(key, flight, ct));
                }
            }
            if (created.Count != 0) _ = RunPrefetchAsync(created, configuration, priority);
            var results = await Task.WhenAll(waits);
            if (ConfigurationStamp(gameId) != configuration) return [];
            return unique.Select((source, index) => new PrefetchedLine(source, results[index].Text)).ToArray();
        }
        finally { Interlocked.Decrement(ref _prefetchRequests); }
    }

    private async Task RunPrefetchAsync(List<PrefetchWork> work, string configuration, PrefetchPriority priority)
    {
        using var stop = new CancellationTokenSource();
        var remaining = work.Count;
        var registrations = work.Select(w => w.Cancellation.Register(() => {
            if (Interlocked.Decrement(ref remaining) == 0) stop.Cancel();
        })).ToArray();
        try
        {
            if (ConfigurationStamp(work[0].Context.GameId!) != configuration)
                throw new OperationCanceledException("翻译配置已改变。", stop.Token);
            var tag = "rika_" + Guid.NewGuid().ToString("N");
            var combined = string.Join("\n", work.Select((w, i) => $"<{tag}_{i}>{w.Context.Input.Text}</{tag}_{i}>"));
            var first = work[0].Context;
            var context = first with {
                Input = new TranslateInput { GameId = first.GameId, Text = combined }, Persist = false,
                Glossary = work.SelectMany(w => w.Context.Glossary).GroupBy(g => g.Source, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()).OrderBy(g => g.Source, StringComparer.Ordinal).Take(200).ToList()
            };
            // Existing control-code protection also protects the line-boundary tags.
            var result = await ExecuteAsync(context, stop.Token, background: true, demanded: () => priority.Demanded);
            var pattern = "\\A" + string.Join("\n", work.Select((_, i) => $"<{tag}_{i}>([\\s\\S]*?)</{tag}_{i}>")) + "\\z";
            var match = Regex.Match(result.Text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) throw new InvalidOperationException("预读译文的分句边界无效，将在显示时逐句翻译。");
            var translated = work.Select((w, i) => ValidateManualTranslation(w.Context.Input.Text, match.Groups[i + 1].Value)).ToArray();
            if (translated.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("预读译文缺少句子，将在显示时逐句翻译。");
            if (ConfigurationStamp(first.GameId!) != configuration) throw new OperationCanceledException("翻译配置已改变。", stop.Token);
            var active = work.Select((w, i) => (Work: w, Text: translated[i])).Where(w => !w.Work.Cancellation.IsCancellationRequested).ToArray();
            if (active.Length != 0)
                await _store.UpdateAsync(data => {
                    // Recheck under the data-store write lock before publishing any speculative cache entry.
                    var game = data.Games.FirstOrDefault(g => g.Id == first.GameId);
                    if (game == null || ConfigurationStamp(data, game) != configuration) throw new OperationCanceledException("翻译配置已改变。");
                    foreach (var item in active)
                    {
                        var c = item.Work.Context;
                        var existing = data.Translations.FirstOrDefault(t => !t.Manual && t.CacheKey == c.CacheKey);
                        if (existing != null) continue;
                        data.Translations.Add(new TranslationRecord { GameId = c.GameId, Source = c.Input.Text,
                            Translation = item.Text, From = c.From, To = c.To, ProviderId = c.Provider.Id, Model = c.Provider.Model,
                            CacheKey = c.CacheKey });
                    }
                });
            foreach (var (item, index) in work.Select((w, i) => (w, i)))
            {
                if (item.Cancellation.IsCancellationRequested) item.Completion.TrySetCanceled(item.Cancellation);
                else item.Completion.TrySetResult(new TranslationResult { Text = translated[index], Cached = false,
                    RequestId = result.RequestId, ElapsedMs = result.ElapsedMs, Configuration = configuration });
            }
            if (active.Length > 1) ChangeStats(s => s.Completed += active.Length - 1);
            _logs.Write("debug", "translation", "预读译文已逐句缓存。", first.GameId, result.RequestId, new { lines = active.Length });
        }
        catch (Exception ex)
        {
            foreach (var item in work)
                if (ex is OperationCanceledException) item.Completion.TrySetCanceled();
                else item.Completion.TrySetException(new InvalidOperationException("预读暂未完成，当前句可继续实时翻译。"));
        }
        finally
        {
            foreach (var registration in registrations) registration.Dispose();
        }
    }

    private sealed record PrefetchWork(RequestContext Context, CancellationToken Cancellation,
        TaskCompletionSource<TranslationResult> Completion);
    private sealed class PrefetchPriority { public volatile bool Demanded; }
}
