using System.Collections.Generic;
using System.Text.RegularExpressions;
using LocalWhisperer.Models;

namespace LocalWhisperer.Services;

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
        return text;
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
                c.Correct.Replace(@"\n", "\n")))
            .ToArray();
    }
}
