using System.IO;
using Teclas.Core;

namespace Teclas.App;

public static class DependencyDiagnostics
{
    public static async Task<string> CheckAsync(string pythonPath, string museScorePath, CancellationToken cancellationToken = default)
    {
        var preparation = new DependencyPreparation(Path.Combine(Settings.DataDirectory, "Dependencies"), Path.Combine(Settings.DataDirectory, "DependencyChecks"));
        var python = await preparation.CheckPythonAsync(pythonPath, cancellationToken);
        var muse = await preparation.CheckMuseScoreAsync(museScorePath, null, cancellationToken);
        return $"✓ .NET Desktop {Environment.Version} — aplicativo em execução.\n{python.Message}\n{muse.Message}";
    }
}
