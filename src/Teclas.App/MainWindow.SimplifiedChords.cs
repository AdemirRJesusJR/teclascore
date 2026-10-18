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
