using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace RestaurantWiFiStorage;

/// <summary>
/// Privileged local administrator client. Use asynchronous I/O with an explicit timeout:
/// PipeStream.ReadTimeout/WriteTimeout are unsupported for named-pipe streams on .NET.
/// </summary>
public static class AdminPipeClient
{
    public const string PipeName = "RestaurantWiFiControlAdmin";
    const int MaxResponseBytes = 8 * 1024 * 1024;

    public static AdminResponse Send(AdminRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        pipe.Connect(3000);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var data = JsonSerializer.SerializeToUtf8Bytes(request);
        if (data.Length > 256 * 1024) throw new InvalidOperationException("Administrative request too large.");

        var frame = new byte[data.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), data.Length);
        data.CopyTo(frame.AsSpan(4));
        pipe.WriteAsync(frame.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
        pipe.FlushAsync(deadline.Token).GetAwaiter().GetResult();

        var header = new byte[4];
        pipe.ReadExactlyAsync(header.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxResponseBytes)
            throw new IOException("Unexpected administrative response size.");

        var bytes = new byte[length];
        pipe.ReadExactlyAsync(bytes.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
        var response = JsonSerializer.Deserialize<AdminResponse>(bytes) ??
            throw new IOException("Empty administrative response.");
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Administrative operation failed.");
        return response;
    }
}
