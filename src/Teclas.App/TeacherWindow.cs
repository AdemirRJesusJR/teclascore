using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Teclas.App;

/// <summary>
/// Questions about the open song to the AI teacher, as a conversation: suggested questions, a question about one measure,
/// answers that appear while they are written (selectable, with Copy) and practice exercises created as library songs.
/// </summary>
public sealed class TeacherWindow : Window
{
    internal static readonly string[] Suggestions =
    [
        "Como simplificar os acordes mais difíceis desta música?",
        "Qual a melhor ordem para estudar esta música?",
        "Quais trechos devo praticar devagar primeiro?",
        "Como fazer a mão esquerda nesta música?",
        "Que acordes parecidos aparecem e como ligar um ao outro?",
    ];
    internal static readonly (string Label, string Kind)[] Exercises =
    [
        ("Escala", "a escala do tom da música, subindo e descendo, em semínimas e depois colcheias"),
        ("Arpejos", "os arpejos dos acordes da música, na ordem em que aparecem"),
        ("Encadeamento", "o encadeamento dos acordes da música com as inversões mais próximas, um acorde por compasso"),
    ];

    private readonly Func<string, Action<string>, CancellationToken, Task<string>> ask;
    private readonly Func<string, CancellationToken, Task<string>>? exercise;
    private readonly StackPanel conversation = new();
    private readonly ScrollViewer scroller;
    private readonly TextBox question, measure;
    private readonly Button send;
    private readonly TextBlock status;
    private readonly List<Button> busyButtons = [];
    private CancellationTokenSource? pending;
    public int Answers { get; private set; }
    public string ConversationText => string.Join("\n\n", conversation.Children.OfType<Border>().Select(b => ((TextBox)b.Child).Text));
    public string StatusText => status.Text;
    /// <summary>The measure question uses this number; the preview fills in the measure being played.</summary>
    public int MeasureNumber { get => int.TryParse(measure.Text, out int n) ? n : 1; set => measure.Text = Math.Max(1, value).ToString(); }

