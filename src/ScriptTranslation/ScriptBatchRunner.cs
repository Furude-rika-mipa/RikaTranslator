namespace GameTranslateToolkit.ScriptTranslation;

public sealed partial class ScriptTranslationService
{
    private readonly ScriptProviderLimiter _limiter = new();
    private static string ProviderLabel(IReadOnlyList<ScriptProviderSpec> providers) => string.Join("、", providers.Select(p => p.Name));
    private static IEnumerable<ScriptSegment> ResumeSegments(ScriptJob job)
    {
        var ids = job.SelectionIds.ToHashSet(StringComparer.Ordinal);
        return job.Segments.Where(s => ids.Contains(s.Id));
    }

    public ScriptTranslationSettings Settings() => _store.Snapshot().Settings.ScriptTranslation ?? new();
    public async Task<ScriptTranslationSettings> UpdateSettingsAsync(ScriptTranslationSettings settings)
    {
        lock (_gate) if (Busy) throw new InvalidOperationException("请先暂停当前剧本任务，再保存 API 分担设置。");
        await _store.UpdateAsync(data => { ScriptBatchPolicy.Validate(settings, data); data.Settings.ScriptTranslation = settings; });
        return Settings();
    }

    private void CheckConfiguration(ScriptJob job, string stamp)
    {
        if (_translation.ScriptConfigurationStamp(job.GameId) != stamp)
            throw new InvalidOperationException("翻译配置已改变，任务已暂停；已完成译文保留，请重新确认剩余范围。");
    }

    private void FinishLines(ScriptJob job, List<IGrouping<string, ScriptSegment>> batch,
        IReadOnlyList<ScriptTranslatedLine> results, string stamp)
    {
        lock (_gate)
        {
            foreach (var result in results)
                foreach (var item in batch.Single(g => g.Key == result.Source))
                {
                    if (item.Status == "translated") continue;
                    item.Translation = result.Text; item.Status = "translated"; item.Configuration = stamp; item.Error = "";
                    item.ProviderId = result.ProviderId; item.Model = result.Model;
                    var provider = job.Providers.First(p => p.ProviderId == result.ProviderId);
                    item.ProviderName = provider.Name; provider.CompletedLines++; job.Finished++;
                }
            job.Message = $"本次已完成 {job.Finished} / {job.Selected} 条；每批结果已保存。"; Save(job);
        }
    }

    private async Task RunBatchAsync(ScriptJob job, List<IGrouping<string, ScriptSegment>> batch,
        IReadOnlyList<ScriptProviderSpec> providers, string stamp, CancellationToken ct)
    {
        CheckConfiguration(job, stamp);
        var cached = _translation.CachedScriptLines(job.GameId, stamp, providers, batch.Select(g => g.Key).ToArray());
        if (cached.Count > 0) FinishLines(job, batch, cached, stamp);
        var remaining = batch.Where(g => g.First().Status != "translated").ToList();
        if (remaining.Count == 0) return;
        var tried = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var (spec, next) = await _limiter.AcquireAsync(providers, tried, ct);
            TranslationApiException? apiFailure = null;
            var progress = job.Providers.First(p => p.ProviderId == spec.ProviderId);
            var counted = false;
            try
            {
                ct.ThrowIfCancellationRequested(); CheckConfiguration(job, stamp);
                tried.Add(spec.ProviderId);
                lock (_gate)
                {
                    progress.Working++; progress.Requests++; progress.NextRequestAt = next; progress.Status = "翻译中"; counted = true;
                    foreach (var item in remaining.SelectMany(g => g)) item.Attempts++;
                    Save(job);
                }
                var results = await _translation.TranslateScriptBatchAsync(job.GameId, stamp, spec, remaining.Select(g => g.Key).ToArray(), ct);
                FinishLines(job, remaining, results, stamp); return;
            }
            catch (TranslationApiException ex)
            {
                apiFailure = ex;
                lock (_gate) { progress.Failures++; job.Message = $"{progress.Name} 暂不可用，正在尝试其他参与服务；已完成译文保留。"; }
                _logs.Write("warning", "script-translation", "本批 API 请求失败，将尝试其他参与服务。", job.GameId,
                    details: new { providerId = spec.ProviderId, retryable = ex.Retryable });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_gate) progress.Failures++;
                throw;
            }
            finally
            {
                var nextAllowed = _limiter.Release(spec.ProviderId, apiFailure);
                lock (_gate)
                {
                    if (counted) progress.Working--;
                    progress.NextRequestAt = nextAllowed;
                    progress.Status = apiFailure is { Retryable: false } ? "本次停用，请检查配置" :
                        progress.Working > 0 ? "翻译中" : apiFailure != null ? "冷却中" : "待命";
                    Save(job);
                }
            }
        }
    }

    private async Task TranslateAsync(ScriptJob job, ScriptSegment[] selected, ScriptProviderSpec[] providers, string stamp, CancellationToken ct)
    {
        var batches = ScriptBatchPolicy.Batches(selected);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var next = -1; var failures = 0; string? stoppedFor = null;
        async Task Worker()
        {
            try
            {
                while (true)
                {
                    stop.Token.ThrowIfCancellationRequested(); CheckConfiguration(job, stamp);
                    var index = Interlocked.Increment(ref next); if (index >= batches.Count) return;
                    try
                    {
                        await RunBatchAsync(job, batches[index], providers, stamp, stop.Token);
                        Interlocked.Exchange(ref failures, 0);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception)
                    {
                        lock (_gate)
                        {
                            foreach (var item in batches[index].SelectMany(g => g).Where(s => s.Status != "translated"))
                            { item.Status = "failed"; item.Error = "本批翻译失败或分句校验未通过，可重试失败项。"; job.Failed++; }
                            Save(job);
                        }
                        CheckConfiguration(job, stamp);
                        if (Interlocked.Increment(ref failures) >= 3)
                        { stoppedFor = "连续三批翻译失败，任务已暂停，请检查服务后继续上次范围。"; stop.Cancel(); return; }
                    }
                }
            }
            catch { stop.Cancel(); throw; }
        }
        try
        {
            var count = Math.Min(batches.Count, providers.Sum(p => p.Concurrency));
            var workers = Enumerable.Range(0, count).Select(_ => Worker()).ToArray();
            try { await Task.WhenAll(workers); }
            catch { stop.Cancel(); try { await Task.WhenAll(workers); } catch { } throw; }
            lock (_gate)
            {
                job.Stage = job.Failed > 0 ? "paused" : "complete";
                job.Message = job.Failed > 0 ? "有失败项，可继续上次范围；已完成项自动跳过。" : $"本次范围已完成：{job.ScopeLabel}。已完成项不会重复提交。";
                Save(job);
            }
        }
        catch (OperationCanceledException)
        { lock (_gate) { job.Stage = "paused"; job.Message = stoppedFor ?? "已暂停，已保存译文保留；可继续上次范围。未收到结果的超时请求仍可能由服务商计费。"; Save(job); } }
        catch (Exception)
        { lock (_gate) { job.Stage = "paused"; job.Message = "翻译配置变化或任务中断，已保存译文保留；请重新确认剩余范围。"; Save(job); } }
        finally
        {
            try { lock (_gate) { foreach (var item in selected.Where(s => s.Status == "queued")) item.Status = "pending"; Save(job); } }
            finally { _end(); }
        }
    }
}
