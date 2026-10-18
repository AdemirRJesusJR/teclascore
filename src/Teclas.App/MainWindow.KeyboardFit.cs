using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Whether the selected song's melody fits the user's keyboard (49, 61, 76 or 88 keys), and how to make it fit.</summary>
public partial class MainWindow
{
    public IReadOnlyList<object> KeyboardSizeOptions { get; } = KeyboardRange.Sizes.Select(keys => (object)new { Value = keys, Label = $"{keys} teclas ({VoiceRange.NoteName(KeyboardRange.Of(keys).Low)}–{VoiceRange.NoteName(KeyboardRange.Of(keys).High)})" }).ToArray();

    public int KeyboardKeys
    {
        get => KeyboardRange.Sizes.Contains(settings.KeyboardKeys) ? settings.KeyboardKeys : 61;
        set
        {
            if (settings.KeyboardKeys == value || !KeyboardRange.Sizes.Contains(value)) return;
            settings.KeyboardKeys = value; PersistPreferences(); Notify(); Notify(nameof(KeyboardFitMessage)); Notify(nameof(KeyboardFitVisibility));
            RefreshLibraryFilters();
        }
    }

    /// <summary>Melody range as it will sound: the chosen melody tracks of the MIDI, with the song's transposition.</summary>
    internal (int Low, int High)? PlayedMelodyRange(MidiRow row) =>
        MelodyRange(row) is { } range ? (range.Low + row.TransposeSemitones, range.High + row.TransposeSemitones) : null;

    /// <summary>The octave move that fits the keyboard: 0 fits, ±1/±2 octaves, null when wider than the keyboard or unknown.</summary>
    internal int? KeyboardOctave(MidiRow row) => PlayedMelodyRange(row) is { } range ? KeyboardRange.OctaveToFit(range.Low, range.High, KeyboardKeys) : 0;

    internal bool FitsKeyboard(MidiRow row) => KeyboardOctave(row) == 0;

    public string KeyboardFitMessage
    {
        get
        {
            if (selectedFile is not { } row || PlayedMelodyRange(row) is not { } range) return "";
            var (low, high) = KeyboardRange.Of(KeyboardKeys);
            string melody = $"A melodia vai de {VoiceRange.NoteName(range.Low)} a {VoiceRange.NoteName(range.High)}";
            return KeyboardRange.OctaveToFit(range.Low, range.High, KeyboardKeys) switch
            {
                0 => "",
                null => $"{melody} e passa da extensão do seu teclado de {KeyboardKeys} teclas ({VoiceRange.NoteName(low)}–{VoiceRange.NoteName(high)}) mesmo mudando de oitava.",
                int octave => $"{melody} e passa do seu teclado de {KeyboardKeys} teclas ({VoiceRange.NoteName(low)}–{VoiceRange.NoteName(high)}). " +
                    $"{(octave > 0 ? "Subindo" : "Descendo")} {(Math.Abs(octave) == 1 ? "uma oitava" : "duas oitavas")} ela cabe: use {(octave > 0 ? "↑ Oitava" : "↓ Oitava")} na Prévia depois de gerar."
            };
        }
    }

    public Visibility KeyboardFitVisibility => KeyboardFitMessage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}
