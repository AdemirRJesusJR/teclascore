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
        var window = new MidiFolderWindow(folder, settings.MidiFolderIncludeSubfolders, Files.Select(f => f.FullPath),
            () => { StopTrackAudition(); if (audioPlaying || countingIn) StopPlayback(); }) { Owner = this };
        bool? accepted = midiFolderDialog(window);
        if (Directory.Exists(window.Folder))
        {
            settings.MidiFolder = window.Folder;
            settings.MidiFolderIncludeSubfolders = window.IncludeSubfolders;
            PersistPreferences();
        }
        if ((accepted == true || window.Accepted) && window.SelectedPaths.Count > 0) await ImportAndOpenLibraryAsync(window.SelectedPaths);
    }
}
