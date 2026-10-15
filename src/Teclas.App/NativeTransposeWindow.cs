using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>A delta always applies to the immutable score opened by this dialog.</summary>
public sealed class NativeTransposeWindow : Window
{
    private readonly TranspositionPlan original;
    private readonly string? originalPreview;
    private readonly Func<TranspositionOptions, IProgress<string>, CancellationToken, Task<TranspositionPlan>> analyze;
    private readonly Func<TranspositionPlan, IProgress<string>, CancellationToken, Task<NativeScoreResult>> prepare;
    private readonly Func<TranspositionPlan, NativeScoreResult, IProgress<string>, CancellationToken, Task<NativeScoreResult>> publish;
    private readonly EditorScorePlayback playback;
    private readonly ComboBox interval;
    private readonly CheckBox removeDiagrams;
    private readonly TextBlock summary, limitations, status, progressText, audioClock;
    private readonly DataGrid changes;
    private readonly ScoreViewer viewer;
    private readonly RadioButton originalView, transposedView;
    private readonly Button prepareButton, saveButton, playButton, pauseButton, stopButton;
    private readonly StackPanel progressPanel;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch elapsed = new();
    private CancellationTokenSource? operation;
    private TranspositionPlan? plan;
    private NativeScoreResult? prepared;
    private int revision, preparedRevision = -1;
    private bool closed, pendingClose, preparingAudio;
    private string stage = "";
    public NativeScoreResult? Result { get; private set; }
    private bool CurrentPreview => prepared is not null && preparedRevision == revision && plan?.CanPrepare == true;
    private TranspositionOptions Options => new((int)(interval.SelectedValue ?? 0), removeDiagrams.IsChecked == true);

