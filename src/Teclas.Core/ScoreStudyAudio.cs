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
        var exporter = new ScoreExporter(museScorePath, cacheDirectory);
        if (speed == 1) return await exporter.PrepareAudioAsync(scorePath, progress, token);
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
                File.Move(temporary, studyScore, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var audio = await exporter.PrepareAudioAsync(studyScore, progress, token);
        if (await HashAsync(scorePath, token) != originalHash)
            throw new IOException("A partitura mudou durante a preparação do estudo. Tente novamente.");
        return audio;
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
