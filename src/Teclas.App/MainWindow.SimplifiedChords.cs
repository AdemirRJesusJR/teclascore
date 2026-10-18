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
            Status = value ? "Ritornelo ligado: trechos repetidos iguais serão escritos uma vez entre sinais de repetição. Gere de novo." : "Ritornelo desligado: a partitura volta a escrever tudo. Gere de novo.";
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
