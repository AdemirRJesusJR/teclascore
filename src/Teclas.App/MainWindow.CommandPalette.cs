using System.Windows;

namespace Teclas.App;

/// <summary>Ctrl+K command palette: the app's actions and the library's songs, found by typing.</summary>
public partial class MainWindow
{
    internal Func<CommandPaletteWindow, bool?> commandPaletteDialog = window => window.ShowDialog();

    internal IReadOnlyList<PaletteItem> PaletteItems()
    {
        var e = new RoutedEventArgs();
        PaletteItem Act(string label, string keywords, Action action, Func<bool>? can = null) =>
            new(label, "", keywords, () => { if (can?.Invoke() ?? IsIdle) action(); else Status = $"\"{label}\" não está disponível agora."; });
        var items = new List<PaletteItem>
        {
            Act("Piloto automático da biblioteca", "lote gerar tudo automatico noite", () => AutopilotClick(this, e)),
            Act("Revisão em sequência", "revisar aprovar conferir", () => ReviewSequenceClick(this, e)),
            Act("Repertório rápido (culto ou show)", "setlist culto show repertorio lista", () => QuickSetlistClick(this, e)),
            Act("Pasta de MIDIs", "importar abrir pasta", () => OpenMidiFolderClick(this, e)),
            Act("Escolher arquivos", "importar abrir midi", () => ImportClick(this, e)),
            Act("Importar MuseScore ou MusicXML", "abrir mscz xml", () => ImportMuseScoreClick(this, e)),
            Act("Áudio para partitura", "mp3 gravacao transcrever", () => AudioToScoreClick(this, e)),
            Act("PDF ou foto para partitura", "audiveris imagem ler", () => SheetToScoreClick(this, e)),
            Act("Gravar ideia", "teclado midi tocar compor", () => RecordIdeaClick(this, e)),
            Act("Buscar tocando ou cantarolando", "melodia achar cantar", () => MelodySearchClick(this, e)),
            Act("Melodias em lote", "escolher melodia", () => MelodyBatchClick(this, e)),
            Act("Fila de conversões", "fila gerar", () => ConversionQueueClick(this, e)),
            Act("PDFs em massa", "pdf exportar varios", () => ExportBatchPdfClick(this, e)),
            Act("Repertório em PDF", "pdf juntar indice", () => RepertoirePdfClick(this, e)),
            Act("Caderno de cifras", "cifra pdf letra", () => SongbookClick(this, e)),
            Act("Acordes para aprender", "estudar acordes", () => ChordsToLearnClick(this, e)),
            Act("Músicas repetidas", "duplicadas limpar", () => FindDuplicatesClick(this, e)),
            Act("Apresentação (palco)", "palco show culto tocar", () => OpenPresentationClick(this, e)),
            Act("Analisar a música selecionada", "acordes analise", () => AnalyzeClick(this, e)),
            Act("Gerar a partitura selecionada", "gerar mscz", () => GenerateClick(this, e)),
            Act("Salvar PDF da partitura", "pdf exportar imprimir", () => ExportPdfClick(this, e), () => CanExportPdf),
            Act("Transpor", "tom voz semitom", () => TransposeScoreClick(this, e)),
            Act("Cifra em texto", "cifra copiar whatsapp chordpro", () => ChordSheetTextClick(this, e), () => CanShowChordSheetText),
            Act("Professor com IA", "pergunta claude ajuda", () => TeacherClick(this, e), () => CanAskTeacher),
            Act("Praticar", "estudo teclado treinar", () => PracticeClick(this, e), () => CanExportPdf),
            Act("Notas caindo", "estudo visual", () => FallingNotesClick(this, e), () => CanExportPdf),
            Act("Histórico de estudo", "estatisticas tempo", () => StudyHistoryClick(this, e), () => true),
            Act("Visão geral", "inicio dashboard", () => NavigateByShortcut("dashboard"), () => true),
            Act("Biblioteca", "musicas cards", () => NavigateByShortcut("library"), () => true),
            Act("Produção", "geradas partituras", () => NavigateByShortcut("production"), () => true),
            Act("Configurações", "opcoes ajustes", () => NavigateByShortcut("settings"), () => true),
            Act("Testar microfone", "microfone audio", () => MicrophoneTestClick(this, e), () => true),
            Act("Atalhos de teclado", "ajuda teclas f1", () => ShortcutsClick(this, e), () => true),
            Act("Cópia da biblioteca (backup)", "backup salvar copia", () => ExportLibraryBackupClick(this, e)),
            Act("Liberar espaço", "limpar temporarios cache", () => CleanTempFilesClick(this, e)),
            Act("Relatório de problema", "suporte erro zip", () => SupportReportClick(this, e), () => true),
        };
        foreach (var row in Files)
        {
            string title = string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle;
            items.Add(new PaletteItem(title, (row.ScoreArtist.Length > 0 ? row.ScoreArtist + " · " : "") + (row.ScorePath is null ? "sem partitura" : row.ConfidenceBadge),
                row.Name + " " + row.Collection + " " + row.Genre + " " + row.Tags, () => SelectSong(row)));
        }
        return items;
    }

    private void SelectSong(MidiRow row)
    {
        SetCurrentPage("library");
        Library.SelectedItem = row; Library.ScrollIntoView(row); RefreshSelection();
    }

    private void CommandPaletteClick(object sender, RoutedEventArgs e) => commandPaletteDialog(new CommandPaletteWindow(PaletteItems()) { Owner = this });
}
