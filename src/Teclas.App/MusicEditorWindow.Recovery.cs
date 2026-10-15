using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

public sealed partial class MusicEditorWindow
{
    private sealed record RecoveryPayload(int Version, DraftState State, int Staff, int Measure);
    private EditorDraftRecoveryStore? recoveryStore;
    private EditorDraftCheckpoint? recoveryCheckpoint;
    private DispatcherTimer? recoveryTimer;
    private TextBlock? recoveryStatus;
    private bool recoveryPublicationPending;

    private void InitializeRecovery(string? directory, EditorDraftCheckpoint? checkpoint)
    {
        if (directory is null) return;
        recoveryStore = new(directory);
        recoveryStatus = new TextBlock { Name = "MusicEditorRecoveryStatus", Text = "As edições serão protegidas automaticamente neste computador.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) };
        if (status.Parent is Panel footer) footer.Children.Insert(footer.Children.IndexOf(status) + 1, recoveryStatus);
        recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        recoveryTimer.Tick += (_, _) => FlushRecovery();
        if (checkpoint is null) return;
        if (!checkpoint.SourceSha256.Equals(model.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O rascunho não corresponde à partitura base. Os arquivos foram preservados.");
        var payload = JsonSerializer.Deserialize<RecoveryPayload>(checkpoint.Payload);
        if (payload is null || payload.Version != 1 || payload.State is null) throw new InvalidDataException("Formato de rascunho não reconhecido.");
        ValidateRecoveryState(payload.State);
        recoveryCheckpoint = checkpoint;
        undo.Add(current); Restore(payload.State);
        staff.SelectedItem = payload.Staff;
        measure.SelectedItem = model.Measures.FirstOrDefault(m => m.Staff == payload.Staff && m.Number == payload.Measure) ?? model.Measures.First();
        recoveryStatus.Text = "Rascunho recuperado. Atualize a prévia para conferir e salvar uma nova versão.";
    }

    private void ValidateRecoveryState(DraftState state)
    {
        var eventIds = model.Measures.SelectMany(m => m.Events).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var noteIds = notes.Select(n => n.Note.Id).ToHashSet(StringComparer.Ordinal);
        var harmonyIds = harmonies.Select(h => h.Harmony.Id).ToHashSet(StringComparer.Ordinal);
        if (state.Notes is null || state.Rhythms is null || state.Harmonies is null || state.EventReplacements is null || state.VoiceCopies is null ||
            state.Notes.Any(p => !noteIds.Contains(p.Key) || p.Value is null || p.Value.Length > 12) ||
            state.Rhythms.Any(p => !eventIds.Contains(p.Key) || p.Value is null || p.Value.Duration is null) ||
            state.Harmonies.Any(p => !harmonyIds.Contains(p.Key) || p.Value is null || p.Value.Root is null || p.Value.Quality is null) ||
            state.EventReplacements.Any(p => p is null || !eventIds.Contains(p.EventId) || p.Parts is null || p.Parts.Any(part => part is null || part.Notes is null || part.Notes.Any(n => n is null))) ||
            state.VoiceCopies.Any(p => p is null || !model.Measures.Any(m => m.Staff == p.SourceStaff && m.Number == p.SourceMeasure) || !model.Measures.Any(m => m.Staff == p.TargetStaff && m.Number == p.TargetMeasure)))
            throw new InvalidDataException("O rascunho contém referências que não correspondem à partitura. A cópia foi preservada.");
    }

    private void ScheduleRecovery()
    {
        if (recoveryStore is null || closed || recoveryPublicationPending) return;
        recoveryTimer!.Stop(); recoveryTimer.Start();
        recoveryStatus!.Text = "Protegendo rascunho…";
    }

    private bool FlushRecovery()
    {
        recoveryTimer?.Stop();
        if (recoveryStore is null || recoveryPublicationPending) return true;
        try
        {
            if (!HasChanges)
            {
                if (recoveryCheckpoint is not null) recoveryStore.Discard(recoveryCheckpoint);
                recoveryCheckpoint = null;
                recoveryStatus!.Text = "Nenhuma alteração pendente no rascunho.";
                return true;
            }
            string payload = JsonSerializer.Serialize(new RecoveryPayload(1, current, staff.SelectedItem is int s ? s : 1, (measure.SelectedItem as MusicMeasure)?.Number ?? 1));
            recoveryCheckpoint = recoveryCheckpoint is null
                ? recoveryStore.Create(model.SourcePath, model.SourceSha256, model.Metadata.Title, payload)
                : recoveryStore.Save(recoveryCheckpoint, payload);
            recoveryStatus!.Text = "Rascunho protegido às " + recoveryCheckpoint.UpdatedAt.ToLocalTime().ToString("HH:mm:ss") + ".";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            recoveryStatus!.Text = "Não foi possível proteger a última edição. Mantenha o editor aberto ou salve uma nova versão. " + error.Message;
            return false;
        }
    }

    private void CompleteRecovery() { recoveryTimer?.Stop(); recoveryPublicationPending = true; }

    // The library must persist its reference to the new version before removing its recovery point.
    public void AcknowledgeRecoveryPublication()
    {
        if (!recoveryPublicationPending || recoveryCheckpoint is null || recoveryStore is null) return;
        try { recoveryStore.Discard(recoveryCheckpoint); recoveryCheckpoint = null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Keep the checkpoint when cleanup is unavailable. */ }
    }

    private void CloseRecovery(bool discarded)
    {
        recoveryTimer?.Stop();
        if (recoveryStore is null || recoveryPublicationPending) return;
        if (!discarded) { FlushRecovery(); return; }
        try { if (recoveryCheckpoint is not null) recoveryStore.Discard(recoveryCheckpoint); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A failed cleanup retains the recoverable draft. */ }
    }
}
