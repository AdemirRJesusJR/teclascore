using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Teclas.Core;

public sealed record ScoreAudio(string Path, TimeSpan Duration);
public sealed record WaveInfo(ushort Format, ushort Channels, uint SampleRate, ushort BitsPerSample, long DataOffset, uint DataLength)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(DataLength / ((double)SampleRate * Channels * (BitsPerSample / 8)));
}

/// <summary>Exports the saved score through the installed MuseScore, without opening its editor.</summary>
public sealed class ScoreExporter(string museScorePath, string cacheDirectory)
{
    private readonly LocalPerformanceRecorder? recorder;
    private readonly TimeSpan processTimeout = TimeSpan.FromMinutes(5);
    public ScoreExporter(string museScorePath, string cacheDirectory, LocalPerformanceRecorder recorder) : this(museScorePath, cacheDirectory)
    { this.recorder = recorder; }
    public ScoreExporter(string museScorePath, string cacheDirectory, LocalPerformanceRecorder recorder, TimeSpan processTimeout)
        : this(museScorePath, cacheDirectory, recorder)
    {
        if (processTimeout <= TimeSpan.Zero || processTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        this.processTimeout = processTimeout;
    }
    /// <summary>Writes a new native MuseScore file. The caller publishes it after musical validation.</summary>
    public async Task ExportNativeAsync(string input, string destination, CancellationToken token)
    {
        ValidateScore(input);
        ValidateNewDestination(input, destination, ".mscz");
        await ExportAsync(input, destination, token, PerformanceOperation.ScoreExportNative);
        using var archive = ZipFile.OpenRead(destination);
        var score = archive.Entries.FirstOrDefault(entry => entry.FullName.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("O MuseScore não produziu uma partitura MSCZ válida.");
        using var stream = score.Open();
        var document = XDocument.Load(stream);
        if (document.Root?.Name.LocalName != "museScore") throw new InvalidDataException("A partitura exportada não está no formato MuseScore.");
    }

    /// <summary>Writes MuseScore's playback MIDI (repeats unrolled, actual time signatures) to a new file.</summary>
    public async Task ExportMidiAsync(string input, string destination, CancellationToken token)
    {
        ValidateScore(input);
        ValidateNewDestination(input, destination, ".mid");
        // MuseScore 4 occasionally crashes (access violation) on this headless export; a fresh process usually succeeds.
        for (int attempt = 1; ; attempt++)
        {
            try { await ExportAsync(input, destination, token, PerformanceOperation.ScoreExportMidi); return; }
            catch (InvalidOperationException) when (attempt < 3 && !token.IsCancellationRequested)
            {
                TryDelete(destination);
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), token);
            }
        }
    }

