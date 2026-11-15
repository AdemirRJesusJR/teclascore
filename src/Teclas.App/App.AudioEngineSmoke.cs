using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Teclas.App;

public partial class App
{
    // Configurações › Áudio para partitura: the engine choice, the state of the YourMT3+ component, its installation
    // and its removal. The engine process is replaced by an injected answer: nothing is downloaded or deleted, and the
    // component installed on this machine is never asked about.
    private async void VerifyAudioEngineUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "audio-engine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "audio engine smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> predicate, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
            if (!predicate()) throw new TimeoutException(what);
            Flush();
        }
        try
        {
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), CurrentPage = "library" }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            var requests = new List<string>(); var confirmations = new List<string>(); var failures = new List<string>();
            bool installed = false, partial = false, offline = false, answer = false;
            window.audioEngineClient = async (request, progress, token) =>
            {
                string mode = request["mode"]!.GetValue<string>(); requests.Add(mode);
                await Task.Delay(20, token);
                if (mode == "installAudioEngine")
                {
                    progress?.Report("Baixando o PyTorch com CUDA (cerca de 2,5 GB)…");
                    if (offline) { partial = true; throw new InvalidOperationException("Baixando o PyTorch falhou: sem conexão."); }
                    installed = true; partial = false;
                }
                if (mode == "removeAudioEngine") installed = false;
                return new JsonObject { ["installed"] = installed, ["folder"] = @"C:\Users\teste\AppData\Local\TeclasPartituras\componentes\yourmt3", ["custom_folder"] = false,
                    ["partial"] = partial, ["cuda"] = installed, ["gpu"] = installed ? "NVIDIA GeForce RTX 3060" : null, ["torch"] = installed ? "2.5.1+cu121" : null };
            };
            window.audioEngineConfirm = question => { confirmations.Add(question); return answer; };
            window.showFailure = (title, message) => failures.Add(title + ": " + message);
            window.Show(); Flush();
            var combo = (ComboBox)window.FindName("AudioEngineCombo");
            var install = (Button)window.FindName("InstallAudioEngineButton");
            var remove = (Button)window.FindName("RemoveAudioEngineButton");
            var status = (TextBlock)window.FindName("AudioEngineStatusText");
            void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Flush(); }

            // 1. Nothing is asked of the engine until Configurações opens; then the card shows what is installed.
            Check(requests.Count == 0 && window.AudioEngineStatus == Teclas.App.MainWindow.AudioEngineChecking, "engine-is-not-asked-before-settings-opens");
            window.SelectPageForSmoke("settings"); Flush();
            await Wait(() => requests.Count == 1 && install.Visibility == Visibility.Visible, "the component state never arrived");
            Check(requests[0] == "audioEngineStatus" && remove.Visibility == Visibility.Collapsed, "missing-component-offers-only-the-installation");
            Check(status.Text.StartsWith(Teclas.App.MainWindow.AudioEngineMissing) && status.Text.Contains("Em uso: motor clássico (automático"), "missing-component-says-the-classic-engine-is-in-use");
            Check((string)combo.SelectedValue == "" && (string)install.Content == "Instalar o YourMT3+…", "engine-choice-starts-automatic");

            // 2. The choice is saved and goes in the transcription request; automatic sends nothing.
            Check(window.AudioTranscriptionRequest("job", "a.wav", "a.mid")["audioEngine"] is null, "automatic-leaves-the-choice-to-the-engine");
            combo.SelectedValue = "yourmt3"; Flush();
            Check(window.AudioTranscriptionRequest("job", "a.wav", "a.mid")["audioEngine"]?.GetValue<string>() == "yourmt3", "chosen-engine-goes-in-the-request");
            Check(status.Text.Contains("ainda não está instalado"), "choosing-the-missing-engine-is-explained");
            window.SaveSessionForSmoke();
            Check(Settings.Load(settingsPath).AudioEngine == "yourmt3", "engine-choice-is-saved");
            combo.SelectedValue = ""; Flush();

            // 3. The installation asks first, with the size and the sources; "no" downloads nothing.
            Click(install);
            Check(confirmations.Count == 1 && confirmations[0].Contains("3 GB") && confirmations[0].Contains("Hugging Face") && requests.Count == 1, "installation-asks-before-downloading");

            // 4. A failed download is shown, and what was downloaded stays for the next try.
            answer = true; offline = true;
            await window.InstallAudioEngineAsync(); Flush();
            Check(requests[^1] == "audioEngineStatus" && requests.Contains("installAudioEngine") && !window.AudioEngineInstalled, "failed-installation-leaves-the-component-missing");
            Check(failures.Count == 1 && failures[0].Contains("sem conexão") && (string)install.Content == "Continuar a instalação do YourMT3+…", "failed-installation-is-shown-and-can-continue");
            Check(window.IsIdle, "window-is-free-after-a-failed-installation");

            // 5. Installed: the card names the video card, the automatic choice now uses YourMT3+, and removal is offered.
            offline = false;
            await window.InstallAudioEngineAsync(); Flush();
            Check(window.AudioEngineInstalled && install.Visibility == Visibility.Collapsed && remove.Visibility == Visibility.Visible, "installed-component-offers-only-the-removal");
            Check(status.Text.Contains("NVIDIA GeForce RTX 3060") && status.Text.Contains("Em uso: YourMT3+ (automático)"), "installed-component-names-the-video-card");
            Check(window.Status.StartsWith("YourMT3+ instalado") && failures.Count == 1, "installation-is-announced");
            combo.SelectedValue = "classico"; Flush();
            Check(status.Text.Contains("Em uso: motor clássico.") && window.AudioTranscriptionRequest("job", "a.wav", "a.mid")["audioEngine"]?.GetValue<string>() == "classico", "classic-engine-can-be-kept-with-the-component-installed");
            combo.SelectedValue = ""; Flush();
            ((FrameworkElement)window.FindName("AudioEngineCard")).BringIntoView(); Flush(); window.UpdateLayout();
            CaptureFolderWindow(window, Path.Combine(reports, "audio-engine-settings.png"));

            // 6. Removal asks too, naming the folder; "no" keeps everything.
            answer = false; int before = requests.Count;
            Click(remove);
            Check(confirmations[^1].Contains("5 GB") && confirmations[^1].Contains(@"componentes\yourmt3") && requests.Count == before && window.AudioEngineInstalled, "removal-asks-before-deleting");
            answer = true;
            await window.RemoveAudioEngineAsync(); Flush();
            Check(requests[^1] == "removeAudioEngine" && !window.AudioEngineInstalled && install.Visibility == Visibility.Visible && remove.Visibility == Visibility.Collapsed, "removed-component-offers-the-installation-again");
            Check(window.Status.StartsWith("YourMT3+ removido") && status.Text.Contains("Em uso: motor clássico (automático"), "removal-is-announced");

            window.Close(); window = null;
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }
}
