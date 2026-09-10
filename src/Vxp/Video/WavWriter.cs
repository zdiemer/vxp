using System.Buffers.Binary;

namespace Vxp.Video;

/// <summary>Writes mono 16-bit PCM WAV files.</summary>
public sealed class WavWriter : IDisposable
{
    private readonly FileStream _stream;
    private int _dataBytes;

    public WavWriter(string path, int sampleRate)
    {
        _stream = File.Create(path);
        SampleRate = sampleRate;
        _stream.Write(new byte[44]); // placeholder, rewritten on close
    }

    public int SampleRate { get; }

    public void Write(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        _stream.Write(bytes);
        _dataBytes += bytes.Length;
    }

    public void Dispose()
    {
        _stream.Seek(0, SeekOrigin.Begin);
        var header = new byte[44];
        var span = header.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + _dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);  // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);  // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);  // block align
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16); // bits
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], _dataBytes);

        _stream.Write(header);
        _stream.Dispose();
    }
}
