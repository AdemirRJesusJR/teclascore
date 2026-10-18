using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Teclas.Core;

/// <summary>Page coordinates are fractions of the complete, uncropped page; page numbers start at one.</summary>
public sealed record ScorePageRegion(int Page, double X, double Y, double Width, double Height);
public sealed record ScoreMeasureRegion(int Measure, ScorePageRegion Bounds);
public sealed record ScoreSegmentRegion(int Id, int Measure, ScorePageRegion Bounds, IReadOnlyList<string> EventIds);
/// <summary>Times use the player's logical PositionSeconds: actual rendered audio seconds multiplied by Speed.</summary>
public sealed record ScorePlaybackOccurrence(int Index, int Measure, double StartSeconds, double EndSeconds, int? SegmentId);
public sealed record ScorePlaybackMap(string SourcePath, string SourceSha256, double Speed,
    IReadOnlyList<string> PreviewPages, IReadOnlyList<ScoreMeasureRegion> Measures, IReadOnlyList<ScoreSegmentRegion> Segments,
    IReadOnlyList<ScorePlaybackOccurrence> Timeline, IReadOnlyList<string> Limitations, bool CanSynchronize)
{
    public IReadOnlyList<ScorePlaybackOccurrence> MeasureTimeline { get; init; } = [];
    public double DurationSeconds => Timeline.Count == 0 ? 0 : Timeline[^1].EndSeconds;
    public ScorePlaybackOccurrence? At(double seconds)
    {
        if (!CanSynchronize || !double.IsFinite(seconds) || seconds < 0 || seconds >= DurationSeconds) return null;
        int low = 0, high = Timeline.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2; var item = Timeline[middle];
            if (seconds < item.StartSeconds) high = middle - 1;
            else if (seconds >= item.EndSeconds) low = middle + 1;
            else return item;
        }
        return null;
    }
    public ScorePlaybackOccurrence? FindOccurrence(int measure, int? segmentId, double? currentSeconds = null)
    {
        var matches = (segmentId is null ? MeasureTimeline : Timeline).Where(t => t.Measure == measure && (segmentId is null || t.SegmentId == segmentId)).ToArray();
        if (matches.Length == 0) return null;
        if (currentSeconds is not { } seconds || !double.IsFinite(seconds)) return matches[0];
        return matches.FirstOrDefault(t => t.StartSeconds <= seconds && seconds < t.EndSeconds) ??
            matches.FirstOrDefault(t => t.StartSeconds >= seconds) ?? matches[0];
    }
}

