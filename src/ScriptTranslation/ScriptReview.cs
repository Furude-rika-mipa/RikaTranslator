using System.Text.RegularExpressions;

namespace GameTranslateToolkit.ScriptTranslation;

public sealed partial class ScriptTranslationService
{
    private sealed record ReviewRow(string Id, string File, string Scene, int Index, string Speaker,
        string Source, string Translation, string Status, string Error);
    private sealed record ReviewIssue(string Kind, string Message, string Suggestion, string Id,
        string File, string Scene, int Index, string Speaker, string Source, string Translation);

    // This is a read-only, local review; it never changes the job or calls a model.
    public object ReviewScript(string id)
    {
        ReviewRow[] rows; string format, engine;
        lock (_gate)
        {
            var job = Job(id); format = job.Format; engine = job.Engine;
            rows = job.Segments.Select(s => new ReviewRow(s.Id, s.File, s.Scene, s.Index,
                s.Speaker ?? "", s.Source ?? "", s.Translation ?? "", s.Status ?? "", s.Error ?? "")).ToArray();
        }
        const int limit = 100;
        var invalid = new List<ReviewIssue>(); var failed = new List<ReviewIssue>(); var lengthy = new List<ReviewIssue>();
        int invalidCount = 0, failedCount = 0, longCount = 0;
        static ReviewIssue Issue(ReviewRow row, string kind, string message, string suggestion) =>
            new(kind, message, suggestion, row.Id, row.File, row.Scene, row.Index, row.Speaker, row.Source, row.Translation);
        foreach (var row in rows)
        {
            if (row.Status == "failed")
            {
                failedCount++;
                if (failed.Count < limit) failed.Add(Issue(row, "failed", string.IsNullOrEmpty(row.Error) ? "此条翻译未完成。" : _logs.Sanitize(row.Error),
                    "检查翻译服务与提示后，可重新选择这条对白或继续上次范围。"));
                continue;
            }
            if (row.Status != "translated") continue;
            string? problem = null;
            var suggestion = "对照原文保留变量、标签及游戏控制符，再重新检查。";
            int maxLength = format == "multi-engine-v1" ? 16384 : 32768;
            if (string.IsNullOrWhiteSpace(row.Translation) || row.Translation.Length > maxLength || row.Translation.Contains('\0') ||
                format == "multi-engine-v1" && row.Translation.Any(c => c < 32 && c is not ('\n' or '\r' or '\t')))
            {
                problem = $"译文为空、超过 {maxLength} 字符或包含无效控制字符。";
                suggestion = "检查译文长度和异常控制字符，再重新检查。";
            }
            else if (format == "multi-engine-v1" && (engine is "BGI / Ethornell" or "Escu:de" or "ExHIBIT" or "YU-RIS") &&
                row.Translation.Any(char.IsSurrogate))
            {
                problem = "此引擎的补丁编码不能表示表情或其他补充平面字符。";
                suggestion = "替换相关字符后重新检查；其他原文、变量和标签应保留。";
            }
            else
            {
                try
                {
                    _ = format == "psb3" ? TranslationService.ValidatePsbPatchTranslation(row.Source, row.Translation)
                        : TranslationService.ValidateManualTranslation(row.Source, row.Translation);
                }
                catch (InvalidOperationException ex) { problem = ex.Message; }
                catch (RegexMatchTimeoutException) { problem = "控制符检查超时，请检查这条对白的格式。"; }
            }
            if (problem != null)
            {
                invalidCount++;
                if (invalid.Count < limit) invalid.Add(Issue(row, "format", problem, suggestion));
            }
            // Character count is only a screen-space hint; actual layout depends on game and font.
            var lines = row.Translation.Replace("\\n", "\n").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Any(line => line.Length > 60))
            {
                longCount++;
                if (lengthy.Count < limit) lengthy.Add(Issue(row, "long", "单行超过 60 个字符，可能需要检查游戏内显示。",
                    "这是按字符数估算的提醒，字体和游戏自动换行会影响实际效果。"));
            }
        }
        return new {
            total = rows.Length, translated = rows.Count(r => r.Status == "translated"),
            pending = rows.Count(r => r.Status == "pending"), queued = rows.Count(r => r.Status == "queued"),
            skipped = rows.Count(r => r.Status == "skipped"), failed = failedCount,
            invalid = invalidCount, longLines = longCount, issueLimit = limit,
            issues = invalid.Concat(failed).Concat(lengthy).Take(limit).ToArray(),
            message = "本地检查不修改译文、不调用 AI；长句仅供参考，最终显示请在游戏中确认。"
        };
    }

    public object LocateScriptLine(string id, string segmentId)
    {
        if (string.IsNullOrEmpty(segmentId) || segmentId.Length > 128) throw new ArgumentException("无效的对白编号。");
        lock (_gate)
        {
            var job = Job(id); var line = job.Segments.FirstOrDefault(s => s.Id == segmentId)
                ?? throw new KeyNotFoundException("找不到这条对白。");
            var rows = job.Segments.Where(s => s.File == line.File && s.Scene == line.Scene).ToArray();
            return new { file = line.File, scene = line.Scene, page = Array.FindIndex(rows, s => s.Id == line.Id) / 50 + 1, lineId = line.Id };
        }
    }

    public object FindIdenticalTranslations(string id, int? page)
    {
        const int pageSize = 50;
        lock (_gate)
        {
            var matches = Job(id).Segments.Where(s => s.Status == "translated" &&
                !string.IsNullOrEmpty(s.Source) && string.Equals(s.Source, s.Translation, StringComparison.Ordinal)).ToArray();
            int pages = Math.Max(1, (matches.Length + pageSize - 1) / pageSize);
            int currentPage = Math.Clamp(page ?? 1, 1, pages);
            var issues = matches.Skip((currentPage - 1) * pageSize).Take(pageSize)
                .Select(s => new ReviewIssue("identical", "译文与原文完全相同。",
                    "人名、拟声词等可能需要保留原文，请核对后再决定是否修订。",
                    s.Id, s.File, s.Scene, s.Index, s.Speaker ?? "", s.Source, s.Translation ?? "")).ToArray();
            return new { total = matches.Length, page = currentPage, pages, issues };
        }
    }

    public object EditScriptLine(string id, string segmentId, ScriptLineEdit request)
    {
        if (!request.Confirmed) throw new ArgumentException("请先确认保存这条译文。");
        if (string.IsNullOrEmpty(segmentId) || segmentId.Length > 128) throw new ArgumentException("无效的对白编号。");
        _begin();
        try
        {
            lock (_gate)
            {
                var job = Job(id);
                if (Busy || job.External?.Enabled == true) throw new InvalidOperationException("请先暂停翻译或外部领取，再修改译文。");
                var line = job.Segments.FirstOrDefault(s => s.Id == segmentId) ?? throw new KeyNotFoundException("找不到这条对白。");
                if (line.Status != "translated") throw new InvalidOperationException("只能在这里修改已完成的译文；失败项可重新翻译。");
                if (line.Translation != request.Expected) throw new InvalidOperationException("译文已发生变化，请重新检查后再修改。");
                int maxLength = job.Format == "multi-engine-v1" ? 16384 : 32768;
                if (string.IsNullOrWhiteSpace(request.Translation) || request.Translation.Length > maxLength || request.Translation.Contains('\0') ||
                    job.Format == "multi-engine-v1" && request.Translation.Any(c => c < 32 && c is not ('\n' or '\r' or '\t')))
                    throw new ArgumentException($"译文不能为空、不能超过 {maxLength} 字符，也不能包含无效控制字符。");
                if (job.Format == "multi-engine-v1" && (job.Engine is "BGI / Ethornell" or "Escu:de" or "ExHIBIT" or "YU-RIS") &&
                    request.Translation.Any(char.IsSurrogate)) throw new ArgumentException("此引擎的补丁编码不能表示表情或其他补充平面字符。");
                var updated = job.Format == "psb3" ? TranslationService.ValidatePsbPatchTranslation(line.Source, request.Translation)
                    : TranslationService.ValidateManualTranslation(line.Source, request.Translation);
                var previous = line.Translation; var previousError = line.Error;
                try { line.Translation = updated; line.Error = ""; Save(job); }
                catch { line.Translation = previous; line.Error = previousError; throw; }
                return new { saved = true, lineId = line.Id };
            }
        }
        finally { _end(); }
    }
}
