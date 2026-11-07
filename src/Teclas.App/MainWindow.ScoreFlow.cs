using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>One chip of the step bar above the editor.</summary>
public sealed record FlowStepView(int Step, int Tab, string Number, string Title, string Subtitle, bool IsEnabled, bool IsCurrent, bool IsDone, bool IsLocked)
{
    /// <summary>The line after the chip (none after the last step) turns green once the step is done.</summary>
    public bool HasConnector => Step < 3;
}

/// <summary>One line of "Para continuar", with the command that resolves it and, for doubts, a confirmation.</summary>
public sealed record FlowCheckView(ScoreFlowCheck Check)
{
    public string Level => Check.Level == ScoreFlowLevel.Block ? "block" : Check.Pending ? "confirm" : "done";
    public string Glyph => Level switch { "block" => "", "confirm" => "", _ => "" };
    public string Message => Check.Message;
    public string ActionLabel => Check.ActionLabel ?? "";
    public Visibility ActionVisibility => Check.Pending && Check.Action is not null ? Visibility.Visible : Visibility.Collapsed;
    public string ConfirmLabel => Check.ConfirmLabel ?? "";
    public Visibility ConfirmVisibility => Check.Level == ScoreFlowLevel.Confirm && !Check.Confirmed && Check.ConfirmLabel is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UndoVisibility => Check.Level == ScoreFlowLevel.Confirm && Check.Confirmed ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Step-by-step generation of a new MIDI: Melodia → Ritmo → Cifras → Gerar (tabs 0, 3, 1 and 2 of the editor).
/// A step opens only when the previous one has no block and no unconfirmed doubt; Gerar partitura exists only in the last step.
/// Songs that already have a score, use the approved review, or continue in the music editor keep the free tabs.
/// </summary>
public partial class MainWindow
{
    private sealed class FlowState
    {
        public readonly HashSet<string> Confirmed = [];
        public string? HeardSignature;
    }

    private readonly ConditionalWeakTable<MidiRow, FlowState> flowStates = new();
    private IReadOnlyList<ScoreFlowCheck> flowChecks = [];
    private bool flowTabGuard;

    private static readonly string[] FlowTitles = ["Melodia", "Ritmo", "Cifras", "Gerar"];
    private static readonly string[] FlowHints = ["Escolha a pista", "Confira trechos", "Acordes e tom", "Revisar e salvar"];

    /// <summary>Editor tab of each step: Melodia 0, Ritmo 3, Cifras 1, Gerar (prévia) 2; the old indexes stay for saved sessions.</summary>
    internal static int TabOfStep(ScoreFlowStep step) => step switch { ScoreFlowStep.Melody => 0, ScoreFlowStep.Analysis => 3, ScoreFlowStep.Chords => 1, _ => 2 };
    internal static ScoreFlowStep StepOfTab(int tab) => tab switch { 0 => ScoreFlowStep.Melody, 3 => ScoreFlowStep.Analysis, 1 => ScoreFlowStep.Chords, _ => ScoreFlowStep.Generate };

    internal static bool IsGuidedFlow(MidiRow? row) =>
        row is { UsesNativeScoreWorkflow: false, Document: not null, UseReviewedProfile: false, ScorePath: null };

    public bool GuidedFlow => IsGuidedFlow(selectedFile);
    public IReadOnlyList<ScoreFlowCheck> FlowCheckList => flowChecks;
    public ScoreFlowStep FlowReachable => ScoreFlowRules.Reachable(flowChecks);
    private bool FlowHasAnalysis(MidiRow row) => row.AnalysisPath is not null;

    /// <summary>The furthest step a guided song may open: Ritmo needs the analysis.</summary>
    public ScoreFlowStep FlowAllowedStep => !GuidedFlow || selectedFile is not { } row ? ScoreFlowStep.Generate : FlowReachable switch
    {
        ScoreFlowStep.Melody => ScoreFlowStep.Melody,
        ScoreFlowStep.Analysis => FlowHasAnalysis(row) ? ScoreFlowStep.Analysis : ScoreFlowStep.Melody,
        var step => step
    };

    /// <summary>The editor tab of <see cref="FlowAllowedStep"/>.</summary>
    public int FlowAllowedTab => TabOfStep(FlowAllowedStep);

