using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    internal Func<MidiFolderWindow, bool?> midiFolderDialog = window => window.ShowDialog();

    private async void OpenMidiFolderClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        string folder = Directory.Exists(settings.MidiFolder) ? settings.MidiFolder : "";
        var library = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files) library[file.FullPath] = file.ScorePath is { } score && File.Exists(score);
        DateTime? lastVisit = folder.Length > 0 && settings.MidiFolderVisits.TryGetValue(Path.GetFullPath(folder), out var visit) ? visit : null;
        var window = new MidiFolderWindow(folder, settings.MidiFolderIncludeSubfolders, library, lastVisit,
            () => { StopTrackAudition(); if (audioPlaying || countingIn) StopPlayback(); }) { Owner = this };
        bool? accepted = midiFolderDialog(window);
        if (Directory.Exists(window.Folder))
        {
            settings.MidiFolder = window.Folder;
            settings.MidiFolderIncludeSubfolders = window.IncludeSubfolders;
            settings.MidiFolderVisits[Path.GetFullPath(window.Folder)] = DateTime.Now;
            PersistPreferences();
        }
        if ((accepted == true || window.Accepted) && window.SelectedPaths.Count > 0) await ImportAndOpenLibraryAsync(window.SelectedPaths);
    }
}
