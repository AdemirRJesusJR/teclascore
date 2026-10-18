using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Search the library by playing: a few notes on the MIDI keyboard (or the screen) list the songs with that melody.</summary>
public sealed class MelodySearchWindow : Window
{
    private const int Low = 48, High = 84; // C3–C6
    private readonly IReadOnlyList<(string Id, string Title, IReadOnlyList<int> Melody)> songs;
    private readonly List<int> played = [];
    private readonly TextBlock notes, summary;
    private readonly ListBox results;
    private readonly ComboBox devices;
    private MidiKeyboardInput? input;
    public IReadOnlyList<MelodySearchHit> Hits { get; private set; } = [];
    public string? ChosenId { get; private set; }

    public MelodySearchWindow(IReadOnlyList<(string Id, string Title, IReadOnlyList<int> Melody)> songs)
    {
        Title = "Buscar tocando"; Width = 820; Height = 600; MinWidth = 640; MinHeight = 480; DialogUi.Theme(this);
        this.songs = songs;
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Buscar tocando", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = $"Toque um trecho da melodia (pelo menos {MelodySearch.MinimumNotes} notas), em qualquer tom. A busca compara os intervalos com a melodia de cada música da biblioteca.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
        var line = new WrapPanel(); top.Children.Add(line);
        line.Children.Add(new TextBlock { Text = "Teclado MIDI", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
        var names = MidiKeyboardInput.Devices();
        devices = new ComboBox { Width = 240, ItemsSource = names.Count > 0 ? names : ["Nenhum teclado MIDI encontrado"], SelectedIndex = 0, IsEnabled = names.Count > 0, Margin = new Thickness(0, 0, 12, 6) };
        AutomationProperties.SetName(devices, "Teclado MIDI");
        devices.SelectionChanged += (_, _) => OpenDevice();
        line.Children.Add(devices);
        line.Children.Add(DialogUi.Button("Apagar notas", (_, _) => { played.Clear(); Update(); }));
        notes = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap };
        top.Children.Add(notes);
        var keyboard = new Canvas { Height = 96, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10) }; top.Children.Add(keyboard);
        BuildKeyboard(keyboard);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(DialogUi.Button("Abrir a música", (_, _) => Choose()));
        var close = DialogUi.Button("Fechar", (_, _) => Close(), true); close.IsCancel = true; footer.Children.Add(close);
        summary = new TextBlock { Margin = new Thickness(0, 0, 0, 6) }; DockPanel.SetDock(summary, Dock.Top); root.Children.Add(summary);
        results = new ListBox { DisplayMemberPath = "Label" }; AutomationProperties.SetName(results, "Músicas encontradas");
        results.MouseDoubleClick += (_, e) => { if (e.ChangedButton == MouseButton.Left) Choose(); };
        root.Children.Add(results);
        Loaded += (_, _) => { OpenDevice(); Update(); };
        Closed += (_, _) => input?.Dispose();
    }

    private void OpenDevice()
    {
        input?.Dispose(); input = null;
        if (!devices.IsEnabled || devices.SelectedIndex < 0) return;
        try { input = new MidiKeyboardInput(devices.SelectedIndex); input.NoteOn += (pitch, _) => Dispatcher.InvokeAsync(() => Play(pitch)); }
        catch (InvalidOperationException error) { summary.Text = error.Message + " Use as teclas da tela."; }
    }

    /// <summary>Adds a played note (the last 16 are kept) and searches again.</summary>
    internal void Play(int pitch)
    {
        played.Add(pitch);
        if (played.Count > 16) played.RemoveAt(0);
        Update();
    }

    private void Update()
    {
        notes.Text = played.Count == 0 ? "Toque as notas…" : string.Join("  ", played.Select(PracticeSession.NoteName));
        Hits = MelodySearch.Search(played, songs);
        results.ItemsSource = Hits.Select(hit => new { hit.Id, Label = $"{hit.Title}  ·  {hit.Similarity:P0} parecida" }).ToArray();
        summary.Text = played.Count < MelodySearch.MinimumNotes ? $"Faltam {MelodySearch.MinimumNotes - played.Count} nota(s) para buscar."
            : Hits.Count == 0 ? "Nenhuma música com essa melodia. Tente outro trecho ou apague e toque de novo." : $"{Hits.Count} música(s) encontrada(s). Clique duas vezes para abrir.";
        if (Hits.Count > 0) results.SelectedIndex = 0;
    }

    private void Choose()
    {
        if (results.SelectedIndex < 0 || results.SelectedIndex >= Hits.Count) return;
        ChosenId = Hits[results.SelectedIndex].Id; DialogResult = true;
    }

    private void BuildKeyboard(Canvas keyboard)
    {
        const double white = 20, height = 90; int index = 0; var blacks = new List<(int, double)>();
        for (int pitch = Low; pitch <= High; pitch++)
        {
            if ((pitch % 12) is 1 or 3 or 6 or 8 or 10) { blacks.Add((pitch, index * white - white * .3)); continue; }
            var key = new Rectangle { Width = white, Height = height, Stroke = Brushes.Black, StrokeThickness = 1, Fill = Brushes.White, Tag = pitch, Cursor = Cursors.Hand };
            Canvas.SetLeft(key, index++ * white); keyboard.Children.Add(key);
        }
        foreach (var (pitch, x) in blacks)
        {
            var key = new Rectangle { Width = white * .6, Height = height * .62, Fill = Brushes.Black, Tag = pitch, Cursor = Cursors.Hand };
            Canvas.SetLeft(key, x); keyboard.Children.Add(key);
        }
        keyboard.Width = index * white;
        foreach (Rectangle key in keyboard.Children) key.MouseLeftButtonDown += (sender, _) => Play((int)((Rectangle)sender).Tag);
    }
}