/// <summary>Uses MuseScore's own measure/segment positions and playback occurrences, never proportional page progress.</summary>
public sealed class ScorePlaybackMapService(string museScorePath, string cacheDirectory)
{
    private const int ExportDpi = 120;
    public async Task<ScorePlaybackMap> PrepareAsync(string scorePath, double speed, IProgress<string>? progress, CancellationToken token)
    {
        if (!ScoreStudyAudio.IsSupportedSpeed(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        token.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(scorePath);
        string hash = await Task.Run(() => NativeMusicEditor.HashFile(source, token), token);
        if (!File.Exists(museScorePath)) throw new FileNotFoundException("Localize o MuseScore para preparar o acompanhamento da partitura.", museScorePath);
        string job = Path.Combine(Path.GetFullPath(cacheDirectory), "mapa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        string snapshot = Path.Combine(job, "origem.mscz"), input = snapshot;
        bool complete = false;
        try
        {
            progress?.Report("Preparando uma cópia para localizar os compassos e as notas…");
            await using (var from = File.OpenRead(source))
            await using (var to = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await from.CopyToAsync(to, token);
            if (NativeMusicEditor.HashFile(snapshot, token) != hash) throw new IOException("A partitura mudou durante a preparação do mapa.");
            if (speed != 1)
            {
                input = Path.Combine(job, "estudo.mscz");
                await Task.Run(() => ScoreStudyAudio.CreateTempoCopy(snapshot, input, speed), token);
            }
            string mpos = Path.Combine(job, "compassos.mpos"), spos = Path.Combine(job, "segmentos.spos"), midi = Path.Combine(job, "partitura.mid");
            string png = Path.Combine(job, "pagina.png"), request = Path.Combine(job, "exportar.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new[] { new { @in = input, @out = new[] { mpos, spos, midi, png } } }), token);
            progress?.Report("Obtendo posições e tempos reais da partitura no MuseScore…");
            await ExportAsync(request, token);
            var pages = Directory.EnumerateFiles(job, "pagina*.png").Select(p => (Path: p, Number: PageNumber(p))).Where(p => p.Number >= 0)
                .OrderBy(p => p.Number).Select(p => p.Path).ToArray();
            if (pages.Length == 0 || !File.Exists(mpos) || !File.Exists(spos) || !File.Exists(midi))
                throw new InvalidDataException("Esta versão do MuseScore não exportou o mapa completo. A reprodução continua disponível sem acompanhamento.");
            progress?.Report("Conferindo compassos, repetições e mudanças de andamento…");
            var map = await Task.Run(() => ReadExports(source, hash, speed, mpos, spos, midi, pages, token), token);
            if (NativeMusicEditor.HashFile(source, token) != hash) throw new IOException("A partitura mudou durante a preparação do mapa.");
            complete = true;
            return map;
        }
        finally
        {
            foreach (string name in new[] { "origem.mscz", "estudo.mscz", "exportar.json" }) DeleteFile(Path.Combine(job, name));
            if (!complete)
            {
                foreach (string file in Directory.EnumerateFiles(job)) DeleteFile(file);
                try { Directory.Delete(job, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Parses exports from one immutable score. Exposed for deterministic validation without playing audio.</summary>
    public static ScorePlaybackMap ReadExports(string scorePath, string sourceSha256, double speed, string measurePositionsPath,
        string segmentPositionsPath, string midiPath, IReadOnlyList<string> previewPages, CancellationToken token = default)
    {
        if (!ScoreStudyAudio.IsSupportedSpeed(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        string source = Path.GetFullPath(scorePath);
        if (NativeMusicEditor.HashFile(source, token) != sourceSha256) throw new IOException("A partitura mudou desde a exportação das posições.");
        var native = NativeScoreArchive.Read(source); var model = NativeMusicEditor.Open(source, token);
        var limitations = new List<string>();
        var style = native.ReadStyle();
        double width = Number(style.Element("pageWidth")?.Value, native.Metadata.PageWidthMm / 25.4) * ExportDpi * 12;
        double height = Number(style.Element("pageHeight")?.Value, native.Metadata.PageHeightMm / 25.4) * ExportDpi * 12;
        var rawMeasures = ReadPositions(measurePositionsPath, token);
        var rawSegments = ReadPositions(segmentPositionsPath, token);
        var measures = rawMeasures.Elements.Select(e => new ScoreMeasureRegion(e.Id + 1, Region(e))).ToArray();
        bool reliableMeasures = native.Score.Elements("Staff").Select(s => s.Elements("Measure").Count()).Distinct().Count() == 1 &&
            rawMeasures.Elements.Length == native.Metadata.MeasureCount && rawMeasures.Elements.Select(e => e.Id).SequenceEqual(Enumerable.Range(0, rawMeasures.Elements.Length));
        if (!reliableMeasures) limitations.Add("Compassos agrupados, pautas com comprimentos diferentes ou posições incompletas impedem a correspondência segura. O acompanhamento está desativado.");
        if (previewPages.Count == 0 || rawMeasures.Elements.Any(e => e.Page < 0 || e.Page >= previewPages.Count))
        { reliableMeasures = false; limitations.Add("As páginas do mapa não correspondem à prévia disponível."); }
        foreach (string page in previewPages)
        {
            token.ThrowIfCancellationRequested();
            var dimensions = PngSize(page);
            if (Math.Abs(dimensions.Width / (width / 12) - 1) > .015 || Math.Abs(dimensions.Height / (height / 12) - 1) > .015)
            { reliableMeasures = false; limitations.Add("A prévia está recortada ou usa outro tamanho de página. Não será exibido um cursor sem correspondência."); break; }
        }
        var segmentRegions = new List<ScoreSegmentRegion>();
        foreach (var e in rawSegments.Elements)
        {
            var owners = rawMeasures.Elements.Where(m => e.Page == m.Page && e.X >= m.X - 2 && e.X < m.X + m.Width + 2 &&
                Math.Abs(e.Y - m.Y) <= Math.Max(3, m.Height * .04)).ToArray();
            int measure = owners.Length == 1 ? owners[0].Id + 1 : 0;
            segmentRegions.Add(new(e.Id, measure, Region(e), []));
        }
        if (segmentRegions.Any(s => s.Measure == 0))
            limitations.Add("Alguns ataques não puderam ser associados a um compasso; a seleção de notas nesses trechos permanece indisponível.");
        if (reliableMeasures)
        {
            foreach (var bar in measures)
            {
                var segments = segmentRegions.Where(s => s.Measure == bar.Measure).OrderBy(s => s.Id).ToArray();
                var events = model.Measures.Where(m => m.Number == bar.Measure).SelectMany(m => m.Events).ToArray();
                var xmlMeasures = native.Score.Elements("Staff").Select(s => s.Elements("Measure").ElementAt(bar.Measure - 1)).ToArray();
                bool special = events.Any(e => e.Quarter is null) || xmlMeasures.Any(m => m.Descendants().Any(e => e.Name.LocalName is "Tuplet" or "tuplet" or "endTuplet" or "timeStretch") || m.Elements("voice").Elements("location").Any()) ||
                    xmlMeasures.Any(m => m.Descendants("Chord").Any(c => c.Elements().Any(e => e.Name.LocalName.Contains("grace", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName is "acciaccatura" or "appoggiatura")));
                if (special) continue;
                var groups = events.GroupBy(e => Math.Round(e.Quarter!.Value, 8)).OrderBy(g => g.Key).ToArray();
                if (groups.Length != segments.Length) continue;
                for (int i = 0; i < segments.Length; i++)
                {
                    int index = segmentRegions.IndexOf(segments[i]);
                    segmentRegions[index] = segments[i] with { EventIds = groups[i].Select(e => e.Id).ToArray() };
                }
            }
        }
        if (segmentRegions.Any(s => s.EventIds.Count == 0))
            limitations.Add("A seleção direta cobre ataques cuja pauta, voz e posição são verificáveis. Quiálteras, ornamentos e estruturas ambíguas continuam disponíveis pela lista de compassos do editor.");
        MidiDocument midi = MidiReader.Read(midiPath);
        var tempo = new PlaybackTempo(midi.Ppq, midi.TempoChanges);
        double endSeconds = tempo.At(midi.EndTick) * speed;
        var timeline = new List<ScorePlaybackOccurrence>();
        var measureTimeline = new List<ScorePlaybackOccurrence>();
        var measureEvents = rawMeasures.Events;
        var segmentById = segmentRegions.ToDictionary(s => s.Id);
        bool validTime = reliableMeasures && measureEvents.Length > 0 && endSeconds > 0 &&
            rawMeasures.Events.All(e => rawMeasures.Elements.Any(m => m.Id == e.Id)) &&
            rawSegments.Events.All(e => segmentById.ContainsKey(e.Id)) &&
            StrictTimes(measureEvents) && OrderedTimes(rawSegments.Events);
        if (validTime)
        {
            for (int i = 0; i < measureEvents.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var occurrence = measureEvents[i];
                double start = occurrence.Milliseconds / 1000 * speed;
                double end = i + 1 < measureEvents.Length ? measureEvents[i + 1].Milliseconds / 1000 * speed : endSeconds;
                if (end <= start || end > endSeconds + .01) { validTime = false; break; }
                int bar = occurrence.Id + 1;
                measureTimeline.Add(new(measureTimeline.Count, bar, start, end, null));
                var segments = rawSegments.Events.Where(e => e.Milliseconds / 1000 * speed >= start - .001 &&
                    e.Milliseconds / 1000 * speed < end - .001 && segmentById[e.Id].Measure == bar)
                    .GroupBy(e => e.Milliseconds).Select(g => g.First()).ToArray();
                double cursor = start;
                for (int j = 0; j < segments.Length; j++)
                {
                    double nextStart = Math.Max(start, segments[j].Milliseconds / 1000 * speed);
                    if (nextStart > cursor + .001) timeline.Add(new(timeline.Count, bar, cursor, nextStart, null));
                    double nextEnd = j + 1 < segments.Length ? Math.Min(end, segments[j + 1].Milliseconds / 1000 * speed) : end;
                    if (nextEnd > nextStart) timeline.Add(new(timeline.Count, bar, nextStart, nextEnd, segments[j].Id));
                    cursor = nextEnd;
                }
                if (cursor < end) timeline.Add(new(timeline.Count, bar, cursor, end, null));
            }
        }
        if (!validTime)
        { timeline.Clear(); measureTimeline.Clear(); limitations.Add("Os eventos de reprodução não correspondem ao MIDI exportado. É possível ouvir o áudio, mas o acompanhamento e o início por clique estão desativados."); }
        if (timeline.Any(t => t.SegmentId is null)) limitations.Add("Em trechos sem ataque localizável, o acompanhamento destaca o compasso inteiro.");
        if (rawMeasures.Events.GroupBy(e => e.Id).Any(g => g.Count() > 1))
            limitations.Add("Repetições seguem as ocorrências exportadas pelo MuseScore. Um clique durante a reprodução escolhe a ocorrência atual ou a próxima; fora da reprodução, a primeira.");
        if (NativeMusicEditor.HashFile(source, token) != sourceSha256) throw new IOException("A partitura mudou durante a leitura das posições.");
        return new(source, sourceSha256, speed, previewPages.ToArray(), measures, segmentRegions.ToArray(), timeline.ToArray(), limitations.Distinct().ToArray(), validTime)
            { MeasureTimeline = measureTimeline.ToArray() };

        ScorePageRegion Region(PositionElement e)
        {
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
                e.X < -width * .02 || e.Y < -height * .02 || e.X + e.Width > width * 1.05 || e.Y + e.Height > height * 1.05)
                throw new InvalidDataException("As coordenadas exportadas não correspondem ao tamanho nativo da página.");
            return new(e.Page + 1, e.X / width, e.Y / height, e.Width / width, e.Height / height);
        }
    }

    /// <summary>Runs the export job; a MuseScore crash (access violation) is retried twice in a fresh process.</summary>
    private async Task ExportAsync(string request, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { await ExportOnceAsync(request, token); return; }
            catch (MuseScoreCrashException) when (attempt < 3 && !token.IsCancellationRequested) { await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), token); }
        }
    }

    private async Task ExportOnceAsync(string request, CancellationToken token)
    {
        var start = new ProcessStartInfo(museScorePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(request)! };
        start.ArgumentList.Add("-r"); start.ArgumentList.Add(ExportDpi.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-j"); start.ArgumentList.Add(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar o mapa da partitura.");
        using var registration = linked.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            token.ThrowIfCancellationRequested(); throw new TimeoutException("O MuseScore excedeu o tempo de preparação do acompanhamento.");
        }
        token.ThrowIfCancellationRequested();
        string details = (await output + "\n" + await error).Trim();
        if (process.ExitCode is -1073741819 or -1073740791) throw new MuseScoreCrashException($"O MuseScore fechou com erro ({process.ExitCode}) ao preparar o acompanhamento.");
        if (process.ExitCode != 0) throw new IOException($"O MuseScore não exportou o acompanhamento (código {process.ExitCode}). " + details[..Math.Min(800, details.Length)]);
    }
    private static PositionFile ReadPositions(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("O mapa de posições é excessivamente grande.");
        var xml = NativeScoreArchive.ParseXml(File.ReadAllBytes(path));
        if (xml.Root?.Name != "score") throw new InvalidDataException("Mapa MuseScore sem estrutura reconhecida.");
        var elements = xml.Root.Element("elements")?.Elements("element").Select(e =>
            new PositionElement(Integer(e,"id"),Integer(e,"page"),RequiredNumber(e,"x"),RequiredNumber(e,"y"),RequiredNumber(e,"sx"),RequiredNumber(e,"sy"))).ToArray() ?? [];
        var events = xml.Root.Element("events")?.Elements("event").Select(e => new PositionEvent(Integer(e,"elid"),RequiredNumber(e,"position"))).ToArray() ?? [];
        if (elements.Any(e=>e.Id<0||e.Page<0||e.Width<0||e.Height<=0) || elements.Select(e=>e.Id).Distinct().Count()!=elements.Length ||
            events.Any(e=>e.Id<0||e.Milliseconds<0)) throw new InvalidDataException("O mapa contém posições ou referências inválidas.");
        return new(elements.OrderBy(e=>e.Id).ToArray(), events);
    }
    private static bool StrictTimes(PositionEvent[] events) => events.Zip(events.Skip(1)).All(p=>p.First.Milliseconds<p.Second.Milliseconds);
    private static bool OrderedTimes(PositionEvent[] events) => events.Zip(events.Skip(1)).All(p=>p.First.Milliseconds<=p.Second.Milliseconds);
    private static double Number(string? value,double fallback) => double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out double result)&&double.IsFinite(result)&&result>0?result:fallback;
    private static double RequiredNumber(XElement e,string name) => double.TryParse(e.Attribute(name)?.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out double result)&&double.IsFinite(result)
        ?result:throw new InvalidDataException("Uma coordenada do mapa está inválida.");
    private static int Integer(XElement e,string name) => int.TryParse(e.Attribute(name)?.Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out int result)
        ?result:throw new InvalidDataException("Uma referência do mapa está inválida.");
    private static (int Width,int Height) PngSize(string path)
    {
        using var stream=File.OpenRead(path); Span<byte> bytes=stackalloc byte[24];stream.ReadExactly(bytes);
        if(!bytes[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidDataException("A prévia não é PNG.");
        return (checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20])),checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24])));
    }
    private static int PageNumber(string path)
    {
        string name=Path.GetFileNameWithoutExtension(path);
        return name=="pagina"?0:name.StartsWith("pagina-",StringComparison.Ordinal)&&int.TryParse(name[7..],out int n)?n:-1;
    }
    private static void DeleteFile(string path) { try { File.Delete(path); } catch(IOException){} catch(UnauthorizedAccessException){} }
    private sealed record PositionElement(int Id,int Page,double X,double Y,double Width,double Height);
    private sealed record PositionEvent(int Id,double Milliseconds);
    private sealed record PositionFile(PositionElement[] Elements,PositionEvent[] Events);
    private sealed class PlaybackTempo
    {
        private readonly List<(long Tick,double Seconds,int Tempo)> segments=[(0,0,500000)];
        private readonly int ppq;
        internal PlaybackTempo(int ppq,IReadOnlyList<MidiTempoChange> changes)
        {
            this.ppq=ppq;
            foreach(var change in changes.OrderBy(t=>t.Tick).ThenBy(t=>t.Track).ThenBy(t=>t.Order))
            {
                var previous=segments[^1];
                double seconds=previous.Seconds+(change.Tick-previous.Tick)*previous.Tempo/(1000000d*ppq);
                segments.Add((change.Tick,seconds,change.MicrosecondsPerQuarter));
            }
        }
        internal double At(long tick)
        {
            var segment=segments.Last(s=>s.Tick<=tick);
            return segment.Seconds+(tick-segment.Tick)*segment.Tempo/(1000000d*ppq);
        }
    }
}