    private int CurrentEditorTab => EditorTabs?.SelectedIndex is int index and >= 0 ? index : 0;

    /// <summary>The step the user is on (the step of the open tab).</summary>
    public ScoreFlowStep FlowCurrentStep => StepOfTab(CurrentEditorTab);

    public IReadOnlyList<FlowStepView> FlowSteps
    {
        get
        {
            var reachable = FlowReachable; var current = FlowCurrentStep; bool guided = GuidedFlow;
            var allowed = FlowAllowedStep;
            return Enumerable.Range(0, 4).Select(i =>
            {
                var step = (ScoreFlowStep)i; int tab = TabOfStep(step);
                bool enabled = CanEditMidi || step == ScoreFlowStep.Generate ? !guided || step <= allowed : false;
                bool done = guided ? step < reachable && step != current : selectedFile?.ScorePath is not null && step != ScoreFlowStep.Generate && step != current;
                bool locked = guided && !enabled;
                int pending = flowChecks.Count(c => c.Step == step && c.Pending);
                string subtitle = !guided || locked || done ? FlowHints[i]
                    : step == ScoreFlowStep.Generate ? "Pronta para gerar"
                    : pending == 0 ? FlowHints[i] : pending == 1 ? "1 pendência" : $"{pending} pendências";
                return new FlowStepView(i, tab, done ? "\uE73E" : (i + 1).ToString(), FlowTitles[i], subtitle, enabled, step == current, done, locked);
            }).ToArray();
        }
    }

    private IEnumerable<ScoreFlowCheck> ChecksOfCurrentStep()
    {
        var step = FlowCurrentStep;
        return step == ScoreFlowStep.Generate ? [] : flowChecks.Where(c => c.Step == step);
    }

    public IReadOnlyList<FlowCheckView> FlowPanelChecks =>
        ChecksOfCurrentStep()
            .OrderBy(c => c.Pending ? c.Level == ScoreFlowLevel.Block ? 0 : 1 : 2).Select(c => new FlowCheckView(c)).ToArray();

    public Visibility FlowPanelVisibility => GuidedFlow ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Guided songs generate from the fixed footer; the old generation row and Analisar acordes stay for the free workflow.</summary>
    public Visibility GenerationButtonsVisibility => GuidedFlow || (selectedFile is not null && FlowCurrentStep == ScoreFlowStep.Generate) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FlowEnqueueVisibility => selectedFile is not null && FlowCurrentStep == ScoreFlowStep.Generate ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FlowChecksVisibility => FlowPanelChecks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectionMessageVisibility => GuidedFlow ? Visibility.Collapsed : Visibility.Visible;

    public string FlowSummary
    {
        get
        {
            var step = FlowCurrentStep;
            var pending = ChecksOfCurrentStep().Where(c => c.Pending).ToArray();
            int blocks = pending.Count(c => c.Level == ScoreFlowLevel.Block), confirms = pending.Length - blocks;
            if (step == ScoreFlowStep.Generate) return CanGenerateNow ? "Tudo conferido. Gere a partitura para ver as páginas." : "Aguarde a operação em andamento.";
            if (pending.Length == 0) return step switch
            {
                ScoreFlowStep.Melody => "Melodia conferida. Siga para conferir o ritmo.",
                ScoreFlowStep.Analysis => "Ritmo conferido. Siga para as cifras.",
                _ => "Cifras conferidas. Siga para gerar a partitura."
            };
            string Part(int n, string one, string many) => n == 1 ? "1 " + one : $"{n} {many}";
            var parts = new List<string>();
            if (blocks > 0) parts.Add(Part(blocks, "bloqueio", "bloqueios"));
            if (confirms > 0) parts.Add(Part(confirms, "confirmação pendente", "confirmações pendentes"));
            return "Para continuar: " + string.Join(" e ", parts) + ".";
        }
    }

    public string FlowNextLabel => FlowCurrentStep switch
    {
        ScoreFlowStep.Melody => "Continuar para ritmo",
        ScoreFlowStep.Analysis => "Continuar para cifras",
        ScoreFlowStep.Chords => "Continuar para gerar",
        _ => selectedFile?.UsesNativeScoreWorkflow == true ? "Melhorar apresentação" : selectedFile?.ScorePath is not null && !GuidedFlow ? "Gerar de novo" : "Gerar partitura"
    };