    public TeacherWindow(string title, Func<string, Action<string>, CancellationToken, Task<string>> ask, Func<string, CancellationToken, Task<string>>? exercise = null, int measures = 0)
    {
        this.ask = ask; this.exercise = exercise;
        Title = "Professor · " + title; Width = 780; Height = 720; MinWidth = 560; MinHeight = 460; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Professor com IA", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock
        {
            Text = "Pergunte sobre esta música. Ao perguntar, o título, a cifra com a letra, as notas de cada compasso e seus resultados de prática desta música são enviados à Anthropic com a sua chave; " +
                   "nenhum arquivo, pasta ou outra música sai do computador. As respostas são sugestões de estudo e podem conter erros.",
            TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 6, 0, 10)
        });
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) }; top.Children.Add(chips);
        foreach (string suggestion in Suggestions) chips.Children.Add(Chip(suggestion, async () => await AskAsync(suggestion)));
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) }; top.Children.Add(tools);
        tools.Children.Add(new TextBlock { Text = "Compasso", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4), FontSize = 12 });
        measure = new TextBox { Width = 44, Text = "1", MaxLength = 4, MinHeight = 0, Height = 24, Padding = new Thickness(4, 0, 4, 0), FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 4),
            ToolTip = measures > 0 ? $"De 1 a {measures}" : null };
        AutomationProperties.SetName(measure, "Número do compasso");
        tools.Children.Add(measure);
        tools.Children.Add(Chip("Explicar este compasso", async () => await AskAsync(
            $"Explique como tocar o compasso {MeasureNumber}: as notas da melodia, o acorde (e a posição no teclado) e um jeito de praticá-lo devagar.")));
        if (exercise is not null)
        {
            tools.Children.Add(new TextBlock { Text = "Exercício:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 4), FontSize = 12 });
            foreach (var (label, kind) in Exercises) tools.Children.Add(Chip(label, async () => await MakeExerciseAsync(label, kind)));
        }
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 6, 0, 0) }; DockPanel.SetDock(status, Dock.Bottom); footer.Children.Add(status);
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        var copy = DialogUi.Button("Copiar", (_, _) => Copy()); copy.ToolTip = "Copia a conversa inteira"; DockPanel.SetDock(copy, Dock.Right); footer.Children.Add(copy);
        send = DialogUi.Button("Perguntar", async (_, _) => await AskAsync(question!.Text), true); send.Name = "AskTeacherButton"; DockPanel.SetDock(send, Dock.Right); footer.Children.Add(send);
        busyButtons.Add(send);
        question = new TextBox { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MaxLength = 2000, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(question, "Sua pergunta ao professor");
        question.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await AskAsync(question.Text); } };
        footer.Children.Add(question);
        scroller = new ScrollViewer { Content = conversation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(scroller, "Conversa com o professor");
        root.Children.Add(scroller);
        Loaded += (_, _) => question.Focus();
        Closed += (_, _) => pending?.Cancel();
    }

    private Button Chip(string text, Func<Task> click)
    {
        var chip = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = FontWeights.Normal, Cursor = Cursors.Hand };
        chip.Click += async (_, _) => await click();
        busyButtons.Add(chip);
        return chip;
    }

    /// <summary>A message bubble; the text is selectable (read-only text box without a frame).</summary>
    private TextBox AddBubble(string text, bool fromUser)
    {
        var box = new TextBox { Text = text, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0) };
        box.SetResourceReference(ForegroundProperty, "Ink");
        AutomationProperties.SetName(box, fromUser ? "Sua pergunta" : "Resposta do professor");
        var bubble = new Border
        {
            Child = box, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), Margin = fromUser ? new Thickness(80, 4, 0, 4) : new Thickness(0, 4, 80, 4),
            HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        bubble.SetResourceReference(Border.BackgroundProperty, fromUser ? "Soft" : "CardBrush");
        bubble.SetResourceReference(Border.BorderBrushProperty, "Line"); bubble.BorderThickness = new Thickness(1);
        conversation.Children.Add(bubble);
        scroller.ScrollToEnd();
        return box;
    }

    private void Busy(bool busy) { foreach (var button in busyButtons) button.IsEnabled = !busy; }

    internal async Task AskAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || pending is not null) return;
        question.Text = ""; AddBubble(text, true);
        var answerBox = AddBubble("…", false); bool started = false;
        pending = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Busy(true); status.Text = "O professor está pensando…";
        void Append(string piece) => Dispatcher.Invoke(() =>
        {
            if (!started) { answerBox.Text = ""; started = true; status.Text = "O professor está respondendo…"; }
            answerBox.AppendText(piece); scroller.ScrollToEnd();
        });
        try
        {
            string answer = await ask(text, Append, pending.Token);
            answerBox.Text = answer.Length > 0 ? answer : "(sem resposta)"; Answers++;
            status.Text = "";
        }
        catch (OperationCanceledException) { answerBox.Text = "(resposta interrompida)"; status.Text = IsLoaded ? "A resposta demorou demais. Tente de novo." : ""; }
        catch (InvalidOperationException error) { answerBox.Text = "(sem resposta)"; status.Text = error.Message; }
        finally { pending.Dispose(); pending = null; Busy(false); if (IsLoaded) question.Focus(); }
    }

    internal async Task MakeExerciseAsync(string label, string kind)
    {
        if (exercise is null || pending is not null) return;
        pending = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Busy(true); status.Text = $"Montando o exercício ({label.ToLowerInvariant()})…";
        try { status.Text = await exercise(kind, pending.Token); }
        catch (OperationCanceledException) { status.Text = IsLoaded ? "O exercício demorou demais. Tente de novo." : ""; }
        catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException or InvalidDataException or IOException) { status.Text = "Não foi possível montar o exercício: " + error.Message; }
        finally { pending.Dispose(); pending = null; Busy(false); }
    }

    private void Copy()
    {
        try { Clipboard.SetText(ConversationText); status.Text = "Conversa copiada."; }
        catch (System.Runtime.InteropServices.ExternalException) { status.Text = "A área de transferência está ocupada por outro programa. Tente de novo."; }
    }
}
