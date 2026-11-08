using Teclas.Core;
namespace Teclas.App;

public partial class MainWindow
{
    /// <summary>Literal repetitions written once between repeat barlines (engine score_repeats.py); per song.</summary>
    public bool FoldRepeats
    {
        get => selectedFile?.FoldRepeats == true;
        set
        {
            if (selectedFile is not { UsesNativeScoreWorkflow: false } row || row.FoldRepeats == value) return;
            RememberVersion(row);
            row.FoldRepeats = value;
            if (row.ScorePath is not null) row.HasPendingScoreProperties = true;
            Notify(); RefreshSelection(); PersistPreferences();
            Status = value ? "Repetições ligadas: trechos repetidos iguais serão escritos uma vez (ritornelo, casas 1 e 2 ou D.S./Coda). Gere de novo." : "Repetições desligadas: a partitura volta a escrever tudo. Gere de novo.";
        }
    }

    /// <summary>Chord chart without melody for MIDIs whose melody is missing; a new analysis follows.</summary>
    public bool ChordsOnly
    {
        get => selectedFile?.ChordsOnly == true;
        set
        {
            if (selectedFile is not { UsesNativeScoreWorkflow: false } row || row.ChordsOnly == value) return;
            RememberVersion(row);
            row.ChordsOnly = value;
            if (row.ScorePath is not null) row.HasPendingScoreProperties = true;
            Notify(); RefreshSelection(); PersistPreferences();
            Status = value ? "Só cifras: a partitura terá barras nos tempos, cifras e diagramas, sem melodia. Analise e gere de novo." : "Só cifras desligado: marque a pista de melodia e analise de novo.";
        }
    }

    /// <summary>Karaoke lyrics of the MIDI written under the melody, when the MIDI has them.</summary>
    public bool IncludeLyrics
    {
        get => selectedFile?.IncludeLyrics != false;
        set
        {
            if (selectedFile is not { UsesNativeScoreWorkflow: false } row || row.IncludeLyrics == value) return;
            RememberVersion(row);
            row.IncludeLyrics = value;
            if (row.ScorePath is not null) row.HasPendingScoreProperties = true;
            Notify(); RefreshSelection(); NotifyLyricsTrack(); PersistPreferences();
            Status = value ? "Letra ligada: as sílabas do MIDI entram na partitura. Gere de novo." : "Letra desligada: a partitura sai só com melodia e cifras. Gere de novo.";
        }
    }

    public IReadOnlyList<object> LyricsModeOptions { get; } =
    [
        new { Value = "auto", Label = "Automática", Hint = "Sob as notas quando a melodia escolhida acompanha o canto; senão, por versos." },
        new { Value = "notes", Label = "Sob as notas", Hint = "Cada sílaba na nota da melodia em que cai; sílabas sem nota próxima ficam de fora." },
        new { Value = "lines", Label = "Por versos", Hint = "Cada verso inteiro sob o primeiro compasso em que é cantado, seja qual for a melodia." },
    ];

    /// <summary>Where the karaoke lyrics go in the score (engine lyrics_mode); per song.</summary>
    public string LyricsMode
    {
        get => selectedFile?.LyricsMode ?? "auto";
        set
        {
            string mode = value is "notes" or "lines" ? value : "auto";
            if (selectedFile is not { UsesNativeScoreWorkflow: false } row || row.LyricsMode == mode) return;
            RememberVersion(row);
            row.LyricsMode = mode;
            if (row.ScorePath is not null && row.IncludeLyrics) row.HasPendingScoreProperties = true;
            Notify(); RefreshSelection(); NotifyLyricsTrack(); PersistPreferences();
            Status = mode switch
            {
                "lines" => "Letra por versos: cada verso entra sob o primeiro compasso em que é cantado, sem depender da melodia escolhida. Gere de novo.",
                "notes" => "Letra sob as notas: cada sílaba vai para a nota da melodia em que cai. Gere de novo.",
                _ => "Letra automática: sob as notas quando a melodia acompanha o canto, senão por versos. Gere de novo."
            };
        }
    }

    /// <summary>The lyric switch and placement on the Melody step: only for MIDIs with karaoke lyrics and a melody score.</summary>
    public System.Windows.Visibility LyricsOptionsVisibility =>
        selectedFile is { ChordsOnly: false } row && LyricsCoverageOf(row).Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>Chord staff below the melody (the diagram notes of each chord); per song.</summary>
    public bool ChordStaff
    {
        get => selectedFile?.ChordStaff == true;
        set
        {
            if (selectedFile is not { UsesNativeScoreWorkflow: false } row || row.ChordStaff == value) return;
            RememberVersion(row);
            row.ChordStaff = value;
            if (row.ScorePath is not null) row.HasPendingScoreProperties = true;
            Notify(); RefreshSelection(); PersistPreferences();
            Status = value ? "Pauta dos acordes ligada: cada acorde aparece escrito na pauta de baixo, com as notas do diagrama. Gere de novo." : "Pauta dos acordes desligada. Gere de novo.";
        }
    }

