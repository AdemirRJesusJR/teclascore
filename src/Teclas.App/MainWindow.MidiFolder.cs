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
            () => { StopTrackAudition(); if (audioPlaying || countingIn) StopPlayback(); }, null,
            settings.WatchMidiFolder, settings.WatchMidiFolderAutopilot) { Owner = this };
        bool? accepted = midiFolderDialog(window);
        string previousFolder = settings.MidiFolder;
        bool previousSubfolders = settings.MidiFolderIncludeSubfolders, wasWatching = settings.WatchMidiFolder, previousAutopilot = settings.WatchMidiFolderAutopilot;
        if (Directory.Exists(window.Folder))
        {
            settings.MidiFolder = window.Folder;
            settings.MidiFolderIncludeSubfolders = window.IncludeSubfolders;
            settings.MidiFolderVisits[Path.GetFullPath(window.Folder)] = DateTime.Now;
            settings.WatchMidiFolder = window.WatchFolder;
            settings.WatchMidiFolderAutopilot = window.WatchAutopilot;
            PersistPreferences();
        }
        else if (!window.WatchFolder && wasWatching) { settings.WatchMidiFolder = false; PersistPreferences(); }
        bool sameFolder = SamePath(previousFolder, settings.MidiFolder) && previousSubfolders == settings.MidiFolderIncludeSubfolders;
        if (settings.WatchMidiFolder != wasWatching || settings.WatchMidiFolder && !sameFolder || settings.WatchMidiFolderAutopilot != previousAutopilot)
        {
            NotifyFolderWatch();
            // A new or different watched folder starts from what it has now: nothing already there is imported in bulk.
            if (settings.WatchMidiFolder && (!wasWatching || !sameFolder)) await StartFolderWatchAsync(catchUp: false);
            else if (!settings.WatchMidiFolder) StopFolderWatch(announce: true);
            else AnnounceFolderWatch();
        }
        if ((accepted == true || window.Accepted) && window.SelectedPaths.Count > 0) await ImportAndOpenLibraryAsync(window.SelectedPaths);
    }

    private static bool SamePath(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return string.IsNullOrWhiteSpace(first) == string.IsNullOrWhiteSpace(second);
        try { return string.Equals(Path.GetFullPath(first).TrimEnd('\\'), Path.GetFullPath(second).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
