using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Configurações › Áudio para partitura: who recognizes the notes of a recording (automatic, YourMT3+ or the classic
/// engine) and the YourMT3+ component, which lives outside the installer: its state, its installation and its removal.
/// The engine answers all three (worker modes audioEngineStatus, installAudioEngine, removeAudioEngine), so the folder
/// and the pinned versions are decided in one place (engine/yourmt3_setup.py).
/// </summary>
public partial class MainWindow
{
    /// <summary>Test hook: replaces the engine process (request, progress) → the component state.</summary>
    internal Func<JsonObject, IProgress<string>?, CancellationToken, Task<JsonObject>>? audioEngineClient = null;
    internal Func<string, bool>? audioEngineConfirm = null;

    internal const string AudioEngineChecking = "Verificando o componente YourMT3+…";
    internal const string AudioEngineMissing = "YourMT3+ não instalado. A instalação baixa cerca de 3 GB das fontes oficiais (PyTorch e o modelo dos autores) e ocupa 5 GB no disco.";

    private JsonObject? audioEngineState;
    private string audioEngineProblem = "";

    public IReadOnlyList<object> AudioEngineOptions { get; } = new object[]
    {
        new { Value = "", Label = "Automático" }, new { Value = "yourmt3", Label = "YourMT3+" }, new { Value = "classico", Label = "Motor clássico" },
    };

    public string AudioEngineSetting
    {
        get => settings.AudioEngine;
        set
        {
            value = value is "yourmt3" or "classico" ? value : "";
            if (settings.AudioEngine == value) return;
            settings.AudioEngine = value;
            Notify(); NotifyAudioEngine(); PersistPreferences();
        }
    }

    internal bool AudioEngineInstalled => audioEngineState?["installed"]?.GetValue<bool>() == true;
    private bool AudioEngineCustomFolder => audioEngineState?["custom_folder"]?.GetValue<bool>() == true;

    public Visibility AudioEngineInstallVisibility => audioEngineState is not null && !AudioEngineInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AudioEngineRemoveVisibility => AudioEngineInstalled && !AudioEngineCustomFolder ? Visibility.Visible : Visibility.Collapsed;
    public string AudioEngineInstallLabel => audioEngineState?["partial"]?.GetValue<bool>() == true ? "Continuar a instalação do YourMT3+…" : "Instalar o YourMT3+…";

    /// <summary>The component's state and, under it, which engine the next transcription will use.</summary>
    public string AudioEngineStatus
    {
        get
        {
            if (audioEngineProblem.Length > 0) return audioEngineProblem;
            if (audioEngineState is null) return AudioEngineChecking;
            string component = !AudioEngineInstalled ? AudioEngineMissing
                : audioEngineState["cuda"]?.GetValue<bool>() == true ? $"YourMT3+ instalado · roda na placa de vídeo {audioEngineState["gpu"]?.GetValue<string>() ?? "NVIDIA"}."
                : "YourMT3+ instalado · sem placa NVIDIA, roda no processador, bem mais devagar.";
            string use = settings.AudioEngine switch
            {
                "classico" => "Em uso: motor clássico.",
                "yourmt3" => AudioEngineInstalled ? "Em uso: YourMT3+." : "Escolhido o YourMT3+, que ainda não está instalado: a transcrição avisa e não começa.",
                _ => AudioEngineInstalled ? "Em uso: YourMT3+ (automático)." : "Em uso: motor clássico (automático, até o YourMT3+ ser instalado).",
            };
            return component + "\n" + use;
        }
    }

    private void NotifyAudioEngine()
    {
        foreach (var name in new[] { nameof(AudioEngineStatus), nameof(AudioEngineInstallVisibility), nameof(AudioEngineRemoveVisibility), nameof(AudioEngineInstallLabel) }) Notify(name);
    }

    private async Task<JsonObject> AskAudioEngineAsync(string mode, IProgress<string>? progress, CancellationToken token)
    {
        var request = new JsonObject { ["mode"] = mode };
        if (audioEngineClient is { } client) return await client(request, progress, token);
        string python = TranscriptionPython;
        if (!File.Exists(python)) throw new InvalidOperationException("O Python do TeclaScore não foi encontrado. Use Preparar este computador, em Ferramentas de conversão.");
        string job = Path.Combine(DataFolder, "jobs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
        request["workspace"] = job;
        try { return await new EngineClient(python, engineDirectory).RunAsync(request, job, progress, token); }
        finally { try { Directory.Delete(job, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>Reads the component's state (a fraction of a second: nothing musical is loaded). Called when the
    /// Configurações page opens and after an installation or removal.</summary>
    internal async Task RefreshAudioEngineStatusAsync()
    {
        try { audioEngineState = await AskAudioEngineAsync("audioEngineStatus", null, CancellationToken.None); audioEngineProblem = ""; }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { audioEngineState = null; audioEngineProblem = "Não foi possível verificar o componente YourMT3+: " + error.Message; }
        NotifyAudioEngine();
    }

    private bool ConfirmAudioEngine(string question, string title) => audioEngineConfirm is { } confirm ? confirm(question)
        : MessageBox.Show(this, question, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async void InstallAudioEngineClick(object sender, RoutedEventArgs e) => await InstallAudioEngineAsync();
    private async void RemoveAudioEngineClick(object sender, RoutedEventArgs e) => await RemoveAudioEngineAsync();

    internal async Task InstallAudioEngineAsync()
    {
        if (!IsIdle || AudioEngineInstalled) return;
        if (!ConfirmAudioEngine("Instalar o YourMT3+ neste computador?\n\n" +
                "• Baixa cerca de 3 GB e ocupa 5 GB no disco.\n" +
                "• Vem das fontes oficiais, com versão conferida: uv (Astral), PyTorch e o modelo dos autores no Hugging Face.\n" +
                "• Fica na pasta de dados do TeclaScore, fora do programa; pode ser removido aqui depois.\n" +
                "• Leva alguns minutos, conforme a internet. Se for cancelada, continua de onde parou na próxima vez.", "Instalar o YourMT3+")) return;
        JsonObject? state = null; string failure = "";
        await RunOperation(async () =>
        {
            try { state = await AskAudioEngineAsync("installAudioEngine", CreateOperationProgress(), cancellation!.Token); }
            catch (InvalidOperationException error) { failure = error.Message; throw; }
        }, operationTitle: "Instalando o YourMT3+");
        if (state is not null) { audioEngineState = state; audioEngineProblem = ""; NotifyAudioEngine(); }
        else await RefreshAudioEngineStatusAsync();          // cancelled or failed: what is there now (maybe a partial installation)
        if (AudioEngineInstalled) Status = "YourMT3+ instalado. As próximas transcrições de áudio usam o novo motor.";
        else if (failure.Length > 0) showFailure("Instalar o YourMT3+", "A instalação não terminou.\n\n" + failure + "\n\nO que já foi baixado fica guardado: use Continuar a instalação.");
    }

    internal async Task RemoveAudioEngineAsync()
    {
        if (!IsIdle || !AudioEngineInstalled || AudioEngineCustomFolder) return;
        string folder = audioEngineState?["folder"]?.GetValue<string>() ?? "";
        if (!ConfirmAudioEngine($"Remover o YourMT3+ deste computador?\n\nLibera cerca de 5 GB em {folder}. As transcrições de áudio voltam ao motor clássico. Você pode instalar de novo quando quiser.", "Remover o YourMT3+")) return;
        JsonObject? state = null; string failure = "";
        await RunOperation(async () =>
        {
            try { state = await AskAudioEngineAsync("removeAudioEngine", CreateOperationProgress(), cancellation!.Token); }
            catch (InvalidOperationException error) { failure = error.Message; throw; }
        }, operationTitle: "Removendo o YourMT3+");
        if (state is not null) { audioEngineState = state; audioEngineProblem = ""; NotifyAudioEngine(); }
        else await RefreshAudioEngineStatusAsync();
        if (!AudioEngineInstalled && failure.Length == 0) Status = "YourMT3+ removido. As transcrições de áudio usam o motor clássico.";
        else if (failure.Length > 0) showFailure("Remover o YourMT3+", "A remoção não terminou.\n\n" + failure);
    }
}
