namespace Teclas.Core;

public sealed partial class MidiAuditionSequence
{
    /// <summary>Original complete MIDI timeline, including every track, program, controller and percussion channel.
    /// The immutable input is never transposed, quantized, trimmed or written back.</summary>
    public static MidiAuditionSequence CreateFull(MidiDocument document, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        token.ThrowIfCancellationRequested();
        if (document.Ppq <= 0) throw new InvalidDataException("O MIDI precisa usar uma resolução musical PPQ válida.");
        if (document.Notes.Count == 0) throw new InvalidOperationException("Este MIDI não contém notas para ouvir.");
        var channels = document.Notes.Select(note => note.Channel)
            .Concat((document.PlaybackEvents ?? document.ChannelEvents).Select(item => item.Channel)).Distinct().Order().ToArray();
        var ordered = new List<OrderedEvent>();
        long endTick = document.EndTick;
        if (document.PlaybackEvents is { } originalEvents)
        {
            // A shared MIDI channel can receive its note-off on another track. Rebuilding
            // from analysis Notes would lose that release, its velocity and original order.
            foreach (var item in originalEvents)
            {
                token.ThrowIfCancellationRequested();
                ordered.Add(new(item.Tick, item.Track, item.Order, 0, Message(item.Kind, item.Channel, item.Data1, item.Data2)));
                endTick = Math.Max(endTick, item.Tick);
            }
        }
        else
        {
            // Synthetic/older callers may supply Notes and controller history directly.
            foreach (var note in document.Notes)
            {
                token.ThrowIfCancellationRequested();
                ordered.Add(new(note.Start, note.Track, note.StartOrder, 1, Message(9, note.Channel, note.Pitch, note.Velocity)));
                long end = Math.Max(note.Start, note.End);
                ordered.Add(new(end, note.Track, note.EndOrder, 0, Message(8, note.Channel, note.Pitch)));
                endTick = Math.Max(endTick, end);
            }
            foreach (var item in document.ChannelEvents)
            {
                token.ThrowIfCancellationRequested();
                ordered.Add(new(item.Tick, item.Track, item.Order, 0, Message(item.Kind, item.Channel, item.Data1, item.Data2)));
                endTick = Math.Max(endTick, item.Tick);
            }
        }
        var tempo = new MidiTempoMap(document.Ppq, document.TempoChanges);
        var events = new List<MidiAuditionEvent>();
        foreach (int channel in channels)
        {
            foreach (uint message in InitialMessages(new(0, channel, 0))) events.Add(new(TimeSpan.Zero, message));
            events.Add(new(TimeSpan.Zero, Message(12, channel, 0)));
        }
        // Track/order is the same merge ordering used by MidiReader. Program changes are not filtered
        // by a selected source, and controllers on conductor/other tracks retain their channel.
        foreach (var item in ordered.OrderBy(item => item.Tick).ThenBy(item => item.Track).ThenBy(item => item.Order).ThenBy(item => item.Priority))
        {
            token.ThrowIfCancellationRequested();
            events.Add(new(tempo.At(item.Tick), item.Message));
        }
        TimeSpan duration = tempo.At(endTick);
        foreach (int channel in channels)
        {
            events.Add(new(duration, Message(11, channel, 64)));
            events.Add(new(duration, Message(11, channel, 66)));
            events.Add(new(duration, Message(11, channel, 123)));
        }
        token.ThrowIfCancellationRequested();
        string[] warnings = document.HasSystemExclusive
            ? ["O sintetizador General MIDI do Windows reproduz as pistas; timbres personalizados por SysEx podem soar diferentes."] : [];
        return new(new(0, channels[0], 0), duration + TimeSpan.FromMilliseconds(250), TimeSpan.Zero, events.ToArray(), warnings, channels);
    }
}
