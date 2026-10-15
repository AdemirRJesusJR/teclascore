using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

public sealed partial class MusicEditorWindow
{
    private readonly List<MusicEventReplacement> structureReplacements = [];
    private readonly List<MusicVoiceCopy> structureCopies = [];
    private readonly Dictionary<string, (string EventId, int Part)> mappedStructureParts = new(StringComparer.Ordinal);
    private ComboBox structureEvent = null!, structurePart = null!, structureDuration = null!, copyTarget = null!, copyVoice = null!;
    private TextBox structurePitch = null!;
    private ListBox structurePending = null!;
    private TextBlock structureHint = null!;
    private sealed record EventChoice(MusicEvent Event, string Label);
    private sealed record PartChoice(int Index, MusicEventPart Part, string Label);
    private sealed record PendingStructure(string? EventId, MusicVoiceCopy? Copy, string Label);
    private void InitializeStructureEditor()
    {
        var panel = new StackPanel { Margin = new(8) };
        panel.Children.Add(new TextBlock { Text = "Inserir notas nas pausas, excluir notas ou copiar a voz de um compasso. As outras vozes e as cifras do destino são preservadas.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Evento deste compasso" });
        structureEvent = new ComboBox { Name = "MusicEditorStructureEvent", DisplayMemberPath = nameof(EventChoice.Label), Margin = new(0, 4, 0, 8) }; panel.Children.Add(structureEvent);
        structurePart = new ComboBox { Name = "MusicEditorStructurePart", DisplayMemberPath = nameof(PartChoice.Label), Margin = new(0, 0, 0, 8) }; panel.Children.Add(structurePart);
        structureHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) }; panel.Children.Add(structureHint);
        panel.Children.Add(new TextBlock { Text = "Nota nova (C4, F#4, Bb3) e duração" });
        var pitchLine = new WrapPanel { Margin = new(0, 4, 0, 4) }; panel.Children.Add(pitchLine);
        structurePitch = new TextBox { Name = "MusicEditorNewPitch", Text = "C4", Width = 95, MaxLength = 12, Margin = new(0, 0, 8, 8) }; pitchLine.Children.Add(structurePitch);
        structureDuration = new ComboBox { Name = "MusicEditorNewDuration", DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Value), Width = 155, Margin = new(0, 0, 0, 8),
            ItemsSource = new[] { new Choice("whole", "Semibreve"), new("half", "Mínima"), new("quarter", "Semínima"), new("eighth", "Colcheia"), new("16th", "Semicolcheia"), new("32nd", "Fusa"), new("64th", "Semifusa") }, SelectedValue = "quarter" }; pitchLine.Children.Add(structureDuration);
        var actions = new WrapPanel(); panel.Children.Add(actions);
        actions.Children.Add(Button("Inserir na pausa", "MusicEditorInsert", (_, _) => StructureAction(InsertNote)));
        actions.Children.Add(Button("Alterar nota inserida", "MusicEditorChangeInserted", (_, _) => StructureAction(ChangeInserted)));
        actions.Children.Add(Button("Excluir nota escolhida", "MusicEditorDeleteNote", (_, _) => StructureAction(DeleteNote)));
        actions.Children.Add(Button("Criar pausa", "MusicEditorMakeRest", (_, _) => StructureAction(MakeRest)));
        panel.Children.Add(new TextBlock { Text = "Copiar esta voz para outro compasso", FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 6) });
        var targets = new WrapPanel(); panel.Children.Add(targets);
        copyTarget = new ComboBox { Name = "MusicEditorCopyTarget", DisplayMemberPath = nameof(MusicMeasure.Label), Width = 205, Margin = new(0, 0, 8, 8), ItemsSource = model.Measures }; targets.Children.Add(copyTarget);
        copyVoice = new ComboBox { Name = "MusicEditorCopyVoice", Width = 80, Margin = new(0, 0, 8, 8) }; targets.Children.Add(copyVoice);
        copyTarget.SelectionChanged += (_, _) => { copyVoice.ItemsSource = (copyTarget.SelectedItem as MusicMeasure)?.Events.Select(e => e.Voice).Distinct().ToArray(); copyVoice.SelectedIndex = 0; };
        targets.Children.Add(Button("Copiar voz", "MusicEditorCopyMeasure", (_, _) => StructureAction(CopyMeasure)));
        panel.Children.Add(new TextBlock { Text = "Mudanças estruturais do rascunho", FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 6) });
        structurePending = new ListBox { Name = "MusicEditorStructurePending", DisplayMemberPath = nameof(PendingStructure.Label), MinHeight = 60, MaxHeight = 110 }; panel.Children.Add(structurePending);
        panel.Children.Add(Button("Remover mudança selecionada", "MusicEditorRemoveStructure", (_, _) => StructureAction(RemoveStructure)));
        structureEvent.SelectionChanged += (_, _) => UpdateStructureParts();
        structurePart.SelectionChanged += (_, _) => { if (structurePart.SelectedItem is PartChoice p && p.Part.Notes.Count == 1) structurePitch.Text = NativeMusicEditor.NoteName(p.Part.Notes[0].Pitch, p.Part.Notes[0].Tpc); };
        tabs.Items.Add(new TabItem { Header = "Inserir / copiar", Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel } });
    }
    private void SelectStructureEvent(string id)
    { if(structureEvent is not null) structureEvent.SelectedItem = structureEvent.Items.Cast<EventChoice>().FirstOrDefault(c => c.Event.Id == id); }
    private void UpdateStructureMeasure(MusicMeasure bar)
    {
        if (structureEvent is null) return;
        string? previous = (structureEvent.SelectedItem as EventChoice)?.Event.Id;
        structureEvent.ItemsSource = bar.Events.Select(e => new EventChoice(e, $"Voz {e.Voice} · tempo {(e.Quarter + 1)?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?"} · {(e.Kind == "Rest" ? "Pausa" : string.Join(" + ", e.Notes.Select(n => n.Name)))}")).ToArray();
        structureEvent.SelectedItem = structureEvent.Items.Cast<EventChoice>().FirstOrDefault(e => e.Event.Id == previous) ?? structureEvent.Items.Cast<EventChoice>().FirstOrDefault();
    }
    private List<MusicEventPart> Parts(MusicEvent e) => structureReplacements.FirstOrDefault(r => r.EventId == e.Id)?.Parts.ToList() ?? [new(e.DurationType, e.Dots, e.Notes.Select(n => new MusicPitch(n.Pitch, n.Tpc)).ToArray())];
    private void UpdateStructureParts()
    {
        if (structureEvent.SelectedItem is not EventChoice selected) return;
        int index = (structurePart.SelectedItem as PartChoice)?.Index ?? 0;
        var parts = Parts(selected.Event);
        structurePart.ItemsSource = parts.Select((p, i) => new PartChoice(i, p, $"{i + 1}. {(p.Notes.Count == 0 ? "Pausa" : string.Join(" + ", p.Notes.Select(n => NativeMusicEditor.NoteName(n.Pitch, n.Tpc))))} · {DurationLabel(p.DurationType)}{(p.Dots > 0 ? $" · {p.Dots} ponto(s)" : "")}")).ToArray();
        structurePart.SelectedIndex = Math.Min(index, parts.Count - 1);
        structureHint.Text = structureCopies.Any(c => c.TargetStaff == selected.Event.Staff && c.TargetMeasure == selected.Event.Measure && c.TargetVoice == selected.Event.Voice)
            ? "Esta voz recebeu uma cópia. Atualize a prévia e salve a nova versão para editar as notas copiadas." : selected.Event.CanRestructure ? "A inserção usa o início da pausa e mantém o restante como pausa. Excluir a última nota cria uma pausa de igual duração." : selected.Event.StructureRestriction;
    }
    private void RefreshStructure()
    {
        if (structurePending is null) return;
        var selected = structurePending.SelectedItem as PendingStructure;
        structurePending.ItemsSource = structureReplacements.Select(r => {
            var e = model.Measures.SelectMany(m => m.Events).Single(x => x.Id == r.EventId);
            return new PendingStructure(r.EventId, null, $"Pauta {e.Staff} · compasso {e.Measure} · voz {e.Voice}: {r.Parts.Count} evento(s)"); })
            .Concat(structureCopies.Select(c => new PendingStructure(null, c, $"Copiar P{c.SourceStaff}/C{c.SourceMeasure}/V{c.SourceVoice} → P{c.TargetStaff}/C{c.TargetMeasure}/V{c.TargetVoice}"))).ToArray();
        if (selected is not null) structurePending.SelectedItem = structurePending.Items.Cast<PendingStructure>().FirstOrDefault(p => p.EventId == selected.EventId && p.Copy == selected.Copy);
        UpdateStructureParts();
    }
    private void StructureAction(Action action)
    {
        if (operation is not null || closed) return;
        var before = Capture();
        try
        {
            action(); var candidate = BuildEdits() with { EventReplacements = structureReplacements.ToArray(), VoiceCopies = structureCopies.ToArray() };
            var errors = NativeMusicEditor.Validate(model, candidate);
            if (errors.Count > 0 && structureReplacements.Count + structureCopies.Count > 0) throw new InvalidOperationException(string.Join("\n", errors.Take(3)));
            Changed(); RefreshStructure();
            if(action.Method.Name == nameof(CopyMeasure)) status.Text = "Voz copiada. Atualize a prévia e salve a nova versão antes de editar as notas copiadas.";
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.IO.IOException)
        {
            structureReplacements.Clear(); structureReplacements.AddRange(before.EventReplacements); structureCopies.Clear(); structureCopies.AddRange(before.VoiceCopies);
            RefreshStructure(); status.Text = error.Message;
        }
    }
    private (MusicEvent Event, List<MusicEventPart> Parts, int Index) StructureSelection()
    {
        if (structureEvent.SelectedItem is not EventChoice selected || structurePart.SelectedItem is not PartChoice part)
            throw new InvalidOperationException("Selecione um evento e um trecho na aba Inserir / copiar.");
        if (!selected.Event.CanRestructure) throw new InvalidOperationException(selected.Event.StructureRestriction);
        return (selected.Event, Parts(selected.Event), part.Index);
    }
    private void SetParts(MusicEvent e, List<MusicEventPart> parts)
    { structureReplacements.RemoveAll(r => r.EventId == e.Id); structureReplacements.Add(new(e.Id, parts.ToArray())); }
    private void InsertNote()
    {
        var (e, parts, index) = StructureSelection(); var part = parts[index];
        if (part.Notes.Count != 0) throw new InvalidOperationException("Selecione uma pausa. Para substituir um ataque, use Criar pausa primeiro.");
        if (!MusicRowLabels.ParsePitch(structurePitch.Text, out int pitch, out int tpc)) throw new InvalidOperationException("Digite uma nota como C4, F#4 ou Bb3.");
        double? length = part.DurationType == "measure" ? e.DurationQuarter : Quarter(part);
        var virtualRest = e with { Kind = "Rest", DurationQuarter = length };
        var added = NativeMusicEditor.SplitRest(virtualRest, pitch, tpc, structureDuration.SelectedValue as string ?? "quarter", 0);
        parts.RemoveAt(index); parts.InsertRange(index, added); SetParts(e, parts);
    }
    private void ChangeInserted()
    {
        var (e, parts, index) = StructureSelection(); var part = parts[index];
        if (!structureReplacements.Any(r => r.EventId == e.Id) || part.Notes.Count != 1) throw new InvalidOperationException("Escolha uma nota inserida neste rascunho. Para notas originais, use a aba Notas.");
        if (!MusicRowLabels.ParsePitch(structurePitch.Text, out int pitch, out int tpc)) throw new InvalidOperationException("Digite uma nota como C4, F#4 ou Bb3.");
        parts[index] = part with { Notes = [new(pitch, tpc)] }; SetParts(e, parts);
    }
    private void DeleteNote()
    {
        var (e, parts, index) = StructureSelection(); var part = parts[index];
        if (part.Notes.Count == 0) throw new InvalidOperationException("O evento selecionado já é uma pausa.");
        MusicPitch remove;
        if (part.Notes.Count == 1) remove = part.Notes[0];
        else if (noteGrid.SelectedItem is MusicNoteRow row && row.Event.Id == e.Id && part.Notes.Any(n => n.Pitch == row.Note.Pitch && n.Tpc == row.Note.Tpc)) remove = new(row.Note.Pitch, row.Note.Tpc);
        else throw new InvalidOperationException("Este ataque tem várias notas. Escolha a nota na aba Notas e volte para Excluir nota escolhida.");
        parts[index] = part with { Notes = part.Notes.Where(n => n != remove).ToArray() }; SetParts(e, parts);
    }
    private void MakeRest()
    {
        var (e, parts, index) = StructureSelection(); var part = parts[index];
        if (part.Notes.Count == 0) throw new InvalidOperationException("O trecho selecionado já é uma pausa.");
        parts[index] = part with { Notes = [] }; SetParts(e, parts);
    }
    private void CopyMeasure()
    {
        var (e, _, _) = StructureSelection();
        if (copyTarget.SelectedItem is not MusicMeasure target || copyVoice.SelectedItem is not int targetVoice)
            throw new InvalidOperationException("Escolha o compasso e a voz de destino.");
        structureCopies.Add(new(e.Staff, e.Measure, e.Voice, target.Staff, target.Number, targetVoice));
    }
    private void RemoveStructure()
    {
        if (structurePending.SelectedItem is not PendingStructure change) throw new InvalidOperationException("Selecione uma mudança na lista abaixo.");
        if (change.EventId is not null) structureReplacements.RemoveAll(r => r.EventId == change.EventId);
        if (change.Copy is not null) structureCopies.Remove(change.Copy);
    }
    private static string DurationLabel(string type) => type switch
    { "long" or "longa" => "Longa", "breve" => "Breve", "whole" => "Semibreve", "half" => "Mínima", "quarter" => "Semínima", "eighth" => "Colcheia", "16th" => "Semicolcheia", "32nd" => "Fusa", "64th" => "Semifusa", "measure" => "Compasso inteiro", _ => type.StartsWith("1", StringComparison.Ordinal) ? "Figura curta" : type };
    private static double? Quarter(MusicEventPart p)
    {
        double? basis = p.DurationType switch { "long" or "longa" => 16, "breve" => 8, "whole" => 4, "half" => 2, "quarter" => 1, "eighth" => .5, "16th" => .25, "32nd" => .125, "64th" => .0625, "128th" => .03125, "256th" => .015625, "512th" => .0078125, "1024th" => .00390625, _ => null };
        return basis * (2 - Math.Pow(.5, p.Dots));
    }
    private void BindStructuredEvents(MusicEvent[] rendered)
    {
        mappedStructureParts.Clear();
        foreach (var voice in model.Measures.SelectMany(m => m.Events).GroupBy(e => (e.Staff, e.Measure, e.Voice)))
        {
            var (staffNumber, barNumber, voiceNumber) = voice.Key;
            if (structureCopies.Any(c => c.TargetStaff == staffNumber && c.TargetMeasure == barNumber && c.TargetVoice == voiceNumber)) continue;
            double? at = 0;
            foreach (var original in voice)
            {
                var parts = structureReplacements.FirstOrDefault(r => r.EventId == original.Id)?.Parts;
                var originals = parts ?? [new MusicEventPart(current.Rhythms.TryGetValue(original.Id, out var rhythm) ? rhythm.Duration : original.DurationType,
                    rhythm?.Dots ?? original.Dots, original.Notes.Select(n => new MusicPitch(n.Pitch, n.Tpc)).ToArray())];
                for (int i = 0; i < originals.Count; i++)
                {
                    var expected = originals[i];
                    if (at is { } position)
                    {
                        var candidate = rendered.FirstOrDefault(e => e.Staff == staffNumber && e.Measure == barNumber && e.Voice == voiceNumber &&
                            e.Quarter is { } renderedAt && Math.Abs(renderedAt - position) < 1e-9 && e.Kind == (expected.Notes.Count == 0 ? "Rest" : "Chord") && e.Notes.Count == expected.Notes.Count);
                        if (candidate is not null)
                        {
                            mappedEventIds[candidate.Id] = original.Id;
                            if (parts is not null) mappedStructureParts[candidate.Id] = (original.Id, i);
                        }
                    }
                    at += expected.DurationType == "measure" ? original.DurationQuarter : Quarter(expected);
                }
            }
        }
    }
    private bool SelectStructuredAttack(ScorePositionSelectedEventArgs e, HashSet<string> ids)
    {
        foreach (string id in e.EventIds)
            if (mappedStructureParts.TryGetValue(id, out var part))
            {
                tabs.SelectedIndex = 3;
                structureEvent.SelectedItem = structureEvent.Items.Cast<EventChoice>().FirstOrDefault(c => c.Event.Id == part.EventId);
                structurePart.SelectedIndex = part.Part;
                status.Text = "Trecho inserido selecionado. Você pode alterar sua nota, criar pausa ou inserir outra nota na pausa restante.";
                return true;
            }
        return false;
    }
}
