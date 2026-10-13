using System.Globalization;
using System.Text.RegularExpressions;
using Teclas.Core;

namespace Teclas.App;

public sealed class MusicNoteRow(MusicNote note, MusicEvent musicalEvent, Action changed) : Observable
{
    public MusicNote Note { get; } = note;
    public MusicEvent Event { get; } = musicalEvent;
    private string value = note.Name;
    public string Value { get => value; set { if (Set(ref this.value, value)) changed(); } }
    public string Original => Note.Name;
    public string Position => MusicRowLabels.Position(Event.Voice, Event.Quarter);
    public bool CanEdit => Note.CanEdit;
    public string Restriction => Note.Restriction;
}

public sealed class MusicRhythmRow(MusicEvent musicalEvent, Action changed) : Observable
{
    public MusicEvent Event { get; } = musicalEvent;
    private string duration = musicalEvent.DurationType;
    private int dots = musicalEvent.Dots;
    public string Duration { get => duration; set { if (Set(ref duration, value)) changed(); } }
    public int Dots { get => dots; set { if (Set(ref dots, value)) changed(); } }
    public string Notes => Event.Notes.Count == 0 ? "Pausa" : string.Join(" · ", Event.Notes.Select(note => note.Name));
    public string Position => MusicRowLabels.Position(Event.Voice, Event.Quarter);
    public bool CanEdit => Event.CanEditDuration;
    public string Restriction => Event.Restriction;
}

public sealed class MusicHarmonyRow(MusicHarmony harmony, Action changed) : Observable
{
    public MusicHarmony Harmony { get; } = harmony;
    private string root = harmony.Root;
    private string quality = harmony.Quality;
    private int inversion = harmony.DiagramInversion ?? 2;
    public string Root { get => root; set { if (Set(ref root, value)) changed(); } }
    public string Quality { get => quality; set { if (Set(ref quality, value)) changed(); } }
    public int Inversion { get => inversion; set { if (Set(ref inversion, value)) changed(); } }
    public string Original => Harmony.Root + Harmony.Quality;
    public string Position => MusicRowLabels.Position(Harmony.Voice, Harmony.Quarter);
    public bool CanEdit => Harmony.CanEdit;
    public bool CanEditDiagram => CanEdit && Harmony.HasDiagram;
    public string Restriction => Harmony.Restriction;
}

internal static class MusicRowLabels
{
    internal static string Position(int voice, double? quarter) => $"Voz {voice} · " + (quarter is { } value ? $"posição {(value + 1).ToString("0.###", CultureInfo.GetCultureInfo("pt-BR"))}" : "posição especial");
    internal static bool ParsePitch(string text, out int pitch, out int tpc)
    {
        pitch = tpc = 0;
        var match = Regex.Match((text ?? "").Trim().Replace('♯', '#').Replace('♭', 'b'), @"^([A-Ga-g])([#b]{0,2})(-1|[0-9])$", RegexOptions.CultureInvariant);
        if (!match.Success || match.Groups[2].Value is "#b" or "b#") return false;
        char name = char.ToUpperInvariant(match.Groups[1].Value[0]);
        int accidental = match.Groups[2].Value.Sum(character => character == '#' ? 1 : -1);
        int natural = name switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, _ => 11 };
        tpc = (name switch { 'C' => 14, 'D' => 16, 'E' => 18, 'F' => 13, 'G' => 15, 'A' => 17, _ => 19 }) + accidental * 7;
        pitch = (int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) + 1) * 12 + natural + accidental;
        return NativeMusicEditor.IsValidPitch(pitch, tpc);
    }
}