    /// <summary>Guided: the current step has nothing pending. Free songs (score already generated, approved review) move freely between the tabs.</summary>
    public bool CanFlowNext => FlowCurrentStep == ScoreFlowStep.Generate
        ? GuidedFlow ? CanGenerateNow : selectedFile is not null && CanGenerateNow
        : IsIdle && selectedFile is not null && CanEditMidi && (!GuidedFlow || !ChecksOfCurrentStep().Any(c => c.Pending));

    public bool CanFlowBack => IsIdle && selectedFile is not null && FlowCurrentStep > ScoreFlowStep.Melody && CanEditMidi;
    public Visibility FlowFooterVisibility => selectedFile is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Generation from the buttons: guided songs only after every step is clear.</summary>
    public bool CanGenerateNow => CanGenerate && (!GuidedFlow || ScoreFlowRules.CanGenerate(flowChecks));
    public bool CanEnqueueNow => CanEnqueueConversion && (!GuidedFlow || ScoreFlowRules.CanGenerate(flowChecks));

    private FlowState StateOf(MidiRow row) => flowStates.GetValue(row, _ => new FlowState());

    /// <summary>Confirmations are tied to what they confirmed: the melody choice, or the analysis that produced the chords.</summary>
    private string FlowContext(MidiRow row, ScoreFlowStep step) => step == ScoreFlowStep.Melody
        ? MelodySignature(row) + "|k" + KeyboardKeys
        : (row.AnalysisPath ?? "") + "|" + MelodySignature(row);

    private static string MelodySignature(MidiRow row) =>
        (row.ChordsOnly ? "chords;" : "") + row.TransposeSemitones + ";" +
        string.Join(",", row.Tracks.Where(t => t.IsMelody).Select(t => $"{t.Source.Key.Track}/{t.Source.Key.Channel}/{t.Source.Key.Program}")) +
        ";s" + row.MelodySections.Count;

    private static string KeyName(int fifths, bool minor)
    {
        string[] major = ["Dó♭", "Sol♭", "Ré♭", "Lá♭", "Mi♭", "Si♭", "Fá", "Dó", "Sol", "Ré", "Lá", "Mi", "Si", "Fá#", "Dó#"];
        string[] minors = ["Lá♭", "Mi♭", "Si♭", "Fá", "Dó", "Sol", "Ré", "Lá", "Mi", "Si", "Fá#", "Dó#", "Sol#", "Ré#", "Lá#"];
        int index = Math.Clamp(fifths, -7, 7) + 7;
        return minor ? minors[index] + " menor" : major[index] + " maior";
    }

    private ScoreFlowInput FlowInput(MidiRow row)
    {
        var state = StateOf(row);
        var confirmed = new HashSet<string>();
        foreach (var key in state.Confirmed)
        {
            int split = key.IndexOf('@');
            int hash = split <= 0 ? -1 : key.IndexOf('#', split);
            if (hash < 0 || !Enum.TryParse<ScoreFlowStep>(key[(split + 1)..hash], out var step)) continue;
            if (key[(hash + 1)..] == FlowContext(row, step)) confirmed.Add(key[..split]);
        }
        var marked = row.Tracks.Where(t => t.IsMelody).ToArray();
        string? lyrics = null;
        if (SuggestedLyricsTrack(row) is { } best)
        {
            var track = row.Tracks.FirstOrDefault(t => t.Source.Key == best.Source);
            lyrics = track is null ? $"a pista {best.Source.Track + 1}" : $"{track.Instrument} (pista {track.Track}, canal {track.Channel})";
        }
        bool analysed = FlowHasAnalysis(row);
        return new ScoreFlowInput
        {
            ChordsOnly = row.ChordsOnly,
            Tracks = row.Tracks.Select(t => new ScoreFlowTrack(TrackLabel(t), t.Hint, t.IsMelody, t.Channel == 10, t.Count)).ToArray(),
            MelodyRange = marked.Length == 0 ? null : PlayedMelodyRange(row),
            KeyboardKeys = KeyboardKeys,
            LyricsTrack = lyrics,
            HeardMarked = marked.Length > 0 && state.HeardSignature == MelodySignature(row),
            HasAnalysis = analysed,
            ReviewPoints = analysed ? row.ReviewPoints.Count : 0,
            BlockingReviewPoints = analysed ? row.ReviewPoints.Count(p => p.Blocking) : 0,
            KeyName = analysed && !row.ChordsOnly || analysed && row.Chords.Count > 0 ? KeyName(row.KeyFifths, row.KeyMinor) : "",
            Chords = analysed ? row.Chords.Select(c => new ScoreFlowChord(c.Quarter, c.Root + c.Quality)).ToArray() : [],
            DoubtfulChords = analysed ? ChordReviewWindow.Doubtful(row.Chords).Count : 0,
            Confirmed = confirmed
        };
    }

