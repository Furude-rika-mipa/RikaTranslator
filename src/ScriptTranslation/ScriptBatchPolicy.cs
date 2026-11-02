using System.Security.Cryptography;
using System.Text.Json;

namespace GameTranslateToolkit.ScriptTranslation;

internal static class ScriptBatchPolicy
{
    public static void Validate(ScriptTranslationSettings settings, AppData data)
    {
        if (settings.BatchLines is < 1 or > 50) throw new ArgumentException("API 每批对白数应为 1–50 条。");
        if (settings.Providers == null || settings.Providers.Count > 8 ||
            settings.Providers.Any(p => p == null || string.IsNullOrEmpty(p.ProviderId) || p.Concurrency is < 1 or > 8) ||
            settings.Providers.Select(p => p.ProviderId).Distinct().Count() != settings.Providers.Count ||
            settings.Providers.Sum(p => p.Concurrency) > 16)
            throw new ArgumentException("最多选择 8 个服务；每个并发 1–8，总并发最多 16。");
        if (settings.MultiProvider && settings.Providers.Count == 0) throw new ArgumentException("请至少选择一个参与分担的 API。");
        if (settings.Providers.Any(p => !data.Providers.Any(provider => provider.Id == p.ProviderId)))
            throw new ArgumentException("分担列表中有已删除的服务，请重新选择并保存。");
    }

    public static int BatchLines(AppData data) => data.Settings.ScriptTranslation?.BatchLines is >= 1 and <= 50
        ? data.Settings.ScriptTranslation.BatchLines : 8;

    public static ScriptProviderSpec[] Resolve(AppData data, GameRecord game)
    {
        var settings = data.Settings.ScriptTranslation ?? new();
        if (settings.MultiProvider) Validate(settings, data);
        var limits = settings.MultiProvider ? settings.Providers :
            [new ScriptProviderLimit(string.IsNullOrWhiteSpace(game.ProviderId) ? data.Settings.ProviderId ?? "" : game.ProviderId)];
        return limits.Select(limit => {
            var provider = data.Providers.FirstOrDefault(p => p.Id == limit.ProviderId)
                ?? throw new InvalidOperationException("请先为此游戏配置翻译服务。");
            if (string.IsNullOrWhiteSpace(provider.Model)) throw new InvalidOperationException("请先填写参与服务的模型名称。");
            return new ScriptProviderSpec(provider.Id, provider.Name, provider.Model, limit.Concurrency);
        }).ToArray();
    }

    public static string Stamp(AppData data, GameRecord game)
    {
        var specs = Resolve(data, game);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            common = TranslationService.ConfigurationStamp(data, game),
            multi = data.Settings.ScriptTranslation?.MultiProvider == true,
            pool = specs.Select(p => new { p.ProviderId, p.Concurrency,
                revision = data.Providers.First(provider => provider.Id == p.ProviderId).Revision })
        })));
    }

    // Preserve the original reading order and whole dialogue paragraphs; never mix scenes in one request.
    public static List<List<IGrouping<string, ScriptSegment>>> Batches(IEnumerable<ScriptSegment> selected, int maxLines = 8)
    {
        if (maxLines is < 1 or > ScriptTranslationService.McpBatchLimit) throw new ArgumentOutOfRangeException(nameof(maxLines));
        var batches = new List<List<IGrouping<string, ScriptSegment>>>();
        foreach (var group in selected.GroupBy(s => s.Source, StringComparer.Ordinal))
        {
            var previous = batches.LastOrDefault();
            var first = group.First();
            if (previous == null || previous.Count >= maxLines || previous.Sum(g => g.Key.Length) + group.Key.Length > TranslationService.PrefetchCharacters ||
                previous[^1].First().File != first.File || previous[^1].First().Scene != first.Scene ||
                group.Key.Length >= 1200 || previous[^1].Key.Length >= 1200)
                batches.Add([]);
            batches[^1].Add(group);
        }
        return batches;
    }
}
