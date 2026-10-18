using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    internal Func<MelodySearchWindow, bool?> melodySearchDialog = window => window.ShowDialog();

    /// <summary>Melody used for search: the chosen melody parts, else the best suggested part, else the whole MIDI's top voice.</summary>
    internal static IReadOnlyList<int> SearchMelody(MidiRow row)
    {
        if (row.Document is not { } document) return [];
        var chosen = row.Tracks.Where(track => track.IsMelody).Select(track => track.Source.Key).ToHashSet();
        if (chosen.Count == 0 && row.Tracks.OrderBy(track => track.MelodySuggestion?.Rank ?? int.MaxValue).FirstOrDefault() is { MelodySuggestion: not null } best)
            chosen.Add(best.Source.Key);
        var notes = chosen.Count == 0 ? document.Notes : document.Notes.Where(note => chosen.Contains(new SourceKey(note.Track, note.Channel, note.Program)));
        return MelodySearch.Contour(notes);
    }

    private void MelodySearchClick(object sender, RoutedEventArgs e)
    {
        var songs = Files.Where(row => row.Document is not null).Select(row => (row.FullPath, Title: row.ScoreTitle.Length > 0 ? row.ScoreTitle : row.Name, SearchMelody(row)))
            .Where(song => song.Item3.Count >= MelodySearch.MinimumNotes).ToArray();
        if (songs.Length == 0) { Status = "Nenhum MIDI da biblioteca tem melodia para buscar."; return; }
        var dialog = new MelodySearchWindow(songs) { Owner = this };
        if (melodySearchDialog(dialog) != true || dialog.ChosenId is not { } id) return;
        if (Files.FirstOrDefault(row => row.FullPath == id) is { } card)
        { SetCurrentPage("library"); Library.SelectedItem = card; Library.ScrollIntoView(card); Status = $"Encontrada: {card.Name}."; }
    }
}
