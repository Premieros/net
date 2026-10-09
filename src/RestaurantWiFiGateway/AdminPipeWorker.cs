using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RestaurantWiFiStorage;

namespace RestaurantWiFiGateway;

/// <summary>
/// Local administrative IPC endpoint. Only SYSTEM and elevated local Administrators
/// can connect. No administrative operation is exposed on the HTTP portal.
/// </summary>
internal sealed class AdminPipeWorker : BackgroundService
{
    readonly StateStore store = new();
    readonly SemaphoreSlim clients = new(8, 8);

    static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await clients.WaitAsync(stoppingToken);
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(AdminPipeClient.PipeName,
                    PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    0, 0, CreateSecurity());
                await pipe.WaitForConnectionAsync(stoppingToken);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() =>
                {
                    try { Serve(connected); }
                    finally { connected.Dispose(); clients.Release(); }
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                clients.Release();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                clients.Release();
                Console.Error.WriteLine("Local administration pipe error: " + ex.Message);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    void Serve(NamedPipeServerStream pipe)
    {
        try
        {
            // PipeStream does not support ReadTimeout/WriteTimeout. Every read/write
            // is bounded via a cancellation token instead.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var header = new byte[4];
            pipe.ReadExactlyAsync(header.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
            var size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size < 1 || size > 256 * 1024) return;
            var input = new byte[size];
            pipe.ReadExactlyAsync(input.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();

            AdminResponse result;
            try
            {
                var request = JsonSerializer.Deserialize<AdminRequest>(input) ??
                    throw new ArgumentException("Invalid administrative request.");
                result = AdminStateCommands.Execute(store, request);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
            {
                result = new AdminResponse(false, Error: ex.Message);
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
            if (bytes.Length > 8 * 1024 * 1024) return;
            var frame = new byte[bytes.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), bytes.Length);
            bytes.CopyTo(frame.AsSpan(4));
            pipe.WriteAsync(frame.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
            pipe.FlushAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or
            UnauthorizedAccessException or JsonException or InvalidOperationException or
            OperationCanceledException)
        {
            Console.Error.WriteLine("Rejected local admin request: " + ex.GetType().Name);
        }
    }

    public override void Dispose()
    {
        clients.Dispose();
        base.Dispose();
    }
}
