using System.Text;
using System.Text.RegularExpressions;

namespace Teclas.Core;

/// <summary>Names exported copies from their displayed properties, without changing the properties themselves.</summary>
public static class ScoreFileName
{
    public static string Stem(string? artist, string? title, string? fallbackPath = null)
    {
        string name = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fallbackPath ?? "") : title.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "Partitura";
        if (!string.IsNullOrWhiteSpace(artist)) name = artist.Trim() + " - " + name;
        name = Regex.Replace(name, @"\s+", " ");
        name = new string(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '-' : c).ToArray());
        name = Regex.Replace(name, @"\s+", " ").Trim(' ', '.');
        if (name.Length == 0) name = "Partitura";
        // Count Unicode scalars so truncation cannot split a surrogate pair.
        name = string.Concat(name.EnumerateRunes().Take(100).Select(rune => rune.ToString())).TrimEnd(' ', '.');
        if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            name = "Partitura - " + name;
        return name;
    }

    public static string Unique(string stem, string extension, HashSet<string> used)
    {
        if (extension is not (".pdf" or ".mscz" or ".png" or ".musicxml")) throw new ArgumentException("Formato de partitura inválido.", nameof(extension));
        string name = stem + extension;
        for (int counter = 2; !used.Add(name); counter++) name = stem + $" ({counter})" + extension;
        return name;
    }
}
