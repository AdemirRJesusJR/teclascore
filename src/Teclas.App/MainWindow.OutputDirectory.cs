using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Teclas.App;

public partial class MainWindow
{
    private string? outputDirectoryDraft;
    private string outputDirectoryStatus = "A pasta salva será usada nas próximas gerações e exportações de PDF e ZIP. Os arquivos existentes permanecem onde estão.";
    private bool outputDirectoryHasError;
    public string OutputDirectory
    {
        get => settings.OutputDirectory;
        set { settings.OutputDirectory = value; outputDirectoryDraft = null; Notify(); Notify(nameof(OutputDirectoryDraft)); }
    }
    public string OutputDirectoryDraft
    {
        get => outputDirectoryDraft ?? OutputDirectory;
        set
        {
            outputDirectoryDraft = value; Notify();
            OutputDirectoryHasError = false;
            OutputDirectoryStatus = "Alteração ainda não salva. Use Salvar pasta padrão para aplicar às próximas gerações.";
        }
    }
    public string OutputDirectoryStatus { get => outputDirectoryStatus; private set { outputDirectoryStatus = value; Notify(); } }
    public bool OutputDirectoryHasError { get => outputDirectoryHasError; private set { outputDirectoryHasError = value; Notify(); } }

    private void BrowseOutput(object sender, RoutedEventArgs e)
    {
        if (!IsIdle) return;
        var dialog = new OpenFolderDialog { Title = "Escolher pasta de saída padrão", Multiselect = false };
        if (Directory.Exists(OutputDirectory)) dialog.InitialDirectory = OutputDirectory;
        if (dialog.ShowDialog(this) == true) OutputDirectoryDraft = dialog.FolderName;
    }

    private void SaveOutputDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle) return;
        if (!Settings.TryValidateOutputDirectory(OutputDirectoryDraft, out string destination, out string error))
        { OutputDirectoryHasError = true; OutputDirectoryStatus = error; return; }
        string previous = OutputDirectory, draft = OutputDirectoryDraft;
        OutputDirectory = destination;
        if (!SaveCurrentSession())
        {
            OutputDirectory = previous; outputDirectoryDraft = draft; Notify(nameof(OutputDirectoryDraft));
            OutputDirectoryHasError = true; OutputDirectoryStatus = PersistenceError; return;
        }
        OutputDirectoryHasError = false;
        OutputDirectoryStatus = "Pasta padrão salva. Será usada nas próximas gerações e exportações de PDF e ZIP; arquivos existentes e trabalhos já enfileirados mantêm seus destinos.";
        Status = "Pasta de saída padrão salva.";
    }

    private async Task<string?> PrepareExportDirectoryAsync()
    {
        string? destination = null;
        if (!IsIdle || operationsDisposed) return null;
        // Directory access may block on removable/network drives. Keep cancellation available
        // in the footer while checking the saved destination; never fall back to another folder.
        await RunOperationCore(async () => destination = await PrepareOutputDirectoryAsync(cancellation!.Token),
            null, "Preparando pasta de saída", null, useOverlay: false);
        return operationsDisposed ? null : destination;
    }

    private async Task<SaveFileDialog?> CreateScoreExportDialogAsync(string title, string filter, string fileName, string extension)
    {
        string? destination = await PrepareExportDirectoryAsync();
        if (destination is null) return null;
        return new SaveFileDialog
        {
            Title = title, Filter = filter, DefaultExt = extension, AddExtension = true, OverwritePrompt = true,
            // An absolute filename also overrides the shell's remembered folder for this dialog.
            FileName = Path.Combine(destination, fileName), InitialDirectory = destination, DefaultDirectory = destination
        };
    }

    private async Task<OpenFolderDialog?> CreateBatchPdfFolderDialogAsync()
    {
        string? destination = await PrepareExportDirectoryAsync();
        return destination is null ? null : new OpenFolderDialog
        {
            Title = "Escolha a pasta para os PDFs das partituras", Multiselect = false,
            FolderName = destination, InitialDirectory = destination, DefaultDirectory = destination
        };
    }

    // Only an operation that will write output calls this method. Saving a preference never probes,
    // creates or deletes content in the destination. The tiny write probe is owned by this call.
    private async Task<string> PrepareOutputDirectoryAsync(CancellationToken token)
    {
        if (!Settings.TryValidateOutputDirectory(OutputDirectory, out string destination, out string error))
            throw new InvalidOperationException(error + " Abra Configurações para salvar a pasta padrão.");
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(destination);
                token.ThrowIfCancellationRequested();
                using var probe = new FileStream(Path.Combine(destination, ".teclas-write-" + Guid.NewGuid().ToString("N") + ".tmp"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                probe.WriteByte(0); probe.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                throw new IOException("A pasta de saída padrão está indisponível ou não permite gravação. Conecte a unidade ou escolha outra pasta em Configurações. " + destination, ex);
            }
            token.ThrowIfCancellationRequested();
        }, token);
        return destination;
    }
}
