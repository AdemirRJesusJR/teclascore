using System.Windows;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    private MidiTrackPlayer? libraryMidiPlayer;
    private MidiRow? libraryPlaybackRow;
    private CancellationTokenSource? libraryPlaybackPreparation;
    private readonly DispatcherTimer libraryPlaybackTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private long libraryPlaybackGeneration;
    private bool libraryPlaybackDisposed;
    private Func<MidiDocument, CancellationToken, Task<MidiAuditionSequence>> librarySequenceFactory =
        (document, token) => Task.Run(() => MidiAuditionSequence.CreateFull(document, token), token);
    public double LibraryFiltersMaximumHeight => Math.Clamp((ActualHeight - 230) * .55, 180, 420);

    private void InitializeLibraryPlayback(Func<IMidiOutputTransport>? outputFactory)
    {
        SizeChanged += (_, _) => Notify(nameof(LibraryFiltersMaximumHeight));
        libraryMidiPlayer = new MidiTrackPlayer(outputFactory);
        libraryMidiPlayer.StateChanged += (_, _) => QueueLibraryPlaybackUpdate();
        libraryMidiPlayer.PlaybackFailed += (_, error) =>
        {
            long generation = Interlocked.Read(ref libraryPlaybackGeneration);
            var owner = libraryPlaybackRow;
            if (Dispatcher.HasShutdownStarted || error.Generation != libraryMidiPlayer.PlaybackGeneration) return;
            Dispatcher.InvokeAsync(() =>
            {
                if (libraryPlaybackDisposed || generation != libraryPlaybackGeneration || error.Generation != libraryMidiPlayer.PlaybackGeneration
                    || !ReferenceEquals(owner, libraryPlaybackRow)) return;
                StopLibraryPlayback();
                if (owner is not null) owner.LibraryPlayback.Status = "Falha ao ouvir: " + error.Message;
                Status = "Não foi possível ouvir o MIDI. " + error.Message;
            });
        };
        libraryPlaybackTimer.Tick += (_, _) => UpdateLibraryPlaybackPosition();
        Files.CollectionChanged += (_, _) =>
        {
            if (libraryPlaybackRow is { } row && !Files.Contains(row)) StopLibraryPlayback();
        };
        Closed += (_, _) =>
        {
            libraryPlaybackDisposed = true;
            StopLibraryPlayback();
            libraryMidiPlayer.Dispose();
        };
    }

    private void OnLibraryPlaybackSelectionChanged()
    {
        if (libraryPlaybackRow is { } row && (!ReferenceEquals(row, selectedFile) || !Files.Contains(row))) StopLibraryPlayback();
    }

    private async void PlayLibraryMidiClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!IsIdle || libraryPlaybackDisposed || sender is not FrameworkElement { Tag: MidiRow row }
            || row.IsNativeScore || row.Document is not { } document || !Files.Contains(row) || !CommitEdits()) return;
        if (ReferenceEquals(row, libraryPlaybackRow) && libraryMidiPlayer?.IsPaused == true)
        {
            try
            {
                libraryMidiPlayer.Resume(); row.LibraryPlayback.Update(playing: true);
                row.LibraryPlayback.Status = "Ouvindo MIDI completo"; libraryPlaybackTimer.Start();
            }
            catch (Exception error) { LibraryPlaybackFailed(row, error.Message); }
            return;
        }
        if (ReferenceEquals(row, libraryPlaybackRow) && libraryMidiPlayer?.IsPlaying == true) return;
        StopTrackAudition(); // Stops the previous card and isolated source as well.
        ResetPlayback();
        Library.SelectedItem = row;
        await RunOperation(async () =>
        {
            long generation = ++libraryPlaybackGeneration;
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellation!.Token);
            libraryPlaybackPreparation = preparation;
            libraryPlaybackRow = row;
            row.LibraryPlayback.Update(preparing: true);
            row.LibraryPlayback.Status = "Preparando todas as pistas…";
            Status = "Preparando todas as pistas, instrumentos e andamento do MIDI…";
            try
            {
                var sequence = await librarySequenceFactory(document, preparation.Token);
                preparation.Token.ThrowIfCancellationRequested();
                if (libraryPlaybackDisposed || generation != libraryPlaybackGeneration || !ReferenceEquals(row, selectedFile)
                    || !ReferenceEquals(row, libraryPlaybackRow) || !Files.Contains(row)) throw new OperationCanceledException();
                libraryMidiPlayer!.Play(sequence);
                row.LibraryPlayback.Update(playing: true);
                row.LibraryPlayback.Status = sequence.Warnings.Count == 0 ? "Ouvindo MIDI completo" : "Ouvindo MIDI completo · " + string.Join(" ", sequence.Warnings);
                Status = "Reprodução do MIDI original: todas as pistas, instrumentos e percussão.";
                libraryPlaybackTimer.Start();
                UpdateLibraryPlaybackPosition();
            }
            catch (OperationCanceledException)
            {
                if (generation == libraryPlaybackGeneration)
                { StopLibraryPlayback(); row.LibraryPlayback.Status = "Preparação cancelada"; }
                throw;
            }
            catch (Exception error)
            {
                if (generation == libraryPlaybackGeneration) LibraryPlaybackFailed(row, error.Message);
                throw;
            }
            finally
            {
                if (ReferenceEquals(libraryPlaybackPreparation, preparation)) libraryPlaybackPreparation = null;
            }
        }, operationTitle: "Preparando MIDI completo para ouvir", operationItem: row.Name);
    }

    private void PauseLibraryMidiClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!IsIdle || sender is not FrameworkElement { Tag: MidiRow row } || !ReferenceEquals(row, libraryPlaybackRow)
            || libraryMidiPlayer?.IsPlaying != true) return;
        try
        {
            libraryMidiPlayer.Pause(); libraryPlaybackTimer.Stop();
            row.LibraryPlayback.Update(paused: true); row.LibraryPlayback.Status = "Pausado · ▶ para continuar";
            UpdateLibraryPlaybackPosition();
        }
        catch (Exception error) { LibraryPlaybackFailed(row, error.Message); }
    }

    private void StopLibraryMidiClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: MidiRow row } && ReferenceEquals(row, libraryPlaybackRow)) StopLibraryPlayback();
    }

    private void StopLibraryPlayback()
    {
        ++libraryPlaybackGeneration;
        libraryPlaybackPreparation?.Cancel();
        libraryPlaybackPreparation = null;
        var previous = libraryPlaybackRow; libraryPlaybackRow = null;
        libraryPlaybackTimer.Stop(); libraryMidiPlayer?.Stop(releaseOutput: true);
        if (previous is null) return;
        previous.LibraryPlayback.Update(); previous.LibraryPlayback.TimeText = "0:00";
        previous.LibraryPlayback.Status = "Ouvir MIDI completo";
    }

    private void LibraryPlaybackFailed(MidiRow row, string details)
    {
        StopLibraryPlayback();
        row.LibraryPlayback.Status = "Falha ao ouvir: " + details;
        Status = row.LibraryPlayback.Status;
    }

    private void QueueLibraryPlaybackUpdate()
    {
        long generation = Interlocked.Read(ref libraryPlaybackGeneration);
        var owner = libraryPlaybackRow;
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (libraryPlaybackDisposed || generation != libraryPlaybackGeneration || !ReferenceEquals(owner, libraryPlaybackRow) || owner is null) return;
            if (libraryMidiPlayer is { IsPlaying: false, IsPaused: false } player && player.Position >= player.Duration && player.Duration > TimeSpan.Zero)
            {
                string end = FormatTime(player.Duration.TotalSeconds);
                StopLibraryPlayback(); owner.LibraryPlayback.TimeText = end + " / " + end;
                owner.LibraryPlayback.Status = "MIDI concluído";
            }
            else UpdateLibraryPlaybackPosition();
        });
    }

    private void UpdateLibraryPlaybackPosition()
    {
        if (libraryPlaybackRow is not { } row || libraryMidiPlayer is null || row.LibraryPlayback.IsPreparing) return;
        row.LibraryPlayback.TimeText = FormatTime(libraryMidiPlayer.Position.TotalSeconds) + " / " + FormatTime(libraryMidiPlayer.Duration.TotalSeconds);
    }
}
