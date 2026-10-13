using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Teclas.Core;

namespace Teclas.App;

/// <summary>A local ordering draft; track selection and audition are unchanged until explicit acceptance.</summary>
public sealed class MelodyPriorityWindow : Window
{
    private sealed record PriorityItem(SourceKey Key, string Source);
    private readonly ObservableCollection<PriorityItem> items;
    private readonly ListBox list;
    private readonly Button up, down;
    public IReadOnlyList<SourceKey> Priority => items.Select(item => item.Key).ToArray();

    public MelodyPriorityWindow(IReadOnlyList<TrackRow> orderedSelectedTracks)
    {
        ArgumentNullException.ThrowIfNull(orderedSelectedTracks);
        if (orderedSelectedTracks.Count < 2 || orderedSelectedTracks.Any(track => !track.IsMelody) ||
            orderedSelectedTracks.Select(track => track.Source.Key).Distinct().Count() != orderedSelectedTracks.Count)
            throw new ArgumentException("Selecione pelo menos duas pistas distintas para ordenar a melodia.", nameof(orderedSelectedTracks));
        items = new(orderedSelectedTracks.Select(track => new PriorityItem(track.Source.Key,
            $"Pista {track.Track} · Canal {track.Channel} · {track.Instrument}")));
        Title = "Prioridade das pistas · TeclaScore"; Width = 720; Height = 500; MinWidth = 560; MinHeight = 420;
        DialogUi.Theme(this);
        var root = new Grid { Margin = new(24) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new(0, 0, 0, 16) }; root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Uma pauta, várias pistas", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "A pista no topo tem preferência quando as notas se sobrepõem. As seguintes completam a melodia nos trechos disponíveis.",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 8) });
        header.Children.Add(new TextBlock { Text = "Selecione uma fonte e use Subir ou Descer. A ordem só muda na biblioteca ao aplicar.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var body = new Grid(); Grid.SetRow(body, 1); root.Children.Add(body);
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        list = new ListBox { Name = "MelodyPriorityList", ItemsSource = items, SelectedIndex = 0,
            SelectionMode = SelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(PriorityItem.Source)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(MarginProperty, new Thickness(10, 12, 10, 12));
        list.ItemTemplate = new DataTemplate { VisualTree = text };
        body.Children.Add(list);
        var moves = new StackPanel { Margin = new(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(moves, 1); body.Children.Add(moves);
        up = DialogUi.Button("↑ Subir", (_, _) => Move(-1)); up.Name = "MelodyPriorityUp"; moves.Children.Add(up);
        down = DialogUi.Button("↓ Descer", (_, _) => Move(1)); down.Name = "MelodyPriorityDown"; moves.Children.Add(down);
        var footer = new StackPanel { Margin = new(0, 16, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        footer.Children.Add(new TextBlock { Text = "Mudar a prioridade exige analisar ou gerar novamente. A partitura anterior continua no Histórico.",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12), FontSize = 12 });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        var cancel = DialogUi.Button("Cancelar", (_, _) => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        var apply = DialogUi.Button("Aplicar prioridade", (_, _) => DialogResult = true, true); apply.IsDefault = true;
        apply.Name = "MelodyPriorityApply"; actions.Children.Add(apply);
        list.SelectionChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Move(int delta)
    {
        int from = list.SelectedIndex, to = from + delta;
        if (from < 0 || to < 0 || to >= items.Count) return;
        var selected = items[from];
        items.Move(from, to);
        list.SelectedItem = selected;
        list.ScrollIntoView(selected);
        Refresh();
    }

    private void Refresh()
    {
        up.IsEnabled = list.SelectedIndex > 0;
        down.IsEnabled = list.SelectedIndex >= 0 && list.SelectedIndex < items.Count - 1;
    }
}
