using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private readonly HashSet<MidiRow> observedRows = [];
    private DashboardMetrics dashboardMetrics = new(0, 0, 0, 0, 0, 0);
    private string currentPage = "dashboard", searchText = "", currentFilter = "all";
    private bool dashboardInitialized, dashboardRefreshing, libraryDirty;
    public ICollectionView LibraryView { get; private set; } = null!;
    public ObservableCollection<MidiRow> RecentFiles { get; } = [];
    public ObservableCollection<MidiRow> GeneratedFiles { get; } = [];
    public string CurrentPage => currentPage;
    public Visibility DashboardVisible => VisibleWhen(currentPage == "dashboard");
    public Visibility LibraryVisible => VisibleWhen(currentPage == "library");
    public Visibility ProductionVisible => VisibleWhen(currentPage == "production");
    public Visibility SettingsVisible => VisibleWhen(currentPage == "settings");
    public Visibility AboutVisible => VisibleWhen(currentPage == "about");
    public string PageTitle => currentPage switch
    {
        "library" => "Biblioteca & editor", "production" => "Sua produção",
        "settings" => "Configurações", "about" => "Sobre o TeclaScore", _ => "Visão geral"
    };
    public string PageSubtitle => currentPage switch
    {
        "library" => "Escolha a melodia, confira as cifras e prepare sua próxima partitura.",
        "production" => "Partituras concluídas e páginas disponíveis na sua biblioteca.",
        "settings" => "Prepare as ferramentas e escolha onde salvar suas partituras.",
        "about" => "Conheça o aplicativo e acompanhe a versão instalada.",
        _ => "Acompanhe os MIDIs e as partituras da sua biblioteca."
    };
    public int TotalCount => dashboardMetrics.Total;
    public int ReadyCount => dashboardMetrics.Ready;
    public int ReviewCount => dashboardMetrics.Review;
    public int GeneratedCount => dashboardMetrics.Generated;
    public int ErrorCount => dashboardMetrics.Error;
    public int CompletedPages => dashboardMetrics.Pages;
    public double CompletionPercent => dashboardMetrics.CompletionPercent;
    public double ReadyPercent => dashboardMetrics.Percent(ReadyCount);
    public double ReviewPercent => dashboardMetrics.Percent(ReviewCount);
    public double GeneratedPercent => dashboardMetrics.Percent(GeneratedCount);
    public double ErrorPercent => dashboardMetrics.Percent(ErrorCount);
    public bool HasFiles => TotalCount > 0;
    public bool HasGenerated => GeneratedCount > 0;
    public bool CanBatch => IsIdle && ReadyCount > 0;
    public string SessionSummary => $"{TotalCount} arquivos importados · {GeneratedCount} partituras disponíveis";
    public string DashboardMessage => TotalCount == 0
        ? "Importe seus MIDIs para começar. Os indicadores serão preenchidos com os arquivos da biblioteca."
        : ReadyCount > 0
            ? $"{ReadyCount} MIDIs com perfil revisado estão prontos para gerar em lote."
            : ReviewCount > 0
                ? "Abra a biblioteca para escolher a melodia e revisar os arquivos pendentes."
                : GeneratedCount > 0
                    ? "Suas partituras estão disponíveis. Abra a produção para conferir os arquivos e criar um ZIP."
                    : "Abra a biblioteca para conferir as falhas e preparar uma nova tentativa.";
    public string SearchText
    {
        get => searchText;
        set { if (searchText == value) return; searchText = value ?? ""; Notify(); libraryDirty = true; RefreshDashboard(); }
    }
    public string CurrentFilter
    {
        get => currentFilter;
        set
        {
            var next = value is "ready" or "review" or "generated" or "error" ? value : "all";
            if (currentFilter == next) return;
            currentFilter = next; Notify(); Notify(nameof(FilterLabel)); libraryDirty = true; RefreshDashboard();
        }
    }
    public string FilterLabel => currentFilter switch
    {
        "ready" => "Prontos para o lote", "review" => "Revisão manual",
        "generated" => "Partituras geradas", "error" => "Falhas", _ => "Todos os arquivos"
    };
    public string LibraryCountText => $"{Files.Count(MatchesLibraryFilter)} de {TotalCount} arquivos";
    public Visibility EmptyLibraryVisible => VisibleWhen(!Files.Any(MatchesLibraryFilter));
    public Visibility EmptyProductionVisible => VisibleWhen(GeneratedCount == 0);
    public Visibility DashboardEmptyVisible => VisibleWhen(TotalCount == 0);
    public Visibility DashboardHasFilesVisible => VisibleWhen(TotalCount > 0);
    public string EmptyLibraryMessage => TotalCount == 0 ? "Importe seus primeiros MIDIs."
        : "Nenhum arquivo corresponde à busca e ao filtro atual.";
    private static Visibility VisibleWhen(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;

    private void InitDashboard()
    {
        if (dashboardInitialized) return;
        LibraryView = CollectionViewSource.GetDefaultView(Files);
        LibraryView.Filter = item => item is MidiRow row && MatchesLibraryFilter(row);
        dashboardInitialized = true;
        Files.CollectionChanged += DashboardFilesChanged;
        SyncRowSubscriptions();
        Notify(nameof(LibraryView));
        RefreshDashboard();
    }

    private void DashboardFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncRowSubscriptions();
        libraryDirty = true;
        RefreshDashboard();
    }

    private void SyncRowSubscriptions()
    {
        foreach (var row in observedRows.Where(row => !Files.Contains(row)).ToArray())
        { row.PropertyChanged -= DashboardRowChanged; observedRows.Remove(row); }
        foreach (var row in Files.Where(row => !observedRows.Contains(row)))
        { observedRows.Add(row); row.PropertyChanged += DashboardRowChanged; }
    }

    private void DashboardRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MidiRow.ScorePath) or nameof(MidiRow.UseReviewedProfile)
            or nameof(MidiRow.HasMusicalEdits)
            or nameof(MidiRow.HasOperationError) or nameof(MidiRow.PageCount) or nameof(MidiRow.GeneratedAt))) return;
        libraryDirty = true;
        RefreshDashboard();
    }

    private bool MatchesLibraryFilter(MidiRow row)
    {
        if (currentFilter != "all" && row.CategoryCode != currentFilter) return false;
        if(settings.FavoritesOnly && !row.IsFavorite)return false;
        if(!string.IsNullOrWhiteSpace(settings.CollectionFilter) && !string.Equals(row.Collection,settings.CollectionFilter,StringComparison.OrdinalIgnoreCase))return false;
        if (!MatchesSmartList(row)) return false;
        if (string.IsNullOrWhiteSpace(searchText)) return true;
        var compare = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;
        const CompareOptions loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        string text = string.Join(" ", row.Name, row.ScoreTitle, row.ScoreArtist, row.ScoreComposer, row.Collection, row.Genre, row.Tags);
        string query = searchText;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // #etiqueta matches a label exactly (accents and case ignored); the other words keep the usual search.
        var tagWords = words.Where(word => word.Length > 1 && word[0] == '#').ToArray();
        if (tagWords.Length > 0)
        {
            if (!tagWords.All(tag => row.TagList.Any(label => compare.Compare(label, tag[1..], loose) == 0))) return false;
            words = words.Except(tagWords).ToArray();
            if (words.Length == 0) return true;
            query = string.Join(' ', words);
        }
        // A lone letter is a chord only when it is the whole search ("E"); inside a phrase it is a word ("Dois corações e uma").
        bool IsChordQuery(string word) => LibrarySearch.LooksLikeChord(word) && (word.Length > 1 || words.Length == 1);
        // A chord query (D, Dm7) must match a chord or key, never letters inside a file name.
        if (!(words.Length == 1 && IsChordQuery(words[0])) && compare.IndexOf(text, query.Trim(), loose) >= 0) return true;
        // Word by word: chord symbols and keys match exactly (Dm is not Dm7); other words match the text and key names.
        var chords = row.Chords.Select(chord => chord.Root.Trim() + chord.Quality.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var key = row.Chords.Count > 0 ? LibrarySearch.KeyNames(row.KeyFifths, row.KeyMinor) : [];
        string keyText = string.Join(" ", key);
        return words.All(word =>
            IsChordQuery(word)
                ? chords.Contains(word) || key.Contains(word, StringComparer.OrdinalIgnoreCase)
                : compare.IndexOf(text, word, loose) >= 0 || compare.IndexOf(keyText, word, loose) >= 0);
    }

    private void RefreshDashboard()
    {
        if (!dashboardInitialized || dashboardRefreshing) return;
        dashboardRefreshing = true;
        try
        {
            dashboardMetrics = DashboardMetrics.Calculate(Files.Select(row => row.DashboardFacts));
            ReplaceRows(RecentFiles, Files.Reverse().Take(4));
            ReplaceRows(GeneratedFiles, Files.Where(row => row.Category == DashboardCategory.Generated)
                .OrderByDescending(row => row.GeneratedAt));
            // Keep the selection stable while a conversion is running. A completed file may
            // leave its category filter after the operation has finished.
            if (libraryDirty && IsIdle)
            {
                libraryDirty = false;
                var selection = Library?.SelectedItem;
                LibraryView.Refresh();
                if (selection is MidiRow row && MatchesLibraryFilter(row) && Library is not null)
                    Library.SelectedItem = row;
            }
            foreach (var name in new[] { nameof(TotalCount), nameof(ReadyCount), nameof(ReviewCount),
                nameof(GeneratedCount), nameof(ErrorCount), nameof(CompletedPages), nameof(CompletionPercent),
                nameof(ReadyPercent), nameof(ReviewPercent), nameof(GeneratedPercent), nameof(ErrorPercent),
                nameof(HasFiles), nameof(HasGenerated), nameof(CanBatch), nameof(CanExportBatchPdf), nameof(SessionSummary),
                nameof(DashboardMessage), nameof(LibraryCountText), nameof(EmptyLibraryVisible),
                nameof(EmptyProductionVisible), nameof(DashboardEmptyVisible), nameof(DashboardHasFilesVisible),
                nameof(EmptyLibraryMessage) }) Notify(name);
        }
        finally { dashboardRefreshing = false; }
    }

    private static void ReplaceRows(ObservableCollection<MidiRow> target, IEnumerable<MidiRow> source)
    {
        var rows = source.ToArray();
        if (target.SequenceEqual(rows)) return;
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }

    private void SetCurrentPage(string page)
    {
        if (page is not ("dashboard" or "library" or "production" or "settings" or "about")) return;
        currentPage = page;
        if (page != "library") StopTrackAudition();
        if (page == "settings") _ = RefreshTempFilesStatus();
        foreach (var name in new[] { nameof(CurrentPage), nameof(DashboardVisible), nameof(LibraryVisible),
            nameof(ProductionVisible), nameof(SettingsVisible), nameof(AboutVisible), nameof(PageTitle), nameof(PageSubtitle) }) Notify(name);
        RefreshDashboard();
    }

    private void NavigationClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page } && CommitEdits()) SetCurrentPage(page);
    }

    private void FilterClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy || !CommitEdits()) return;
        if (sender is FrameworkElement { Tag: string filter })
        {
            if (currentPage == "dashboard") SearchText = "";
            CurrentFilter = filter; SetCurrentPage("library");
        }
    }

    private void OpenEditorClick(object sender, RoutedEventArgs e)
    {
        TryOpenEditor(sender as FrameworkElement);
    }

    private bool TryOpenEditor(FrameworkElement? element)
    {
        if (IsBusy || !CommitEdits() || element is null) return false;
        if ((element.Tag as MidiRow ?? element.DataContext as MidiRow) is not { } row) return false;
        SearchText = ""; CurrentFilter = "all";FavoritesOnly=false;CollectionFilter="";
        SetCurrentPage("library"); Library.SelectedItem = row;
        return true;
    }

    private void OpenGeneratedClick(object sender, RoutedEventArgs e)
    {
        if (TryOpenEditor(sender as FrameworkElement) && FindName("EditorTabs") is TabControl tabs)
            tabs.SelectedIndex = 2;
    }

    public void SelectPageForSmoke(string page, int subtab = 0)
    {
        SetCurrentPage(page);
        if (FindName("EditorTabs") is TabControl tabs) tabs.SelectedIndex = subtab;
    }
}
