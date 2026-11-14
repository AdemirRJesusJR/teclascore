using System.IO;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// MusicXML and Guitar Pro in the library: MuseScore plays each file into a MIDI saved in the output folder
/// ("Convertidos para MIDI"), with one track per instrument, and that MIDI follows the usual steps (Melodia, Ritmo,
/// Cifras, Gerar). The original file is only read. A file converted before is found by its content and reused.
/// </summary>
public partial class MainWindow
{
    /// <summary>File-dialog filter shared by "Adicionar" and the MIDI folder window.</summary>
    internal static string LibraryImportFilter =>
        $"Músicas (MIDI, MusicXML, Guitar Pro, MuseScore)|*.mid;*.midi;*.kar;{NotationSource.MusicXmlPattern};{NotationSource.GuitarProPattern};*.mscz" +
        $"|MIDI e karaokê|*.mid;*.midi;*.kar|MusicXML|{NotationSource.MusicXmlPattern}|Guitar Pro|{NotationSource.GuitarProPattern}|MuseScore 4|*.mscz";

    /// <summary>Test hook: replaces MuseScore's part of the conversion (source, new MIDI) → writes the MIDI.</summary>
    internal Func<string, string, CancellationToken, Task>? notationMidiExporter = null;
    internal Task ImportFilesForSmoke(IEnumerable<string> paths) => ImportAndOpenLibraryAsync(paths);
    private int notationConverted, notationReused;
    private readonly List<string> notationFailures = [];

    /// <summary>
    /// Runs inside the import operation. Returns the chosen paths with every MusicXML or Guitar Pro file replaced by
    /// its MIDI; a file that could not be converted is left out and reported after the import.
    /// </summary>
    private async Task<IReadOnlyList<string>> ConvertNotationSourcesAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        notationConverted = notationReused = 0; notationFailures.Clear();
        var sources = paths.Where(NotationSource.IsSupported).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sources.Length == 0) return paths;
        // The chosen order is kept: each source gives its place to its MIDI.
        var midis = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> Chosen() => paths.Select(path => !NotationSource.IsSupported(path) ? path : midis.GetValueOrDefault(Path.GetFullPath(path))).OfType<string>().ToArray();
        if (notationMidiExporter is null && !File.Exists(MuseScorePath))
        {
            notationFailures.Add("Localize o MuseScore Studio 4 em Configurações: é ele que converte MusicXML e Guitar Pro em MIDI.");
            return Chosen();
        }
        string folder;
        try { folder = Path.Combine(await PrepareOutputDirectoryAsync(token), NotationSource.Folder); Directory.CreateDirectory(folder); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        { notationFailures.Add("A pasta de saída não está disponível para guardar os MIDIs convertidos: " + error.Message); return Chosen(); }
        var taken = new HashSet<string>(Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var exporter = new ScoreExporter(MuseScorePath, PlaybackCacheDirectory);
        for (int i = 0; i < sources.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            string source = sources[i], name = Path.GetFileName(source);
            SetOperationItems(i, sources.Length, name);
            try
            {
                if (!File.Exists(source)) throw new FileNotFoundException("o arquivo não foi encontrado.");
                if (!NotationSource.IsScoreFile(source)) throw new InvalidDataException("este .xml não é uma partitura MusicXML.");
                if (NotationSource.FindConverted(folder, await Task.Run(() => NotationSource.Sha256(source), token)) is { } ready)
                { midis[source] = ready; notationReused++; continue; }
                Status = $"Convertendo {name} ({NotationSource.KindName(source)}) em MIDI no MuseScore…";
                string target = Path.Combine(folder, ScoreFileName.Unique(ScoreFileName.Stem(null, Path.GetFileNameWithoutExtension(source)), ".mid", taken));
                await NotationSource.ConvertAsync(notationMidiExporter ?? exporter.ExportMidiAsync, source, target, token);
                midis[source] = target; notationConverted++;
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException)
            {
                string reason = error.Message.ReplaceLineEndings(" ").Trim();
                notationFailures.Add($"{name}: {(reason.Length > 260 ? reason[..260] + "…" : reason)}");
            }
        }
        return Chosen();
    }

    /// <summary>After the cards entered: says what was converted and shows what could not be (a status line alone is easy to miss).</summary>
    private void ReportNotationImport()
    {
        int converted = notationConverted + notationReused, reused = notationReused; var failed = notationFailures.ToArray();
        // Nothing is carried to the next import, whatever road this one took.
        notationConverted = notationReused = 0; notationFailures.Clear();
        if (converted > 0)
            Status += $" MusicXML e Guitar Pro: {converted} arquivo(s) em MIDI na pasta \"{NotationSource.Folder}\""
                + (reused > 0 ? $" ({reused} já convertido(s) antes)" : "") + "; os originais não mudaram.";
        if (failed.Length == 0) return;
        string failures = string.Join("\n", failed.Take(8)) + (failed.Length > 8 ? $"\n… e mais {failed.Length - 8}." : "");
        Status = (converted > 0 ? Status + " " : "") + $"{failed.Length} arquivo(s) MusicXML ou Guitar Pro não entraram: " + failed[0];
        showFailure("Importar MusicXML ou Guitar Pro", "Não foi possível converter:\n\n" + failures
            + "\n\nAbra o arquivo no MuseScore para conferir se ele é lido; o original não foi alterado.");
    }
}
