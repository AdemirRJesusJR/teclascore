using System.IO;
using System.Runtime.InteropServices;

namespace Teclas.App;

/// <summary>
/// Decodes any audio file Windows can play (MP3, M4A/AAC, WMA, WAV, FLAC) to 16-bit PCM WAV with Media Foundation, the
/// system's own decoder: nothing is installed or downloaded.
/// </summary>
internal static class AudioDecoder
{
    public static readonly string[] Extensions = [".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac"];
    private const uint MfVersion = 0x00020070, FirstAudioStream = 0xFFFFFFFD, EndOfStream = 0x2;
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71"), Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"),
        SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");

    /// <summary>Writes the decoded audio as WAV; returns its length in seconds.</summary>
    public static double ToWav(string input, string output, CancellationToken token)
    {
        Check(MFStartup(MfVersion, 0), "iniciar o Media Foundation");
        IMFSourceReader? reader = null;
        try
        {
            Check(MFCreateSourceReaderFromURL(Path.GetFullPath(input), IntPtr.Zero, out reader), "abrir o áudio");
            Check(MFCreateMediaType(out var wanted), "preparar o formato");
            Check(wanted.SetGUID(MajorType, Audio), "formato"); Check(wanted.SetGUID(Subtype, Pcm), "formato"); Check(wanted.SetUINT32(BitsPerSample, 16), "formato");
            Check(reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, wanted), "converter para PCM");
            Marshal.ReleaseComObject(wanted);
            Check(reader.GetCurrentMediaType(FirstAudioStream, out var actual), "ler o formato");
            Check(actual.GetUINT32(Channels, out uint channels), "canais"); Check(actual.GetUINT32(SamplesPerSecond, out uint rate), "taxa");
            Marshal.ReleaseComObject(actual);
            using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            file.Write(new byte[44]);
            long bytes = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Check(reader.ReadSample(FirstAudioStream, 0, out _, out uint flags, out _, out var sample), "decodificar");
                if (sample is not null)
                {
                    Check(sample.ConvertToContiguousBuffer(out var buffer), "ler o bloco");
                    Check(buffer.Lock(out IntPtr data, out _, out uint length), "ler o bloco");
                    var chunk = new byte[length]; Marshal.Copy(data, chunk, 0, (int)length); buffer.Unlock();
                    file.Write(chunk); bytes += length;
                    Marshal.ReleaseComObject(buffer); Marshal.ReleaseComObject(sample);
                    if (bytes > 2L * 1024 * 1024 * 1024 - 64) throw new InvalidDataException("Áudio longo demais.");
                }
                if ((flags & EndOfStream) != 0) break;
            }
            file.Position = 0;
            using var header = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true);
            header.Write("RIFF"u8); header.Write((uint)(36 + bytes)); header.Write("WAVEfmt "u8); header.Write(16u); header.Write((ushort)1); header.Write((ushort)channels);
            header.Write(rate); header.Write(rate * channels * 2); header.Write((ushort)(channels * 2)); header.Write((ushort)16); header.Write("data"u8); header.Write((uint)bytes);
            return channels == 0 || rate == 0 ? 0 : bytes / 2.0 / channels / rate;
        }
        finally
        {
            if (reader is not null) Marshal.ReleaseComObject(reader);
            MFShutdown();
        }
    }

    private static void Check(int result, string step)
    {
        if (result < 0) throw new InvalidDataException($"O Windows não conseguiu {step} (código 0x{result:X8}). Confira se o arquivo é um áudio válido.");
    }

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IMFSourceReader reader);

    // Vtable order matters; methods never called are kept as placeholders.
    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        void GetStreamSelection(); void SetStreamSelection(); void GetNativeMediaType();
        [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
        void SetCurrentPosition();
        [PreserveSig] int ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample? sample);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        [PreserveSig] int GetUINT32([In] in Guid key, out uint value);
        void GetUINT64(); void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        [PreserveSig] int SetUINT32([In] in Guid key, uint value);
        void SetUINT64(); void SetDouble();
        [PreserveSig] int SetGUID([In] in Guid key, [In] in Guid value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble(); void GetGUID();
        void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
        void SetItem(); void DeleteItem(); void DeleteAllItems(); void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString();
        void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime(); void SetSampleTime(); void GetSampleDuration(); void SetSampleDuration();
        void GetBufferCount(); void GetBufferByIndex();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
    }
}
