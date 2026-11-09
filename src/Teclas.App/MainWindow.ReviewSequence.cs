using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Review in sequence: approve, redo with another melody, choose chord alternatives or edit each generated score, the riskiest first.</summary>
public partial class MainWindow
{
    internal Func<ReviewSequenceWindow, bool?> reviewSequenceDialog = window => window.ShowDialog();
    /// <summary>Test hook: regenerates a row after its melody changed (instead of analysis + generation).</summary>
    internal Func<MidiRow, Task>? reviewRegenerate = null;
    /// <summary>Test hook: the measure map of a score for the review (instead of MuseScore).</summary>
    internal Func<MidiRow, CancellationToken, Task<ScorePlaybackMap>>? reviewMapOverride = null;

    /// <summary>Generated scores not approved yet, lowest rating first.</summary>
    internal IReadOnlyList<MidiRow> ReviewSequenceRows()
    {
        RefreshAllConfidence();
        return Files.Where(r => r.ScorePath is not null && !r.Approved && r.Confidence is not null).OrderBy(r => r.Confidence!.Score).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The services the review window takes from the library: measure maps, chord changes, regeneration, reports.</summary>
    internal ReviewSequenceServices ReviewServices() => new()
    {
        PrepareMap = reviewMapOverride ?? PrepareReviewMapAsync,
        ChooseChord = ApplyChordAlternative,
        Regenerate = RegenerateAfterReviewAsync,
        ReadReport = row => ScoreValidationReport.Read(row.ScorePath)
    };

    private void ReviewSequenceClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var rows = ReviewSequenceRows();
        if (rows.Count == 0) { Status = "Nada para revisar: todas as partituras geradas já foram aprovadas."; return; }
        var window = new ReviewSequenceWindow(rows, row => { row.Approved = true; PersistPreferences(); }, RedoWithNextMelodyAsync, PrepareReviewAudioAsync, ReviewServices()) { Owner = this };
        reviewSequenceDialog(window);
        libraryDirty = true; RefreshDashboard(); PersistPreferences();
        Status = window.Approved > 0 ? $"{window.Approved} partitura(s) aprovada(s) na revisão." : Status;
        if (window.EditRequested is { } edit)
        {
            IsLibraryTableView = false;
            SetCurrentPage("library"); Library.SelectedItem = edit; Library.ScrollIntoView(edit); RefreshSelection();
            OpenMusicEditorClick(this, new RoutedEventArgs());
        }
    }

    /// <summary>The audio of a score for the review in sequence (Space), from the playback cache when already prepared.</summary>
    private Task<string> PrepareReviewAudioAsync(MidiRow row, CancellationToken token)
    {
        string? score = row.ScorePath; string museScore = MuseScorePath, cache = PlaybackCacheDirectory;
        if (score is null || !File.Exists(score)) return Task.FromException<string>(new InvalidOperationException("a partitura não está no disco"));
        if (!File.Exists(museScore)) return Task.FromException<string>(new InvalidOperationException("o MuseScore não foi encontrado"));
        return Task.Run(async () => (await new ScoreStudyAudio(museScore, cache).PrepareAsync(score, 1.0, null, token)).Path, token);
    }

    /// <summary>The measure map of a score (positions on the page and times), from the shared cache when already prepared.</summary>
    private Task<ScorePlaybackMap> PrepareReviewMapAsync(MidiRow row, CancellationToken token)
    {
        string? score = row.ScorePath; string museScore = MuseScorePath;
        if (score is null || !File.Exists(score)) return Task.FromException<ScorePlaybackMap>(new InvalidOperationException("a partitura não está no disco"));
        if (!File.Exists(museScore)) return Task.FromException<ScorePlaybackMap>(new InvalidOperationException("o MuseScore não foi encontrado"));
        var service = new ScorePlaybackMapService(museScore, Path.Combine(PlaybackCacheDirectory, "score-maps"));
        return scoreMapCache.GetOrPrepareAsync(score, museScore, 1.0, request => service.PrepareAsync(score, 1.0, null, request), null, token);
    }

