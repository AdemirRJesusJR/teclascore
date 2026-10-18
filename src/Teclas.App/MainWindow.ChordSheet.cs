using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    internal Func<ChordSheetWindow, bool?> chordSheetDialog = window => window.ShowDialog();
    /// <summary>Test hook: replaces the engine run for a chord sheet score (request → score path).</summary>
    internal Func<JsonObject, CancellationToken, Task<string>>? chordSheetEngine = null;

    /// <summary>Position remembered for a chord in the library (null: the default position).</summary>
    internal int? RememberedInversion(string root, string quality) =>
        DiagramPositionPropagation.Shape(root, quality, 0) is { } shape && chordPreferences.TryGetValue(shape, out int inversion) ? inversion : null;

    private void ChordSheetClick(object sender, RoutedEventArgs e)
    {
        var window = new ChordSheetWindow(settings.DefaultDiagramInversion, RememberedInversion) { Owner = this, createScore = CreateChordSheetScoreAsync };
        window.RefreshActions();
        chordSheetDialog(window);
    }

    /// <summary>The engine request for a chord sheet score: one measure per chord, words and line starts, remembered positions.</summary>
    internal JsonObject ChordSheetRequest(IReadOnlyList<SheetMeasure> measures, string title, string workspace, string outputDirectory) => new()
    {
        ["mode"] = "chordSheet", ["workspace"] = workspace, ["inputMidi"] = "", ["scoreTitle"] = title, ["beats"] = 4,
        ["diagramInversion"] = settings.DefaultDiagramInversion, ["musescorePath"] = MuseScorePath, ["outputDirectory"] = outputDirectory,
        ["noteNames"] = settings.NoteNames == "solfejo" ? "solfejo" : "letras",
        ["measures"] = new JsonArray(measures.Select(m => (JsonNode)new JsonObject
        {
            ["root"] = m.Chord.Root, ["quality"] = m.Chord.Quality, ["bass"] = m.Chord.Bass, ["words"] = m.Words, ["lineStart"] = m.LineStart,
            ["diagramInversion"] = RememberedInversion(m.Chord.Root, m.Chord.Quality)
        }).ToArray())
    };

    /// <summary>Creates the score in the output folder and opens it in the library. Returns false when it failed.</summary>
    internal async Task<bool> CreateChordSheetScoreAsync(IReadOnlyList<SheetMeasure> measures, string title)
    {
        string? scorePath = null;
        await RunOperation(async () =>
        {
            ValidateSettings(true);
            string output = await PrepareOutputDirectoryAsync(cancellation!.Token);
            string workspace = Path.Combine(persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!, "jobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var request = ChordSheetRequest(measures, title, workspace, output);
            Status = "Criando a partitura da cifra…";
            if (chordSheetEngine is { } run) scorePath = await run(request, cancellation!.Token);
            else
            {
                var result = await new EngineClient(PythonPath, engineDirectory).RunAsync(request, workspace, CreateOperationProgress(), cancellation!.Token);
                scorePath = result["scorePath"]!.GetValue<string>();
            }
        }, operationTitle: "Criando partitura da cifra");
        if (scorePath is null || !File.Exists(scorePath)) return false;
        await ImportAndOpenLibraryAsync([scorePath]);
        Status = $"Partitura da cifra criada com {measures.Count} acordes: {Path.GetFileName(scorePath)}.";
        return true;
    }
}
