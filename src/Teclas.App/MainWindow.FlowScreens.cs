using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Teclas.Core;

namespace Teclas.App;

/// <summary>A measure to check on the Ritmo step.</summary>
public sealed record ReviewPointView(ScoreReviewPoint Point, string Title, string Message, bool IsChecked)
{
    public Visibility CheckedVisibility => IsChecked ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisibility => IsChecked ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>A chord of the Cifras step, as a tile of the grid by measure.</summary>
public sealed record ChordTileView(ChordRow Chord, string Position, string Symbol, bool IsDoubtful, bool IsSelected);

/// <summary>A line of the "Progresso da partitura" panel.</summary>
public sealed record ProgressItemView(string Number, string Title, string Detail, bool IsDone, bool IsCurrent, bool IsLocked);

/// <summary>
/// Content of the four step screens (Melodia, Ritmo, Cifras, Gerar), the progress panel, the "Seu teclado" card and the song header.
/// Everything here only reads the song and the flow; the actions reuse the editor's existing commands.
/// </summary>
public partial class MainWindow
{
    private const double WideLayoutWidth = 1560, TopBarLabelsWidth = 1560, CommandTextWidth = 1280, StackedLabelsWidth = 1180;
    private bool wideLayout = true, topBarWide = true, commandText = true, stackedLabels = true, showAllTracks, keyEditorOpen;
    private MidiRow? visibleTracksRow;
    private ListCollectionView? visibleTracks;

    // ---- Responsive layout -------------------------------------------------------------------------------------------

    /// <summary>Wide windows show the progress panel on the right; narrower ones keep the checks above the footer.</summary>
    public Visibility ProgressPanelVisibility => wideLayout && !IsScoreFocusApplied && !IsLibraryTableView ? Visibility.Visible : Visibility.Collapsed;
    public GridLength ProgressPanelColumnWidth => wideLayout && !IsScoreFocusApplied ? new GridLength(276) : new GridLength(0);
    public GridLength ProgressPanelGapWidth => wideLayout && !IsScoreFocusApplied ? new GridLength(16) : new GridLength(0);
    public Visibility FooterChecksVisibility => GuidedFlow && !wideLayout ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Navigation labels in the top bar: hidden by the "só ícones" preference or when the window is narrow.</summary>
    public Visibility NavLabelVisibility => topBarWide && !IsSidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Medium windows: the menu name goes under the icon, so the icons never stand alone.</summary>
    public Visibility NavStackedLabelVisibility => !topBarWide && stackedLabels && !IsSidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CommandBoxTextVisibility => commandText ? Visibility.Visible : Visibility.Collapsed;

