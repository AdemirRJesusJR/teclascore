using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Chords to learn across the library, a one-chord drill, and songs that share the chords of the selected one.</summary>
public partial class MainWindow
{
    private const string DrillPrefix = "acorde:";
    internal Func<Window, bool?> chordStudyDialog = window => window.ShowDialog();

    /// <summary>The song's chords as printed (transposition and simplified chords applied).</summary>
    private SongChords SongChordsFor(MidiRow row) => new(row.FullPath, string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle,
        row.Chords.Select(c => DiagramPositionPropagation.Shape(c.Root, row.SimplifiedChords ? ChordSimplifier.Simplify(c.Quality) : c.Quality, row.TransposeSemitones))
            .OfType<ChordShape>().ToHashSet(),
        settings.StudyHistory.Any(record => string.Equals(record.Song, row.FullPath, StringComparison.OrdinalIgnoreCase)));

    private IReadOnlySet<ChordShape> DrilledChords() => settings.StudyHistory.Where(record => record.Song.StartsWith(DrillPrefix, StringComparison.Ordinal))
        .Select(record => record.Song[DrillPrefix.Length..].Split(':', 2)).Where(parts => parts.Length == 2 && int.TryParse(parts[0], out _))
        .Select(parts => new ChordShape(int.Parse(parts[0]), parts[1])).ToHashSet();

    internal IReadOnlyList<ChordToLearn> ChordsToLearn() =>
        ChordLibraryStats.ToLearn(Files.Where(row => row.Chords.Count > 0).Select(SongChordsFor).ToArray(), DrilledChords());

    private void ChordsToLearnClick(object sender, RoutedEventArgs e)
    {
        var chords = ChordsToLearn();
        if (chords.Count == 0) { Status = "Ainda não há cifras na biblioteca: analise os acordes de algumas músicas primeiro."; return; }
        chordStudyDialog(new ChordsToLearnWindow(chords, settings.DefaultDiagramInversion, DrillChord) { Owner = this });
    }

    /// <summary>A one-chord drill in the practice window: the chord eight times, in the default diagram position.</summary>
    internal void DrillChord(ChordToLearn chord)
    {
        var pitches = KeyboardDiagramVoicing.Pitches(chord.Shape.PitchClass, chord.Shape.Quality, settings.DefaultDiagramInversion);
        var steps = Enumerable.Range(0, 8).Select(i => new PracticeStep(i + 1, 0, pitches) { Start = i * 4 }).ToArray();
        StopTrackAudition(); if (audioPlaying || countingIn) StopPlayback();
        var window = new PracticeWindow("Acorde " + chord.Name, steps, steps, settings.PracticeAnyOctave, settings.PracticeBpm, null, null, null) { Owner = this };
        practiceDialog(window);
        foreach (var attempt in window.Attempts)
        { attempt.Song = $"{DrillPrefix}{chord.Shape.PitchClass}:{chord.Shape.Quality}"; attempt.Title = "Acorde " + chord.Name; settings.StudyHistory.Add(attempt); }
        if (window.Attempts.Count > 0) { settings.StudyHistory = StudyHistory.Normalize(settings.StudyHistory); PersistPreferences(); Status = $"Acorde {chord.Name} praticado; ele passa a contar como conhecido."; }
    }

    public bool CanShowSimilarSongs => IsIdle && selectedFile is { Chords.Count: > 1 };

    private void SimilarSongsClick(object sender, RoutedEventArgs e)
    {
        if (!CanShowSimilarSongs || selectedFile is not { } row) return;
        var library = Files.Where(other => other.Chords.Count > 1).Select(SongChordsFor).ToArray();
        var similar = ChordLibraryStats.Similar(SongChordsFor(row), library);
        if (similar.Count == 0) { Status = "Nenhuma outra música da biblioteca usa quase os mesmos acordes."; return; }
        var window = new SimilarSongsWindow(SongChordsFor(row).Title, similar) { Owner = this };
        if (chordStudyDialog(window) == true && window.Chosen is { } chosen && Files.FirstOrDefault(file => file.FullPath == chosen.Id) is { } target)
        { ((ListBox)FindName("Library")).SelectedItem = target; Library.ScrollIntoView(target); }
    }
}

