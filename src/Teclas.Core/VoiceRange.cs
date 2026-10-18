namespace Teclas.Core;

/// <summary>A transposition for the singer: semitones (−6 to +5), the octave the singer actually uses, and how far the melody still goes outside the voice.</summary>
public sealed record VoiceSuggestion(int Semitones, int Octave, int Outside, int Low, int High);

/// <summary>
/// Chooses the key that fits a melody in the singer's comfortable range. Singers take a melody in their own octave (a man
/// sings a soprano line an octave lower), so the octave is free; among keys that leave the fewest semitones outside the
/// range, the smallest change wins.
/// </summary>
public static class VoiceRange
{
    private static readonly string[] Names = ["Dó", "Dó♯", "Ré", "Mi♭", "Mi", "Fá", "Fá♯", "Sol", "Lá♭", "Lá", "Si♭", "Si"];

    public static string NoteName(int pitch) => Names[((pitch % 12) + 12) % 12] + (pitch / 12 - 1);

    public static VoiceSuggestion Suggest(int melodyLow, int melodyHigh, int voiceLow, int voiceHigh)
    {
        if (melodyHigh < melodyLow) throw new ArgumentException("Extensão da melodia inválida.");
        if (voiceHigh <= voiceLow) throw new ArgumentException("A nota mais aguda precisa ficar acima da mais grave.");
        var options = from semitones in Enumerable.Range(-6, 12)
                      from octave in new[] { -2, -1, 0, 1, 2 }
                      let low = melodyLow + semitones + 12 * octave
                      let high = melodyHigh + semitones + 12 * octave
                      let outside = Math.Max(0, voiceLow - low) + Math.Max(0, high - voiceHigh)
                      // Then prefer the melody centered in the voice, so the comfortable range has room on both sides.
                      let offCenter = Math.Abs(low + high - voiceLow - voiceHigh)
                      orderby outside, Math.Abs(semitones), offCenter, Math.Abs(octave)
                      select new VoiceSuggestion(semitones, octave, outside, low, high);
        return options.First();
    }
}
