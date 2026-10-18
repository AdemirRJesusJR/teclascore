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
    private static readonly string[] StyleNames = ["Acordes", "Arpejo", "Valsa", "Balada", "Baião", "Xote", "Bossa nova", "Sertanejo", "Guarânia"];
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
                : "Na espera, os acordes da cifra e o baixo soam conforme você avança, no seu ritmo, e esperam se você parar. No tempo, tocam junto com o metrônomo, no estilo escolhido."
        };
        AutomationProperties.SetName(followMe, "Acompanhamento me segue");
        followMe.Click += (_, _) =>
        {
            // Works in wait mode (following the player) and in time mode (with the metronome); the score audio has its own.
            if (followMe.IsChecked == true) { mode.SelectedIndex = 0; withMusic.IsChecked = false; }
            Reset();
        };
        study.Children.Add(followMe);
        followStyle = new ComboBox { Width = 112, ItemsSource = StyleNames, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 6), IsEnabled = chordSteps.Count > 0,
            ToolTip = "Como a mão esquerda toca: acordes segurados, arpejo, valsa, balada ou ritmos brasileiros (baião, xote, bossa nova, sertanejo e guarânia)" };
        AutomationProperties.SetName(followStyle, "Estilo do acompanhamento");
        followStyle.SelectionChanged += (_, _) => { ReleaseFollow(); if (follower is { Label.Length: > 0 }) RestartPattern(); };
        study.Children.Add(followStyle);
        followTimer.Tick += (_, _) => FollowTick();
    }

    /// <summary>Starts (or stops) following for a new attempt.</summary>
    private void ResetFollow()
    {
        StopFollow();
        follower = FollowMe && Mode == PracticeMode.Notes && !WithMusic && chordSteps.Count > 0 ? new AccompanimentFollower(chordSteps) : null;
        timedChord = null;
        if (follower is null) return;
        try { followOutput ??= followOutputFactory(); } catch (InvalidOperationException) { followOutput = null; }
    }

    /// <summary>The player reached this melody step: changes chord when the cipher changes, and resumes a paused pattern.</summary>
    private void FollowStep(PracticeStep step)
    {
        if (follower is null || InTime) return;
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

    private PracticeStep? timedChord;

    /// <summary>
    /// Time mode: the chords follow the clock (the score position in quarters) instead of the player, each chord's
    /// pattern starting where the chord starts and repeating until the next one.
    /// </summary>
    internal void TimedFollow(double position)
    {
        if (follower is null || !InTime || WithMusic) return;
        if (follower.ChordAt(position) is not { } chord) { ReleaseFollow(); timedChord = null; return; }
        if (!ReferenceEquals(chord, timedChord))
        {
            ReleaseFollow(); timedChord = chord;
            pattern = AccompanimentPattern.Bar(FollowStyle, AccompanimentFollower.Voice(chord));
            patternBeats = AccompanimentPattern.BeatsPerBar(FollowStyle, 4); patternNext = 0;
            // Joining in the middle of a chord (after a pause), the pattern resumes at the right beat.
            double into = (position - chord.Start) % patternBeats;
            // (a clock tick lands a little after the chord start: events of the last eighth of a beat still sound)
            while (patternNext < pattern.Count && pattern[patternNext].Beat < into - .12) patternNext++;
            patternStart = chord.Start + Math.Floor((position - chord.Start) / patternBeats) * patternBeats;
        }
        double beat = position - patternStart;
        while (true)
        {
            if (patternNext >= pattern.Count)
            {
                if (beat < patternBeats) break;
                patternStart += patternBeats; beat -= patternBeats; patternNext = 0;
            }
            var e = pattern[patternNext];
            if (e.Beat > beat) break;
            Send(e.Pitch, e.On); patternNext++;
        }
    }

    /// <summary>Time mode paused or stopped: the keys are released; the chord starts again where the clock resumes.</summary>
    internal void PauseTimedFollow() { ReleaseFollow(); timedChord = null; }

    private void ReleaseFollow() { foreach (int pitch in followSounding.ToArray()) Send(pitch, false); }

    private string FollowText => follower is { Label.Length: > 0 } f ? $" · acompanhando {f.Label}{(f.Bpm is { } bpm ? $", você está a ♩ ≈ {bpm:0}" : "")}" : "";

    private void StopFollow()
    {
        followTimer.Stop(); patternPaused = true;
        ReleaseFollow();
        follower?.Stop();
    }
}
