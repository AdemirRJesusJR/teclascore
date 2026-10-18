using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>An MP3 of the current score for practicing on the phone: slower, with a count-in, with or without the melody.</summary>
public partial class MainWindow
{
    internal Func<StudyAudioWindow, bool?> studyAudioDialog = window => window.ShowDialog();
    internal Func<string, string?>? studyAudioTarget = null;
    internal Func<string, string, StudyAudioOptions, CancellationToken, Task>? studyAudioExporter = null;

    public bool CanExportStudyAudio => IsIdle && CurrentScorePath is { } score && File.Exists(score);

    private async void StudyAudioClick(object sender, RoutedEventArgs e)
    {
        if (!CanExportStudyAudio || CurrentScorePath is not { } score || selectedFile is not { } row) return;
        if (studyAudioExporter is null && !File.Exists(MuseScorePath)) { Status = "Use Preparar este computador em Configurações para localizar o MuseScore Studio 4."; return; }
        var dialog = new StudyAudioWindow(ScoreStudyAudio.IsSupportedSpeed(ScorePlaybackSpeed) ? ScorePlaybackSpeed : 1) { Owner = this };
        if (studyAudioDialog(dialog) != true) return;
        var options = dialog.Options;
        string name = (string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle) + (options.Speed != 1 ? $" - {options.Speed * 100:0}%" : "") + (options.AccompanimentOnly ? " - acompanhamento" : "");
        string? target = studyAudioTarget is { } choose ? choose(name) : null;
        if (studyAudioTarget is null)
        {
            var save = new SaveFileDialog { Title = "Salvar áudio de estudo", Filter = "Áudio MP3|*.mp3", DefaultExt = ".mp3", AddExtension = true, FileName = ScoreFileName.Stem(null, name) + ".mp3" };
            if (settings.LastExportDirectory is { Length: > 0 } last && Directory.Exists(last)) save.InitialDirectory = last;
            target = save.ShowDialog(this) == true ? save.FileName : null;
        }
        if (target is null) return;
        await RunOperation(async () =>
        {
            Status = "Gerando o áudio de estudo no MuseScore…";
            if (studyAudioExporter is { } export) await export(score, target, options, cancellation!.Token);
            else await StudyAudioExport.ExportAsync(MuseScorePath, score, target, options, Path.Combine(PlaybackCacheDirectory, "audio-estudo", Guid.NewGuid().ToString("N")), cancellation!.Token);
            RememberExportDirectory(Path.GetDirectoryName(target)!);
            Status = $"Áudio de estudo salvo: {Path.GetFileName(target)}" + (options.CountIn ? " · com contagem" : "") + (options.AccompanimentOnly ? " · só o acompanhamento" : "") + ". Copie para o celular e pratique junto.";
        }, row, "Gerando áudio de estudo");
    }
}

/// <summary>Choices for the study audio: speed, count-in and accompaniment only.</summary>
public sealed class StudyAudioWindow : Window
{
    private readonly ComboBox speed;
    private readonly CheckBox countIn, accompaniment;
    public StudyAudioOptions Options => new((double)(speed.SelectedValue ?? 1.0), countIn.IsChecked == true, accompaniment.IsChecked == true);

    public StudyAudioWindow(double currentSpeed)
    {
        Title = "Áudio de estudo"; Width = 500; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; DialogUi.Theme(this);
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Áudio para praticar no celular", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Um MP3 da partitura, gerado pelo MuseScore, para ouvir e tocar junto longe do computador. A partitura não muda.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14) });
        var speedRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(speedRow);
        var speedLabel = new Label { Content = "_Velocidade", Padding = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        speed = new ComboBox { Name = "StudyAudioSpeed", Width = 160, ItemsSource = new[] { .5, .75, 1.0, 1.25 }.Select(v => new { Value = v, Label = v == 1 ? "100% · normal" : $"{v * 100:0}%" }).ToArray(),
            DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = new[] { .5, .75, 1.0, 1.25 }.Contains(currentSpeed) ? currentSpeed : 1.0 };
        speedLabel.Target = speed; AutomationProperties.SetName(speed, "Velocidade do áudio");
        speedRow.Children.Add(speedLabel); speedRow.Children.Add(speed);
        countIn = new CheckBox { Name = "StudyAudioCountIn", Content = "_Contagem de um compasso antes da música", IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
        accompaniment = new CheckBox { Name = "StudyAudioAccompaniment", Content = "Só o _acompanhamento (sem a melodia, para você tocá-la)", Margin = new Thickness(0, 0, 0, 16),
            ToolTip = "A melodia fica muda; as cifras continuam tocando no som de acordes do MuseScore." };
        panel.Children.Add(countIn); panel.Children.Add(accompaniment);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        var save = DialogUi.Button("Salvar MP3…", (_, _) => DialogResult = true, true); save.IsDefault = true; buttons.Children.Add(save);
        Loaded += (_, _) => speed.Focus();
    }
}