    /// <summary>
    /// Saves a copy as MusicXML (.musicxml) or as one PNG per page (.png, 200 dpi). Rendering happens in the cache;
    /// existing PNG files are never replaced (repeated names receive a number). Returns the saved files.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExportCopyAsync(string scorePath, string destination, IProgress<string>? progress, CancellationToken token)
    {
        ValidateScore(scorePath);
        string extension = Path.GetExtension(destination).ToLowerInvariant();
        if (!Path.IsPathFullyQualified(destination) || extension is not (".musicxml" or ".png"))
            throw new InvalidOperationException("Escolha um arquivo MusicXML ou PNG com caminho completo.");
        string job = Path.Combine(cacheDirectory, "copia-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            string rendered = Path.Combine(job, "partitura" + extension);
            progress?.Report(extension == ".png" ? "Desenhando cada página em PNG…" : "Exportando a partitura em MusicXML…");
            await ExportAsync(scorePath, rendered, token, PerformanceOperation.ScoreExportCopy, allowPagedPng: extension == ".png", resolution: 200);
            token.ThrowIfCancellationRequested();
            string folder = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(folder);
            if (extension == ".musicxml")
            {
                if (!File.Exists(rendered)) throw new InvalidDataException("O MuseScore não produziu o MusicXML.");
                File.Copy(rendered, destination, overwrite: true);
                return [destination];
            }
            var pages = FindPreviewPages(rendered);
            if (pages.Length == 0) throw new InvalidDataException("O MuseScore não gerou as páginas em PNG.");
            var taken = new HashSet<string>(Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
            string stem = Path.GetFileNameWithoutExtension(destination);
            var saved = new List<string>();
            for (int index = 0; index < pages.Length; index++)
            {
                string name = ScoreFileName.Unique(pages.Length == 1 ? stem : $"{stem} - página {index + 1}", ".png", taken);
                string target = Path.Combine(folder, name);
                File.Copy(pages[index], target, overwrite: false); saved.Add(target);
            }
            return saved;
        }
        finally { try { Directory.Delete(job, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>Renders every page as PNG at <paramref name="resolution"/> dpi, optionally with a page style (MuseScore -S) applied to the export only.</summary>
    public async Task<IReadOnlyList<string>> ExportPagesAsync(string scorePath, string destinationPng, int resolution, string? stylePath, CancellationToken token)
    {
        ValidateScore(scorePath);
        if (stylePath is not null && !File.Exists(stylePath)) throw new FileNotFoundException("Estilo de página não encontrado.", stylePath);
        await ExportAsync(scorePath, destinationPng, token, PerformanceOperation.ScoreExportCopy, allowPagedPng: true, resolution: resolution, stylePath: stylePath);
        var pages = FindPreviewPages(destinationPng);
        if (pages.Length == 0) throw new InvalidDataException("O MuseScore não gerou as páginas em PNG.");
        return pages;
    }

    /// <summary>Renders all pages, returning numbered PNG files in page order.</summary>
    public async Task<IReadOnlyList<string>> ExportPreviewAsync(string input, string destinationPng, CancellationToken token)
    {
        ValidateScore(input);
        ValidateNewDestination(input, destinationPng, ".png");
        if (FindPreviewPages(destinationPng).Length != 0)
            throw new IOException("A pasta já contém páginas com esse nome. Escolha um novo destino para a prévia.");
        await ExportAsync(input, destinationPng, token, PerformanceOperation.ScoreExportPreview, allowPagedPng: true);
        var pages = FindPreviewPages(destinationPng);
        if (pages.Length == 0) throw new InvalidDataException("O MuseScore não gerou as páginas da prévia.");
        foreach (string page in pages)
        {
            token.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(page);
            byte[] header = new byte[24]; stream.ReadExactly(header);
            if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4)) == 0 || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4)) == 0)
                throw new InvalidDataException("O MuseScore gerou uma página de prévia inválida.");
        }
        return pages;
    }

