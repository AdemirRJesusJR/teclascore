using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public sealed record DiagramInversionOption(int Value, string Label);
    public IReadOnlyList<DiagramInversionOption> DiagramInversionOptions { get; } =
        [new(0, "Posição fundamental"), new(1, "1ª inversão"), new(2, "2ª inversão")];
    public int DefaultDiagramInversion
    {
        get => settings.DefaultDiagramInversion;
        set
        {
            int normalized = Settings.NormalizeDiagramInversion(value);
            if (settings.DefaultDiagramInversion == normalized) return;
            settings.DefaultDiagramInversion = normalized;
            Notify();
            PersistPreferences();
        }
    }
    public bool CanEditScoreProperties => IsIdle && selectedFile is not null;
    public string ScorePropertiesSummary => selectedFile is { } row
        ? row.ScoreTitle + (string.IsNullOrWhiteSpace(row.ScoreArtist)?"":" · "+row.ScoreArtist) + " · " + (row.IsNativeScore ? "MuseScore · apresentação e metadados" : (row.Chords.Any(c=>c.DiagramInversion is not null)?"Diagramas personalizados · padrão: ":"") + ScoreMetadataDefaults.InversionLabel(row.EffectiveDiagramInversion) + $" · transposição {row.TransposeSemitones:+0;-0;0}")
          + (row.HasPendingScoreProperties ? " · atualização pendente" : "")
        : "Título, artista, subtítulo, autor e inversão dos diagramas.";

    private async void ScorePropertiesClick(object sender, RoutedEventArgs e)
    {
        if (!CanEditScoreProperties || selectedFile is not { } row || !CommitEdits()) return;
        if(row.IsNativeScore){ImproveNativeScoreClick(sender,e);return;}
        var dialog = new ScorePropertiesWindow(row.ScoreTitle, row.ScoreSubtitle, row.ScoreComposer,
            row.DiagramInversion, row.DefaultDiagramInversion, row.ScoreArtist, row.MetadataOrganizationNotice) { Owner = this };
        var suggestion=ScoreMetadataOrganizer.Suggest(row.FullPath,row.Document?.TextEvents);
        if(suggestion.Title!=row.ScoreTitle || suggestion.Artist!=row.ScoreArtist || (!string.IsNullOrEmpty(suggestion.Composer) && string.IsNullOrEmpty(row.ScoreComposer)))
            dialog.ConfigureMetadataSuggestion(suggestion);
        if (dialog.ShowDialog() != true) return;
        bool changed = row.ScoreTitle != dialog.TitleValue || row.ScoreSubtitle != dialog.SubtitleValue
            || row.ScoreComposer != dialog.ComposerValue || row.ScoreArtist != dialog.ArtistValue || row.DiagramInversion != dialog.InversionOverride;
        if (!changed && !row.HasPendingScoreProperties) return;
        RememberVersion(row);
        row.ScoreTitle = string.IsNullOrWhiteSpace(dialog.TitleValue) ? suggestion.Title : dialog.TitleValue;
        row.ScoreSubtitle = dialog.SubtitleValue;
        row.ScoreComposer = dialog.ComposerValue;
        row.ScoreArtist = dialog.ArtistValue;
        row.MetadataOrganizationNotice = dialog.UsedMetadataSuggestion ? dialog.OrganizationNoticeValue : "";
        row.DiagramInversion = dialog.InversionOverride;
        row.HasPendingScoreProperties = true;
        ResetPlayback();
        RefreshSelection();
        PersistPreferences();

        if (row.Document is null)
        {
            Status = "Propriedades salvas. Reconecte o MIDI original para atualizar a partitura.";
            return;
        }
        if (row.AnalysisPath is null && !row.UseReviewedProfile && !row.Tracks.Any(track => track.IsMelody))
        {
            Status = "Propriedades salvas. Escolha a pista de melodia para gerar a partitura.";
            return;
        }
        await RunOperation(async () =>
        {
            await Generate(row);
            EditorTabs.SelectedIndex = 2;
        }, row);
        PersistPreferences();
    }

}
