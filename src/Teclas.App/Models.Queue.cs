using System.Windows;

namespace Teclas.App;

/// <summary>Where a card is in the background conversion queue.</summary>
public enum QueueCardState { None, Waiting, Generating }

public sealed partial class MidiRow
{
    private QueueCardState queueState;

    /// <summary>The card is waiting or being generated in the background queue: its options cannot be edited meanwhile.</summary>
    public QueueCardState QueueState
    {
        get => queueState;
        set
        {
            if (queueState == value) return;
            queueState = value;
            Notify(); Notify(nameof(IsQueued)); Notify(nameof(QueueBadge)); Notify(nameof(QueueBadgeVisibility)); NotifyTriage();
        }
    }

    public bool IsQueued => queueState != QueueCardState.None;
    public string QueueBadge => queueState switch { QueueCardState.Generating => "Gerando…", QueueCardState.Waiting => "Na fila", _ => "" };
    public Visibility QueueBadgeVisibility => IsQueued ? Visibility.Visible : Visibility.Collapsed;
}
