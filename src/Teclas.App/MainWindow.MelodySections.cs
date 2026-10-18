using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanEditMelodySections => CanSelectTracks && selectedFile?.Document is not null;
    public string MelodySectionsSummary => selectedFile is not { UsesNativeScoreWorkflow: false } row ? "" :
        row.MelodySections.Count == 0 ? "Toda a música usa as pistas e a prioridade geral." :
        row.UseReviewedProfile ? $"{row.MelodySections.Count} trechos manuais guardados · desative o perfil revisado para usá-los." :
        $"{row.MelodySections.Count} trechos com fonte exclusiva · os demais compassos usam a prioridade geral.";

    private void NotifyMelodySections()
    {
        Notify(nameof(CanEditMelodySections));
        Notify(nameof(MelodySectionsSummary));
    }
    private JsonArray MelodySections(MidiRow row) =>
        row.UseReviewedProfile ? new JsonArray() : MelodySectionRules.ToJson(row.MelodySections);

    private async void MelodySectionsClick(object sender, RoutedEventArgs e)
        => await OpenMelodySections();

    private async Task OpenMelodySections(int? initialMeasure = null)
    {
        if (!CanEditMelodySections || selectedFile is not { Document: { } document } row || !CommitEdits()) return;
        MelodyMeasureMap? map = null;
        await RunOperation(async () =>
        {
            ValidateSettings(false);
            string workspace = Path.Combine(LocalJobsDirectory, "measure-maps", Guid.NewGuid().ToString("N"));
            var request = new JsonObject { ["mode"] = "measureMap", ["inputMidi"] = row.FullPath, ["workspace"] = workspace };
            var response = await new EngineClient(PythonPath, engineDirectory).RunAsync(request, workspace, CreateOperationProgress(), cancellation!.Token);
            var result = MelodyMeasureMap.Read(response);
            if (!string.Equals(result.SourceSha256, document.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("O MIDI mudou desde a importação. Reimporte sua versão atual antes de escolher os trechos.");
            cancellation.Token.ThrowIfCancellationRequested(); map = result;
        }, operationTitle: "Lendo compassos da música", operationItem: row.ScoreTitle);
        if (operationsDisposed || map is null || selectedFile != row || row.Document != document || !CanEditMelodySections) return;
        var measureMap = map;
        var dialog = new MelodySectionsWindow(map, row.Tracks.Where(track => track.Channel != 10).ToArray(), row.MelodySections, initialMeasure) { Owner = this, Propose = () => ProposeSections(row, measureMap) };
        if (dialog.ShowDialog() == true) ApplyMelodySections(row, map, dialog.Sections);
    }

    private void ApplyMelodySections(MidiRow row, MelodyMeasureMap map, IReadOnlyList<SavedMelodySection> sections)
    {
        if (!IsIdle || selectedFile != row || row.Document is null || row.UsesNativeScoreWorkflow || row.UseReviewedProfile) return;
        if (!string.Equals(map.SourceSha256, row.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O mapa pertence a outra versão do MIDI.");
        var validated = MelodySectionRules.Validate(sections, map, row.Tracks.Select(track => track.Source.Key));
        if (JsonNode.DeepEquals(MelodySectionRules.ToJson(validated), MelodySectionRules.ToJson(row.MelodySections)))
        {
            Status = "As escolhas por trecho foram mantidas. Sua análise continua disponível.";
            return;
        }
        RememberVersion(row);
        row.MelodySections = Settings.CloneMelodySections(validated);
        InvalidateAnalysis(row); RefreshSelection(); NotifyMelodySections(); PersistPreferences();
        Status = "Fontes por trecho atualizadas. Analise os acordes ou gere a partitura para aplicar as escolhas.";
    }
}
