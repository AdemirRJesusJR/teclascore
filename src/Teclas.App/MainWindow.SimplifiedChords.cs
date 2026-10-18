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
            Notify(); RefreshSelection(); PersistPreferences();
            Status = value ? "Letra ligada: as sílabas do MIDI entram sob a melodia. Gere de novo." : "Letra desligada: a partitura sai só com melodia e cifras. Gere de novo.";
        }
    }

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
