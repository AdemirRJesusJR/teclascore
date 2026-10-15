using System.Windows;

namespace Teclas.App;

public sealed partial class MidiRow
{
    // Transient transport state; SavedMidi and score history do not serialize this object.
    public LibraryMidiPlaybackState LibraryPlayback { get; } = new();
    public Visibility LibraryMidiPlaybackVisibility => !IsNativeScore && Document is not null ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class LibraryMidiPlaybackState : Observable
{
    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsPreparing { get; private set; }
    public bool CanPlay => !IsPlaying && !IsPreparing;
    public bool CanPause => IsPlaying;
    public bool CanStop => IsPlaying || IsPaused || IsPreparing;
    private string status = "Ouvir MIDI completo";
    public string Status { get => status; internal set => Set(ref status, value); }
    private string timeText = "0:00";
    public string TimeText { get => timeText; internal set => Set(ref timeText, value); }
    internal void Update(bool playing = false, bool paused = false, bool preparing = false)
    {
        IsPlaying = playing; IsPaused = paused; IsPreparing = preparing;
        foreach (string property in new[] { nameof(IsPlaying), nameof(IsPaused), nameof(IsPreparing), nameof(CanPlay), nameof(CanPause), nameof(CanStop) }) Notify(property);
    }
}
