using System.Collections.Generic;
using System.Text.RegularExpressions;
using LocalWhisperer.Models;

namespace LocalWhisperer.Services;

public static class CorrectorService
{
    /// <summary>
    /// Applies word-boundary replacements, case-insensitive — mirrors server corrector.py.
    /// </summary>
    public static string Apply(string text, IList<CorrectionEntry> corrections)
    {
        foreach (var c in corrections)
        {
            if (string.IsNullOrEmpty(c.Wrong)) continue;
            var pattern     = $@"(?<![a-zA-ZæøåÆØÅ]){Regex.Escape(c.Wrong)}(?![a-zA-ZæøåÆØÅ])";
            var replacement = c.Correct.Replace(@"\n", "\n");
            text = Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase);
        }
        return text;
    }
}