    /// <summary>A chord of any song takes an alternative (review in sequence); the score waits for a new generation.</summary>
    internal void ApplyChordAlternative(MidiRow row, ChordRow chord, ChordAlternative alternative)
    {
        if (!row.Chords.Contains(chord)) return;
        if (ReferenceEquals(row, selectedFile)) { UseChordAlternative(chord, alternative); return; }
        RememberVersion(row);
        chord.Root = alternative.Root; chord.Quality = alternative.Quality;
        if (row.ScorePath is not null) row.HasPendingScoreProperties = true;
        PersistPreferences();
    }

    /// <summary>After chord changes in the review: the song is generated again with its current melody, through the background queue.</summary>
    internal async Task<string> RegenerateAfterReviewAsync(MidiRow row)
    {
        row.Approved = false;
        if (reviewRegenerate is null && !IsAutopilotRunning)
        {
            PersistPreferences();
            await EnqueueConversion(row, CancellationToken.None);
            StartBackgroundQueue();
            return "Na fila para gerar de novo com as cifras escolhidas. Continue a revisão; a nova versão aparece no card quando ficar pronta.";
        }
        bool ok = false;
        await RunOperation(async () =>
        {
            if (reviewRegenerate is { } regenerate) await regenerate(row);
            else await Generate(row);
            ok = true;
        }, operationTitle: "Gerando com as cifras escolhidas", operationItem: row.Name);
        RefreshConfidence(row); PersistPreferences();
        return ok ? $"Gerada de novo com as cifras escolhidas{(row.Confidence is { } c ? $": {c.Score}%" : "")}." : $"Não foi possível gerar de novo: {Status}";
    }

    /// <summary>Moves the song to its next melody candidate (by the advisor's ranking); false when there is no other track.</summary>
    internal bool TryAdvanceMelody(MidiRow row, out string name)
    {
        name = "";
        var current = row.Tracks.Where(t => t.IsMelody).Select(t => t.Source.Key).ToHashSet();
        var ranked = row.Tracks.Where(t => t.MelodySuggestion is not null).OrderBy(t => t.MelodySuggestion!.Rank).ToList();
        if (ranked.Count == 0) ranked = row.Tracks.ToList();
        int at = ranked.FindIndex(t => current.Contains(t.Source.Key));
        var next = ranked.Skip(at + 1).Concat(ranked.Take(Math.Max(0, at))).FirstOrDefault(t => !current.Contains(t.Source.Key));
        if (next is null) return false;
        foreach (var track in row.Tracks) track.IsMelody = track == next;
        row.AutoMelody = false; row.Approved = false;
        name = $"{next.Instrument} (pista {next.Track}, canal {next.Channel})";
        return true;
    }

    /// <summary>The next melody candidate (by the advisor's ranking) replaces the current choice and the score is made again.</summary>
    internal async Task<string> RedoWithNextMelodyAsync(MidiRow row)
    {
        if (!TryAdvanceMelody(row, out string name)) throw new InvalidOperationException("Não há outra pista candidata a melodia nesta música. Abra no editor (E) para ajustar.");
        if (reviewRegenerate is null && !IsAutopilotRunning)
        {
            // Through the background queue: the review goes on while the new version is generated.
            PersistPreferences();
            await EnqueueConversion(row, CancellationToken.None);
            StartBackgroundQueue();
            return $"Na fila para gerar de novo com a melodia em {name}. Continue a revisão; a nova versão aparece no card quando ficar pronta.";
        }
        bool ok = false;
        await RunOperation(async () =>
        {
            if (reviewRegenerate is { } regenerate) await regenerate(row);
            else { await Analyze(row); await Generate(row); }
            ok = true;
        }, operationTitle: "Gerando com outra melodia", operationItem: row.Name);
        RefreshConfidence(row); PersistPreferences();
        return ok ? $"Gerada de novo com a melodia em {name}{(row.Confidence is { } c ? $": {c.Score}%" : "")}." : $"Não foi possível gerar com {name}: {Status}";
    }
}
