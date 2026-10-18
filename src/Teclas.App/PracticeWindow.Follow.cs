using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

/// <summary>"Acompanhamento me segue": while the player plays the melody in wait mode, the chords and bass of the cipher sound at the player's own pace.</summary>
public partial class PracticeWindow
{
    private CheckBox? followMe;
    private AccompanimentFollower? follower;
    private IMidiOutputTransport? followOutput;
    private readonly Stopwatch followClock = Stopwatch.StartNew();
    /// <summary>Opens the output for the following accompaniment; smoke tests replace it with a recorder.</summary>
    internal Func<IMidiOutputTransport?> followOutputFactory = () => new WinMmMidiOutputTransport();
    public AccompanimentFollower? Follower => follower;
    public bool FollowMe { get => followMe?.IsChecked == true; set { if (followMe is not null) { followMe.IsChecked = value; Reset(); } } }

    private void BuildFollow(Panel study)
    {
        followMe = new CheckBox
        {
            Content = "Acompanhamento me segue", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 6), IsEnabled = chordSteps.Count > 0,
            ToolTip = chordSteps.Count == 0 ? "Esta partitura não tem cifras para acompanhar."
                : "Na espera, tocando as notas da partitura: os acordes da cifra e o baixo soam conforme você avança, no seu ritmo. Se você parar, o acompanhamento espera."
        };
        AutomationProperties.SetName(followMe, "Acompanhamento me segue");
        followMe.Click += (_, _) =>
        {
            if (followMe.IsChecked == true) { mode.SelectedIndex = 0; inTime.IsChecked = false; withMusic.IsChecked = false; }
            Reset();
        };
        study.Children.Add(followMe);
    }

    /// <summary>Starts (or stops) following for a new attempt.</summary>
    private void ResetFollow()
    {
        StopFollow();
        follower = FollowMe && Mode == PracticeMode.Notes && !InTime && chordSteps.Count > 0 ? new AccompanimentFollower(chordSteps) : null;
        if (follower is null) return;
        try { followOutput ??= followOutputFactory(); } catch (InvalidOperationException) { followOutput = null; }
    }

    /// <summary>The player reached this melody step: changes chord when the cipher changes.</summary>
    private void FollowStep(PracticeStep step)
    {
        if (follower?.Follow(step.Start, followClock.Elapsed.TotalSeconds) is not { } change) return;
        try
        {
            foreach (int pitch in change.Off) followOutput?.Send(0x81u | (uint)pitch << 8);
            foreach (int pitch in change.On) followOutput?.Send(0x91u | (uint)pitch << 8 | (uint)(pitch < AccompanimentFollower.ChordFloor ? 84 : 66) << 16);
        }
        catch (InvalidOperationException) { followOutput = null; }
    }

    private string FollowText => follower is { Label.Length: > 0 } f ? $" · acompanhando {f.Label}{(f.Bpm is { } bpm ? $", você está a ♩ ≈ {bpm:0}" : "")}" : "";

    private void StopFollow()
    {
        if (follower is null) return;
        try { foreach (int pitch in follower.Stop()) followOutput?.Send(0x81u | (uint)pitch << 8); }
        catch (InvalidOperationException) { followOutput = null; }
    }
}
