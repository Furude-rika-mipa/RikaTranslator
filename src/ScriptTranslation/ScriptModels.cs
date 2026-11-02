namespace GameTranslateToolkit.ScriptTranslation;

public sealed class ScriptJob
{
    public string Engine { get; set; } = "KiriKiri";
    public string Format { get; set; } = "psb3";
    public string Id { get; set; } = "";
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string Archive { get; set; } = "";
    public string Stage { get; set; } = "extracting";
    public string Message { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string Model { get; set; } = "";
    public string Scope { get; set; } = "selected";
    public string ScopeLabel { get; set; } = "选中的对白";
    public List<string> SelectionIds { get; set; } = [];
    public List<ScriptProviderProgress> Providers { get; set; } = [];
    public ScriptExternalState? External { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Resources { get; set; }
    public int ScriptFiles { get; set; }
    public int Scenes { get; set; }
    public int Selected { get; set; }
    public int Finished { get; set; }
    public int Failed { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<ScriptSegment> Segments { get; set; } = [];
}

public sealed class ScriptSegment
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public string Scene { get; set; } = "";
    public int Index { get; set; }
    public string Speaker { get; set; } = "";
    public string Source { get; set; } = "";
    public string Translation { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string Error { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string Model { get; set; } = "";
    public int Attempts { get; set; }
}

public sealed class ScriptProviderProgress
{
    public string ProviderId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public int Concurrency { get; set; }
    public int Working { get; set; }
    public int Requests { get; set; }
    public int CompletedLines { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset NextRequestAt { get; set; }
    public string Status { get; set; } = "待命";
}

public sealed record ScriptProviderSpec(string ProviderId, string Name, string Model, int Concurrency);
public sealed record ScriptExtractionRequest(string GameId, string Archive);
public sealed record ScriptBatchRequest(List<string> Ids, bool Confirmed);
public sealed record ScriptLineEdit(string Translation, string Expected, bool Confirmed);
public sealed record ScriptScopeRequest(string Scope, string? File = null, string? Fingerprint = null, bool Confirmed = false);
public sealed record ScriptScopePlan(string Scope, string? File, string Label, int Lines, int UniqueLines,
    long Characters, int Completed, int Skipped, int Batches, string ProviderName, string Model, string Configuration, string Fingerprint,
    IReadOnlyList<ScriptProviderSpec> Providers);

public sealed class ScriptExternalState
{
    public bool Enabled { get; set; }
    public string Generation { get; set; } = "";
    public string Configuration { get; set; } = "";
    public List<string> Ids { get; set; } = [];
    public List<ScriptExternalLease> Leases { get; set; } = [];
    public List<ScriptExternalReceipt> Receipts { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class ScriptExternalLease
{
    public string Id { get; set; } = "";
    public string ClientId { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public List<ScriptExternalLeaseItem> Items { get; set; } = [];
}
public sealed record ScriptExternalLeaseItem(string Id, List<string> Ids);
public sealed record ScriptExternalReceipt(string LeaseId, string ClientId, string Digest, int Accepted);
public sealed record ScriptExternalRequest(string Scope = "selected", List<string>? Ids = null,
    string? File = null, string? Fingerprint = null, bool Confirmed = false);
public sealed record ScriptExternalPlan(string Scope, string? File, string Label, int Lines, int UniqueLines,
    long Characters, int Completed, int Skipped, int Batches, string From, string To, string Fingerprint, int BatchLines);
public sealed record ScriptExternalClaim(string ClientId, int? MaxLines = null);
public sealed record ScriptMcpSettings(int BatchLines = 8);
public sealed record ScriptExternalLeaseRequest(string ClientId, string LeaseId);
public sealed record ScriptExternalTranslation(string Id, string Text);
public sealed record ScriptExternalSubmit(string ClientId, string LeaseId, List<ScriptExternalTranslation> Translations);
