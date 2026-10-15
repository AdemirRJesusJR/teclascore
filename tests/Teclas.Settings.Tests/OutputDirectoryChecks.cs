using Teclas.App;

internal static class OutputDirectoryChecks
{
    public static void Run(string folder, Action<bool, string> check)
    {
        string settingsPath = Path.Combine(folder, "output-preference.json");
        var fresh = Settings.Load(settingsPath);
        check(fresh.OutputDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Partituras geradas"),
            "Fresh installations retain the established product output default");
        string next = Path.Combine(folder, "Saída padrão 🎹", "próximas partituras");
        check(Settings.TryValidateOutputDirectory("  " + next + "  ", out string normalized, out string error)
            && normalized == next && error == "", "The output preference accepts Unicode and normalizes surrounding whitespace");
        check(!Directory.Exists(next), "Validating a new output folder does not create it");
        string oldScore = Path.Combine(folder, "existing-score.mscz"), oldPreview = Path.Combine(folder, "existing-preview.png");
        File.WriteAllBytes(oldScore, [1, 4, 9]); File.WriteAllBytes(oldPreview, [8, 5, 2]);
        fresh.OutputDirectory = normalized;
        fresh.Library.Add(new SavedMidi { FullPath = Path.Combine(folder, "existing.mid"), ScorePath = oldScore,
            PreviewPath = oldPreview, Versions = [new SavedScoreVersion { ScorePath = oldScore }] });
        fresh.Save(settingsPath);
        var loaded = Settings.Load(settingsPath);
        check(loaded.OutputDirectory == next, "The output preference persists before the destination exists");
        check(!Directory.Exists(next), "Saving and reopening preferences do not create the output folder");
        check(loaded.Library[0].ScorePath == oldScore && loaded.Library[0].PreviewPath == oldPreview
            && loaded.Library[0].Versions[0].ScorePath == oldScore, "Changing the output preference preserves existing score, preview and history paths");
        check(File.ReadAllBytes(oldScore).SequenceEqual(new byte[] { 1, 4, 9 }) && File.ReadAllBytes(oldPreview).SequenceEqual(new byte[] { 8, 5, 2 }),
            "Changing the output preference does not modify existing files");
        foreach (string? invalid in new string?[] { null, "", "  ", "relative", @"C:relative", @"\rooted", @"C:\bad?path",
            @"C:\bad|path", @"C:\bad:path", "C:\\bad\0path", @"C:\CON", @"C:\nul.txt", @"C:\COM1\scores",
            @"C:\LPT³.txt", @"C:\NUL .txt", @"C:\trailing.\scores", @"C:\trailing \scores", @"\\?\C:\scores", @"\\.\C:\scores",
            @"\\server", @"C:\" + new string('a', 256) })
        {
            check(!Settings.TryValidateOutputDirectory(invalid, out var rejected, out var message) && rejected == "" && message.Length > 0,
                "An invalid output path is rejected without a usable destination: " + (invalid ?? "<null>"));
        }
        check(Settings.TryValidateOutputDirectory(@"\\unavailable-server\share\scores", out _, out _),
            "A disconnected network destination remains a valid saved preference without probing the network");
        check(Settings.TryValidateOutputDirectory(@"Z:\offline\scores", out _, out _),
            "A disconnected drive remains a valid saved preference without probing the drive");
        loaded.OutputDirectory = @"Z:\offline\scores"; loaded.Save(settingsPath);
        check(Settings.Load(settingsPath).OutputDirectory == @"Z:\offline\scores", "An unavailable saved destination is not replaced with a different default");
    }
}