    /// <summary>Track names in the checks: the instrument, with the track number when two tracks share it.</summary>
    private string TrackLabel(TrackRow track) =>
        selectedFile is { } row && row.Tracks.Count(t => t.Instrument == track.Instrument) > 1 ? $"{track.Instrument} (pista {track.Track}, canal {track.Channel})" : track.Instrument;

    private void NotifyFlow()
    {
        flowChecks = selectedFile is { } row && IsGuidedFlow(row) ? ScoreFlowRules.Evaluate(FlowInput(row)) : [];
        foreach (var name in new[] { nameof(GuidedFlow), nameof(FlowCheckList), nameof(FlowReachable), nameof(FlowAllowedTab), nameof(FlowCurrentStep), nameof(FlowSteps),
            nameof(FlowPanelChecks), nameof(FlowPanelVisibility), nameof(GenerationButtonsVisibility), nameof(SelectionMessageVisibility), nameof(FlowSummary),
            nameof(FlowNextLabel), nameof(CanFlowNext), nameof(FlowEnqueueVisibility), nameof(FlowChecksVisibility), nameof(GenerateButtonLabel), nameof(CanGenerateNow), nameof(CanEnqueueNow),
            nameof(FlowAllowedStep), nameof(CanFlowBack), nameof(FlowFooterVisibility) }) Notify(name);
        NotifyFlowScreens();
        GuardFlowTab();
    }

