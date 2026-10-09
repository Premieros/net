using System.IO.Pipes;
using System.Text.Json;

namespace RestaurantWiFiStorage;

/// <summary>
/// Privileged Windows administrator client. The Windows service owns the SQLite database;
/// this process only sends allowlisted commands over a local named pipe.
/// </summary>
public static class AdminPipeClient
{
    public const string PipeName = "RestaurantWiFiControlAdmin";
    const int MaxResponseBytes = 8 * 1024 * 1024;

    public static AdminResponse Send(AdminRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(3000);
        pipe.ReadTimeout = 5000;
        pipe.WriteTimeout = 5000;
        var data = JsonSerializer.SerializeToUtf8Bytes(request);
        if (data.Length > 256 * 1024) throw new InvalidOperationException("Administrative request too large.");
        using var writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
        using var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();
        var length = reader.ReadInt32();
        if (length < 1 || length > MaxResponseBytes)
            throw new IOException("Unexpected administrative response size.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Incomplete administrative response.");
        var response = JsonSerializer.Deserialize<AdminResponse>(bytes) ??
            throw new IOException("Empty administrative response.");
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "Administrative operation failed.");
        return response;
    }
}
