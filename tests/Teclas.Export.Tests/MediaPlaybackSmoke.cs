using System.Windows.Media;
using System.Windows.Threading;

internal static class MediaPlaybackSmoke
{
    public static Task<string> RunAsync(string audioPath)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var player = new MediaPlayer { Volume = 0, IsMuted = true };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            var frame = new DispatcherFrame();
            var started = DateTime.UtcNow;
            bool opened = false, paused = false, sought = false;
            double pausedPosition = 0;
            DateTime phaseStarted = started;
            string? error = null;
            void Finish(string? failure)
            {
                error = failure; timer.Stop(); player.Close(); frame.Continue = false;
            }
            player.MediaFailed += (_, e) => Finish("Windows MediaPlayer failed: " + e.ErrorException.Message);
            player.MediaOpened += (_, _) =>
            {
                if (!player.HasAudio || !player.NaturalDuration.HasTimeSpan || player.NaturalDuration.TimeSpan.TotalSeconds <= 1)
                { Finish("Media opened without a usable audio track or duration."); return; }
                opened = true; phaseStarted = DateTime.UtcNow; player.Play();
            };
            timer.Tick += (_, _) =>
            {
                if ((DateTime.UtcNow - started).TotalSeconds > 25) { Finish("Windows audio decoding or playback timed out."); return; }
                if (!opened) return;
                if (!paused && player.Position.TotalSeconds > .4)
                {
                    player.Pause(); paused = true; pausedPosition = player.Position.TotalSeconds; phaseStarted = DateTime.UtcNow; return;
                }
                if (paused && !sought && (DateTime.UtcNow - phaseStarted).TotalSeconds > .6)
                {
                    if (Math.Abs(player.Position.TotalSeconds - pausedPosition) > .1) { Finish("Pause did not hold playback position."); return; }
                    player.Position = TimeSpan.FromSeconds(2); player.Play(); sought = true; phaseStarted = DateTime.UtcNow; return;
                }
                if (sought && player.Position.TotalSeconds > 2.3)
                {
                    player.Stop();
                    if (player.Position.TotalSeconds > .1) { Finish("Stop did not reset playback position."); return; }
                    Finish(null);
                }
            };
            try
            {
                player.Open(new Uri(audioPath)); timer.Start(); Dispatcher.PushFrame(frame);
                if (error is not null) result.TrySetException(new InvalidOperationException(error));
                else result.TrySetResult("MediaOpened + HasAudio + duration + muted Play + Pause + Seek + Stop passed.");
            }
            catch (Exception ex) { result.TrySetException(ex); }
            finally { timer.Stop(); player.Close(); }
        }) { IsBackground = true, Name = "TeclaScore muted media verification" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
}
