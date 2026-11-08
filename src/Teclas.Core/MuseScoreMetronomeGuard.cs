using System.Text;
using System.Text.RegularExpressions;

namespace Teclas.Core;

/// <summary>
/// MuseScore mixes its own metronome into exported audio when it was left on in MuseScore (Reproduzir › Metrônomo,
/// saved as <c>playback\metronomeEnabled</c> in MuseScore4.ini). TeclaScore adds clicks only when its own option asks,
/// so every audio export runs with that preference off, and it is switched back on when the last export finishes.
/// A marker file remembers a pending restore if TeclaScore closes in the middle of an export.
/// </summary>
public static class MuseScoreMetronomeGuard
{
    private const string Key = @"playback\metronomeEnabled=";
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static int users;
    private static bool switchedOff;

    public static string SettingsPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MuseScore", "MuseScore4.ini");
    public static string MarkerPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeclasPartituras", "musescore-metronomo-a-restaurar");

    /// <summary>Keeps MuseScore's metronome off until the returned lease is disposed.</summary>
    public static async Task<IAsyncDisposable> SilenceAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (users++ == 0)
            {
                bool changed = TrySet(from: true, to: false);
                if (changed) TryWriteMarker();
                switchedOff = changed || File.Exists(MarkerPath);
            }
        }
        finally { gate.Release(); }
        return new Lease();
    }

    /// <summary>Restores a preference left off by an export that did not finish (for example, TeclaScore was closed).</summary>
    public static void RestorePending()
    {
        gate.Wait();
        try { if (users == 0 && File.Exists(MarkerPath)) Restore(); }
        finally { gate.Release(); }
    }

    private static async Task ReleaseAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (--users == 0 && switchedOff) Restore(); }
        finally { gate.Release(); }
    }

    private static void Restore()
    {
        // Only a value still "false" goes back on: a change the user made in MuseScore meanwhile wins.
        TrySet(from: false, to: true);
        try { File.Delete(MarkerPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        switchedOff = false;
    }

    private static bool TrySet(bool from, bool to)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            string text; Encoding encoding;
            using (var reader = new StreamReader(SettingsPath, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
            { text = reader.ReadToEnd(); encoding = reader.CurrentEncoding; }
            var pattern = new Regex("(?m)^" + Regex.Escape(Key) + (from ? "true" : "false") + @"(?=\r?$)");
            if (!pattern.IsMatch(text)) return false;
            string temporary = SettingsPath + ".teclas-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, pattern.Replace(text, Key + (to ? "true" : "false"), 1), encoding);
            File.Move(temporary, SettingsPath, true);
            return true;
        }
        // Without access to MuseScore's settings the export still runs, as before this guard existed.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void TryWriteMarker()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!); File.WriteAllText(MarkerPath, SettingsPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private int disposed;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0 ? new(ReleaseAsync()) : ValueTask.CompletedTask;
    }
}
