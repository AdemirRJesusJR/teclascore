using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Teclas.App;

/// <summary>Questions about the open song to the AI teacher, as a conversation, with suggested starting questions.</summary>
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

    private readonly Func<string, CancellationToken, Task<string>> ask;
    private readonly StackPanel conversation = new();
    private readonly ScrollViewer scroller;
    private readonly TextBox question;
    private readonly Button send;
    private readonly TextBlock status;
    private CancellationTokenSource? pending;
    public int Answers { get; private set; }
    public string ConversationText => string.Join("\n\n", conversation.Children.OfType<Border>().Select(b => ((TextBlock)b.Child).Text));

    public TeacherWindow(string title, Func<string, CancellationToken, Task<string>> ask)
    {
        this.ask = ask;
        Title = "Professor · " + title; Width = 760; Height = 700; MinWidth = 520; MinHeight = 440; DialogUi.Theme(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Professor com IA", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock
        {
            Text = "Pergunte sobre esta música. Ao perguntar, o título, a cifra com a letra e seus resultados de prática desta música são enviados à Anthropic com a sua chave; " +
                   "nenhum arquivo, pasta ou outra música sai do computador. As respostas são sugestões de estudo e podem conter erros.",
            TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 6, 0, 10)
        });
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) }; top.Children.Add(chips);
        foreach (string suggestion in Suggestions)
        {
            var chip = new Button { Content = suggestion, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4), MinHeight = 0, FontSize = 12, FontWeight = FontWeights.Normal, Cursor = Cursors.Hand };
            chip.Click += async (_, _) => await AskAsync(suggestion);
            chips.Children.Add(chip);
        }
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 6, 0, 0) }; DockPanel.SetDock(status, Dock.Bottom); footer.Children.Add(status);
        var close = DialogUi.Button("Fechar", (_, _) => Close()); close.IsCancel = true; DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        send = DialogUi.Button("Perguntar", async (_, _) => await AskAsync(question!.Text), true); send.Name = "AskTeacherButton"; DockPanel.SetDock(send, Dock.Right); footer.Children.Add(send);
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

    private void AddBubble(string text, bool fromUser)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        var bubble = new Border
        {
            Child = block, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), Margin = fromUser ? new Thickness(80, 4, 0, 4) : new Thickness(0, 4, 80, 4),
            HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        bubble.SetResourceReference(Border.BackgroundProperty, fromUser ? "Soft" : "CardBrush");
        bubble.SetResourceReference(Border.BorderBrushProperty, "Line"); bubble.BorderThickness = new Thickness(1);
        conversation.Children.Add(bubble);
        scroller.ScrollToEnd();
    }

    internal async Task AskAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || pending is not null) return;
        question.Text = ""; AddBubble(text, true);
        pending = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        send.IsEnabled = false; status.Text = "O professor está pensando…";
        try
        {
            string answer = await ask(text, pending.Token);
            AddBubble(answer.Length > 0 ? answer : "(sem resposta)", false); Answers++;
            status.Text = "";
        }
        catch (OperationCanceledException) { status.Text = IsLoaded ? "A resposta demorou demais. Tente de novo." : ""; }
        catch (InvalidOperationException error) { status.Text = error.Message; }
        finally { pending.Dispose(); pending = null; send.IsEnabled = true; if (IsLoaded) question.Focus(); }
    }
}
