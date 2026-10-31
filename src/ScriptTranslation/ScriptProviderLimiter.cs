namespace GameTranslateToolkit.ScriptTranslation;

// Independent concurrency for script jobs; cooldowns come only from failed requests.
internal sealed class ScriptProviderLimiter
{
    private sealed class State { public int Active; public DateTimeOffset Next; public bool Disabled; }
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _states = [];
    private TaskCompletionSource _changed = Signal();
    private int _cursor;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { var old = _changed; _changed = Signal(); old.TrySetResult(); }

    public void Begin(IReadOnlyList<ScriptProviderSpec> providers, IEnumerable<ScriptProviderProgress> history)
    {
        lock (_gate)
        {
            foreach (var spec in providers)
            {
                if (!_states.TryGetValue(spec.ProviderId, out var state)) _states[spec.ProviderId] = state = new();
                state.Disabled = false;
                var persisted = history.Where(p => p.ProviderId == spec.ProviderId).Select(p => p.NextRequestAt).DefaultIfEmpty().Max();
                if (persisted > state.Next) state.Next = persisted;
                // Corrupt local timestamps must not cause an unbounded wait.
                if (state.Next > DateTimeOffset.UtcNow.AddMinutes(15)) state.Next = DateTimeOffset.UtcNow.AddMinutes(15);
            }
            Pulse();
        }
    }

    public async Task<(ScriptProviderSpec Provider, DateTimeOffset Next)> AcquireAsync(IReadOnlyList<ScriptProviderSpec> providers,
        HashSet<string> tried, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task changed; TimeSpan delay;
            lock (_gate)
            {
                var eligible = providers.Where(p => !tried.Contains(p.ProviderId) && !_states[p.ProviderId].Disabled).ToArray();
                if (eligible.Length == 0) throw new InvalidOperationException("本批次的可用 API 已用尽，请稍后重试失败项。");
                var now = DateTimeOffset.UtcNow;
                for (var i = 0; i < providers.Count; i++)
                {
                    var index = (_cursor + i) % providers.Count; var spec = providers[index]; var state = _states[spec.ProviderId];
                    if (tried.Contains(spec.ProviderId) || state.Disabled || state.Active >= spec.Concurrency || state.Next > now) continue;
                    state.Active++; _cursor = (index + 1) % providers.Count;
                    return (spec, state.Next);
                }
                var available = eligible.Where(p => _states[p.ProviderId].Active < p.Concurrency).ToArray();
                delay = available.Length == 0 ? TimeSpan.FromSeconds(60) : available.Select(p => _states[p.ProviderId].Next - now).Min();
                if (delay < TimeSpan.FromMilliseconds(1)) delay = TimeSpan.FromMilliseconds(1);
                changed = _changed.Task;
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(changed, Task.Delay(delay, wait.Token)).WaitAsync(ct);
            wait.Cancel();
        }
    }

    public DateTimeOffset Release(string id, TranslationApiException? failure)
    {
        lock (_gate)
        {
            var state = _states[id]; state.Active--;
            if (failure != null)
            {
                if (!failure.Retryable) state.Disabled = true;
                var retry = DateTimeOffset.UtcNow + failure.RetryAfter;
                if (retry > state.Next) state.Next = retry;
            }
            Pulse(); return state.Next;
        }
    }
}