    public IReadOnlyList<object> NoteNameOptions { get; } = [new { Value = "letras", Label = "C, D, E (letras)" }, new { Value = "solfejo", Label = "Dó, Ré, Mi (solfejo)" }];

    /// <summary>Names under the C keys of every keyboard diagram, in the app and in the next generated scores.</summary>
    public string NoteNames
    {
        get => settings.NoteNames == "solfejo" ? "solfejo" : "letras";
        set
        {
            string name = value == "solfejo" ? "solfejo" : "letras";
            if (settings.NoteNames == name) return;
            settings.NoteNames = name; KeyboardDiagramPreview.CLabel = name == "solfejo" ? "Dó" : "C";
            Notify(); PersistPreferences();
            Status = "Nomes das teclas trocados: valem nos diagramas do app e nas próximas partituras geradas (as já geradas mudam ao gerar de novo).";
        }
    }

    public IReadOnlyList<object> DiagramStyleOptions { get; } =
    [
        new { Value = "destacado", Label = "Destacado (cores)" },
        new { Value = "cinza", Label = "Destacado em cinza (P&B)" },
        new { Value = "classico", Label = "Clássico (pontos)" }
    ];

    /// <summary>Drawing of the keyboard diagrams in the app and in the next generated scores.</summary>
    public string DiagramStyle
    {
        get => KeyboardDiagramSvg.Normalize(settings.DiagramStyle);
        set
        {
            string style = KeyboardDiagramSvg.Normalize(value);
            if (settings.DiagramStyle == style) return;
            settings.DiagramStyle = style; KeyboardDiagramPreview.Style = style;
            Notify(); PersistPreferences();
            Status = "Desenho dos diagramas trocado: vale no app e nas próximas partituras geradas (as já geradas mudam ao gerar de novo).";
        }
    }

    /// <summary>Title and "página X de Y" at the foot of every page of the next generated scores.</summary>
    public bool PageFooter
    {
        get => settings.PageFooter;
        set { if (settings.PageFooter == value) return; settings.PageFooter = value; Notify(); PersistPreferences(); }
    }

    /// <summary>Each PDF ends with a page of the song's chords and keyboard diagrams.</summary>
    public bool PdfChordPage
    {
        get => settings.PdfChordPage;
        set { if (settings.PdfChordPage == value) return; settings.PdfChordPage = value; ScoreExporter.AppendChordPage = value; Notify(); PersistPreferences(); }
    }

    /// <summary>PDFs get a "Mapa da música" page with the chords bar by bar, part by part.</summary>
    public bool PdfMusicMap
    {
        get => settings.PdfMusicMap;
        set { if (settings.PdfMusicMap == value) return; settings.PdfMusicMap = value; ScoreExporter.AppendMusicMap = value; Notify(); PersistPreferences(); }
    }

    /// <summary>Section names (Intro, A, Refrão…) in the next generated scores, each starting a system.</summary>
    public bool SectionMarks
    {
        get => settings.SectionMarks;
        set { if (settings.SectionMarks == value) return; settings.SectionMarks = value; Notify(); PersistPreferences(); }
    }

    /// <summary>Key, time signature and tempo under the title of the next generated scores.</summary>
    public bool HeaderInfo
    {
        get => settings.HeaderInfo;
        set { if (settings.HeaderInfo == value) return; settings.HeaderInfo = value; Notify(); PersistPreferences(); }
    }

    /// <summary>Note lengths written for reading (fewer tiny rests); attacks never move.</summary>
    public bool CleanDurations
    {
        get => settings.CleanDurations;
        set { if (settings.CleanDurations == value) return; settings.CleanDurations = value; Notify(); PersistPreferences(); }
    }

    /// <summary>Presentation options every generation request carries.</summary>
    private void AddPresentation(System.Text.Json.Nodes.JsonObject request)
    {
        request["diagramStyle"] = KeyboardDiagramSvg.Normalize(settings.DiagramStyle);
        if (!settings.PageFooter) request["pageFooter"] = false;
        if (!settings.CleanDurations) request["cleanDurations"] = false;
        if (!settings.SectionMarks) request["sectionMarks"] = false;
        if (!settings.HeaderInfo) request["headerInfo"] = false;
    }

    public bool SimplifiedChords
    {
        get => selectedFile?.SimplifiedChords == true;
        set
        {
            if (selectedFile is not { UsesNativeScoreWorkflow: false } row || row.SimplifiedChords == value) return;
            RememberVersion(row);
            row.SimplifiedChords = value;
            if (row.ScorePath is not null) row.HasPendingScoreProperties = true;
            Notify(); RefreshSelection(); PersistPreferences();
            Status = value ? "Cifras simplificadas ligadas: gere de novo para atualizar a partitura." : "Cifras completas: gere de novo para atualizar a partitura.";
        }
    }
}