/// <summary>The library's chords, unknown first, ordered by how many songs each one completes.</summary>
public sealed class ChordsToLearnWindow : Window
{
    public ChordsToLearnWindow(IReadOnlyList<ChordToLearn> chords, int inversion, Action<ChordToLearn> drill)
    {
        Title = "Acordes para aprender"; Width = 760; Height = 640; MinWidth = 560; MinHeight = 420; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        int unknown = chords.Count(c => !c.Known);
        top.Children.Add(new TextBlock { Text = "Acordes para aprender", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = $"{chords.Count} acordes na biblioteca, {unknown} ainda não praticados. \"Destrava\" conta as músicas em que este é o único acorde que falta; um acorde conta como praticado quando aparece numa música que você já estudou ou foi treinado aqui.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) });
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; close.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var previews = new KeyboardDiagramPreview();
        var list = new StackPanel();
        foreach (var chord in chords)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var width in new[] { 90.0, 150, 1, 110 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = width == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
            row.Children.Add(new TextBlock { Text = chord.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (previews.GetImage(ChordLibraryStats.Name(chord.Shape with { Quality = "" }), chord.Shape.Quality, inversion, 0) is { } image)
            {
                var picture = new Border { Background = System.Windows.Media.Brushes.White, Padding = new Thickness(2), CornerRadius = new CornerRadius(3), Child = new Image { Source = image, Height = 40 }, HorizontalAlignment = HorizontalAlignment.Left };
                Grid.SetColumn(picture, 1); row.Children.Add(picture);
            }
            var facts = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 10, 0),
                Text = $"{(chord.Known ? "Praticado" : "Novo")} · em {chord.Songs} música(s)" + (chord.Unlocks > 0 ? $" · destrava {chord.Unlocks}" : "") };
            Grid.SetColumn(facts, 2); row.Children.Add(facts);
            var train = DialogUi.Button("Treinar", (_, _) => drill(chord)); train.ToolTip = "Abre a prática com este acorde oito vezes, na posição padrão dos diagramas";
            AutomationProperties.SetName(train, "Treinar " + chord.Name); Grid.SetColumn(train, 3); row.Children.Add(train);
            list.Children.Add(row);
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
}

/// <summary>Songs of the library with the same chords (also in another key); double-click or Abrir selects one.</summary>
public sealed class SimilarSongsWindow : Window
{
    private readonly ListBox list;
    public SongChords? Chosen => (list.SelectedItem as ListBoxItem)?.Tag as SongChords;

    public SimilarSongsWindow(string title, IReadOnlyList<SimilarSong> similar)
    {
        Title = "Músicas com os mesmos acordes"; Width = 560; Height = 480; MinWidth = 420; MinHeight = 320; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Parecidas com " + title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        top.Children.Add(new TextBlock { Text = "Músicas que usam quase os mesmos acordes: boas para estudar em seguida. \"Em outro tom\" indica a mesma sequência alguns semitons acima ou abaixo.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        list = new ListBox { Name = "SimilarSongsList" };
        foreach (var item in similar)
            list.Items.Add(new ListBoxItem { Tag = item.Song, Padding = new Thickness(6, 4, 6, 4),
                Content = $"{item.Song.Title} · {item.Score:P0} dos acordes" + (item.Transpose != 0 ? $" · em outro tom ({item.Transpose:+0;-0} semitons)" : "") });
        list.MouseDoubleClick += (_, _) => { if (Chosen is not null) DialogResult = true; };
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        root.Children.Add(list);
        var open = DialogUi.Button("Abrir na biblioteca", (_, _) => { if (Chosen is not null) DialogResult = true; }, true); buttons.Children.Add(open);
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; buttons.Children.Add(close);
    }
}