    private static void ValidateNewDestination(string input, string destination, string extension)
    {
        if (!Path.IsPathFullyQualified(destination) || !string.Equals(Path.GetExtension(destination), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Escolha um destino {extension} com caminho completo.");
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) || File.Exists(destination))
            throw new IOException("A exportação precisa de um novo arquivo de destino para preservar a partitura existente.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    }

    private static string[] FindPreviewPages(string destination)
    {
        string folder = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(folder)) return [];
        string name = Path.GetFileNameWithoutExtension(destination);
        return Directory.EnumerateFiles(folder, "*.png").Select(path =>
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            int number = string.Equals(stem, name, StringComparison.OrdinalIgnoreCase) ? 0 :
                stem.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase) && int.TryParse(stem[(name.Length + 1)..], out int parsed) ? parsed : -1;
            return (Path: path, Number: number);
        }).Where(page => page.Number >= 0).OrderBy(page => page.Number).Select(page => page.Path).ToArray();
    }

    public async Task<ScoreAudio> PrepareAudioAsync(string scorePath, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        progress?.Report("Conferindo a partitura e procurando um áudio já preparado…");
        ValidateScore(scorePath);
        Directory.CreateDirectory(cacheDirectory);
        string hash;
        await using (var source = File.OpenRead(scorePath))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
        var executable = new FileInfo(museScorePath);
        string renderer = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.FullName + executable.Length + executable.LastWriteTimeUtc.Ticks)))[..16];
        string audioPath = Path.Combine(cacheDirectory, $"{hash}-{renderer}-pcm16.wav");
        if (File.Exists(audioPath))
        {
            try
            {
                var info = ReadWaveInfo(audioPath);
                token.ThrowIfCancellationRequested();
                progress?.Report("Áudio já preparado encontrado. Conferindo a reprodução…");
                return new(audioPath, info.Duration);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { TryDelete(audioPath); }
        }
        string raw = Path.Combine(cacheDirectory, ".render-" + Guid.NewGuid().ToString("N") + ".wav");
        string prepared = Path.Combine(cacheDirectory, ".audio-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            progress?.Report("Gerando o áudio da partitura no MuseScore…");
            await ExportAsync(scorePath, raw, token, PerformanceOperation.ScoreExportAudio);
            progress?.Report("Preparando o áudio para o reprodutor do Windows…");
            var info = await Task.Run(() => ConvertToPcm16(raw, prepared, token), token);
            progress?.Report("Conferindo se o áudio corresponde à partitura atual…");
            await using (var source = File.OpenRead(scorePath))
            {
                string currentHash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
                if (currentHash != hash) throw new IOException("A partitura foi alterada durante a preparação do áudio. Tente reproduzir novamente.");
            }
            token.ThrowIfCancellationRequested();
            progress?.Report("Salvando o áudio preparado para as próximas reproduções…");
            File.Move(prepared, audioPath, true);
            return new(audioPath, info.Duration);
        }
        finally { TryDelete(raw); TryDelete(prepared); }
    }

    public async Task ExportPdfAsync(string scorePath, string destination, IProgress<string>? progress, CancellationToken token, string? stylePath = null)
    {
        token.ThrowIfCancellationRequested();
        progress?.Report("Conferindo a partitura e o destino do PDF…");
        ValidateScore(scorePath);
        if (!Path.IsPathFullyQualified(destination) || !string.Equals(Path.GetExtension(destination), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Escolha um arquivo PDF com caminho completo.");
        if (string.Equals(Path.GetFullPath(scorePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O PDF deve ser salvo em outro arquivo, preservando a partitura.");
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".teclas-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            progress?.Report("Exportando todas as páginas para PDF…");
            await ExportAsync(scorePath, temporary, token, PerformanceOperation.ScoreExportPdf, stylePath: stylePath);
            progress?.Report("Conferindo o PDF gerado…");
            await using (var stream = File.OpenRead(temporary))
            {
                byte[] header = new byte[5];
                await stream.ReadExactlyAsync(header, token);
                if (stream.Length < 100 || !header.AsSpan().SequenceEqual("%PDF-"u8))
                    throw new InvalidDataException("O MuseScore não produziu um PDF válido. O arquivo anterior foi preservado.");
            }
            token.ThrowIfCancellationRequested();
            progress?.Report("Salvando o PDF no destino escolhido…");
            File.Move(temporary, destination, true);
        }
        finally { TryDelete(temporary); }
    }

    private void ValidateScore(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Gere ou importe uma partitura antes de reproduzir ou exportar.", path);
        if (!File.Exists(museScorePath)) throw new FileNotFoundException("Localize o MuseScore Studio 4 em Configurações para preparar o áudio e exportar PDF.", museScorePath);
    }

    /// <summary>
    /// MuseScore 4 sometimes crashes (access violation) on a headless export that succeeds in a fresh process; such crashes
    /// are retried twice, removing any partial output first. Ordinary export errors are reported at once.
    /// </summary>
    private async Task ExportAsync(string scorePath, string outputPath, CancellationToken token, PerformanceOperation operation, bool allowPagedPng = false, int resolution = 120, string? stylePath = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { await ExportOnceAsync(scorePath, outputPath, token, operation, allowPagedPng, resolution, stylePath); return; }
            catch (MuseScoreCrashException) when (attempt < 3 && !token.IsCancellationRequested)
            {
                TryDelete(outputPath);
                if (allowPagedPng) foreach (var page in FindPreviewPages(outputPath)) TryDelete(page);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), token);
            }
        }
    }

    private async Task ExportOnceAsync(string scorePath, string outputPath, CancellationToken token, PerformanceOperation operation, bool allowPagedPng, int resolution, string? stylePath)
    {
        using var measurement = (recorder ?? PerformanceDiagnostics.Recorder)?.Begin(operation, PerformanceProcessKind.MuseScore, PerformancePhase.Process)
            ?? PerformanceMeasurement.Disabled;
        Process? process = null;
        try
        {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(museScorePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(outputPath)!
        };
        if (allowPagedPng) { start.ArgumentList.Add("-r"); start.ArgumentList.Add(resolution.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (stylePath is not null) { start.ArgumentList.Add("-S"); start.ArgumentList.Add(stylePath); }
        start.ArgumentList.Add("-o"); start.ArgumentList.Add(outputPath); start.ArgumentList.Add(scorePath);
        // On Windows the normal -o conversion is headless. The Qt offscreen platform can hang this build.
        using var timeout = new CancellationTokenSource(processTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar a exportação no MuseScore.");
        measurement.TrackProcess(process);
        using var registration = linked.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            // The cancellation callback kills the owned process tree. Wait for file handles to close before cleanup.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("A exportação no MuseScore excedeu o limite de tempo. Tente novamente ou confira a partitura.");
        }
        token.ThrowIfCancellationRequested();
        string details = (await stdout + "\n" + await stderr).Trim();
        if (process.ExitCode != 0 || !(File.Exists(outputPath) || allowPagedPng && FindPreviewPages(outputPath).Length > 0))
        {
            if (details.Length > 1200) details = details[^1200..];
            string message = $"Não foi possível exportar a partitura (MuseScore {process.ExitCode}). {details}".Trim();
            // 0xC0000005 (access violation) and 0xC0000409 (stack buffer overrun): the process crashed.
            if (process.ExitCode is -1073741819 or -1073740791) throw new MuseScoreCrashException(message);
            throw new InvalidOperationException(message);
        }
        measurement.Success();
        }
        catch (OperationCanceledException) { measurement.Cancelled(); throw; }
        catch (TimeoutException) { measurement.TimedOut(); throw; }
        finally { measurement.Dispose(); process?.Dispose(); }
    }

    public static WaveInfo ReadWaveInfo(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.BaseStream.Length < 44 || reader.ReadUInt32() != 0x46464952)
            throw new InvalidDataException("Áudio WAV inválido.");
        uint riffSize = reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x45564157 || (long)riffSize + 8 > reader.BaseStream.Length)
            throw new InvalidDataException("Áudio WAV incompleto.");
        ushort format = 0, channels = 0, bits = 0; uint rate = 0, dataLength = 0; long dataOffset = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            uint kind = reader.ReadUInt32(), length = reader.ReadUInt32();
            long next = reader.BaseStream.Position + length + (length & 1);
            if (next > reader.BaseStream.Length) throw new InvalidDataException("Áudio WAV truncado.");
            if (kind == 0x20746d66)
            {
                if (length < 16) throw new InvalidDataException("Formato de áudio WAV inválido.");
                format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadUInt32();
                reader.ReadUInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16();
                if (format == 0xfffe && length >= 40)
                {
                    reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt32(); format = reader.ReadUInt16();
                }
            }
            else if (kind == 0x61746164) { dataLength = length; dataOffset = reader.BaseStream.Position; }
            reader.BaseStream.Position = next;
        }
        if (channels is 0 or > 8 || rate == 0 || dataLength == 0 || dataOffset == 0 ||
            !(format == 1 && bits is 8 or 16 or 24 or 32 || format == 3 && bits is 32 or 64) ||
            dataLength % (channels * (bits / 8)) != 0)
            throw new InvalidDataException("O áudio da partitura está vazio ou usa um formato não suportado.");
        return new(format, channels, rate, bits, dataOffset, dataLength);
    }

    /// <summary>MuseScore emits IEEE float WAV; PCM16 also plays on Windows installations without a float decoder.</summary>
    public static WaveInfo ConvertToPcm16(string source, string destination, CancellationToken token)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A conversão de áudio precisa de um arquivo de destino separado.");
        WaveInfo info = ReadWaveInfo(source);
        long samples = info.DataLength / (info.BitsPerSample / 8);
        uint bytes = checked((uint)(samples * 2));
        using var input = new BinaryReader(File.OpenRead(source));
        using var output = new BinaryWriter(File.Create(destination));
        output.Write("RIFF"u8); output.Write(checked(bytes + 36)); output.Write("WAVEfmt "u8); output.Write(16u);
        output.Write((ushort)1); output.Write(info.Channels); output.Write(info.SampleRate);
        output.Write(checked(info.SampleRate * info.Channels * 2)); output.Write((ushort)(info.Channels * 2)); output.Write((ushort)16);
        output.Write("data"u8); output.Write(bytes);
        input.BaseStream.Position = info.DataOffset;
        for (long i = 0; i < samples; i++)
        {
            if (i % 8192 == 0) token.ThrowIfCancellationRequested();
            short sample;
            if (info.Format == 3)
            {
                double value = info.BitsPerSample == 32 ? input.ReadSingle() : input.ReadDouble();
                sample = double.IsFinite(value) ? (short)Math.Clamp(Math.Round(value * 32768), short.MinValue, short.MaxValue) : (short)0;
            }
            else sample = info.BitsPerSample switch
            {
                8 => (short)((input.ReadByte() - 128) << 8), 16 => input.ReadInt16(),
                24 => Read24(input), 32 => (short)(input.ReadInt32() >> 16),
                _ => throw new InvalidDataException("Resolução WAV não suportada.")
            };
            output.Write(sample);
        }
        output.Flush();
        return new(1, info.Channels, info.SampleRate, 16, 44, bytes);
    }

    private static short Read24(BinaryReader reader)
    { reader.ReadByte(); return reader.ReadInt16(); }
    private static void TryDelete(string path)
    { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

/// <summary>MuseScore ended with a crash code; a new attempt in a fresh process usually works.</summary>
public sealed class MuseScoreCrashException(string message) : InvalidOperationException(message);
