using System.IO;
using System.Text.RegularExpressions;

namespace Teclas.App;

public static class ScoreMetadataDefaults
{
    public static string Title(string path) => Regex.Replace(Path.GetFileNameWithoutExtension(path).Replace('_', ' '), @"\s+", " ").Trim();
    public static string InversionLabel(int inversion) => inversion switch { 0 => "Posição fundamental", 1 => "1ª inversão", _ => "2ª inversão" };
}
