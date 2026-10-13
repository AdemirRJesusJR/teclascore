using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Teclas.App;

public partial class App
{
    // Explicit QA command: every window receives an isolated settings file and only copied MIDIs are changed.
    private void VerifyScoreMetadata(string reportPath, string originalMidi)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new Dictionary<string, bool>();
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        using var listener = new TextWriterTraceListener(diagnostics);
        var trace = PresentationTraceSources.DataBindingSource;
        var previousLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning; trace.Listeners.Add(listener);
        string report = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        string folder = Path.Combine(Path.GetDirectoryName(report)!, "metadata-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("Metadata regression: " + name); checks[name] = true; }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        MainWindow Open(string path)
        {
            var window = new MainWindow(true, path) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            window.Show(); Flush(); window.UpdateLayout(); return window;
        }
        void Close(MainWindow window)
        {
            bool closed = false; window.Closed += (_, _) => closed = true;
            window.Close(); Flush();
            if (!closed) throw new InvalidOperationException("Closing metadata QA window failed: " + window.PersistenceError);
        }
        void Select(MainWindow window, MidiRow row)
        {
            window.SearchText = ""; window.CurrentFilter = "all"; window.SelectPageForSmoke("library", 2);
            ((ListBox)window.FindName("Library")).SelectedItem = row;
            Flush(); window.UpdateLayout();
        }
        static Button? FindButton(DependencyObject parent, string content, params string[] names) => Descendants(parent).OfType<Button>()
            .FirstOrDefault(button => names.Contains(button.Name) || button.Content?.ToString()?.Contains(content, StringComparison.OrdinalIgnoreCase) == true);
        void Properties(MainWindow window, string title, string? subtitle, string composer, int? inversion, bool save)
        {
            var open = window.FindName("ScorePropertiesButton") as Button
                ?? FindButton(window, "Propriedades", "ScorePropertiesButton", "PropertiesButton")
                ?? throw new InvalidOperationException("The properties button was not found.");
            if (!open.IsEnabled) throw new InvalidOperationException("The properties button is disabled for an imported MIDI.");
            Exception? failure = null; bool visited = false;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                Window? dialog = Windows.OfType<Window>().FirstOrDefault(candidate => candidate.Owner == window);
                try
                {
                    if (dialog is null) throw new InvalidOperationException("The properties action did not open an owned modal window.");
                    dialog.Opacity = 0; dialog.ShowInTaskbar = false;
                    ((TextBox)dialog.FindName("ScoreTitleInput")).Text = title;
                    var subtitleBox = (TextBox)dialog.FindName("ScoreSubtitleInput");
                    var automatic = (CheckBox)dialog.FindName("AutomaticSubtitleCheck");
                    automatic.IsChecked = subtitle is null;
                    if (subtitle is not null) subtitleBox.Text = subtitle;
                    ((TextBox)dialog.FindName("ScoreComposerInput")).Text = composer;
                    var combo = dialog.FindName("DiagramInversionCombo") as ComboBox
                        ?? dialog.FindName("ScoreInversionInput") as ComboBox
                        ?? Descendants(dialog).OfType<ComboBox>().Single();
                    combo.SelectedValue = inversion ?? -1;
                    if (!Equals(combo.SelectedValue, inversion ?? -1)) throw new InvalidOperationException("The selected diagram inversion was not applied.");
                    if (subtitle is null && subtitleBox.Text != ScoreMetadataDefaults.Subtitle(inversion ?? window.SelectedFile!.DefaultDiagramInversion))
                        throw new InvalidOperationException("The automatic subtitle did not follow the selected diagram inversion.");
                    var button = save
                        ? FindButton(dialog, "Salvar", "SavePropertiesButton", "SaveButton", "PropertiesSaveButton")
                        : FindButton(dialog, "Cancelar", "CancelPropertiesButton", "CancelButton", "PropertiesCancelButton");
                    if (button is null) throw new InvalidOperationException("The modal action button was not found.");
                    Keyboard.Focus(button); visited = true;
                    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                }
                catch (Exception ex) { failure = ex; dialog?.Close(); }
            }));
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
            if (failure is not null) throw new InvalidOperationException("The modal properties test failed.", failure);
            if (!visited) throw new InvalidOperationException("The modal properties edit was not exercised.");
        }
        static bool Metadata(MidiRow row, string title, string? subtitle, string composer, int? inversion) =>
            row.ScoreTitle == title && row.ScoreSubtitle == subtitle && row.ScoreComposer == composer && row.DiagramInversion == inversion;
        static void LeaveUnanalyzed(MidiRow row)
        {
            row.UseReviewedProfile = false;
            foreach (var track in row.Tracks) track.IsMelody = false;
        }

        try
        {
            string originalHash = Hash(originalMidi);
            string firstPath = Path.Combine(folder, "Minha__Canção   01.mid"), secondPath = Path.Combine(folder, "outra_faixa.mid");
            File.Copy(originalMidi, firstPath); File.Copy(originalMidi, secondPath);
            string settingsPath = Path.Combine(folder, "settings.json");
            var first = Open(settingsPath);
            first.ImportForSmoke(firstPath); first.ImportForSmoke(secondPath);
            var one = first.Files.Single(row => row.FullPath == firstPath);
            var two = first.Files.Single(row => row.FullPath == secondPath);
            LeaveUnanalyzed(one); LeaveUnanalyzed(two);
            Check(one.Document is not null && two.Document is not null, "both-midi-fixtures-are-readable");
            Check(Metadata(one, "Minha Canção 01", null, "", null) && one.DefaultDiagramInversion == 2 && one.EffectiveDiagramInversion == 2,
                "new-midi-gets-normalized-filename-and-default-properties");
            Select(first, one);
            Properties(first, "Descartar título", "Descartar subtítulo", "Descartar autor", 0, false);
            Check(Metadata(one, "Minha Canção 01", null, "", null), "modal-cancel-preserves-all-four-original-properties");
            Properties(first, "Canção & Sol <2026>", "Estudo — teclado", "Ademir José / Autoria", 1, true);
            Check(Metadata(one, "Canção & Sol <2026>", "Estudo — teclado", "Ademir José / Autoria", 1), "modal-save-applies-title-subtitle-composer-and-inversion");
            Select(first, two);
            Check(Metadata(two, "outra faixa", null, "", null), "different-midi-with-same-hash-keeps-independent-defaults");
            Properties(first, "", "", "", 0, true);
            Check(Metadata(two, "outra faixa", "", "", 0) && one.ScoreTitle == "Canção & Sol <2026>" && one.DiagramInversion == 1,
                "blank-title-falls-back-to-filename-and-empty-subtitle-composer-stay-per-midi");
            Select(first, one);
            Properties(first, "Canção & Sol (revisada)", "Estudo — teclado", "Ademir José / Autoria", 2, true);
            Check(one.FullPath == firstPath && one.Name == Path.GetFileName(firstPath) && two.FullPath == secondPath
                && Hash(firstPath) == originalHash && Hash(secondPath) == originalHash, "editing-properties-does-not-rename-or-rewrite-midi-files");
            Check(one.HasPendingScoreProperties && two.HasPendingScoreProperties && first.IsIdle
                && one.ScorePath is null && two.ScorePath is null, "saving-unanalyzed-properties-remains-pending-without-generating");
            first.SelectPageForSmoke("settings", 0); Flush();
            ((ComboBox)first.FindName("DefaultDiagramInversionCombo")).SelectedValue = 0; Flush();
            Check(first.DefaultDiagramInversion == 0 && one.DefaultDiagramInversion == 2 && two.DefaultDiagramInversion == 2,
                "changing-global-default-preserves-existing-library-snapshots");
            string thirdPath = Path.Combine(folder, "novo_padrao.mid"); File.Copy(originalMidi, thirdPath);
            first.ImportForSmoke(thirdPath);
            var three = first.Files.Single(row => row.FullPath == thirdPath); LeaveUnanalyzed(three);
            Check(three.DefaultDiagramInversion == 0 && three.DiagramInversion is null && three.EffectiveDiagramInversion == 0,
                "new-import-adopts-current-global-inversion-snapshot");
            Select(first, three);
            Properties(first, "Novo padrão", null, "", 1, true);
            Check(Metadata(three, "Novo padrão", null, "", 1) && three.EffectiveDiagramInversion == 1,
                "automatic-subtitle-follows-first-inversion-without-becoming-custom");
            Properties(first, "Novo padrão", null, "", null, true);
            Check(Metadata(three, "Novo padrão", null, "", null) && three.EffectiveDiagramInversion == 0 && three.DefaultDiagramInversion == 0,
                "modal-default-option-removes-override-and-restores-imported-snapshot");
            Close(first);

            var reopened = Open(settingsPath);
            var restoredOne = reopened.Files.Single(row => row.FullPath == firstPath);
            var restoredTwo = reopened.Files.Single(row => row.FullPath == secondPath);
            Check(Metadata(restoredOne, "Canção & Sol (revisada)", "Estudo — teclado", "Ademir José / Autoria", 2), "native-reopen-restores-last-metadata-edit");
            Check(Metadata(restoredTwo, "outra faixa", "", "", 0), "native-reopen-preserves-empty-values-and-root-position");
            var restoredThree = reopened.Files.Single(row => row.FullPath == thirdPath);
            Check(reopened.DefaultDiagramInversion == 0 && restoredOne.DefaultDiagramInversion == 2
                && restoredTwo.DefaultDiagramInversion == 2 && restoredThree.DefaultDiagramInversion == 0
                && restoredThree.DiagramInversion is null && restoredThree.ScoreSubtitle is null,
                "native-reopen-preserves-global-default-snapshots-and-automatic-subtitle");
            Close(reopened);

            // Editorial metadata belongs to the library item, independently of the source-note checksum.
            using (var append = new FileStream(firstPath, FileMode.Append)) append.WriteByte(0);
            var changed = Open(settingsPath);
            Check(changed.Files.Single(row => row.FullPath == firstPath).Document is not null
                && Metadata(changed.Files.Single(row => row.FullPath == firstPath), "Canção & Sol (revisada)", "Estudo — teclado", "Ademir José / Autoria", 2),
                "changed-midi-retains-editorial-metadata");
            Close(changed);
            File.Move(secondPath, secondPath + ".removed");
            var unavailable = Open(settingsPath);
            var missing = unavailable.Files.Single(row => row.FullPath == secondPath);
            Check(missing.Document is null && Metadata(missing, "outra faixa", "", "", 0), "unavailable-midi-retains-properties");
            Close(unavailable);
            var savedMissing = Settings.Load(settingsPath).Library.Single(row => row.FullPath == secondPath);
            Check(savedMissing.ScoreTitle == "outra faixa" && savedMissing.ScoreSubtitle == "" && savedMissing.ScoreComposer == "" && savedMissing.DiagramInversion == 0,
                "saving-unavailable-midi-preserves-its-properties");

            string legacyPath = Path.Combine(folder, "legacy.json");
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(new { DefaultDiagramInversion = 0, Library = new[] { new { FullPath = firstPath } } }));
            var legacy = Open(legacyPath);
            Check(Metadata(legacy.Files.Single(), "Minha Canção 01", null, "", null)
                && legacy.Files.Single().DefaultDiagramInversion == 2 && legacy.DefaultDiagramInversion == 0,
                "legacy-library-uses-dynamic-metadata-defaults-and-second-inversion-snapshot");
            Close(legacy);
            Check(Hash(originalMidi) == originalHash, "original-user-midi-is-unchanged");
            listener.Flush();
            Check(string.IsNullOrEmpty(diagnostics.ToString()), "modal-and-restored-window-have-no-binding-errors");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", folder, checks }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", folder, checks, error = ex.ToString(), bindingDiagnostics = diagnostics.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(1);
        }
        finally { trace.Listeners.Remove(listener); trace.Switch.Level = previousLevel; }
    }
}
