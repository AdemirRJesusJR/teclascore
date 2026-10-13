using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Teclas.App;

internal static class ScorePageFiles
{
    // MuseScore exports pagina-1.png, pagina-2.png, ... . Other scores in the
    // same directory must never enter this score's page list.
    internal static IReadOnlyList<string> Discover(string? firstPagePath)
    {
        if (string.IsNullOrWhiteSpace(firstPagePath)) return [];
        var fullPath = Path.GetFullPath(firstPagePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null || !Directory.Exists(directory)) return [];
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var match = Regex.Match(stem, @"^(.*[-_])(\d+)$", RegexOptions.CultureInvariant);
        if (!match.Success)
            return File.Exists(fullPath) ? [fullPath] : [];

        var prefix = match.Groups[1].Value;
        return Directory.EnumerateFiles(directory)
            .Where(path => string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path, Stem: Path.GetFileNameWithoutExtension(path)))
            .Where(item => item.Stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Path, Number: ParseNumber(item.Stem[prefix.Length..])))
            .Where(item => item.Number > 0)
            .OrderBy(item => item.Number)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path).ToArray();
    }

    private static long ParseNumber(string text) =>
        text.Length > 0 && text.All(char.IsAsciiDigit) &&
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : -1;
}
