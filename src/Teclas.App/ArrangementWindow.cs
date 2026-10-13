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

public sealed class ArrangementWindow : Window
{
    private sealed record LevelChoice(ArrangementLevel Value, string Label);
    private sealed record ChangeRow(ArrangementChange Change)
    {
        public string Position => $"Compasso {Change.Measure}" + (Change.Quarter is double quarter ? $" · posição {quarter + 1:0.##}" : "");
        public string Before => Change.Before;
        public string After => Change.After;
        public string Reason => Change.Reason;
    }

    private readonly NativeMusicModel model;
    private readonly string? originalPreview;
    private readonly Func<ArrangementOptions, IProgress<string>, CancellationToken, Task<ArrangementPlan>> analyze;
    private readonly Func<ArrangementPlan, IProgress<string>, CancellationToken, Task<NativeScoreResult>> prepare;
    private readonly Func<ArrangementPlan, NativeScoreResult, IProgress<string>, CancellationToken, Task<NativeScoreResult>> publish;
    private readonly EditorScorePlayback playback;
    private readonly ComboBox staff, voice, level, speed;
    private readonly DataGrid changes;
    private readonly TextBlock summary, limitations, status, previewStatus, operationText, audioClock;
    private readonly ScoreViewer viewer;
    private readonly RadioButton originalView, arrangementView;
    private readonly Button analyzeButton, prepareButton, saveButton, playButton, pauseButton, stopButton;
    private readonly StackPanel progressPanel;
    private readonly Slider seek;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch elapsed = new();
    private ArrangementPlan? plan;
    private NativeScoreResult? prepared;
    private CancellationTokenSource? operation;
    private int revision, planRevision = -1, preparedRevision = -1;
    private bool initializing, closed, pendingClose, discardConfirmed, updatingSeek, preparingAudio;
    private string operationStage = "";
    public NativeScoreResult? Result { get; private set; }
    public ArrangementPlan? ResultPlan { get; private set; }
    public bool HasChanges => planRevision == revision && plan?.Changes.Count > 0;
    private bool HasCurrentPreview => prepared is not null && preparedRevision == revision && planRevision == revision;

