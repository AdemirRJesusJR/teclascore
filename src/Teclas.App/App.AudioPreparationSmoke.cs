using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Teclas.App;

public partial class App
{
    // Controlled asynchronous work only: no music, audio device, renderer, network or real preferences.
    private async void VerifyAudioPreparationUi(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string folder = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "audio-preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        using var bindingOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(bindingOutput);
        var trace = PresentationTraceSources.DataBindingSource;
        var originalLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning; trace.Listeners.Add(listener);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        timeout.Tick += (_, _) =>
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "Audio preparation verification exceeded 40 seconds." }));
            Shutdown(1);
        };
        timeout.Start();
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("Audio preparation regression: " + name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        static object? Invoke(MainWindow target, string name, params object?[] arguments) => typeof(MainWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
        static object? Field(MainWindow target, string name) => typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
        static void Click(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Run(MainWindow target, Func<Task> action, bool audio = true, string? title = null) =>
            (Task)Invoke(target, audio ? "RunAudioOperation" : "RunOperation", action, null,
                title ?? (audio ? "Preparando áudio de teste" : "Gerando partitura"), "Fixture sintética")!;
        void Capture(MainWindow target, string name)
        {
            Flush(); target.UpdateLayout();
            var root = (FrameworkElement)target.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(reports, name + ".png")); encoder.Save(output);
        }
        bool Fits(FrameworkElement control, MainWindow target)
        {
            if (!control.IsVisible) return false;
            var root = (FrameworkElement)target.Content;
            var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
        }
        try
        {
            string settingsPath = Path.Combine(folder, "settings.json");
            window = new MainWindow(true, settingsPath)
            { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 740 };
            window.Show(); Flush(); window.UpdateLayout();
            Check((string?)Field(window, "persistencePath") == settingsPath, "uses-only-explicit-isolated-settings");
            var overlay = (Grid)window.FindName("OperationOverlay");
            var footerCancel = (Button)window.FindName("FooterOperationCancelButton");
            var footerStatus = (TextBlock)window.FindName("FooterOperationStatus");
            var footerProgress = (ProgressBar)window.FindName("FooterOperationProgressBar");
            var modalCancel = (Button)window.FindName("OperationCancelButton");
            int audioOverlayAppearances = 0, modalFocusDuringAudio = 0;
            window.PropertyChanged += (_, _) =>
            {
                if (window.IsAudioOperation && window.OperationVisibility == Visibility.Visible) audioOverlayAppearances++;
            };
            overlay.IsVisibleChanged += (_, _) => { if (window.IsAudioOperation && overlay.IsVisible) audioOverlayAppearances++; };
            modalCancel.GotFocus += (_, _) => { if (window.IsAudioOperation) modalFocusDuringAudio++; };
            Check(window.SaveSessionForSmoke(), "initial-session-saves-for-write-regression-check");
            byte[] originalSettingsBytes = File.ReadAllBytes(settingsPath);
            DateTime originalSettingsWriteTime = File.GetLastWriteTimeUtc(settingsPath);
            var preview = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
            preview.Freeze();
            typeof(MainWindow).GetProperty(nameof(window.Preview))!.SetValue(window, preview);

            bool quickEntered = false;
            Task quick = Run(window, () =>
            {
                quickEntered = true;
                Check(window.IsBusy && window.IsAudioOperation && !window.IsModalOperation, "fast-audio-keeps-busy-with-explicit-inline-presentation");
                Check(window.OperationVisibility == Visibility.Collapsed, "fast-audio-never-requests-overlay");
                return Task.CompletedTask;
            });
            Check(quickEntered, "fast-audio-starts-action-without-dispatcher-delay");
            await quick; Flush();
            Check(window.IsIdle && !overlay.IsVisible && !window.IsAudioOperation, "fast-audio-completion-clears-state-without-popup");
            Check(ReferenceEquals(window.Preview, preview), "audio-preserves-existing-preview-instance-without-reloading");
            Check(File.GetLastWriteTimeUtc(settingsPath) == originalSettingsWriteTime
                && File.ReadAllBytes(settingsPath).SequenceEqual(originalSettingsBytes), "audio-does-not-rewrite-persisted-library-or-settings");

            var slowGate = Gate(); IProgress<string>? slowProgress = null; CancellationToken slowToken = default;
            Task slow = Run(window, async () =>
            {
                slowToken = ((CancellationTokenSource)Field(window, "cancellation")!).Token;
                slowProgress = (IProgress<string>)Invoke(window, "CreateOperationProgress")!;
                slowProgress.Report("Preparando os instrumentos do MIDI…");
                await slowGate.Task.WaitAsync(slowToken);
            });
            await V018Wait(() => footerStatus.Text.Contains("instrumentos", StringComparison.Ordinal));
            Check(window.IsBusy && window.IsAudioOperation && !overlay.IsVisible && window.CanCancelOperation,
                "slow-audio-stays-inline-and-cancellable");
            Check(footerCancel.IsEnabled && footerCancel.IsVisible && footerCancel.IsTabStop
                && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(footerCancel)), "footer-cancel-is-enabled-visible-and-keyboard-accessible");
            Check(footerProgress.IsIndeterminate && footerProgress.IsVisible && footerStatus.IsVisible
                && AutomationProperties.GetLiveSetting(footerStatus) == AutomationLiveSetting.Polite, "slow-audio-exposes-live-status-and-progress-in-footer");
            Check(new[] { "WorkspaceSidebar", "WorkspaceHeader", "WorkspacePages" }
                .All(name => !((FrameworkElement)window.FindName(name)).IsEnabled), "busy-blocks-workspace-without-disabling-footer-cancel");
            Check(Field(window, "operationPreviousFocus") is null && !modalCancel.IsKeyboardFocusWithin,
                "audio-does-not-capture-focus-for-modal-restoration");
            string firstElapsed = window.OperationElapsedText;
            await V018Wait(() => window.OperationElapsedText != firstElapsed);
            Check(!overlay.IsVisible && window.IsAudioOperation, "long-audio-never-opens-delayed-overlay");
            foreach (string theme in new[] { "graphite", "light" })
            {
                window.SetThemeForSmoke(theme); Flush(); window.UpdateLayout();
                Check(Fits(footerCancel, window) && Fits(footerProgress, window) && Fits(footerStatus, window), "inline-controls-fit-minimum-window-" + theme);
                Capture(window, "audio-preparation-inline-" + theme);
            }
            Invoke(window, "SetOperationItems", 1, 4, "Etapa sintética"); Flush();
            Check(!footerProgress.IsIndeterminate && Math.Abs(footerProgress.Value - 25) < .01,
                "footer-displays-real-counted-progress-when-available");
            Click(footerCancel);
            await V018Wait(() => slowToken.IsCancellationRequested);
            Check(!window.CanCancelOperation, "cancel-request-is-not-repeatable");
            await slow; Flush();
            Check(window.IsIdle && !overlay.IsVisible && window.Status.Contains("cancelada", StringComparison.OrdinalIgnoreCase),
                "footer-cancel-completes-without-a-popup");
            string cancelledStatus = window.Status;
            slowProgress!.Report("OBSOLETE_AUDIO_PROGRESS"); Flush();
            Check(window.Status == cancelledStatus, "queued-progress-cannot-overwrite-cancel-result");

            await Run(window, () => Task.FromException(new InvalidOperationException("Synthetic audio failure"))); Flush();
            Check(window.IsIdle && window.Status == "Synthetic audio failure" && footerStatus.Text == window.Status && !overlay.IsVisible,
                "audio-failure-remains-visible-inline-and-releases-busy");
            var nextGate = Gate();
            Task next = Run(window, () => nextGate.Task);
            slowProgress.Report("OBSOLETE_AUDIO_PROGRESS"); Flush();
            Check(window.Status != "OBSOLETE_AUDIO_PROGRESS", "old-audio-progress-cannot-bleed-into-new-operation");
            nextGate.SetResult(true); await next; Flush();

            var conversionGate = Gate(); bool conversionEntered = false;
            window.Width = 1140; Flush(); window.UpdateLayout();
            Task conversion = Run(window, async () => { conversionEntered = true; await conversionGate.Task; }, audio: false);
            await V018Wait(() => conversionEntered); Flush();
            Check(window.IsModalOperation && !window.IsAudioOperation && overlay.IsVisible && modalCancel.IsEnabled,
                "conversion-retains-central-modal-progress-and-cancel");
            conversionGate.SetResult(true); await conversion; Flush();
            Check(window.Preview is null && Settings.Load(settingsPath).WindowWidth is { } savedWidth
                && Math.Abs(savedWidth - window.ActualWidth) < 2, "conversion-still-refreshes-preview-and-persists-current-session");
            await Run(window, () => Task.CompletedTask); Flush();
            Check(!overlay.IsVisible && window.IsIdle && audioOverlayAppearances == 0 && modalFocusDuringAudio == 0,
                "audio-after-conversion-never-reuses-overlay-or-steals-modal-focus");

            var closeGate = Gate(); CancellationToken closeToken = default; bool reachedAudioStart = false, closed = false;
            window.Closed += (_, _) => closed = true;
            Task closing = Run(window, async () =>
            {
                closeToken = ((CancellationTokenSource)Field(window, "cancellation")!).Token;
                await closeGate.Task.WaitAsync(closeToken);
                closeToken.ThrowIfCancellationRequested(); reachedAudioStart = true;
            });
            window.Close(); Flush();
            Check(closeToken.IsCancellationRequested && !closed, "closing-during-audio-requests-cancel-before-saving");
            await closing; Flush(); closeGate.TrySetResult(true); Flush();
            Check(!reachedAudioStart && window.IsIdle && !overlay.IsVisible, "cancelled-preparation-cannot-start-audio-after-close-request");
            window.Close(); Flush();
            Check(closed && File.Exists(settingsPath), "idle-close-saves-isolated-preferences");
            bool ranAfterClose = false;
            await Run(window, () => { ranAfterClose = true; return Task.CompletedTask; });
            slowProgress.Report("AFTER_CLOSE_PROGRESS"); Flush();
            Check(!ranAfterClose && !overlay.IsVisible && !window.IsAudioOperation, "closed-window-rejects-new-work-and-delayed-popup");
            listener.Flush(); Check(string.IsNullOrWhiteSpace(bindingOutput.ToString()), "no-wpf-binding-warnings");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, audioDevicesOpened = 0, rendererCalls = 0 },
                new JsonSerializerOptions { WriteIndented = true }));
            timeout.Stop(); Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = ex.ToString(), bindingDiagnostics = bindingOutput.ToString() },
                new JsonSerializerOptions { WriteIndented = true }));
            timeout.Stop();
            if (window is not null) { Invoke(window, "RequestOperationCancellation"); window.Close(); }
            Shutdown(1);
        }
        finally { timeout.Stop(); trace.Listeners.Remove(listener); trace.Switch.Level = originalLevel; }
    }
}
