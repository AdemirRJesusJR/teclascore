using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// Faster steps: Ctrl+Enter continues (or generates on the last step), Alt+← / Alt+→ change the step, Ctrl+Shift+Enter
/// generates in the background and opens the next song, and "Confirmar etapa" confirms at once the simple doubts of the
/// current step (never a block, never a check that needs an action).
/// </summary>
public partial class MainWindow
{
    private IReadOnlyList<ScoreFlowCheck> ConfirmableOfCurrentStep() =>
        GuidedFlow && FlowCurrentStep != ScoreFlowStep.Generate ? ScoreFlowRules.Confirmable(flowChecks, FlowCurrentStep) : [];

    public bool CanConfirmFlowStep => CanEditMidi && ConfirmableOfCurrentStep().Count > 0;
    public Visibility ConfirmFlowStepVisibility => ConfirmableOfCurrentStep().Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string ConfirmFlowStepToolTip
    {
        get
        {
            var confirmable = ConfirmableOfCurrentStep();
            int blocks = ChecksOfCurrentStep().Count(c => c.Pending && c.Level == ScoreFlowLevel.Block);
            return "Confirmar pendências desta etapa: " + (confirmable.Count == 1 ? "1 confirmação" : $"{confirmable.Count} confirmações") +
                " (" + string.Join("; ", confirmable.Select(c => c.Message.Length > 60 ? c.Message[..60] + "…" : c.Message)) + ")." +
                (blocks > 0 ? $" Os bloqueios ({blocks}) continuam e precisam ser resolvidos." : "");
        }
    }

    public string FlowNextToolTip => FlowSummary + (FlowCurrentStep == ScoreFlowStep.Generate ? " (Ctrl+Enter)" : " (Ctrl+Enter ou Alt+→)");

    /// <summary>Confirms the pending confirmations of one step of a song (the doubts with a Confirmar button); returns how many.</summary>
    internal int ConfirmPendingOfStep(MidiRow row, ScoreFlowStep step)
    {
        var confirmable = ScoreFlowRules.Confirmable(ScoreFlowRules.Evaluate(FlowInput(row)), step);
        foreach (var check in confirmable) StateOf(row).Confirmed.Add($"{check.Id}@{check.Step}#{FlowContext(row, check.Step)}");
        if (confirmable.Count > 0) NotifyFlow();
        return confirmable.Count;
    }

    private void ConfirmFlowStepClick(object sender, RoutedEventArgs e) => ConfirmCurrentFlowStep();

    internal int ConfirmCurrentFlowStep()
    {
        if (!CanConfirmFlowStep || selectedFile is not { } row) return 0;
        int count = ConfirmPendingOfStep(row, FlowCurrentStep);
        int blocks = ChecksOfCurrentStep().Count(c => c.Pending && c.Level == ScoreFlowLevel.Block);
        Status = (count == 1 ? "1 confirmação feita." : $"{count} confirmações feitas.") +
            (blocks > 0 ? " Ainda há bloqueio nesta etapa: veja Para continuar." : CanFlowNext ? " Ctrl+Enter continua." : "");
        PersistPreferences();
        return count;
    }

    /// <summary>The flow shortcuts; returns true when the key is one of them (even if unavailable right now).</summary>
    private bool HandleFlowShortcut(Key key, ModifierKeys modifiers, IInputElement? focus)
    {
        const ModifierKeys ctrl = ModifierKeys.Control, ctrlShift = ModifierKeys.Control | ModifierKeys.Shift;
        // Multi-line fields keep Enter for themselves.
        if (key == Key.Enter && focus is TextBox { AcceptsReturn: true }) return false;
        if (currentPage != "library" || selectedFile is null) return false;
        switch (modifiers, key)
        {
            case (ctrl, Key.Enter):
                if (CanFlowNext) FlowNextClick(this, new RoutedEventArgs());
                else Status = FlowSummary;
                return true;
            case (ctrlShift, Key.Enter):
                if (CanGenerateInBackground) _ = GenerateInBackgroundAndOpenNextAsync();
                else Status = "Gerar em segundo plano fica disponível na etapa Gerar, com todas as etapas conferidas.";
                return true;
            case (ModifierKeys.Alt, Key.Left):
                if (CanFlowBack) FlowBackClick(this, new RoutedEventArgs());
                return true;
            case (ModifierKeys.Alt, Key.Right):
                var step = FlowCurrentStep;
                if (step == ScoreFlowStep.Generate || !CanEditMidi) return true;
                if (!GuidedFlow || step + 1 <= FlowAllowedStep && (step != ScoreFlowStep.Chords || CommitEdits())) EditorTabs.SelectedIndex = TabOfStep(step + 1);
                else if (CanFlowNext) FlowNextClick(this, new RoutedEventArgs());
                else Status = FlowSummary;
                return true;
        }
        return false;
    }
}
