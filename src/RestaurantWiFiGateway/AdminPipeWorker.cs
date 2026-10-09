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
            pipe.ReadTimeout = 5000;
            pipe.WriteTimeout = 5000;
            using var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            using var writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            var size = reader.ReadInt32();
            if (size < 1 || size > 256 * 1024) return;
            var input = reader.ReadBytes(size);
            if (input.Length != size) return;
            AdminResponse result;
            try
            {
                var request = JsonSerializer.Deserialize<AdminRequest>(input) ??
                    throw new ArgumentException("Invalid administrative request.");
                result = AdminStateCommands.Execute(store, request);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                result = new AdminResponse(false, Error: ex.Message);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
            if (bytes.Length > 8 * 1024 * 1024) return;
            writer.Write(bytes.Length);
            writer.Write(bytes);
            writer.Flush();
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or
            UnauthorizedAccessException or JsonException or InvalidOperationException)
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
