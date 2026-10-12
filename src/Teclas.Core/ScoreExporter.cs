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
    /// <summary>Writes a new native MuseScore file. The caller publishes it after musical validation.</summary>
    public async Task ExportNativeAsync(string input, string destination, CancellationToken token)
    {
        ValidateScore(input);
        ValidateNewDestination(input, destination, ".mscz");
        await ExportAsync(input, destination, token);
        using var archive = ZipFile.OpenRead(destination);
        var score = archive.Entries.FirstOrDefault(entry => entry.FullName.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("O MuseScore não produziu uma partitura MSCZ válida.");
        using var stream = score.Open();
        var document = XDocument.Load(stream);
        if (document.Root?.Name.LocalName != "museScore") throw new InvalidDataException("A partitura exportada não está no formato MuseScore.");
    }

    /// <summary>Renders all pages, returning numbered PNG files in page order.</summary>
    public async Task<IReadOnlyList<string>> ExportPreviewAsync(string input, string destinationPng, CancellationToken token)
    {
        ValidateScore(input);
        ValidateNewDestination(input, destinationPng, ".png");
        if (FindPreviewPages(destinationPng).Length != 0)
            throw new IOException("A pasta já contém páginas com esse nome. Escolha um novo destino para a prévia.");
        await ExportAsync(input, destinationPng, token, allowPagedPng: true);
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
            try { return new(audioPath, ReadWaveInfo(audioPath).Duration); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { TryDelete(audioPath); }
        }
        string raw = Path.Combine(cacheDirectory, ".render-" + Guid.NewGuid().ToString("N") + ".wav");
        string prepared = Path.Combine(cacheDirectory, ".audio-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            progress?.Report("Preparando o áudio da partitura em segundo plano…");
            await ExportAsync(scorePath, raw, token);
            var info = await Task.Run(() => ConvertToPcm16(raw, prepared, token), token);
            await using (var source = File.OpenRead(scorePath))
            {
                string currentHash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
                if (currentHash != hash) throw new IOException("A partitura foi alterada durante a preparação do áudio. Tente reproduzir novamente.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(prepared, audioPath, true);
            return new(audioPath, info.Duration);
        }
        finally { TryDelete(raw); TryDelete(prepared); }
    }

    public async Task ExportPdfAsync(string scorePath, string destination, IProgress<string>? progress, CancellationToken token)
    {
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
            await ExportAsync(scorePath, temporary, token);
            await using (var stream = File.OpenRead(temporary))
            {
                byte[] header = new byte[5];
                await stream.ReadExactlyAsync(header, token);
                if (stream.Length < 100 || !header.AsSpan().SequenceEqual("%PDF-"u8))
                    throw new InvalidDataException("O MuseScore não produziu um PDF válido. O arquivo anterior foi preservado.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { TryDelete(temporary); }
    }

    private void ValidateScore(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Gere ou importe uma partitura antes de reproduzir ou exportar.", path);
        if (!File.Exists(museScorePath)) throw new FileNotFoundException("Localize o MuseScore Studio 4 em Configurações para preparar o áudio e exportar PDF.", museScorePath);
    }

    private async Task ExportAsync(string scorePath, string outputPath, CancellationToken token, bool allowPagedPng = false)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(museScorePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(outputPath)!
        };
        if (allowPagedPng) { start.ArgumentList.Add("-r"); start.ArgumentList.Add("120"); }
        start.ArgumentList.Add("-o"); start.ArgumentList.Add(outputPath); start.ArgumentList.Add(scorePath);
        // On Windows the normal -o conversion is headless. The Qt offscreen platform can hang this build.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar a exportação no MuseScore.");
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
            throw new TimeoutException("A exportação no MuseScore excedeu cinco minutos. Tente novamente ou confira a partitura.");
        }
        token.ThrowIfCancellationRequested();
        string details = (await stdout + "\n" + await stderr).Trim();
        if (process.ExitCode != 0 || !(File.Exists(outputPath) || allowPagedPng && FindPreviewPages(outputPath).Length > 0))
        {
            if (details.Length > 1200) details = details[^1200..];
            throw new InvalidOperationException($"Não foi possível exportar a partitura (MuseScore {process.ExitCode}). {details}".Trim());
        }
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
