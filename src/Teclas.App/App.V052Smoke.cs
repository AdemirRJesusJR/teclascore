using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Teclas.App;

public partial class App
{
    // Library selection mode: right-click › Selecionar, check boxes, the floating bar and its batch actions.
    // Export and generation are replaced by stubs: no engine or MuseScore runs.
    private async void VerifyV052Ui(string reportPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string report = Path.GetFullPath(reportPath), reports = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(reports);
        string work = Path.Combine(Directory.GetParent(reports)!.FullName, "work", "v052-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var checks = new Dictionary<string, bool>();
        MainWindow? window = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) => { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = "v0.52 smoke timed out." })); Shutdown(1); };
        timeout.Start();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name, true); }
        void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            string settingsPath = Path.Combine(work, "settings.json");
            new Settings { OutputDirectory = Path.Combine(work, "saida"), PythonPath = Path.Combine(work, "sem-python.exe"), MuseScorePath = Path.Combine(work, "sem-musescore.exe") }.Save(settingsPath);
            window = new MainWindow(true, settingsPath) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 1300, Height = 900 };
            window.Show(); Flush();
            // 1.7.0: batch actions show "O que vai acontecer" first; this smoke accepts it as is.
            window.batchPreviewDialog = _ => true;
            window.HandleShortcut(Key.D2, ModifierKeys.Control, null); Flush();
            var library = (ListBox)window.FindName("Library");
            T Named<T>(string name) where T : class => (T)window.FindName(name);
            string score = Path.Combine(work, "pronta.mscz"); WriteVerseScore(score);
            var rows = new List<MidiRow>();
            foreach (var (name, shift) in new[] { ("Os Barões - Chupadinha", 1), ("O bebê", 2), ("Sexta-feira rotineira", 3), ("Mamãe que é mamãe", 4) })
            {
                string path = Path.Combine(work, name + ".mid"); File.WriteAllBytes(path, TwoPartMidi(shift));
                window.ImportForSmoke(path); Flush(); rows.Add(window.Files.Single(r => r.FullPath == path));
            }
            // Two songs with a score, one with a melody chosen and no score, one untouched.
            rows[0].ScorePath = score; rows[0].HasPendingScoreProperties = false;
            rows[3].ScorePath = score; rows[3].HasPendingScoreProperties = false;
            foreach (var t in rows[1].Tracks) t.IsMelody = t.Source.Key.Track == 1;
            library.SelectedItem = rows[2]; Flush();
            library.UpdateLayout(); Flush();
            ListBoxItem Item(MidiRow row) { library.ScrollIntoView(row); library.UpdateLayout(); return (ListBoxItem)library.ItemContainerGenerator.ContainerFromItem(row); }
            void Press(MidiRow row, RoutedEvent kind, MouseButton button)
            {
                var item = Item(row);
                item.RaiseEvent(new MouseButtonEventArgs(InputManager.Current.PrimaryMouseDevice, Environment.TickCount, button) { RoutedEvent = kind, Source = item });
                Flush();
            }
            CheckBox CardCheck(MidiRow row) => Descendants<CheckBox>(Item(row)).Single(c => c.Name == "CardCheck");

            // 1. Out of selection mode the cards show no check boxes and the usual action bar.
            Check(!window.LibrarySelectionMode && CardCheck(rows[0]).Visibility != Visibility.Visible && window.LibraryActionBarVisibility == Visibility.Visible
                && Named<Border>("LibrarySelectionBar").Visibility != Visibility.Visible, "starts-without-selection");

            // 2. Right-click opens the card menu without opening the song; Selecionar shows the boxes with that card marked.
            Press(rows[0], Mouse.PreviewMouseUpEvent, MouseButton.Right);
            var menu = Named<Popup>("LibraryCardMenu");
            bool pdfEnabled = Named<Button>("CardMenuPdfButton").IsEnabled;
            Check(menu.IsOpen && library.SelectedItem == rows[2] && pdfEnabled && Named<Button>("CardMenuSelectButton").Visibility == Visibility.Visible,
                $"right-click-opens-the-card-menu ctx={window.LibraryContextRowForSmoke == rows[0]} open={menu.IsOpen} sel={library.SelectedItem == rows[2]} pdf={pdfEnabled} idle={window.IsIdle} pending={rows[0].HasPendingScoreProperties}");
            Named<Button>("CardMenuSelectButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(window.LibrarySelectionMode && rows[0].IsChecked && !menu.IsOpen && CardCheck(rows[0]).Visibility == Visibility.Visible && CardCheck(rows[0]).IsChecked == true
                && Named<Border>("LibrarySelectionBar").Visibility == Visibility.Visible && window.LibraryActionBarVisibility == Visibility.Collapsed
                && window.LibrarySelectionText == "1 selecionada", "selecionar-shows-the-check-boxes " + window.LibrarySelectionText);

            // 3. Clicking a card marks it instead of opening it; Shift+click marks a range; a second click unmarks.
            Press(rows[3], Mouse.PreviewMouseDownEvent, MouseButton.Left);
            Check(rows[3].IsChecked && library.SelectedItem == rows[2] && window.LibrarySelectionText == "2 selecionadas", "click-marks-without-opening");
            Press(rows[3], Mouse.PreviewMouseDownEvent, MouseButton.Left);
            Check(!rows[3].IsChecked, "second-click-unmarks");
            window.ToggleLibraryCheck(rows[3], range: true); Flush();
            var visible = window.LibraryView.Cast<MidiRow>().ToList();
            int i0 = visible.IndexOf(rows[0]), i3 = visible.IndexOf(rows[3]);
            Check(visible.Skip(Math.Min(i0, i3)).Take(Math.Abs(i0 - i3) + 1).All(r => r.IsChecked), "shift-click-marks-a-range");

            // 4. Select all / clear, section boxes and Esc.
            window.SetAllLibraryChecks(true); Flush();
            Check(window.Files.All(r => r.IsChecked) && window.LibrarySelectAllText == "Limpar seleção" && window.LibrarySelectionText == "4 selecionadas", "select-all");
            var groupBoxes = Descendants<CheckBox>(library).Where(c => c.Name == "GroupCheck").ToList();
            Check(groupBoxes.Count > 0 && groupBoxes.All(c => c.IsChecked == true && c.Visibility == Visibility.Visible), "section-boxes-follow-the-cards");
            window.SetAllLibraryChecks(false); Flush();
            Check(window.CheckedRows.Count == 0 && window.LibrarySelectAllText == "Selecionar todas", "clear-selection");
            Check(window.HandleShortcut(Key.Escape, ModifierKeys.None, library) && !window.LibrarySelectionMode && window.Files.All(r => !r.IsChecked), "esc-leaves-the-selection");

            // 5. Leaving clears the marks; mark two songs for the batch actions.
            Keyboard.Focus(library);
            window.EnterLibrarySelection(rows[0]); window.ExitLibrarySelection(); Flush();
            Check(!window.LibrarySelectionMode && !rows[0].IsChecked, "exit-clears-the-marks");
            window.EnterLibrarySelection(rows[0]); window.ToggleLibraryCheck(rows[3]); Flush();
            Check(window.CheckedRows.SequenceEqual(new[] { rows[0], rows[3] }.OrderBy(window.Files.IndexOf)), "two-songs-marked");

            // 6. PDFs of the marked songs go straight to the folder, without the choose-the-cards window.
            var exported = new List<string>();
            window.batchPdfSelectionDialog = _ => throw new InvalidOperationException("A janela de escolha não deveria abrir.");
            window.batchPdfFolderOverride = () => Task.FromResult<string?>(Path.Combine(work, "pdfs"));
            Directory.CreateDirectory(Path.Combine(work, "pdfs"));
            window.batchPdfExport = (source, target, _, _) => { exported.Add(source); File.WriteAllBytes(target, System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 " + new string('x', 150) + " %%EOF")); return Task.CompletedTask; };
            Named<Button>("SelectedPdfsButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 100 && !window.IsIdle; i++) { await Task.Delay(50); Flush(); }
            for (int i = 0; i < 20 && exported.Count < 2; i++) { await Task.Delay(50); Flush(); }
            Check(exported.Count == 2 && Directory.GetFiles(Path.Combine(work, "pdfs"), "*.pdf").Length == 2 && window.LibrarySelectionMode, "pdfs-of-the-marked-songs " + exported.Count + " " + window.Status);

            // 7. Repertoire: the window (which still asks the title and options) lists only the marked songs with a score.
            int offered = -1;
            window.batchPdfSelectionDialog = dialog => { offered = dialog.Rows.Count; return false; };
            window.SetAllLibraryChecks(false); window.ToggleLibraryCheck(rows[0]); window.ToggleLibraryCheck(rows[1]); Flush();
            Named<Button>("SelectedRepertoireButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(offered == 1, "repertoire-offers-only-marked-songs-with-score " + offered);
            int all = -1; window.batchPdfSelectionDialog = dialog => { all = dialog.Rows.Count; return false; };
            window.RepertoirePdfClickForSmoke(); Flush();
            Check(all == 2, "library-menu-repertoire-still-lists-every-score " + all);

            // 8. Gerar: songs with a melody go to the queue; the one without a score or melody gets the melody proposal first.
            window.SetAllLibraryChecks(true); Flush();
            var queued = new List<MidiRow>(); int proposed = -1;
            window.melodyBatchDialog = dialog => { proposed = dialog.Rows.Count; foreach (var row in dialog.Rows) row.Selected = true; return true; };
            window.melodyBatchEnqueue = (row, _) => { queued.Add(row); return Task.CompletedTask; };
            Named<Button>("SelectedGenerateButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 100 && (queued.Count == 0 || !window.IsIdle); i++) { await Task.Delay(50); Flush(); }
            Check(proposed >= 1 && queued.Contains(rows[1]) && queued.Contains(rows[2]) && window.Status.Contains("Fila de conversões"), $"generate-the-marked-songs proposed={proposed} queued={queued.Count} " + window.Status);

            // 9. Favorites and collection for every marked song.
            Named<Button>("SelectedFavoriteButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(window.Files.All(r => r.IsFavorite) && window.LibraryFavoriteActionText == "Desfavoritar", "favorite-the-marked-songs");
            Named<Button>("SelectedFavoriteButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(window.Files.All(r => !r.IsFavorite), "unfavorite-the-marked-songs");
            string? suggested = null;
            window.libraryCollectionDialog = dialog => { suggested = dialog.Collection; dialog.NameBox.Text = "Forró 2026"; return true; };
            Named<Button>("SelectedCollectionButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(suggested == "" && window.Files.All(r => r.Collection == "Forró 2026") && window.LibraryCollections.Contains("Forró 2026"), "collection-for-the-marked-songs");

            CaptureElement(LibraryCard(library), Path.Combine(reports, "library-selection.png"));

            // 10. Remove: every marked song leaves at once and Desfazer brings them back in their places.
            var order = window.Files.ToList();
            window.SetAllLibraryChecks(false); window.ToggleLibraryCheck(rows[1]); window.ToggleLibraryCheck(rows[3]); Flush();
            Named<Button>("SelectedRemoveButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            // 1.7.0: the removal first saves a restore point off the UI thread.
            for (int i = 0; i < 100 && window.Files.Count > 2; i++) { await Task.Delay(50); Flush(); }
            Check(window.Files.Count == 2 && !window.Files.Contains(rows[1]) && !window.Files.Contains(rows[3]) && !window.LibrarySelectionMode && window.CanUndoLibraryRemoval && window.Status.Contains("2 músicas removidas"),
                "remove-the-marked-songs " + window.Status);
            window.UndoLibraryRemovalForSmoke(); Flush();
            Check(window.Files.SequenceEqual(order), "undo-restores-them-in-place");

            // 11. The card menu also works out of selection mode: favorite and remove one song.
            Press(rows[2], Mouse.PreviewMouseUpEvent, MouseButton.Right);
            Check(menu.IsOpen && !Named<Button>("CardMenuPdfButton").IsEnabled && ((TextBlock)window.FindName("CardMenuFavoriteText")).Text == "Favoritar", "card-menu-for-a-song-without-score");
            Named<Button>("CardMenuFavoriteButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(rows[2].IsFavorite && !menu.IsOpen, "card-menu-favorite");
            Press(rows[2], Mouse.PreviewMouseUpEvent, MouseButton.Right);
            Named<Button>("CardMenuRemoveButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Flush();
            Check(!window.Files.Contains(rows[2]) && window.CanUndoLibraryRemoval, "card-menu-remove");
            Check(Teclas.App.MainWindow.KeyboardShortcuts.Any(s => s.Keys.Contains("Ctrl+clique")), "shortcut-list-mentions-the-selection");

            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks, enginesRun = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); window = null; Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close(); Shutdown(1);
        }
        finally { timeout.Stop(); }
    }

    /// <summary>The library card (song list and its bottom bar) that holds the list.</summary>
    private static FrameworkElement LibraryCard(FrameworkElement list)
    {
        DependencyObject node = list;
        while (VisualTreeHelper.GetParent(node) is { } parent) { node = parent; if (node is Border { Child: DockPanel } border) return border; }
        return list;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