    /// <summary>Keeps a guided song on a step it has unlocked (also after restoring the last tab or when an earlier step changes).</summary>
    private void GuardFlowTab()
    {
        if (EditorTabs is null || flowTabGuard || !GuidedFlow || FlowCurrentStep <= FlowAllowedStep) return;
        flowTabGuard = true;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (GuidedFlow && FlowCurrentStep > FlowAllowedStep)
                {
                    EditorTabs.SelectedIndex = FlowAllowedTab;
                    Status = "Conclua a etapa " + FlowTitles[(int)FlowReachable] + " antes de seguir. Veja o que falta em Progresso da partitura.";
                }
            }
            finally { flowTabGuard = false; NotifyFlow(); }
        }, DispatcherPriority.Background);
    }

    private void InitializeScoreFlow()
    {
        EditorTabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, EditorTabs)) NotifyFlow(); };
        ChordGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(NotifyFlow, DispatcherPriority.Background);
        ChordGrid.RowEditEnding += (_, _) => Dispatcher.BeginInvoke(NotifyFlow, DispatcherPriority.Background);
        ChordGrid.SelectionChanged += (_, _) => Notify(nameof(ChordTiles));
        InitializeResponsiveLayout();
    }

    /// <summary>The marked tracks were heard together (or the single marked track alone).</summary>
    private void MarkFlowHeard(MidiRow row, IReadOnlyList<TrackRow> tracks)
    {
        var marked = row.Tracks.Where(t => t.IsMelody).ToArray();
        if (marked.Length == 0 || !marked.All(tracks.Contains)) return;
        StateOf(row).HeardSignature = MelodySignature(row);
        NotifyFlow();
    }

    /// <summary>Brings back the confirmations saved with the session; each one still only counts while its melody/analysis context matches.</summary>
    private void RestoreFlow(MidiRow row, SavedMidi saved)
    {
        var state = StateOf(row);
        state.Confirmed.Clear();
        foreach (var key in saved.FlowConfirmations ?? []) state.Confirmed.Add(key);
        state.HeardSignature = saved.FlowHeard;
    }

    /// <summary>Smoke helper: listens to the marked melody and confirms every pending confirmation, as a user would, step by step.</summary>
    internal void CompleteFlowForSmoke(MidiRow row)
    {
        StateOf(row).HeardSignature = MelodySignature(row);
        for (int pass = 0; pass < 4; pass++)
        {
            var checks = ScoreFlowRules.Evaluate(FlowInput(row));
            var pending = checks.Where(c => c.Pending && c.Level == ScoreFlowLevel.Confirm).Select(c => c.Id).ToArray();
            if (pending.Length == 0) break;
            foreach (var id in pending) ConfirmFlow(row, id);
        }
    }

    private void ConfirmFlow(MidiRow row, string id)
    {
        var check = flowChecks.FirstOrDefault(c => c.Id == id);
        var step = check?.Step ?? (id.StartsWith("melody", StringComparison.Ordinal) ? ScoreFlowStep.Melody : id.StartsWith("chords", StringComparison.Ordinal) ? ScoreFlowStep.Chords : ScoreFlowStep.Analysis);
        StateOf(row).Confirmed.Add($"{id}@{step}#{FlowContext(row, step)}");
        NotifyFlow();
    }

    private void UndoFlowConfirmation(MidiRow row, string id)
    {
        StateOf(row).Confirmed.RemoveWhere(key => key.StartsWith(id + "@", StringComparison.Ordinal));
        if (id == "melody-listen") StateOf(row).HeardSignature = null;
        NotifyFlow();
    }

    /// <summary>Pending melody checks stop "Analisar acordes" from skipping the first step.</summary>
    private bool FlowBlocksAnalysis(out string message)
    {
        message = "";
        if (!GuidedFlow) return false;
        var pending = flowChecks.Where(c => c.Step == ScoreFlowStep.Melody && c.Pending).ToArray();
        if (pending.Length == 0) return false;
        message = "Antes de analisar, resolva a etapa Melodia: " + pending[0].Message;
        return true;
    }

    private void FlowStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FlowStepView step } || !step.IsEnabled) return;
        EditorTabs.SelectedIndex = step.Tab;
    }

    private async void FlowNextClick(object sender, RoutedEventArgs e)
    {
        if (!CanFlowNext || selectedFile is not { } row) return;
        var step = FlowCurrentStep;
        if (step == ScoreFlowStep.Generate) { GenerateClick(sender, e); return; }
        if (step == ScoreFlowStep.Melody)
        {
            if (FlowHasAnalysis(row) && HasCurrentMelodyAnalysis(row)) { EditorTabs.SelectedIndex = TabOfStep(ScoreFlowStep.Analysis); return; }
            await RunOperation(async () => { await Analyze(row); NotifyFlow(); EditorTabs.SelectedIndex = TabOfStep(ScoreFlowStep.Analysis); }, row, "Analisando notas e acordes");
            return;
        }
        if (step == ScoreFlowStep.Analysis) { EditorTabs.SelectedIndex = TabOfStep(ScoreFlowStep.Chords); return; }
        if (!CommitEdits()) return;
        NotifyFlow();
        if (!GuidedFlow || ScoreFlowRules.CanGenerate(flowChecks)) EditorTabs.SelectedIndex = TabOfStep(ScoreFlowStep.Generate);
    }

    private void FlowBackClick(object sender, RoutedEventArgs e)
    {
        if (!CanFlowBack) return;
        if (FlowCurrentStep == ScoreFlowStep.Chords && !CommitEdits()) return;
        EditorTabs.SelectedIndex = TabOfStep(FlowCurrentStep - 1);
    }

    private void FlowActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FlowCheckView view }) return;
        switch (view.Check.Action)
        {
            case "compare": MelodySuggestionsClick(sender, e); break;
            case "sections": MelodySectionsClick(sender, e); break;
            case "use-lyrics": UseLyricsTrackClick(sender, e); break;
            case "listen": PlayMarkedTracksClick(sender, e); break;
            case "analyze": FlowNextClick(sender, e); break;
            case "review": ReviewScoreClick(sender, e); break;
            case "chord-review": ChordReviewClick(sender, e); break;
        }
    }

    private void FlowConfirmClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FlowCheckView view } && selectedFile is { } row) ConfirmFlow(row, view.Check.Id);
    }

    private void FlowUndoClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FlowCheckView view } && selectedFile is { } row) UndoFlowConfirmation(row, view.Check.Id);
    }
}
