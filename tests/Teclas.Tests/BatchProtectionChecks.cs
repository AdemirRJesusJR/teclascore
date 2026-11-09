using System.Text.Json;
using Teclas.Core;

/// <summary>Protections for batch work: the preview groups, the time estimate, the batch log and the import duplicates.</summary>
static class BatchProtectionChecks
{
    public static void Run(Action<bool, string> check)
    {
        // The preview: groups, headline and the text printed by --simular.
        var items = new List<BatchPreviewItem>
        {
            new("a", "Asa Branca", BatchPreview.Generate, "melodia clara", true), new("b", "Xote das Meninas", BatchPreview.Generate, "", true),
            new("c", "Anunciação", BatchPreview.SkipApproved, "", false), new("d", "Chão de Giz", BatchPreview.NeedsYou, "melodia sem escolha clara", false),
            new("e", "Lamento", BatchPreview.AlreadyGenerated, "aprovada", false)
        };
        var preview = new BatchPreview("Piloto automático", items, 95, 3);
        check(preview.Count(BatchPreview.Generate) == 2 && preview.ActiveCount == 2 && preview.HasWork && preview.Of(BatchPreview.NeedsYou).Single().Detail.StartsWith("melodia"), "preview: groups and counts");
        check(preview.Headline() == "Vai gerar 2 · pula 1 aprovada · 1 precisa de você · 1 já gerada", "preview: headline " + preview.Headline());
        check(preview.EstimateText().Contains("cerca de 2 min") && preview.EstimateText().Contains("3 gerações"), "preview: estimate text " + preview.EstimateText());
        string text = preview.ToText();
        check(text.StartsWith("O que vai acontecer · Piloto automático · 5 músicas") && text.Contains("Vai gerar (2)") && text.Contains("• Chão de Giz — melodia sem escolha clara") && text.Contains("Pula · aprovada (1)"), "preview: text report");
        check(new BatchPreview("x", [items[2]], null, 0).Headline() == "pula 1 aprovada" && !new BatchPreview("x", [items[2]], null, 0).HasWork, "preview: nothing to do when only approved");
        check(new BatchPreview("x", [], null, 0).Headline() == "Nada a fazer.", "preview: empty");
        check(new BatchPreview("x", items.Take(2).ToList(), null, 0).EstimateText().Contains("sem medições"), "preview: no samples yet");

        // The estimate: the median of successful engine generations, scaled by the parallelism.
        PerformanceRecord Record(PerformanceOperation operation, double seconds, PerformanceOutcome outcome = PerformanceOutcome.Success) =>
            new(DateTimeOffset.UtcNow, operation, PerformancePhase.Process, PerformanceProcessKind.Worker, outcome, seconds * 1000, null, null, 0, new Dictionary<PerformanceCount, long>());
        var (median, samples) = BatchEstimate.MedianGenerationSeconds([Record(PerformanceOperation.EngineGenerate, 20), Record(PerformanceOperation.EngineGenerate, 100), Record(PerformanceOperation.EngineGenerate, 30),
            Record(PerformanceOperation.EngineGenerate, 5, PerformanceOutcome.Failure), Record(PerformanceOperation.EngineAnalyze, 1)]);
        check(median == 30 && samples == 3, $"estimate: median ignores failures and other operations ({median}, {samples})");
        var (even, _) = BatchEstimate.MedianGenerationSeconds([Record(PerformanceOperation.EngineGenerate, 10), Record(PerformanceOperation.EngineGenerate, 20)]);
        check(even == 15, "estimate: even count averages the middle pair");
        var (app, appSamples) = BatchEstimate.MedianGenerationSeconds([Record(PerformanceOperation.AppGenerate, 40)]);
        check(app == 40 && appSamples == 1, "estimate: falls back to the app's generations");
        check(BatchEstimate.MedianGenerationSeconds([]).Median is null, "estimate: no records, no median");
        check(BatchEstimate.Seconds(30, 10, 1) == 300 && BatchEstimate.Seconds(30, 10, 4) == 90 && BatchEstimate.Seconds(null, 10, 2) is null && BatchEstimate.Seconds(30, 0, 2) is null, "estimate: seconds by parallelism");
        check(BatchEstimate.Format(30) == "menos de 1 min" && BatchEstimate.Format(150) == "cerca de 3 min" && BatchEstimate.Format(3600) == "cerca de 1 h" && BatchEstimate.Format(5400) == "cerca de 1 h 30 min", "estimate: format");

        string root = Path.Combine(Path.GetTempPath(), "teclas-lotes-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // The batch log: one file per batch, readable back, "nada foi apagado" or the changes.
            var store = new BatchLogStore(Path.Combine(root, "lotes"));
            var start = new DateTimeOffset(2026, 11, 9, 14, 2, 0, TimeSpan.FromHours(-3));
            var entry = BatchLogEntry.Create("1.7.0", "Piloto automático", start,
                [new("Asa Branca", @"C:\m\asa.mid", "Prontas", "92%", @"C:\p\asa.mscz"), new("Anunciação", @"C:\m\anu.mid", "pulada · aprovada"), new("Xote", @"C:\m\xote.mid", "Prontas")],
                [], "Antes de: Piloto automático · 09/11 14:02 · 3 músicas");
            check(entry.NadaApagado && entry.Total == 3 && entry.Resumo["Prontas"] == 2 && entry.Resumo["pulada · aprovada"] == 1 && entry.ChangesText() == "Nada foi apagado.", "log: entry summary");
            string path = store.Write(entry);
            check(Path.GetFileName(path) == "2026-11-09-140200.json" && File.Exists(path), "log: file named by the start time " + path);
            string json = File.ReadAllText(path);
            check(json.Contains("\"nadaApagado\": true") && json.Contains("\"operacao\": \"Piloto automático\"") && json.Contains("Anunciação") && json.Contains("\"pontoDeRestauracao\""), "log: camelCase JSON with readable accents");
            string second = store.Write(entry with { Mudancas = ["2 cards saíram da biblioteca"], NadaApagado = false });
            check(Path.GetFileName(second) == "2026-11-09-140200-2.json", "log: a second batch in the same second gets a suffix");
            var read = store.ReadRecent();
            check(read.Count == 2 && read[0].Mudancas.Count == 1 && !read[0].NadaApagado && read[0].ChangesText().StartsWith("O que mudou: 2 cards") && read[1].NadaApagado && read[1].Itens.Count == 3, "log: read back newest first");
            File.WriteAllText(Path.Combine(root, "lotes", "2026-11-10-000000.json"), "{ quebrado");
            check(store.ReadRecent().Count == 2, "log: an unreadable file is skipped");
            check(new BatchLogStore(Path.Combine(root, "vazio")).ReadRecent().Count == 0, "log: missing folder reads as empty");
            check(BatchLogStore.DefaultDirectory.EndsWith(Path.Combine("TeclasPartituras", "lotes")), "log: default folder under the app data");

            // Restore points: labels, newest first, the newest five kept (older packages and extracted folders deleted).
            var points = new RestorePointStore(Path.Combine(root, "pontos"));
            check(RestorePointStore.DefaultDirectory.EndsWith(Path.Combine("TeclasPartituras", "pontos-de-restauracao")), "points: default folder");
            var created = new List<RestorePointInfo>();
            for (int i = 0; i < 7; i++)
            {
                string id = points.NewId(start.AddMinutes(i)); string package = points.PackagePath(id);
                Directory.CreateDirectory(points.Directory); File.WriteAllBytes(package, [1, 2, 3]);
                Directory.CreateDirectory(points.RestoreDirectory(id));
                var info = new RestorePointInfo(id, i == 0 ? "Piloto automático" : "Gerar as marcadas", start.AddMinutes(i), 168, i, package);
                var removed = points.Register(info); created.Add(info);
                check(removed.Count == (i >= RestorePointStore.Kept ? 1 : 0), $"points: register {i} prunes {removed.Count}");
            }
            check(created[0].Label == "Antes de: Piloto automático · 09/11 14:02 · 168 músicas", "points: label " + created[0].Label);
            var listed = points.List();
            check(listed.Count == RestorePointStore.Kept && listed[0].Id == created[6].Id && listed[^1].Id == created[2].Id, "points: newest five, newest first");
            check(!File.Exists(created[0].Package) && !Directory.Exists(points.RestoreDirectory(created[0].Id)) && File.Exists(created[2].Package), "points: pruned packages and folders are deleted");
            File.Delete(created[6].Package);
            check(points.List().Count == RestorePointStore.Kept - 1 && points.List().All(p => p.IsAvailable), "points: a missing package is not listed");
            points.Remove(created[5].Id);
            check(points.List().Count == RestorePointStore.Kept - 2 && !File.Exists(created[5].Package), "points: remove deletes the package");
            bool rejected = false;
            try { points.Register(new RestorePointInfo("x", "op", start, 1, 0, Path.Combine(root, "fora.teclasbackup"))); } catch (ArgumentException) { rejected = true; }
            check(rejected, "points: a package outside the folder is rejected");
            try { points.PackagePath("../fora"); rejected = false; } catch (ArgumentException) { rejected = true; }
            check(rejected, "points: an unsafe id is rejected");
            check(new RestorePointInfo("a", "op", start, 1, 0, "x").Label.EndsWith("1 música"), "points: singular label");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }

        // Import duplicates: same content first, then same title and artist; files already in the library by path are no conflict.
        var library = new List<ImportLibraryEntry>
        {
            new("1", @"C:\lib\asa.mid", "AAA", "Asa Branca", "Luiz Gonzaga", true), new("2", @"C:\lib\xote.mid", "BBB", "Xote das Meninas", "", false),
            new("3", @"C:\lib\asa2.mid", "CCC", "Asa Branca", "Luiz Gonzaga", false)
        };
        var found = ImportDuplicateDetector.Find(
        [
            new(@"C:\novo\copia.mid", "AAA", "Outra coisa", ""), new(@"C:\novo\Asa branca (1).mid", "DDD", "Asa Branca", "Luiz Gonzaga"),
            new(@"C:\novo\xote.mid", "EEE", "Xote das Meninas", "Dominguinhos"), new(@"C:\lib\xote.mid", "BBB", "Xote das Meninas", ""), new(@"C:\novo\nova.mid", "FFF", "Nova", "")
        ], library);
        check(found.Count == 2, "duplicates: two conflicts " + found.Count);
        check(found[0].Reason == ImportDuplicateDetector.SameContent && found[0].Existing.Id == "1", "duplicates: same content wins and names the card");
        check(found[1].Reason == ImportDuplicateDetector.SameTitleArtist && found[1].Existing.Id == "1", "duplicates: same title and artist prefers the card with a score");
        check(ImportDuplicateDetector.Key("Asa Branca (1)", "Luiz Gonzaga") == ImportDuplicateDetector.Key("asa branca", "LUIZ GONZAGA") && ImportDuplicateDetector.Key("", "x") == "", "duplicates: key ignores case, accents and copy marks");
        check(ImportDuplicateDetector.Key("Asa Branca", "") != ImportDuplicateDetector.Key("Asa Branca", "Luiz Gonzaga"), "duplicates: the artist is part of the key");

        // --simular on the command line.
        var simulate = BatchCommandLine.Parse(["--gerar", "a.mid", "--simular"], Path.GetTempPath());
        check(simulate.Options is { Simulate: true } && BatchCommandLine.Parse(["--gerar", "a.mid"], Path.GetTempPath()).Options is { Simulate: false }, "cli: --simular is parsed");
        check(BatchCommandLine.Usage.Contains("--simular") && BatchCommandLine.Usage.Contains("ponto de restauração"), "cli: usage explains --simular and the restore point");
        check(BatchCommandLine.ExitCode([new("x.mid", BatchFileReport.Simulated)]) == 0, "cli: a simulation exits 0");
        var simulatedReport = BatchRunReport.Create("1.7.0", DateTimeOffset.Now, [new("x.mid", BatchFileReport.Simulated, "X", null, null, "Vai gerar · melodia clara")]);
        check(simulatedReport.Resumo.Prontas == 1 && JsonSerializer.Deserialize<JsonElement>(BatchCommandLine.ToJson(simulatedReport)).GetProperty("arquivos")[0].GetProperty("situacao").GetString() == "simulada", "cli: simulated files in the report");
    }
}
