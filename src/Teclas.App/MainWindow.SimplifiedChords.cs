namespace Teclas.App;

public partial class MainWindow
{
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
