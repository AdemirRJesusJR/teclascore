using Teclas.App;

var directory = Path.Combine(Path.GetTempPath(), "TeclaScore-viewer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var checks = 0;
void Check(bool result, string name)
{
    if (!result) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine($"OK {name}");
}
void Touch(string name) => File.WriteAllText(Path.Combine(directory, name), "fixture");
try
{
    Check(ScorePageFiles.Discover(null).Count == 0, "Sem partitura");
    Check(ScorePageFiles.Discover("  ").Count == 0, "Caminho vazio");
    Check(ScorePageFiles.Discover(Path.Combine(directory, "ausente", "pagina-1.png")).Count == 0, "Pasta ausente");
    foreach (var name in new[] { "pagina-10.png", "pagina-2.png", "pagina-1.png", "pagina-3.PNG", "outra-1.png", "pagina-4-backup.png", "pagina-5.txt", "pagina-0.png", "pagina--2.png", "pagina-9999999999999999999999999.png" }) Touch(name);
    var files = ScorePageFiles.Discover(Path.Combine(directory, "pagina-1.png"));
    Check(files.Select(Path.GetFileName).SequenceEqual(new[] { "pagina-1.png", "pagina-2.png", "pagina-3.PNG", "pagina-10.png" }), "Ordem numérica; exclui outros arquivos, backups e números inválidos");
    Check(ScorePageFiles.Discover(Path.Combine(directory, "pagina-9.png")).Count == 4, "Primeira página ausente não impede ler as demais");
    Check(ScorePageFiles.Discover(Path.Combine(directory, "PAGINA-1.png")).Count == 4, "Prefixo sem distinção de maiúsculas");
    Touch("Canção 2026 - Show-1.png");
    Touch("Canção 2026 - Show-11.png");
    Touch("Canção 2026 - Show-2.png");
    Check(ScorePageFiles.Discover(Path.Combine(directory, "Canção 2026 - Show-1.png")).Select(Path.GetFileName).SequenceEqual(new[] { "Canção 2026 - Show-1.png", "Canção 2026 - Show-2.png", "Canção 2026 - Show-11.png" }), "Nome Unicode, espaços e hífens do MuseScore");
    Touch("unica.png");
    Check(ScorePageFiles.Discover(Path.Combine(directory, "unica.png")).Count == 1, "Página única sem numeração");
    Check(ScorePageFiles.Discover(Path.Combine(directory, "ausente.png")).Count == 0, "Página única ausente");
    Touch("export_1.png");
    Touch("export_2.png");
    Check(ScorePageFiles.Discover(Path.Combine(directory, "export_1.png")).Count == 2, "Numeração com sublinhado");
    Console.WriteLine($"{checks} verificações concluídas.");
}
finally { Directory.Delete(directory, true); }