    public NativeTransposeWindow(TranspositionPlan original, string? originalPreview,
        Func<TranspositionOptions, IProgress<string>, CancellationToken, Task<TranspositionPlan>> analyze,
        Func<TranspositionPlan, IProgress<string>, CancellationToken, Task<NativeScoreResult>> prepare,
        Func<TranspositionPlan, NativeScoreResult, IProgress<string>, CancellationToken, Task<NativeScoreResult>> publish,
        EditorScorePlayback playback)
    {
        this.original = original; this.originalPreview = originalPreview;
        this.analyze = analyze; this.prepare = prepare; this.publish = publish; this.playback = playback;
        Title = "Transpor partitura MuseScore · TeclaScore"; Width = 1180; Height = 820; MinWidth = 940; MinHeight = 700;
        DialogUi.Theme(this);
        var root = new Grid { Margin = new(22) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new(0, 0, 0, 12) }; root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Escolha o novo tom", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = original.Metadata.Title, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 8) });
        header.Children.Add(new TextBlock { Text = "O intervalo parte desta versão aberta. Confira todas as pautas, as cifras e os diagramas antes de salvar uma nova cópia.", TextWrapping = TextWrapping.Wrap });
        var choices = new WrapPanel { Margin = new(0, 12, 0, 8) }; header.Children.Add(choices);
        choices.Children.Add(new TextBlock { Text = "Intervalo", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0) });
        interval = new ComboBox { Name = "NativeTransposeInterval", Width = 240, DisplayMemberPath = "Label", SelectedValuePath = "Value",
            ItemsSource = Enumerable.Range(-12, 25).Select(n => new { Value = n, Label = n == 0 ? "0 · tom da versão aberta" : $"{n:+0;-0} semitons" }).ToArray(), SelectedValue = 0 };
        choices.Children.Add(interval);
        removeDiagrams = new CheckBox { Name = "NativeTransposeRemoveDiagrams", Margin = new(0, 4, 0, 0),
            Content = new TextBlock { Text = "Remover da cópia imagens e diagramas não reconhecidos nos compassos", TextWrapping = TextWrapping.Wrap } };
        header.Children.Add(removeDiagrams);
        var body = new Grid(); Grid.SetRow(body, 1); root.Children.Add(body);
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = new(14) });
        body.ColumnDefinitions.Add(new() { Width = new(1.2, GridUnitType.Star) });
        var left = new Grid(); body.Children.Add(left);
        left.RowDefinitions.Add(new() { Height = GridLength.Auto });
        left.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 60 });
        left.RowDefinitions.Add(new() { Height = new(.6, GridUnitType.Star), MinHeight = 70 });
        summary = new TextBlock { Name = "NativeTransposeSummary", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) }; left.Children.Add(summary);
        changes = new DataGrid { Name = "NativeTransposeChanges", AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, MinRowHeight = 32, RowHeight = double.NaN };
        var wrap = new Style(typeof(TextBlock)); wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); wrap.Setters.Add(new Setter(MarginProperty, new Thickness(4)));
        foreach (var column in new[] { ("Pauta", "Staff", 55), ("Comp.", "Measure", 55), ("Elemento", "Kind", 95), ("Antes", "Before", 100), ("Depois", "After", 100) })
            changes.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new Binding(column.Item2), Width = new(column.Item3, DataGridLengthUnitType.Star), ElementStyle = wrap });
        Grid.SetRow(changes, 1); left.Children.Add(changes);
        limitations = new TextBlock { Name = "NativeTransposeLimitations", TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
        var scroll = new ScrollViewer { Content = limitations, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 2); left.Children.Add(scroll);
        var right = new DockPanel(); Grid.SetColumn(right, 2); body.Children.Add(right);
        var comparison = new WrapPanel { Margin = new(0, 0, 0, 10) }; DockPanel.SetDock(comparison, Dock.Top); right.Children.Add(comparison);
        originalView = new RadioButton { Content = "Original", Name = "NativeTransposeOriginal", GroupName = "TransposeView", IsChecked = true, Margin = new(0, 0, 18, 0) };
        transposedView = new RadioButton { Content = "Proposta transposta", Name = "NativeTransposeProposed", GroupName = "TransposeView" };
        comparison.Children.Add(originalView); comparison.Children.Add(transposedView);
        viewer = new ScoreViewer { FirstPagePath = originalPreview }; right.Children.Add(viewer);
        var footer = new StackPanel { Margin = new(0, 12, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        progressPanel = new StackPanel { Visibility = Visibility.Collapsed }; footer.Children.Add(progressPanel);
        progressPanel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5, Name = "NativeTransposeProgress" });
        var progressLine = new DockPanel { Margin = new(0, 6, 0, 4) }; progressPanel.Children.Add(progressLine);
        var cancel = Button("Cancelar operação", "NativeTransposeCancel", (_, _) => { operation?.Cancel(); playback.Stop(); }); DockPanel.SetDock(cancel, Dock.Right); progressLine.Children.Add(cancel);
        progressText = new TextBlock { TextWrapping = TextWrapping.Wrap }; progressLine.Children.Add(progressText);
        status = new TextBlock { Name = "NativeTransposeStatus", TextWrapping = TextWrapping.Wrap, MinHeight = 32 }; footer.Children.Add(status);
        var audio = new WrapPanel(); footer.Children.Add(audio);
        playButton = Button("▶ Ouvir original", "NativeTransposePlay", async (_, _) => await Play()); audio.Children.Add(playButton);
        pauseButton = Button("Pausar", "NativeTransposePause", (_, _) => { if (playback.IsPaused) playback.Resume(); else playback.Pause(); Refresh(); }); audio.Children.Add(pauseButton);
        stopButton = Button("■ Parar", "NativeTransposeStop", (_, _) => { if (preparingAudio) operation?.Cancel(); playback.Stop(); Refresh(); }); audio.Children.Add(stopButton);
        audioClock = new TextBlock { Text = "0:00 / 0:00", Margin = new(8, 8, 0, 0) }; audio.Children.Add(audioClock);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        prepareButton = Button("Preparar prévia", "NativeTransposePrepare", async (_, _) => await Prepare()); actions.Children.Add(prepareButton);
        actions.Children.Add(Button("Fechar", "NativeTransposeClose", (_, _) => Close()));
        saveButton = Button("Salvar nova versão", "NativeTransposeSave", async (_, _) => await Save(), true); actions.Children.Add(saveButton);
        interval.SelectionChanged += (_, _) => Invalidate(); removeDiagrams.Checked += (_, _) => Invalidate(); removeDiagrams.Unchecked += (_, _) => Invalidate();
        originalView.Checked += (_, _) => SelectPreview(); transposedView.Checked += (_, _) => SelectPreview();
        playback.StateChanged += PlaybackChanged; playback.PlaybackFailed += PlaybackFailed;
        timer.Tick += (_, _) => Tick(); timer.Start(); Closing += OnClosing;
        Closed += (_, _) => { closed = true; operation?.Cancel(); timer.Stop(); playback.StateChanged -= PlaybackChanged; playback.PlaybackFailed -= PlaybackFailed; playback.Dispose(); };
        Invalidate();
    }

    private static Button Button(string text, string name, RoutedEventHandler action, bool primary = false)
    { var button = DialogUi.Button(text, action, primary); button.Name = name; return button; }
    private void Invalidate()
    {
        revision++; operation?.Cancel(); playback.Stop(); prepared = null; plan = null; preparedRevision = -1;
        changes.ItemsSource = null; summary.Text = "Notas, armaduras e cifras serão conferidas juntas.";
        limitations.Text = "Diagramas TeclaScore reconhecidos acompanham a transposição. Imagens e diagramas não reconhecidos dentro dos compassos só podem ser retirados da nova cópia com a opção acima marcada. Imagens do cabeçalho são preservadas. Estruturas incompatíveis serão identificadas antes de salvar.";
        status.Text = Options.Semitones == 0 ? "Escolha um intervalo diferente de zero para preparar a transposição." : "Prepare a prévia para conferir as mudanças.";
        originalView.IsChecked = true; SelectPreview(); Refresh();
    }
    private void SelectPreview()
    {
        if (closed || viewer is null) return;
        playback.Stop(); bool proposal = transposedView.IsChecked == true && CurrentPreview;
        viewer.FirstPagePath = proposal ? prepared!.PreviewPages.FirstOrDefault() : originalPreview;
        playButton.Content = proposal ? "▶ Ouvir proposta" : "▶ Ouvir original"; Refresh();
    }
    private IProgress<string> Progress()
    {
        var current = operation;
        return new Progress<string>(text => { if (!closed && operation == current && current?.IsCancellationRequested == false) { stage = text; Tick(); } });
    }
    private Task Prepare() => Run("Conferindo a transposição", async token =>
    {
        int expected = revision; var options = Options; prepared = null; plan = null; preparedRevision = -1;
        originalView.IsChecked = true; SelectPreview();
        var proposed = await analyze(options, Progress(), token); token.ThrowIfCancellationRequested();
        if (closed || expected != revision) throw new OperationCanceledException(token);
        if (!string.Equals(proposed.SourcePath, original.SourcePath, StringComparison.OrdinalIgnoreCase) || proposed.SourceSha256 != original.SourceSha256 || proposed.Options != options)
            throw new IOException("A partitura mudou desde a abertura. Feche esta janela e abra a versão atual.");
        plan = proposed; changes.ItemsSource = plan.Changes;
        summary.Text = $"{plan.Notes} notas · {plan.Harmonies} cifras · {plan.KeySignatures} armaduras\n{plan.UpdatedDiagrams} diagramas atualizados · {plan.RemovedDiagrams} removidos da cópia";
        limitations.Text = string.Join("\n\n", plan.Limitations);
        if (!plan.CanPrepare) { status.Text = "Não foi criada uma prévia. Confira as observações e ajuste as opções quando indicado."; return; }
        var result = await prepare(plan, Progress(), token); token.ThrowIfCancellationRequested();
        if (closed || expected != revision) throw new OperationCanceledException(token);
        prepared = result; preparedRevision = expected; transposedView.IsChecked = true; SelectPreview();
        status.Text = "Prévia pronta. Compare as páginas, ouça e salve a nova versão.";
    });
    private Task Play() => Run("Preparando áudio", async token =>
    {
        preparingAudio = true; Refresh();
        try
        {
            bool proposal = transposedView.IsChecked == true && CurrentPreview;
            if (!proposal)
            {
                string hash = await Task.Run(() =>
                {
                    using var input = File.OpenRead(original.SourcePath);
                    return Convert.ToHexString(SHA256.HashData(input));
                }, token);
                token.ThrowIfCancellationRequested();
                if (!hash.Equals(original.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A partitura original mudou. Feche esta janela e reabra a versão atual antes de comparar.");
            }
            await playback.PlayAsync(proposal ? prepared!.Path : original.SourcePath, 1, Progress(), token);
        }
        finally { preparingAudio = false; }
    });
    private Task Save() => Run("Salvando transposição", async token =>
    {
        if (!CurrentPreview || plan is null || prepared is null) throw new InvalidOperationException("Prepare a prévia atual antes de salvar.");
        playback.Stop(); Result = await publish(plan, prepared, Progress(), token);
        // Publication is the commit point: a late cancellation must not hide the saved result.
        DialogResult = true;
    });
    private async Task Run(string title, Func<CancellationToken, Task> action)
    {
        if (closed || operation is not null) return;
        using var active = new CancellationTokenSource(); operation = active; stage = title; elapsed.Restart(); Refresh();
        try { await action(active.Token); }
        catch (OperationCanceledException) { if (!closed) status.Text = "Operação cancelada. A partitura original foi preservada."; }
        catch (Exception error) { if (!closed) status.Text = error.Message; }
        finally
        {
            operation = null; elapsed.Stop();
            if (!closed) { Refresh(); if (pendingClose) { pendingClose = false; Close(); } }
        }
    }
    private void Refresh()
    {
        if (closed) return;
        bool idle = operation is null;
        interval.IsEnabled = removeDiagrams.IsEnabled = idle;
        prepareButton.IsEnabled = idle && Options.Semitones != 0; saveButton.IsEnabled = idle && CurrentPreview;
        originalView.IsEnabled = idle; transposedView.IsEnabled = idle && CurrentPreview;
        playButton.IsEnabled = idle && !playback.IsPlaying; pauseButton.IsEnabled = idle && (playback.IsPlaying || playback.IsPaused);
        pauseButton.Content = playback.IsPaused ? "Continuar" : "Pausar"; stopButton.IsEnabled = playback.HasAudio || preparingAudio;
        progressPanel.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Tick()
    {
        if (closed) return;
        progressText.Text = $"{stage} · {elapsed.Elapsed:mm\\:ss}";
        audioClock.Text = $"{TimeSpan.FromSeconds(playback.PositionSeconds):m\\:ss} / {TimeSpan.FromSeconds(playback.DurationSeconds):m\\:ss}";
    }
    private void PlaybackChanged(object? sender, EventArgs e) { if (!closed) { Refresh(); Tick(); } }
    private void PlaybackFailed(object? sender, string message) { if (!closed) { status.Text = message; Refresh(); } }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Result is not null) return;
        if (operation is not null) { e.Cancel = true; pendingClose = true; operation.Cancel(); playback.Stop(); status.Text = "Cancelando antes de fechar…"; return; }
        if (CurrentPreview && MessageBox.Show(this, "Descartar a transposição preparada e fechar?", "Transpor partitura", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
    }
}
