using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>A chord the analysis offered instead of the chosen one ("Am7" → A, m7).</summary>
public sealed record ChordAlternative(string Root, string Quality)
{
    public string Label => Root + Quality;

    /// <summary>Reads the analysis labels (comma separated, root plus the app's quality names); unknown ones are left out.</summary>
    public static IReadOnlyList<ChordAlternative> Parse(string? text) => (text ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(label => label.Length > 0 && label[0] is >= 'A' and <= 'G'
            ? (Root: label.Length > 1 && label[1] is '#' or 'b' ? label[..2] : label[..1], Label: label) : (Root: "", Label: label))
        .Where(item => item.Root.Length > 0 && KeyboardDiagramVoicing.SupportsQuality(item.Label[item.Root.Length..]))
        .Select(item => new ChordAlternative(item.Root, item.Label[item.Root.Length..])).Distinct().ToArray();
}

public partial class MainWindow
{
    internal Func<IMidiOutputTransport?> chordPreviewOutput = () => new WinMmMidiOutputTransport();
    private IMidiOutputTransport? chordPreview;
    private readonly DispatcherTimer chordPreviewStop = new() { Interval = TimeSpan.FromMilliseconds(1100) };
    internal IReadOnlyList<int> LastChordPreview { get; private set; } = [];

    private void InitializeChordAlternatives()
    {
        ChordGrid.SelectionChanged += (_, _) => RefreshChordAlternatives();
        chordPreviewStop.Tick += (_, _) => SilenceChordPreview();
        Closed += (_, _) => { SilenceChordPreview(); chordPreview?.Dispose(); chordPreview = null; StopPassage(); reviewPlayer?.Dispose(); };
    }

    /// <summary>Buttons to hear the chosen chord and each alternative in the diagram position, and to use one.</summary>
    internal void RefreshChordAlternatives()
    {
        ChordAlternativesPanel.Children.Clear();
        if (ChordGrid.SelectedItem is not ChordRow chord || !CanCustomizeDiagrams) return;
        var alternatives = ChordAlternative.Parse(chord.Alternatives);
        if (alternatives.Count == 0) return;
        Button Make(string text, string tip, RoutedEventHandler click)
        {
            var button = new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6), FontSize = 12 };
            button.Click += click; ChordAlternativesPanel.Children.Add(button); return button;
        }
        Make($"▶ {chord.Root}{chord.Quality}", "Ouvir a cifra escolhida, na posição do diagrama", (_, _) => PreviewChord(chord.Root, chord.Quality, chord.DiagramInversion));
        foreach (var alternative in alternatives)
        {
            Make($"▶ {alternative.Label}", "Ouvir esta alternativa, na mesma posição", (_, _) => PreviewChord(alternative.Root, alternative.Quality, chord.DiagramInversion));
            Make($"Usar {alternative.Label}", "Trocar a cifra por esta alternativa (gere de novo para atualizar a partitura)", (_, _) => UseChordAlternative(chord, alternative));
        }
    }

    internal void UseChordAlternative(ChordRow chord, ChordAlternative alternative)
    {
        if (selectedFile is not { } row || !row.Chords.Contains(chord)) return;
        RememberVersion(row);
        chord.Root = alternative.Root; chord.Quality = alternative.Quality;
        if (row.ScorePath is not null) row.HasPendingScoreProperties = true;
        RefreshSelection(); PersistPreferences(); RefreshChordAlternatives();
        Status = $"Cifra trocada para {alternative.Label}. Gere de novo para atualizar a partitura e os diagramas.";
    }

    /// <summary>Plays the chord briefly on the Windows synthesizer (piano), voiced like its keyboard diagram.</summary>
    internal void PreviewChord(string root, string quality, int? inversion)
    {
        if (!KeyboardDiagramVoicing.TryRootPitchClass(root, out int pitchClass) || !KeyboardDiagramVoicing.SupportsQuality(quality)) return;
        var pitches = KeyboardDiagramVoicing.Pitches(pitchClass, quality, inversion ?? selectedFile?.EffectiveDiagramInversion ?? settings.DefaultDiagramInversion);
        SilenceChordPreview();
        try
        {
            chordPreview ??= chordPreviewOutput();
            if (chordPreview is null) { LastChordPreview = pitches; return; }
            chordPreview.Send(0xC0); // channel 1: acoustic grand piano
            foreach (int pitch in pitches) chordPreview.Send(0x90 | (uint)(pitch << 8) | (90u << 16));
            LastChordPreview = pitches; chordPreviewStop.Start();
        }
        catch (InvalidOperationException error) { chordPreview?.Dispose(); chordPreview = null; Status = error.Message; }
    }

    private void SilenceChordPreview()
    {
        chordPreviewStop.Stop();
        if (chordPreview is null || LastChordPreview.Count == 0) return;
        try { foreach (int pitch in LastChordPreview) chordPreview.Send(0x80 | (uint)(pitch << 8)); }
        catch (InvalidOperationException) { chordPreview.Dispose(); chordPreview = null; }
    }
}
