using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Teclas.Core;

/// <summary>Changes playback tempo in a private score copy; notes and the user's score never change.</summary>
public sealed class ScoreStudyAudio(string museScorePath, string cacheDirectory)
{
    public static bool IsSupportedSpeed(double speed) => speed is .25 or .5 or .75 or 1 or 1.25 or 1.5;

    public async Task<ScoreAudio> PrepareAsync(string scorePath, double speed, IProgress<string>? progress, CancellationToken token)
    {
        if (!IsSupportedSpeed(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        token.ThrowIfCancellationRequested();
        var exporter = new ScoreExporter(museScorePath, cacheDirectory);
        if (speed == 1) return await exporter.PrepareAudioAsync(scorePath, progress, token);
        var (studyScore, originalHash) = await PrepareStudyScoreAsync(scorePath, speed, progress, token);
        var audio = await exporter.PrepareAudioAsync(studyScore, progress, token);
        progress?.Report("Conferindo a partitura original antes de iniciar a reprodução…");
        if (await HashAsync(scorePath, token) != originalHash)
            throw new IOException("A partitura mudou durante a preparação do estudo. Tente novamente.");
        return audio;
    }

    /// <summary>Beats of the audio rendered at this speed, from MuseScore's own playback MIDI (repeats unrolled).</summary>
    public async Task<IReadOnlyList<MetronomeBeat>> PrepareBeatsAsync(string scorePath, double speed, IProgress<string>? progress, CancellationToken token)
    {
        if (!IsSupportedSpeed(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        token.ThrowIfCancellationRequested();
        string played = speed == 1 ? scorePath : (await PrepareStudyScoreAsync(scorePath, speed, progress, token)).Path;
        Directory.CreateDirectory(cacheDirectory);
        string midiPath = Path.Combine(cacheDirectory, $"{await HashAsync(played, token)}-pulsos-v1.mid");
        if (!File.Exists(midiPath))
        {
            string temporary = Path.Combine(cacheDirectory, ".pulsos-" + Guid.NewGuid().ToString("N") + ".mid");
            try
            {
                progress?.Report("Localizando os tempos dos compassos para o metrônomo…");
                await new ScoreExporter(museScorePath, cacheDirectory).ExportMidiAsync(played, temporary, token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, midiPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        try { return ScoreMetronome.BuildBeats(MidiReader.Read(midiPath)); }
        catch (InvalidDataException) { File.Delete(midiPath); throw; }
    }

    /// <summary>Returns a cached copy of the prepared audio with metronome clicks; the original audio stays available.</summary>
    public async Task<ScoreAudio> PrepareMetronomeAudioAsync(ScoreAudio audio, IReadOnlyList<MetronomeBeat> beats, IProgress<string>? progress, CancellationToken token)
    {
        string target = Path.Combine(Path.GetDirectoryName(audio.Path)!, Path.GetFileNameWithoutExtension(audio.Path) + "-metronomo-v1.wav");
        if (File.Exists(target))
        {
            try { return new(target, ScoreExporter.ReadWaveInfo(target).Duration); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { File.Delete(target); }
        }
        string temporary = Path.Combine(Path.GetDirectoryName(audio.Path)!, ".metronomo-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            progress?.Report("Acrescentando o metrônomo ao áudio…");
            var info = await Task.Run(() => ScoreMetronome.MixClicks(audio.Path, temporary, beats, token), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
            return new(target, info.Duration);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<(string Path, string OriginalHash)> PrepareStudyScoreAsync(string scorePath, double speed, IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report($"Conferindo a partitura para estudo a {speed:P0}…");
        Directory.CreateDirectory(cacheDirectory);
        var originalHash = await HashAsync(scorePath, token);
        var studyScore = Path.Combine(cacheDirectory, $"{originalHash}-study-v1-{(int)(speed * 100)}.mscz");
        if (!File.Exists(studyScore))
        {
            var temporary = Path.Combine(cacheDirectory, ".study-" + Guid.NewGuid().ToString("N") + ".mscz");
            try
            {
                progress?.Report($"Preparando estudo a {speed:P0}, mantendo a altura das notas…");
                await Task.Run(() => CreateTempoCopy(scorePath, temporary, speed), token);
                token.ThrowIfCancellationRequested();
                progress?.Report("Salvando a cópia de estudo com o andamento escolhido…");
                File.Move(temporary, studyScore, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        else progress?.Report("Cópia de estudo encontrada. Procurando o áudio preparado…");
        return (studyScore, originalHash);
    }

    public static void CreateTempoCopy(string source, string destination, double speed)
    {
        if (!IsSupportedSpeed(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) || File.Exists(destination))
            throw new IOException("O estudo precisa de uma nova cópia, preservando a partitura original.");
        var archive = NativeScoreArchive.Read(source);
        foreach (var score in archive.MainDocument.Descendants("Score"))
        {
            var staves = score.Elements("Staff").ToArray();
            var firstVoices = staves.SelectMany(staff => staff.Elements("Measure").FirstOrDefault()?.Elements("voice") ?? []);
            var hasStartingTempo = firstVoices.Any(voice => voice.Elements().TakeWhile(element => element.Name.LocalName is not ("Chord" or "Rest" or "location" or "tick")).Any(element => element.Name == "Tempo"));
            if (!hasStartingTempo)
            {
                var voice = staves.FirstOrDefault()?.Elements("Measure").FirstOrDefault()?.Elements("voice").FirstOrDefault()
                    ?? throw new InvalidDataException("A partitura não contém uma voz para preparar o estudo.");
                voice.AddFirst(new XElement("Tempo", new XElement("tempo", "2"), new XElement("followText", "0"), new XElement("visible", "0"), new XElement("text", "Estudo")));
            }
        }
        foreach (var marking in archive.MainDocument.Descendants("Tempo"))
        {
            var tempo = marking.Element("tempo");
            if (tempo is null || !double.TryParse(tempo.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
                throw new InvalidDataException("Uma indicação de andamento não pôde ser interpretada. Confira-a no MuseScore antes de usar o modo de estudo.");
            tempo.Value = (value * speed).ToString("R", CultureInfo.InvariantCulture);
            // MuseScore's followText=1 would overwrite the numeric value from the visible label.
            marking.SetElementValue("followText", "0");
        }
        archive.Save(destination);
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    { await using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, token)); }
}
