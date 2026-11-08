using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Teclas.App;

/// <summary>
/// Review in sequence: the generated scores one after another, the riskiest first, with the page large and keyboard
/// shortcuts: A approves, R makes it again with the next melody candidate, E opens it in the editor, arrows move,
/// Shift+arrows turn the pages and Space plays the song (its audio is prepared ahead, the next song's too).
/// </summary>
public sealed class ReviewSequenceWindow : Window
{
    private readonly IReadOnlyList<MidiRow> rows;
    private readonly Action<MidiRow> approve;
    private readonly Func<MidiRow, Task<string>> redo;
    private readonly Image page = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock position = new() { Opacity = .7 }, title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
        reasons = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) }, status = new() { TextWrapping = TextWrapping.Wrap, Opacity = .8 };
    private bool busy;
    private readonly Func<MidiRow, CancellationToken, Task<string>>? prepareAudio;
    private readonly Dictionary<MidiRow, Task<string>> audioTasks = [];
    private readonly CancellationTokenSource audioCancellation = new();
    private readonly MediaPlayer player = new();
    private IReadOnlyList<string> pages = [];
    private bool playing;
    public int Index { get; private set; }
    /// <summary>The page shown of the current song (0-based) and how many it has.</summary>
    public int PageIndex { get; private set; }
    public int PageCount => pages.Count;
    public bool IsPlaying => playing;
    public MidiRow? Current => Index < rows.Count ? rows[Index] : null;
    /// <summary>The song to open in the editor after the window closes (E).</summary>
    public MidiRow? EditRequested { get; private set; }
    public int Approved { get; private set; }
    public string StatusText => status.Text;

    /// <param name="prepareAudio">Prepares (or finds in the cache) the audio of a song and returns its file; null hides Space.</param>
    public ReviewSequenceWindow(IReadOnlyList<MidiRow> rows, Action<MidiRow> approve, Func<MidiRow, Task<string>> redo,
        Func<MidiRow, CancellationToken, Task<string>>? prepareAudio = null)
    {
        this.rows = rows; this.approve = approve; this.redo = redo; this.prepareAudio = prepareAudio;
        player.MediaEnded += (_, _) => { playing = false; player.Stop(); };
        Closed += (_, _) => { audioCancellation.Cancel(); player.Stop(); player.Close(); };
        Title = "Revisão em sequência"; Width = 980; Height = 900; MinWidth = 640; MinHeight = 520; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(position); top.Children.Add(title); top.Children.Add(reasons);
        AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Polite);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        var previous = DialogUi.Button("◀", (_, _) => Move(-1)); previous.ToolTip = "Música anterior (←); Shift+← volta a página"; buttons.Children.Add(previous);
        if (prepareAudio is not null)
        {
            var listen = DialogUi.Button("Ouvir (Espaço)", async (_, _) => await TogglePlayAsync());
            listen.ToolTip = "Ouvir ou pausar a partitura (Espaço); o áudio da próxima música é preparado enquanto você revisa";
            buttons.Children.Add(listen);
        }
        buttons.Children.Add(DialogUi.Button("Aprovar (A)", (_, _) => Approve(), true));
        buttons.Children.Add(DialogUi.Button("Refazer com outra melodia (R)", async (_, _) => await RedoAsync()));
        buttons.Children.Add(DialogUi.Button("Editar (E)", (_, _) => Edit()));
        var next = DialogUi.Button("▶", (_, _) => Move(1)); next.ToolTip = "Próxima música (→); Shift+→ avança a página"; buttons.Children.Add(next);
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; buttons.Children.Add(close);
        footer.Children.Add(status);
        var frame = new Border { BorderThickness = new Thickness(1), Child = page, Background = Brushes.White };
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        AutomationProperties.SetName(page, "Página da partitura");
        root.Children.Add(frame);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.OriginalSource is TextBox) return;
            if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key is Key.Left or Key.Right) { e.Handled = true; TurnPage(e.Key == Key.Right ? 1 : -1); return; }
            switch (e.Key)
            {
                case Key.Space when prepareAudio is not null: e.Handled = true; await TogglePlayAsync(); break;
                case Key.A: e.Handled = true; Approve(); break;
                case Key.R: e.Handled = true; await RedoAsync(); break;
                case Key.E: e.Handled = true; Edit(); break;
                case Key.Right: case Key.PageDown: e.Handled = true; Move(1); break;
                case Key.Left: case Key.PageUp: e.Handled = true; Move(-1); break;
            }
        };
        Show(0);
    }

    private void Show(int index)
    {
        Index = Math.Clamp(index, 0, Math.Max(0, rows.Count));
        if (Current is not { } row)
        {
            position.Text = $"{rows.Count} de {rows.Count}"; title.Text = "Fim da revisão"; page.Source = null;
            reasons.Text = $"{Approved} partitura(s) aprovada(s) nesta revisão. Feche ou volte com ◀.";
            return;
        }
        title.Text = string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle;
        reasons.Text = (row.ConfidenceBadge.Length > 0 ? row.ConfidenceBadge + " — " : "") + row.ConfidenceToolTip;
        StopAudio();
        pages = ScorePageFiles.Discover(row.PreviewPath);
        if (pages.Count == 0 && row.PreviewPath is { } single && File.Exists(single)) pages = [single];
        ShowPage(0);
        if (page.Source is null) status.Text = "Sem prévia desta partitura; abra no editor (E) para conferir.";
        // The next song's audio is prepared while this one is reviewed, so Space starts at once.
        PrewarmAudio(row);
        if (Index + 1 < rows.Count) PrewarmAudio(rows[Index + 1]);
    }

    private void ShowPage(int index)
    {
        PageIndex = pages.Count == 0 ? 0 : Math.Clamp(index, 0, pages.Count - 1);
        page.Source = pages.Count == 0 ? null : LoadPage(pages[PageIndex]);
        position.Text = $"{Index + 1} de {rows.Count}" + (pages.Count > 1 ? $" · página {PageIndex + 1} de {pages.Count}" : "");
    }

    /// <summary>Shift+→ / Shift+←: the next or previous page of the same song.</summary>
    internal void TurnPage(int step)
    {
        if (Current is null || pages.Count < 2) return;
        ShowPage(PageIndex + step);
    }

    private Task<string>? PrewarmAudio(MidiRow row)
    {
        if (prepareAudio is null || row.ScorePath is null) return null;
        if (audioTasks.TryGetValue(row, out var running) && !running.IsFaulted && !running.IsCanceled) return running;
        var task = prepareAudio(row, audioCancellation.Token);
        audioTasks[row] = task;
        return task;
    }

    /// <summary>Space: plays the current song from the start (preparing it if needed), or pauses it.</summary>
    internal async Task TogglePlayAsync()
    {
        if (Current is not { } row) return;
        if (playing) { StopAudio(); status.Text = "Áudio parado."; return; }
        if (PrewarmAudio(row) is not { } preparing) { status.Text = "Esta partitura não tem áudio para ouvir."; return; }
        try
        {
            if (!preparing.IsCompleted) status.Text = "Preparando o áudio…";
            string path = await preparing;
            if (!ReferenceEquals(Current, row) || !IsLoaded && !IsVisible) return;
            player.Open(new Uri(path)); player.Play(); playing = true;
            status.Text = "Ouvindo · Espaço para parar.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException or UriFormatException)
        { audioTasks.Remove(row); status.Text = "Não foi possível preparar o áudio: " + error.Message; }
    }

    private void StopAudio()
    {
        if (!playing) return;
        playing = false; player.Stop();
    }

    private static ImageSource? LoadPage(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.CreateOptions = BitmapCreateOptions.IgnoreImageCache; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException) { return null; }
    }

    internal void Move(int step) { if (!busy) { status.Text = ""; Show(Index + step); } }

    internal void Approve()
    {
        if (busy || Current is not { } row) return;
        approve(row); Approved++;
        status.Text = $"Aprovada: {title.Text}.";
        Show(Index + 1);
    }

    internal async Task RedoAsync()
    {
        if (busy || Current is not { } row) return;
        busy = true; status.Text = "Gerando de novo com a próxima melodia candidata…";
        try { string message = await redo(row); audioTasks.Remove(row); Show(Index); status.Text = message; }
        catch (Exception error) when (error is InvalidOperationException or IOException) { status.Text = error.Message; }
        finally { busy = false; }
    }

    internal void Edit()
    {
        if (busy || Current is not { } row) return;
        EditRequested = row; Close();
    }
}
