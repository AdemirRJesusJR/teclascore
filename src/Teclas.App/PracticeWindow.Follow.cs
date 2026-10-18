using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// "Acompanhamento me segue": while the player plays the melody in wait mode, the chords and bass of the cipher sound at
/// the player's own pace, in the chosen left-hand style. Patterns run at the tempo the player is really playing and stop
/// (releasing the keys) when the player pauses for more than a bar; the next note starts the bar again.
/// </summary>
public partial class PracticeWindow
{
    private static readonly string[] StyleNames = ["Acordes", "Arpejo", "Valsa", "Balada"];
    private CheckBox? followMe;
    private ComboBox? followStyle;
    private AccompanimentFollower? follower;
    private IMidiOutputTransport? followOutput;
    private readonly Stopwatch followClock = Stopwatch.StartNew();
    private readonly DispatcherTimer followTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
    private readonly HashSet<int> followSounding = [];
    private IReadOnlyList<PatternEvent> pattern = [];
    private int patternBeats = 4, patternNext;
    private double patternStart, lastStepAt, patternBpm;
    private bool patternPaused = true;
    /// <summary>Opens the output for the following accompaniment; smoke tests replace it with a recorder.</summary>
    internal Func<IMidiOutputTransport?> followOutputFactory = () => new WinMmMidiOutputTransport();
    /// <summary>Seconds on the following clock; smoke tests drive it by hand.</summary>
    internal Func<double> followNow = null!;
    public AccompanimentFollower? Follower => follower;
    public bool FollowMe { get => followMe?.IsChecked == true; set { if (followMe is not null) { followMe.IsChecked = value; Reset(); } } }
    public AccompanimentStyle FollowStyle
    {
        get => (AccompanimentStyle)Math.Max(0, followStyle?.SelectedIndex ?? 0);
        set { if (followStyle is not null) followStyle.SelectedIndex = (int)value; }
    }
    internal IReadOnlyCollection<int> FollowSounding => followSounding;

    private void BuildFollow(Panel study)
    {
        followNow = () => followClock.Elapsed.TotalSeconds;
        followMe = new CheckBox
        {
            Content = "Acompanhamento me segue", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 6), IsEnabled = chordSteps.Count > 0,
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
        followStyle = new ComboBox { Width = 92, ItemsSource = StyleNames, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 6), IsEnabled = chordSteps.Count > 0,
            ToolTip = "Como a mão esquerda toca: acordes segurados, arpejo em colcheias, valsa (baixo e dois acordes) ou balada (baixo e acorde no 1º e no 3º tempo)" };
        AutomationProperties.SetName(followStyle, "Estilo do acompanhamento");
        followStyle.SelectionChanged += (_, _) => { ReleaseFollow(); if (follower is { Label.Length: > 0 }) RestartPattern(); };
        study.Children.Add(followStyle);
        followTimer.Tick += (_, _) => FollowTick();
    }

    /// <summary>Starts (or stops) following for a new attempt.</summary>
    private void ResetFollow()
    {
        StopFollow();
        follower = FollowMe && Mode == PracticeMode.Notes && !InTime && chordSteps.Count > 0 ? new AccompanimentFollower(chordSteps) : null;
        if (follower is null) return;
        try { followOutput ??= followOutputFactory(); } catch (InvalidOperationException) { followOutput = null; }
    }

    /// <summary>The player reached this melody step: changes chord when the cipher changes, and resumes a paused pattern.</summary>
    private void FollowStep(PracticeStep step)
    {
        if (follower is null) return;
        double now = followNow();
        bool changed = follower.Follow(step.Start, now) is not null;
        lastStepAt = now;
        if (changed || patternPaused) RestartPattern();
        FollowTick();
    }

    private void RestartPattern()
    {
        if (follower is null || follower.Sounding.Count == 0) return;
        ReleaseFollow();
        pattern = AccompanimentPattern.Bar(FollowStyle, follower.Sounding);
        patternBeats = AccompanimentPattern.BeatsPerBar(FollowStyle, 4);
        patternStart = followNow(); patternNext = 0; patternPaused = false;
        patternBpm = Math.Clamp(follower.Bpm ?? Bpm, 30, 240);
        followTimer.Start();
    }

    /// <summary>Plays the pattern events that are due; loops the bar, holds Block chords, pauses when the player stops.</summary>
    internal void FollowTick()
    {
        if (follower is null || patternPaused) { followTimer.Stop(); return; }
        double now = followNow();
        if (follower.Bpm is { } measured) patternBpm = Math.Clamp(measured, 30, 240);
        double beat = (now - patternStart) * patternBpm / 60;
        if ((now - lastStepAt) * patternBpm / 60 > patternBeats + .5) { ReleaseFollow(); patternPaused = true; followTimer.Stop(); return; }
        if (FollowStyle == AccompanimentStyle.Block)
        {
            // Held chord: switch everything on once and keep it until the next chord.
            foreach (var e in pattern.Where(e => e.On)) Send(e.Pitch, true);
            patternPaused = false; followTimer.Stop(); return;
        }
        while (true)
        {
            if (patternNext >= pattern.Count)
            {
                if (beat < patternBeats) break;
                patternStart += patternBeats * 60 / patternBpm; beat -= patternBeats; patternNext = 0;
            }
            var e = pattern[patternNext];
            if (e.Beat > beat) break;
            Send(e.Pitch, e.On); patternNext++;
        }
    }

    private void Send(int pitch, bool on)
    {
        try
        {
            if (on && followSounding.Add(pitch)) followOutput?.Send(0x91u | (uint)pitch << 8 | (uint)(pitch < AccompanimentFollower.ChordFloor ? 84 : 66) << 16);
            else if (!on && followSounding.Remove(pitch)) followOutput?.Send(0x81u | (uint)pitch << 8);
        }
        catch (InvalidOperationException) { followOutput = null; }
    }

    private void ReleaseFollow() { foreach (int pitch in followSounding.ToArray()) Send(pitch, false); }

    private string FollowText => follower is { Label.Length: > 0 } f ? $" · acompanhando {f.Label}{(f.Bpm is { } bpm ? $", você está a ♩ ≈ {bpm:0}" : "")}" : "";

    private void StopFollow()
    {
        followTimer.Stop(); patternPaused = true;
        ReleaseFollow();
        follower?.Stop();
    }
}
