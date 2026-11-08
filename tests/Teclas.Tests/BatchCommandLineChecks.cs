using System.Text.Json;
using Teclas.Core;

/// <summary>Arguments of Teclas.Partituras.exe --gerar, the collected MIDIs, the exit codes and the JSON report.</summary>
static class BatchCommandLineChecks
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "teclas-cli-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            // Which invocations belong to the command line (and not to the window or the smoke checks).
            check(BatchCommandLine.IsRequest(["--gerar", "x.mid"]) && BatchCommandLine.IsRequest(["--ajuda"]) && BatchCommandLine.IsRequest(["/?"]), "cli: --gerar and --ajuda are recognized");
            check(!BatchCommandLine.IsRequest([]) && !BatchCommandLine.IsRequest(["--smoke", "a.png"]) && !BatchCommandLine.IsRequest(["--verify-midi-folder-ui", "r.json"]), "cli: window and smokes are left alone");
            check(BatchCommandLine.Parse(["--ajuda"]).ShowHelp && BatchCommandLine.Parse(["--gerar", "a.mid", "-h"]).ShowHelp, "cli: help wins over everything");
            check(BatchCommandLine.Usage.Contains("--gerar") && BatchCommandLine.Usage.Contains("--recursivo") && BatchCommandLine.Usage.Contains("--pdf")
                && BatchCommandLine.Usage.Contains("--saida") && BatchCommandLine.Usage.Contains("--relatorio") && BatchCommandLine.Usage.Contains("Código de saída"), "cli: usage lists every option");

            // A full command: several targets, relative paths resolved against the working folder, every option.
            var full = BatchCommandLine.Parse(["--gerar", "musicas", @"C:\Outros\hino.mid", "musicas", "--recursivo", "--pdf", "pdfs", "--saida", @"D:\Partituras", "--relatorio", "rel.json"], root);
            var o = full.Options;
            check(full.Error is null && !full.ShowHelp && o is not null, "cli: a complete command parses " + full.Error);
            check(o!.Targets.SequenceEqual([Path.Combine(root, "musicas"), @"C:\Outros\hino.mid"]), "cli: targets are absolute and not repeated");
            check(o.Recursive && o.PdfDirectory == Path.Combine(root, "pdfs") && o.OutputDirectory == @"D:\Partituras" && o.ReportPath == Path.Combine(root, "rel.json") && o.SettingsPath is null, "cli: options keep their values");
            var minimal = BatchCommandLine.Parse(["--GERAR", "a.mid"], root).Options!;
            check(!minimal.Recursive && minimal.PdfDirectory is null && minimal.OutputDirectory is null && minimal.ReportPath is null, "cli: options are optional and names ignore case");
            check(BatchCommandLine.Parse(["--recursivo", "--gerar", "pasta"], root).Options?.Recursive == true, "cli: options may come before --gerar");

            // Mistakes get a message in Portuguese instead of a run.
            foreach (var (args, why) in new (string[], string)[]
            {
                (["--gerar"], "no targets"), (["--pdf", "x"], "no --gerar"), (["--gerar", "a.mid", "--pdf"], "missing value"),
                (["--gerar", "a.mid", "--pdf", "--saida", "y"], "value is an option"), (["--gerar", "a.mid", "--cor", "azul"], "unknown option"),
                (["solto.mid"], "loose argument"), (["--gerar", "a.mid", "--pdf", "x", "--pdf", "y"], "repeated option"),
                (["--gerar", "a.mid", "--gerar", "b.mid"], "repeated --gerar"), (["--gerar", "a?.mid"], "invalid path"),
                (["--gerar", "a.mid", "--relatorio", root], "report is a folder"), (["--gerar", "a.mid", "--configuracoes", Path.Combine(root, "nao.json")], "missing settings")
            })
            {
                var parse = BatchCommandLine.Parse(args, root);
                check(parse.Options is null && !parse.ShowHelp && parse.Error is { Length: > 0 }, "cli: rejects " + why);
            }

            // Targets: a folder gives its visible MIDIs (subfolders only with --recursivo); other files are reported.
            string folder = Path.Combine(root, "musicas"), sub = Path.Combine(folder, "Forró");
            Directory.CreateDirectory(sub);
            foreach (var name in new[] { "a.mid", "b.KAR", "c.midi" }) File.WriteAllBytes(Path.Combine(folder, name), [1]);
            File.WriteAllBytes(Path.Combine(sub, "d.mid"), [1]);
            File.WriteAllText(Path.Combine(folder, "letra.txt"), "x");
            string loose = Path.Combine(root, "solto.mid"); File.WriteAllBytes(loose, [1]);
            var problems = new List<BatchFileReport>();
            var flat = BatchCommandLine.CollectMidis(new([folder, loose, Path.Combine(folder, "a.mid"), Path.Combine(folder, "letra.txt"), Path.Combine(root, "sumiu.mid")], false, null, null, null, null), problems);
            check(flat.Count == 4 && flat.Contains(loose) && !flat.Any(p => p.EndsWith("d.mid")), "cli: a folder gives its MIDIs without subfolders and without repeats");
            check(problems.Count == 2 && problems.All(p => p.Situacao == BatchFileReport.Failed && p.Motivo is { Length: > 0 }), "cli: other files and missing ones are reported");
            var deep = BatchCommandLine.CollectMidis(new([folder], true, null, null, null, null), []);
            check(deep.Count == 4 && deep.Any(p => p.EndsWith("d.mid")), "cli: --recursivo includes the subfolders");
            var empty = new List<BatchFileReport>();
            check(BatchCommandLine.CollectMidis(new([sub + "-vazia"], false, null, null, null, null), empty).Count == 0 && empty.Count == 1, "cli: a missing folder is reported");

            // Exit codes and the report.
            BatchFileReport F(string situation, string? pdfError = null) => new("x.mid", situation, PdfErro: pdfError);
            check(BatchCommandLine.ExitCode([F(BatchFileReport.Ready), F(BatchFileReport.AlreadyGenerated)]) == 0, "cli: exit 0 when everything is ready");
            check(BatchCommandLine.ExitCode([F(BatchFileReport.Ready), F(BatchFileReport.Review)]) == 2 && BatchCommandLine.ExitCode([F(BatchFileReport.NeedsYou)]) == 2, "cli: exit 2 when something waits for review");
            check(BatchCommandLine.ExitCode([F(BatchFileReport.Review), F(BatchFileReport.Failed)]) == 1 && BatchCommandLine.ExitCode([F(BatchFileReport.Ready, "sem MuseScore")]) == 1
                && BatchCommandLine.ExitCode([]) == 1 && BatchCommandLine.ExitCode([F(BatchFileReport.Ready)], "erro geral") == 1, "cli: exit 1 on errors and failures");
            var files = new[] { new BatchFileReport(@"C:\m\Canção.mid", BatchFileReport.Ready, "Canção", @"C:\p\Canção.mscz", @"C:\p\Canção.pdf", "confiança 92%"), F(BatchFileReport.NeedsYou) };
            var report = BatchRunReport.Create("1.3.0", DateTimeOffset.Now.AddMinutes(-1), files);
            string json = Path.Combine(root, "saida", "relatorio.json");
            BatchCommandLine.WriteReport(report, json);
            using var parsed = JsonDocument.Parse(File.ReadAllText(json));
            var top = parsed.RootElement;
            var first = top.GetProperty("arquivos")[0];
            check(top.GetProperty("codigoSaida").GetInt32() == 2 && top.GetProperty("resumo").GetProperty("prontas").GetInt32() == 1 && top.GetProperty("resumo").GetProperty("pdfs").GetInt32() == 1
                && top.GetProperty("erro").ValueKind == JsonValueKind.Null, "cli: report has the exit code and the summary");
            check(first.GetProperty("situacao").GetString() == "pronta" && first.GetProperty("partitura").GetString() == @"C:\p\Canção.mscz" && first.GetProperty("pdf").GetString() == @"C:\p\Canção.pdf"
                && first.GetProperty("motivo").GetString() == "confiança 92%" && File.ReadAllText(json).Contains("Canção"), "cli: report lists each file with readable accents");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
