using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LocalWhisper.Models;

namespace LocalWhisper.Services;

public static class CorrectorService
{
    private record CompiledEntry(Regex Pattern, string Replacement);

    private static IList<CorrectionEntry>? _lastSource;
    private static CompiledEntry[] _compiled = [];

    /// <summary>
    /// Applies word-boundary replacements, case-insensitive — mirrors server corrector.py.
    /// Regex patterns are compiled once and reused until the corrections list changes.
    /// </summary>
    public static string Apply(string text, IList<CorrectionEntry> corrections)
    {
        EnsureCompiled(corrections);
        foreach (var e in _compiled)
            text = e.Pattern.Replace(text, e.Replacement);
        if (text.Contains('\n'))
            text = string.Join('\n', text.Split('\n').Select(l => l.TrimStart()));
        return text;
    }

    /// <summary>
    /// Removes each stop phrase from <paramref name="text"/> (case-insensitive substring match)
    /// and trims the result. Returns null if the text is empty or whitespace after removal.
    /// </summary>
    public static string? ApplyStopPhrases(string text, IList<string> stopPhrases)
    {
        foreach (var phrase in stopPhrases)
        {
            if (string.IsNullOrEmpty(phrase)) continue;
            int idx = text.IndexOf(phrase, StringComparison.CurrentCultureIgnoreCase);
            while (idx >= 0)
            {
                text = text.Remove(idx, phrase.Length);
                idx  = text.IndexOf(phrase, Math.Max(0, idx), StringComparison.CurrentCultureIgnoreCase);
            }
        }
        var trimmed = text.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static void EnsureCompiled(IList<CorrectionEntry> corrections)
    {
        if (ReferenceEquals(corrections, _lastSource) && _compiled.Length == corrections.Count)
            return;

        _lastSource = corrections;
        _compiled = corrections
            .Where(c => !string.IsNullOrEmpty(c.Wrong))
            .Select(c => new CompiledEntry(
                new Regex(
                    $@"(?<![a-zA-ZæøåÆØÅ]){Regex.Escape(c.Wrong)}(?![a-zA-ZæøåÆØÅ])",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                c.Correct.Replace(@"\n", "\n").Replace(@"\t", "\t")))
            .ToArray();
    }
}