    public ArrangementWindow(NativeMusicModel model, string? initialPreview,
        Func<ArrangementOptions, IProgress<string>, CancellationToken, Task<ArrangementPlan>> analyze,
        Func<ArrangementPlan, IProgress<string>, CancellationToken, Task<NativeScoreResult>> prepare,
        Func<ArrangementPlan, NativeScoreResult, IProgress<string>, CancellationToken, Task<NativeScoreResult>> publish,
        EditorScorePlayback playback)
    {
        this.model = model; originalPreview = initialPreview; this.analyze = analyze;
        this.prepare = prepare; this.publish = publish; this.playback = playback;
        Title = "Arranjo por nível · TeclaScore"; Width = 1240; Height = 840; MinWidth = 1020; MinHeight = 680;
        DialogUi.Theme(this);
        var root = new Grid { Margin = new(20) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new(0, 0, 0, 10) }; root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Arranjo por nível", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = model.Metadata.Title, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 6) });
        header.Children.Add(new TextBlock { Text = "Escolha uma pauta e uma voz, confira as mudanças e ouça. Salvar cria uma nova versão no Histórico.", TextWrapping = TextWrapping.Wrap });
        var selectors = new WrapPanel { Margin = new(0, 12, 0, 0) }; header.Children.Add(selectors);
        Label(selectors, "Pauta");
        staff = new ComboBox { Name = "ArrangementStaff", Width = 75, ItemsSource = model.Measures.Select(m => m.Staff).Distinct().Order().ToArray(), Margin = new(0, 0, 12, 6) };
        selectors.Children.Add(staff); Label(selectors, "Voz");
        voice = new ComboBox { Name = "ArrangementVoice", Width = 65, Margin = new(0, 0, 12, 6) }; selectors.Children.Add(voice); Label(selectors, "Nível");
        level = new ComboBox { Name = "ArrangementLevel", Width = 165, DisplayMemberPath = nameof(LevelChoice.Label), Margin = new(0, 0, 12, 6),
            ItemsSource = new[] { new LevelChoice(ArrangementLevel.Original, "Original"), new LevelChoice(ArrangementLevel.Intermediate, "Intermediário"), new LevelChoice(ArrangementLevel.Beginner, "Iniciante") } };
        selectors.Children.Add(level);
        analyzeButton = Button("Recalcular mudanças", "ArrangementAnalyze", async (_, _) => await Analyze()); selectors.Children.Add(analyzeButton);
        var body = new Grid(); Grid.SetRow(body, 1); root.Children.Add(body);
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 400 });
        body.ColumnDefinitions.Add(new() { Width = new(12) });
        body.ColumnDefinitions.Add(new() { Width = new(1.15, GridUnitType.Star), MinWidth = 420 });
        var left = new Grid(); body.Children.Add(left);
        left.RowDefinitions.Add(new() { Height = GridLength.Auto });
        left.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 64 });
        left.RowDefinitions.Add(new() { Height = new(.55, GridUnitType.Star), MinHeight = 76 });
        summary = new TextBlock { Name = "ArrangementSummary", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 10) };
        left.Children.Add(summary);
        changes = new DataGrid { Name = "ArrangementChanges", AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single, RowHeight = double.NaN, MinRowHeight = 44 };
        var wrap = new Style(typeof(TextBlock));
        wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        wrap.Setters.Add(new Setter(MarginProperty, new Thickness(5)));
        changes.Columns.Add(new DataGridTextColumn { Header = "Trecho", Binding = new Binding(nameof(ChangeRow.Position)), Width = 116, ElementStyle = wrap });
        changes.Columns.Add(new DataGridTextColumn { Header = "Antes", Binding = new Binding(nameof(ChangeRow.Before)), Width = 90, ElementStyle = wrap });
        changes.Columns.Add(new DataGridTextColumn { Header = "Depois", Binding = new Binding(nameof(ChangeRow.After)), Width = 90, ElementStyle = wrap });
        changes.Columns.Add(new DataGridTextColumn { Header = "Motivo", Binding = new Binding(nameof(ChangeRow.Reason)), Width = new(1, DataGridLengthUnitType.Star), MinWidth = 130, ElementStyle = wrap });
        Grid.SetRow(changes, 1); left.Children.Add(changes);
        var limits = new DockPanel { Margin = new(0, 10, 0, 0) }; Grid.SetRow(limits, 2); left.Children.Add(limits);
        var limitsHeader = new TextBlock { Text = "O que conferir", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 5) }; DockPanel.SetDock(limitsHeader, Dock.Top); limits.Children.Add(limitsHeader);
        limitations = new TextBlock { Name = "ArrangementLimitations", TextWrapping = TextWrapping.Wrap };
        limits.Children.Add(new ScrollViewer { Content = limitations, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var right = new DockPanel(); Grid.SetColumn(right, 2); body.Children.Add(right);
        var previewHeader = new StackPanel(); DockPanel.SetDock(previewHeader, Dock.Top); right.Children.Add(previewHeader);
        var compare = new WrapPanel(); previewHeader.Children.Add(compare);
        originalView = new RadioButton { Name = "ArrangementOriginalView", Content = "Original", GroupName = "ArrangementPreview", IsChecked = true, Margin = new(0, 0, 16, 8) };
        arrangementView = new RadioButton { Name = "ArrangementDraftView", Content = "Arranjo preparado", GroupName = "ArrangementPreview", Margin = new(0, 0, 0, 8) };
        compare.Children.Add(originalView); compare.Children.Add(arrangementView);
        previewStatus = new TextBlock { Name = "ArrangementPreviewStatus", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) }; previewHeader.Children.Add(previewStatus);
        viewer = new ScoreViewer { FirstPagePath = initialPreview }; right.Children.Add(viewer);
        var footer = new StackPanel { Margin = new(0, 10, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        progressPanel = new StackPanel { Visibility = Visibility.Collapsed }; footer.Children.Add(progressPanel);
        progressPanel.Children.Add(new ProgressBar { Name = "ArrangementProgress", IsIndeterminate = true, Height = 5 });
        var progressLine = new DockPanel { Margin = new(0, 6, 0, 6) }; progressPanel.Children.Add(progressLine);
        var cancel = Button("Cancelar operação", "ArrangementCancel", (_, _) => { operation?.Cancel(); playback.Stop(); });
        DockPanel.SetDock(cancel, Dock.Right); progressLine.Children.Add(cancel);
        operationText = new TextBlock { Name = "ArrangementOperation", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; progressLine.Children.Add(operationText);
        status = new TextBlock { Name = "ArrangementStatus", TextWrapping = TextWrapping.Wrap, MinHeight = 30 }; footer.Children.Add(status);
        var audio = new WrapPanel(); footer.Children.Add(audio);
        playButton = Button("▶ Ouvir original", "ArrangementPlay", async (_, _) => await Play()); audio.Children.Add(playButton);
        pauseButton = Button("Pausar", "ArrangementPause", (_, _) => { if (playback.IsPaused) playback.Resume(); else playback.Pause(); Refresh(); }); audio.Children.Add(pauseButton);
        stopButton = Button("■ Parar", "ArrangementStop", (_, _) => { if (preparingAudio) operation?.Cancel(); playback.Stop(); status.Text = "Reprodução parada."; }); audio.Children.Add(stopButton);
        speed = new ComboBox { Name = "ArrangementSpeed", Width = 85, ItemsSource = new[] { .5, .75, 1.0 }, SelectedItem = 1.0, ItemStringFormat = "{0:P0}", Margin = new(0, 0, 8, 8) }; audio.Children.Add(speed);
        seek = new Slider { Name = "ArrangementSeek", Width = 180, Minimum = 0, Maximum = 1, Margin = new(0, 4, 8, 8) }; audio.Children.Add(seek);
        audioClock = new TextBlock { Text = "0:00 / 0:00", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 0, 8) }; audio.Children.Add(audioClock);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        prepareButton = Button("Preparar prévia", "ArrangementPrepare", async (_, _) => await Prepare()); actions.Children.Add(prepareButton);
        actions.Children.Add(Button("Fechar", "ArrangementClose", (_, _) => Close()));
        saveButton = Button("Salvar nova versão", "ArrangementSave", async (_, _) => await Save(), true); actions.Children.Add(saveButton);
        staff.SelectionChanged += (_, _) => { if (!initializing) { SetVoices(); Invalidate(); } };
        voice.SelectionChanged += (_, _) => { if (!initializing) Invalidate(); };
        level.SelectionChanged += (_, _) => { if (!initializing) Invalidate(); };
        originalView.Checked += (_, _) => SelectPreview();
        arrangementView.Checked += (_, _) => SelectPreview();
        speed.SelectionChanged += (_, _) => { playback.Stop(); status.Text = "Clique em Ouvir para usar a velocidade selecionada."; };
        seek.ValueChanged += (_, _) => { if (!updatingSeek && playback.HasAudio) playback.Seek(seek.Value); };
        playback.StateChanged += PlaybackChanged; playback.PlaybackFailed += PlaybackFailed;
        timer.Tick += (_, _) => Tick(); timer.Start();
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; operation?.Cancel(); timer.Stop(); playback.StateChanged -= PlaybackChanged; playback.PlaybackFailed -= PlaybackFailed; playback.Dispose(); };
        initializing = true; staff.SelectedIndex = 0; level.SelectedIndex = 0; SetVoices(); initializing = false;
        Invalidate();
    }

    private static void Label(Panel parent, string text) => parent.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 6) });
    private static Button Button(string text, string name, RoutedEventHandler handler, bool primary = false)
    { var result = DialogUi.Button(text, handler, primary); result.Name = name; return result; }
    private void SetVoices()
    {
        bool wasInitializing = initializing; initializing = true;
        try
        {
            voice.ItemsSource = staff.SelectedItem is int id ? model.Measures.Where(m => m.Staff == id).SelectMany(m => m.Events).Select(e => e.Voice).Distinct().Order().ToArray() : Array.Empty<int>();
            voice.SelectedIndex = 0;
        }
        finally { initializing = wasInitializing; }
    }
    private ArrangementOptions Options()
    {
        if (staff.SelectedItem is not int s || voice.SelectedItem is not int v || level.SelectedItem is not LevelChoice choice)
            throw new InvalidOperationException("Escolha uma pauta, uma voz e um nível disponíveis.");
        return new(choice.Value, s, v);
    }
    private void Invalidate()
    {
        if (closed) return;
        revision++; operation?.Cancel(); playback.Stop(); plan = null; prepared = null; planRevision = preparedRevision = -1;
        changes.ItemsSource = Array.Empty<ChangeRow>();
        summary.Text = "Confira primeiro as mudanças propostas para a pauta e a voz escolhidas.";
        limitations.Text = "Os níveis propõem simplificações conservadoras. Trechos que não podem ser alterados com segurança serão mantidos e explicados aqui. A reprodução inclui a partitura inteira.";
        status.Text = level.SelectedItem is LevelChoice { Value: ArrangementLevel.Original }
            ? "Original serve para comparação. Escolha outro nível para propor um arranjo."
            : "Escolhas alteradas. Recalcule as mudanças ou prepare a prévia.";
        originalView.IsChecked = true; SelectPreview(); Refresh();
    }
    private void SelectPreview()
    {
        if (closed || viewer is null) return;
        playback.Stop();
        bool draft = arrangementView.IsChecked == true && HasCurrentPreview;
        viewer.FirstPagePath = draft ? prepared!.PreviewPages.FirstOrDefault() : originalPreview;
        previewStatus.Text = draft ? "Arranjo preparado · confira todas as páginas antes de salvar."
            : "Partitura original · a reprodução inclui todas as pautas e vozes.";
        playButton.Content = draft ? "▶ Ouvir arranjo" : "▶ Ouvir original";
        Refresh();
    }
    private IProgress<string> Progress()
    {
        var request = operation;
        return new Progress<string>(text => { if (!closed && operation == request && request is not null && !request.IsCancellationRequested) { operationStage = text; Tick(); } });
    }
    private async Task EnsurePlan(CancellationToken token)
    {
        if (plan is not null && planRevision == revision) return;
        int expected = revision; var options = Options();
        var proposal = await analyze(options, Progress(), token);
        token.ThrowIfCancellationRequested();
        if (closed || revision != expected) throw new OperationCanceledException(token);
        if (!string.Equals(proposal.SourcePath, model.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(proposal.SourceSha256, model.SourceSha256, StringComparison.OrdinalIgnoreCase) || proposal.Options != options)
            throw new IOException("A partitura mudou desde a abertura. Reabra a versão atual antes de preparar o arranjo.");
        plan = proposal; planRevision = expected;
        changes.ItemsSource = plan.Changes.Select(change => new ChangeRow(change)).ToArray();
        int measures = plan.Changes.Select(change => change.Measure).Distinct().Count();
        string changeCount = plan.Changes.Count == 1 ? "1 alteração proposta" : $"{plan.Changes.Count} alterações propostas";
        string measureCount = measures == 1 ? "1 compasso" : $"{measures} compassos";
        summary.Text = $"{changeCount} em {measureCount} · pauta {options.Staff}, voz {options.Voice}.";
        limitations.Text = string.Join("\n\n", plan.Limitations.Append("A reprodução inclui a partitura inteira. Confira musicalmente a proposta antes de salvar."));
        status.Text = plan.CanPrepare && HasChanges ? "Mudanças calculadas. Prepare a prévia para conferir e ouvir o arranjo."
            : "Nenhuma nova versão será salva. Confira as observações e compare com o original.";
    }
    private Task Analyze() => Run("Calculando mudanças do arranjo", async token =>
    {
        playback.Stop(); prepared = null; preparedRevision = -1; plan = null; planRevision = -1;
        changes.ItemsSource = Array.Empty<ChangeRow>();
        summary.Text = "Ainda não há uma proposta atual para estas escolhas.";
        limitations.Text = "As observações serão exibidas após um novo cálculo.";
        originalView.IsChecked = true; SelectPreview();
        await EnsurePlan(token);
    });
    private async Task PrepareCurrent(CancellationToken token)
    {
        if (HasCurrentPreview) return;
        await EnsurePlan(token);
        if (plan is not { CanPrepare: true } current || !HasChanges) return;
        int expected = revision;
        var result = await prepare(current, Progress(), token);
        token.ThrowIfCancellationRequested();
        if (closed || expected != revision || plan != current) throw new OperationCanceledException(token);
        prepared = result; preparedRevision = expected;
        arrangementView.IsChecked = true; SelectPreview();
        status.Text = "Prévia pronta. Compare, ouça e salve uma nova versão quando estiver satisfeito.";
    }
    private Task Prepare() => Run("Preparando arranjo por nível", async token => { playback.Stop(); await PrepareCurrent(token); });
    private Task Play() => Run("Preparando reprodução", async token =>
    {
        bool draft = arrangementView.IsChecked == true;
        if (draft && !HasCurrentPreview) throw new InvalidOperationException("Prepare a prévia atual antes de ouvir o arranjo.");
        string path = draft ? prepared!.Path : model.SourcePath;
        if (!draft)
        {
            await using var stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), model.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A partitura mudou desde a abertura. Reabra a versão atual antes de ouvir.");
        }
        await playback.PlayAsync(path, speed.SelectedItem is double rate ? rate : 1, Progress(), token);
        token.ThrowIfCancellationRequested();
        status.Text = draft ? "Reproduzindo o arranjo preparado completo." : "Reproduzindo a partitura original completa.";
    }, true);
    private Task Save() => Run("Salvando nova versão do arranjo", async token =>
    {
        if (!HasCurrentPreview || !HasChanges || plan is not { CanPrepare: true } current)
            throw new InvalidOperationException("Prepare a prévia do arranjo atual antes de salvar.");
        playback.Stop();
        Result = await publish(current, prepared!, Progress(), token);
        ResultPlan = current; discardConfirmed = true; DialogResult = true;
    });
    private async Task Run(string title, Func<CancellationToken, Task> action, bool audio = false)
    {
        if (closed || operation is not null) return;
        var request = new CancellationTokenSource(); operation = request; preparingAudio = audio;
        operationStage = title; status.Text = title + "…"; elapsed.Restart(); Refresh();
        try { await Dispatcher.Yield(DispatcherPriority.Background); request.Token.ThrowIfCancellationRequested(); await action(request.Token); }
        catch (OperationCanceledException) { if (!closed) { playback.Stop(); status.Text = "Operação cancelada. A partitura original foi preservada."; } }
        catch (Exception error) { if (!closed) { playback.Stop(); status.Text = error.Message; } }
        finally
        {
            if (operation == request) operation = null;
            preparingAudio = false; request.Dispose(); elapsed.Stop();
            if (!closed) { Refresh(); if (pendingClose) { pendingClose = false; Close(); } }
        }
    }
    private void PlaybackChanged(object? sender, EventArgs e) { if (!closed) { Refresh(); Tick(); } }
    private void PlaybackFailed(object? sender, string text) { if (!closed) status.Text = "Não foi possível ouvir a partitura. " + text; }
    private void Refresh()
    {
        bool idle = operation is null, selected = staff.SelectedItem is int && voice.SelectedItem is int;
        staff.IsEnabled = voice.IsEnabled = level.IsEnabled = speed.IsEnabled = idle;
        analyzeButton.IsEnabled = idle && selected;
        prepareButton.IsEnabled = idle && selected && level.SelectedItem is LevelChoice { Value: not ArrangementLevel.Original } && (plan is null || plan.CanPrepare);
        saveButton.IsEnabled = idle && HasCurrentPreview && HasChanges && plan?.CanPrepare == true;
        originalView.IsEnabled = idle; arrangementView.IsEnabled = idle && HasCurrentPreview;
        playButton.IsEnabled = idle && (arrangementView.IsChecked == true ? HasCurrentPreview : File.Exists(model.SourcePath));
        pauseButton.IsEnabled = idle && playback.HasAudio; pauseButton.Content = playback.IsPaused ? "Continuar" : "Pausar";
        stopButton.IsEnabled = playback.HasAudio || preparingAudio;
        seek.IsEnabled = idle && playback.HasAudio;
        progressPanel.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Tick()
    {
        if (closed) return;
        if (operation is not null) operationText.Text = $"{operationStage} · {elapsed.Elapsed:mm\\:ss}";
        updatingSeek = true;
        try { seek.Maximum = Math.Max(1, playback.DurationSeconds); seek.Value = playback.PositionSeconds; }
        finally { updatingSeek = false; }
        audioClock.Text = $"{TimeSpan.FromSeconds(playback.PositionSeconds):m\\:ss} / {TimeSpan.FromSeconds(playback.DurationSeconds):m\\:ss}";
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (discardConfirmed || Result is not null) return;
        if (operation is not null) { e.Cancel = true; pendingClose = true; operation.Cancel(); playback.Stop(); status.Text = "Cancelando antes de fechar…"; return; }
        if (HasCurrentPreview && HasChanges && MessageBox.Show(this, "Descartar o arranjo preparado e fechar?", "Arranjo por nível",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
    }
}