    private void InitializeResponsiveLayout()
    {
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
        ApplyResponsiveLayout(Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (double.IsNaN(width) || width <= 0) return;
        bool wide = width >= WideLayoutWidth, labels = width >= TopBarLabelsWidth, command = width >= CommandTextWidth, stacked = width >= StackedLabelsWidth;
        if (wide != wideLayout)
        {
            wideLayout = wide;
            foreach (var name in new[] { nameof(ProgressPanelVisibility), nameof(ProgressPanelColumnWidth), nameof(ProgressPanelGapWidth), nameof(FooterChecksVisibility) }) Notify(name);
        }
        if (labels != topBarWide || stacked != stackedLabels) { topBarWide = labels; stackedLabels = stacked; Notify(nameof(NavLabelVisibility)); Notify(nameof(NavStackedLabelVisibility)); }
        if (command != commandText) { commandText = command; Notify(nameof(CommandBoxTextVisibility)); }
    }

    // ---- Song header --------------------------------------------------------------------------------------------------

    public IReadOnlyList<string> SongFacts
    {
        get
        {
            if (selectedFile is not { } row) return [];
            var facts = row.Facts.ToList();
            if (row.AnalysisPath is not null && !row.UsesNativeScoreWorkflow) facts.Add(KeyName(row.KeyFifths, row.KeyMinor));
            return facts;
        }
    }

    public Visibility SongHeaderVisibility => selectedFile is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoSongVisibility => selectedFile is null ? Visibility.Visible : Visibility.Collapsed;

    private void SongHeaderPlayClick(object sender, RoutedEventArgs e)
    {
        if (selectedFile is not { } row) return;
        if (row.LibraryPlayback.IsPlaying) PauseLibraryMidiClick(new Button { Tag = row }, e);
        else PlayLibraryMidiClick(new Button { Tag = row }, e);
    }

    private void SongHeaderStopClick(object sender, RoutedEventArgs e)
    {
        if (selectedFile is { } row) StopLibraryMidiClick(new Button { Tag = row }, e);
    }

    private void SongHeaderRemoveClick(object sender, RoutedEventArgs e)
    {
        if (selectedFile is { } row) RemoveLibraryCardClick(new Button { Tag = row }, e);
    }

    // ---- Melodia ------------------------------------------------------------------------------------------------------

    /// <summary>The track the advisor ranks first (drums excluded), shown above the table.</summary>
    public TrackRow? SuggestedTrack => selectedFile is { } row
        ? row.Tracks.Where(t => t.Channel != 10 && t.MelodySuggestion is not null).OrderBy(t => t.MelodySuggestion!.Rank).FirstOrDefault()
          ?? row.Tracks.FirstOrDefault(t => t.Channel != 10 && t.Hint == "Boa candidata")
        : null;

    public Visibility SuggestionVisibility => SuggestedTrack is not null && CanSelectTracks && selectedFile?.ChordsOnly != true ? Visibility.Visible : Visibility.Collapsed;
    public string SuggestedTrackDetail => SuggestedTrack is { } t ? $"Pista {t.Track} · canal {t.Channel} · {t.Count} notas · {RangeName(t.Source.Minimum, t.Source.Maximum)}" : "";
    public string SuggestedTrackReason => SuggestedTrack?.SuggestionReason ?? "";
    public bool SuggestedTrackChosen => SuggestedTrack?.IsMelody == true;
    public string UseSuggestionLabel => SuggestedTrackChosen ? "Escolhida" : "Usar como melodia";

    private static string RangeName(int low, int high) => $"{VoiceRange.NoteName(low)}–{VoiceRange.NoteName(high)}";

    private void UseSuggestedTrackClick(object sender, RoutedEventArgs e)
    {
        if (SuggestedTrack is not { } track || !CanSelectTracks) return;
        track.IsMelody = true;
        Status = $"{track.Instrument} marcada como melodia. Ouça antes de continuar.";
    }

    private void ListenSuggestedTrackClick(object sender, RoutedEventArgs e)
    {
        if (SuggestedTrack is { } track) TrackAuditionClick(new Button { Tag = track }, e);
    }

    public int KeyboardLowNote => KeyboardRange.Of(KeyboardKeys).Low;
    public int KeyboardHighNote => KeyboardRange.Of(KeyboardKeys).High;
    public string KeyboardKeysText => $"{KeyboardKeys} teclas";
    public string KeyboardLowText => VoiceRange.NoteName(KeyboardLowNote);
    public string KeyboardHighText => VoiceRange.NoteName(KeyboardHighNote);

    /// <summary>The table of tracks: percussion and almost empty tracks fold under "Mais pistas" when the MIDI has many.</summary>
    public ICollectionView? VisibleTracks
    {
        get
        {
            if (selectedFile is not { } row) return null;
            if (!ReferenceEquals(visibleTracksRow, row) || visibleTracks is null)
            {
                visibleTracksRow = row;
                visibleTracks = new ListCollectionView(row.Tracks) { Filter = item => item is TrackRow track && ShowsTrack(row, track) };
            }
            return visibleTracks;
        }
    }

    private bool ShowsTrack(MidiRow row, TrackRow track) =>
        showAllTracks || row.Tracks.Count <= 6 || track.IsMelody || track.IsSecondVoice || track.IsAuditioning || !IsMinorTrack(row, track);

    private static bool IsMinorTrack(MidiRow row, TrackRow track)
    {
        int busiest = row.Tracks.Count == 0 ? 0 : row.Tracks.Max(t => t.Count);
        return track.Channel == 10 || track.Count < Math.Max(8, busiest / 20);
    }

    public int HiddenTrackCount => selectedFile is { } row && !showAllTracks && row.Tracks.Count > 6
        ? row.Tracks.Count(t => !ShowsTrack(row, t)) : 0;

    public string MoreTracksLabel => showAllTracks
        ? "Mostrar menos pistas"
        : HiddenTrackCount == 1 ? "Mais 1 pista · percussão ou pista com poucas notas" : $"Mais {HiddenTrackCount} pistas · percussão e pistas com poucas notas";

    public Visibility MoreTracksVisibility => showAllTracks || HiddenTrackCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void MoreTracksClick(object sender, RoutedEventArgs e)
    {
        showAllTracks = !showAllTracks;
        visibleTracks?.Refresh();
        Notify(nameof(MoreTracksLabel)); Notify(nameof(MoreTracksVisibility)); Notify(nameof(HiddenTrackCount));
    }

    // ---- Ritmo ---------------------------------------------------------------------------------------------------------

    private static string PointKey(ScoreReviewPoint point) => "analysis-point:" + point.Quarter.ToString("0.###", CultureInfo.InvariantCulture);

    private bool IsFlowConfirmed(MidiRow row, string id, ScoreFlowStep step) =>
        StateOf(row).Confirmed.Contains($"{id}@{step}#{FlowContext(row, step)}");

    public IReadOnlyList<ReviewPointView> RhythmPoints
    {
        get
        {
            if (selectedFile is not { } row || row.AnalysisPath is null) return [];
            bool all = IsFlowConfirmed(row, "analysis-review", ScoreFlowStep.Analysis);
            return row.ReviewPoints.OrderBy(p => p.Quarter).Select(p => new ReviewPointView(p,
                $"Compasso {p.Measure}, tempo {SongMeter.BeatText(p.Beat)}",
                p.Message.Length > 0 ? p.Message : "Confira se o ritmo da melodia ficou como no MIDI.",
                all || IsFlowConfirmed(row, PointKey(p), ScoreFlowStep.Analysis))).ToArray();
        }
    }

    public string RhythmTitle => selectedFile?.AnalysisPath is null ? "O ritmo ainda não foi analisado"
        : RhythmPoints.Count switch { 0 => "Nenhum trecho para conferir", 1 => "1 trecho para conferir", int n => $"{n} trechos para conferir" };

    public string RhythmSubtitle => selectedFile?.AnalysisPath is null
        ? "Volte para Melodia e use Continuar para ritmo: o TeclaScore analisa as notas, o ritmo e os acordes."
        : RhythmPoints.Count == 0 ? "A análise não encontrou pontos em que o ritmo possa ter ficado diferente do MIDI."
        : RhythmPoints.Count == 1 ? "A análise encontrou um ponto em que o ritmo pode ter ficado diferente do MIDI. Ouça e confirme."
        : "A análise encontrou pontos em que o ritmo pode ter ficado diferente do MIDI. Ouça cada um e confirme.";

    public string RhythmMeasuresText => selectedFile?.Meter is { } meter ? $"Música inteira · {meter.Measures.Count} compassos" : "";

    public string RhythmCleanText
    {
        get
        {
            if (selectedFile is not { AnalysisPath: not null, Meter: { } meter }) return "";
            int marked = RhythmMarkers.Count, rest = Math.Max(0, meter.Measures.Count - marked);
            return marked == 0 ? $"Os {meter.Measures.Count} compassos não têm pontos de atenção." : rest == 1 ? "O outro compasso não tem pontos de atenção." : $"Os outros {rest} compassos não têm pontos de atenção.";
        }
    }

    public Visibility RhythmCleanVisibility => RhythmCleanText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Notes of the chosen melody (or of every track before a choice) in each measure.</summary>
    public IReadOnlyList<double> RhythmDensities
    {
        get
        {
            if (selectedFile is not { Document: { } document, Meter: { } meter } row) return [];
            var keys = row.Tracks.Where(t => t.IsMelody).Select(t => t.Source.Key).ToHashSet();
            var counts = new double[meter.Measures.Count];
            double ppq = Math.Max(1, document.Ppq);
            foreach (var note in document.Notes)
            {
                if (keys.Count > 0 ? !keys.Contains(new SourceKey(note.Track, note.Channel, note.Program)) : note.Channel == 10) continue;
                counts[meter.MeasureAt(note.Start / ppq).Number - 1]++;
            }
            return counts;
        }
    }

    public IReadOnlyList<int> RhythmMarkers => selectedFile is { Meter: { } meter } row && row.AnalysisPath is not null
        ? row.ReviewPoints.Select(p => meter.MeasureAt(p.Quarter).Number - 1).Distinct().ToArray() : [];

    public IReadOnlyList<int> RhythmDoneMarkers => selectedFile is { Meter: { } meter }
        ? RhythmPoints.Where(p => p.IsChecked).Select(p => meter.MeasureAt(p.Point.Quarter).Number - 1).Distinct().ToArray() : [];

    private async void RhythmListenClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ReviewPointView view } || selectedFile is not { } row) return;
        await ListenPoint(row, view.Point);
    }

    private async Task ListenPoint(MidiRow row, ScoreReviewPoint point)
    {
        var track = row.Tracks.FirstOrDefault(t => t.IsMelody) ?? SuggestedTrack ?? row.Tracks.FirstOrDefault();
        if (track is null) { Status = "Esta música não tem pistas para ouvir."; return; }
        // Chord and key points are judged by the accompaniment; a measure where the melody rests would be silent.
        bool whole = point.Kind.StartsWith("chord", StringComparison.Ordinal) || point.Kind.StartsWith("harmony", StringComparison.Ordinal)
            || point.Kind.StartsWith("key", StringComparison.Ordinal) || !HasNotesIn(row, track, point);
        try { Status = await PlayReviewMeasure(row, point, track, settings.TrackAuditionSpeed, whole, CancellationToken.None); }
        catch (OperationCanceledException) { }
    }

    private static bool HasNotesIn(MidiRow row, TrackRow track, ScoreReviewPoint point)
    {
        if (row.Document is not { } document) return false;
        double ppq = Math.Max(1, document.Ppq), start = point.MeasureStartQuarter * ppq, end = point.MeasureEndQuarter * ppq;
        var key = track.Source.Key;
        return document.Notes.Any(note => note.Start < end && note.End > start && new SourceKey(note.Track, note.Channel, note.Program) == key);
    }

    private void RhythmFixClick(object sender, RoutedEventArgs e) => ReviewScoreClick(sender, e);

    private void RhythmOkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ReviewPointView view } || selectedFile is not { } row) return;
        StateOf(row).Confirmed.Add($"{PointKey(view.Point)}@{ScoreFlowStep.Analysis}#{FlowContext(row, ScoreFlowStep.Analysis)}");
        if (row.ReviewPoints.All(p => IsFlowConfirmed(row, PointKey(p), ScoreFlowStep.Analysis))) ConfirmFlow(row, "analysis-review");
        else NotifyFlow();
        Status = $"{view.Title}: conferido.";
    }

    private void RhythmUndoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ReviewPointView view } || selectedFile is not { } row) return;
        StateOf(row).Confirmed.RemoveWhere(key => key.StartsWith(PointKey(view.Point) + "@", StringComparison.Ordinal) || key.StartsWith("analysis-review@", StringComparison.Ordinal));
        NotifyFlow();
    }

    private async void RhythmTimelineClick(object? sender, int measure)
    {
        if (selectedFile is not { Meter: { } meter } row) return;
        if (measure < 0 || measure >= meter.Measures.Count) return;
        // Every bar plays its measure; a bar with a point to check plays it the same way as "Ouvir trecho".
        var bar = meter.Measures[measure];
        var point = row.ReviewPoints.FirstOrDefault(p => meter.MeasureAt(p.Quarter).Number - 1 == measure)
            ?? new ScoreReviewPoint { Quarter = bar.StartQuarter, Measure = bar.Number, Beat = 1, MeasureStartQuarter = bar.StartQuarter, MeasureEndQuarter = bar.EndQuarter };
        await ListenPoint(row, point);
    }

    // ---- Cifras --------------------------------------------------------------------------------------------------------

    private static readonly string[] MajorLetters = ["C♭", "G♭", "D♭", "A♭", "E♭", "B♭", "F", "C", "G", "D", "A", "E", "B", "F♯", "C♯"];
    private static readonly string[] MinorLetters = ["A♭m", "E♭m", "B♭m", "Fm", "Cm", "Gm", "Dm", "Am", "Em", "Bm", "F♯m", "C♯m", "G♯m", "D♯m", "A♯m"];
    private static readonly string[] SharpOrder = ["Fá♯", "Dó♯", "Sol♯", "Ré♯", "Lá♯", "Mi♯", "Si♯"];
    private static readonly string[] FlatOrder = ["Si♭", "Mi♭", "Lá♭", "Ré♭", "Sol♭", "Dó♭", "Fá♭"];

    public string KeyTitle => selectedFile is { } row ? "Tom: " + KeyName(row.KeyFifths, row.KeyMinor) : "";
    public string KeyBadge => selectedFile is { } row ? (row.KeyMinor ? MinorLetters : MajorLetters)[Math.Clamp(row.KeyFifths, -7, 7) + 7] : "";

    public string KeySignatureText
    {
        get
        {
            if (selectedFile is not { } row) return "";
            int fifths = Math.Clamp(row.KeyFifths, -7, 7), count = Math.Abs(fifths);
            if (count == 0) return "Armadura sem sustenidos nem bemóis";
            var names = (fifths > 0 ? SharpOrder : FlatOrder).Take(count).ToArray();
            string kind = fifths > 0 ? count == 1 ? "sustenido" : "sustenidos" : count == 1 ? "bemol" : "bemóis";
            string list = names.Length == 1 ? names[0] : string.Join(", ", names[..^1]) + " e " + names[^1];
            return $"Armadura com {count} {kind} ({list})";
        }
    }

    private bool KeyCheckPending => flowChecks.Any(c => c.Id == "chords-key" && c.Pending);
    private bool KeyCheckDone => flowChecks.Any(c => c.Id == "chords-key" && !c.Pending);
    public Visibility KeyConfirmVisibility => KeyCheckPending ? Visibility.Visible : Visibility.Collapsed;
    public Visibility KeyConfirmedVisibility => KeyCheckDone ? Visibility.Visible : Visibility.Collapsed;
    public Visibility KeyEditorVisibility => keyEditorOpen || !GuidedFlow ? Visibility.Visible : Visibility.Collapsed;
    public string KeyEditorLabel => keyEditorOpen ? "Fechar" : "Mudar tom";

    private void KeyConfirmClick(object sender, RoutedEventArgs e) { if (selectedFile is { } row) ConfirmFlow(row, "chords-key"); }
    private void KeyUndoClick(object sender, RoutedEventArgs e) { if (selectedFile is { } row) UndoFlowConfirmation(row, "chords-key"); }

    private void KeyEditorClick(object sender, RoutedEventArgs e)
    {
        keyEditorOpen = !keyEditorOpen;
        Notify(nameof(KeyEditorVisibility)); Notify(nameof(KeyEditorLabel));
    }

    public IReadOnlyList<ChordTileView> ChordTiles
    {
        get
        {
            if (selectedFile is not { } row || row.Chords.Count == 0) return [];
            var doubtful = ChordReviewWindow.Doubtful(row.Chords).ToHashSet();
            var meter = row.Meter; var selected = ChordGrid?.SelectedItem;
            return row.Chords.OrderBy(c => c.Quarter).Select(c =>
            {
                string position = meter is null ? $"Posição {c.Quarter:0.##}" : meter.Locate(c.Quarter) is var (measure, beat) && beat >= 1.08
                    ? $"Compasso {measure} · {SongMeter.BeatText(beat)}" : $"Compasso {meter.Locate(c.Quarter).Measure}";
                string symbol = c.Root + c.Quality + (string.IsNullOrEmpty(c.Bass) ? "" : "/" + c.Bass);
                return new ChordTileView(c, position, symbol, doubtful.Contains(c), ReferenceEquals(selected, c));
            }).ToArray();
        }
    }

    public string ChordCountText
    {
        get
        {
            if (selectedFile is not { } row) return "";
            int total = row.Chords.Count, doubtful = ChordReviewWindow.Doubtful(row.Chords).Count;
            string chords = total == 1 ? "1 cifra" : $"{total} cifras";
            return doubtful == 0 ? chords : chords + (doubtful == 1 ? " · 1 com dúvida" : $" · {doubtful} com dúvida");
        }
    }

    private void ChordTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ChordTileView tile }) return;
        ChordGrid.SelectedItem = tile.Chord; ChordGrid.ScrollIntoView(tile.Chord);
        Notify(nameof(ChordTiles));
    }

    // ---- Gerar ---------------------------------------------------------------------------------------------------------

    public Visibility GenerateSummaryVisibility => selectedFile is { ScorePath: null, UsesNativeScoreWorkflow: false } ? Visibility.Visible : Visibility.Collapsed;

    public IReadOnlyList<ProgressItemView> GenerateSummary
    {
        get
        {
            if (selectedFile is not { } row) return [];
            var items = ProgressItems.Take(3).Select(i => i with { Number = i.IsDone ? "" : i.Number }).ToList();
            var options = new List<string>();
            if (row.IncludeLyrics) options.Add(row.LyricsMode switch { "lines" => "letra do MIDI por versos", "notes" => "letra do MIDI sob as notas", _ => "letra do MIDI" });
            if (row.SimplifiedChords) options.Add("cifras simplificadas");
            if (row.FoldRepeats) options.Add("repetições");
            if (row.ChordStaff) options.Add("pauta dos acordes");
            options.Add(ScoreMetadataDefaults.InversionLabel(row.EffectiveDiagramInversion).ToLowerInvariant());
            items.Add(new ProgressItemView("4", "Opções", string.Join(" · ", options), false, true, false));
            return items;
        }
    }

    // ---- Progress panel ------------------------------------------------------------------------------------------------

    private int FlowDoneSteps => selectedFile is not { } row ? 0
        : row.ScorePath is not null ? 4 : GuidedFlow ? (int)FlowReachable : 0;

    public double FlowProgress => FlowDoneSteps / 4.0;
    public string FlowProgressText => $"{FlowDoneSteps * 25}%";
    public string FlowStageTitle => selectedFile is null ? "" : $"Etapa {(int)FlowCurrentStep + 1} de 4";

    public string FlowStageSubtitle
    {
        get
        {
            if (selectedFile is not { } row) return "";
            if (!GuidedFlow) return row.ScorePath is not null ? "Partitura gerada" : "Revisão aprovada: gere quando quiser";
            int pending = ChecksOfCurrentStep().Count(c => c.Pending);
            if (FlowCurrentStep == ScoreFlowStep.Generate) return "Pronta para gerar";
            return pending == 0 ? "Tudo certo nesta etapa" : pending == 1 ? "Falta 1 item" : $"Faltam {pending} itens";
        }
    }

    public IReadOnlyList<ProgressItemView> ProgressItems
    {
        get
        {
            if (selectedFile is not { } row) return [];
            bool guided = GuidedFlow, generated = row.ScorePath is not null;
            var current = FlowCurrentStep; var allowed = FlowAllowedStep;
            var marked = row.Tracks.Where(t => t.IsMelody).ToArray();
            string melody = row.ChordsOnly ? "Só cifras, sem melodia"
                : marked.Length == 0 ? "Escolha a pista"
                : string.Join(" + ", marked.Select(t => t.Instrument)) + (PlayedMelodyRange(row) is { } r ? " · " + RangeName(r.Low, r.High) : "");
            int points = row.ReviewPoints.Count, checkedPoints = RhythmPoints.Count(p => p.IsChecked);
            string rhythm = row.AnalysisPath is null ? "Depois da melodia"
                : points == 0 ? "Nenhum trecho a conferir"
                : checkedPoints == points ? (points == 1 ? "1 trecho conferido" : $"{points} trechos conferidos")
                : RhythmPoints.FirstOrDefault(p => !p.IsChecked) is { } next ? next.Title + " a confirmar" : "";
            string chords = row.AnalysisPath is null && row.Chords.Count == 0 ? "Depois do ritmo"
                : $"{KeyName(row.KeyFifths, row.KeyMinor)} · {(row.Chords.Count == 1 ? "1 cifra" : $"{row.Chords.Count} cifras")}";
            string score = generated ? row.PageCountText : guided && allowed < ScoreFlowStep.Generate ? "Depois das cifras" : "Pronta para gerar";
            string[] titles = ["Melodia escolhida", "Ritmo conferido", "Tom e cifras", "Partitura gerada"];
            string[] details = [melody, rhythm, chords, score];
            return Enumerable.Range(0, 4).Select(i =>
            {
                var step = (ScoreFlowStep)i;
                bool done = generated || guided && step < FlowReachable;
                bool locked = guided && step > allowed;
                return new ProgressItemView((i + 1).ToString(), titles[i], details[i], done, step == current, locked);
            }).ToArray();
        }
    }

    public string KeyboardFitChip => selectedFile is { } row && PlayedMelodyRange(row) is not null ? FitsKeyboard(row) ? "Cabe" : "Não cabe" : "";
    public Visibility KeyboardFitChipVisibility => KeyboardFitChip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool KeyboardFits => KeyboardFitChip == "Cabe";
    public int MelodyLowNote => selectedFile is { } row && PlayedMelodyRange(row) is { } r ? r.Low : 0;
    public int MelodyHighNote => selectedFile is { } row && PlayedMelodyRange(row) is { } r ? r.High : -1;

    public string KeyboardCardText => selectedFile is not { } row ? "Selecione uma música para ver a extensão da melodia."
        : PlayedMelodyRange(row) is not { } r ? row.ChordsOnly ? "Só cifras: nenhuma melodia para conferir." : "Marque a pista da melodia para ver se ela cabe."
        : FitsKeyboard(row) ? $"A melodia vai de {VoiceRange.NoteName(r.Low)} a {VoiceRange.NoteName(r.High)}." : KeyboardFitMessage;

    public Visibility GuidedFlowVisibility => GuidedFlow ? Visibility.Visible : Visibility.Collapsed;

    private void NotifyFlowScreens()
    {
        foreach (var name in new[] { nameof(SongFacts), nameof(SongHeaderVisibility), nameof(NoSongVisibility), nameof(SuggestedTrack), nameof(SuggestionVisibility),
            nameof(SuggestedTrackDetail), nameof(SuggestedTrackReason), nameof(SuggestedTrackChosen), nameof(UseSuggestionLabel), nameof(VisibleTracks), nameof(HiddenTrackCount),
            nameof(MoreTracksLabel), nameof(MoreTracksVisibility), nameof(RhythmPoints), nameof(RhythmTitle), nameof(RhythmSubtitle), nameof(RhythmMeasuresText),
            nameof(RhythmCleanText), nameof(RhythmCleanVisibility), nameof(RhythmDensities), nameof(RhythmMarkers), nameof(RhythmDoneMarkers), nameof(KeyTitle), nameof(KeyBadge),
            nameof(KeySignatureText), nameof(KeyConfirmVisibility), nameof(KeyConfirmedVisibility), nameof(KeyEditorVisibility), nameof(ChordTiles), nameof(ChordCountText),
            nameof(GenerateSummaryVisibility), nameof(GenerateSummary), nameof(FlowProgress), nameof(FlowProgressText), nameof(FlowStageTitle), nameof(FlowStageSubtitle),
            nameof(ProgressItems), nameof(KeyboardFitChip), nameof(KeyboardFitChipVisibility), nameof(KeyboardFits), nameof(MelodyLowNote), nameof(MelodyHighNote),
            nameof(KeyboardCardText), nameof(KeyboardLowNote), nameof(KeyboardHighNote), nameof(KeyboardKeysText), nameof(KeyboardLowText), nameof(KeyboardHighText),
            nameof(FooterChecksVisibility), nameof(FlowFooterVisibility), nameof(GuidedFlowVisibility) }) Notify(name);
    }
}
