using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// A tiny local-only "what's happening right now" channel for a live consumer (OmniBIM's
/// dashboard) that does not want to wait for a heartbeat to land in the CSV.
///
/// THE CSV STAYS THE PERMANENT RECORD. This is a read-only snapshot of in-memory state -
/// nothing here is ever written to disk, and if this server never starts or a client never
/// connects, time tracking behaves exactly as it always has. It answers a different question
/// than the ledger does: not "what happened", but "what is happening right now".
///
/// ONE CONNECTION, ONE REPLY, CLOSED. A client opens the pipe, the server writes one JSON
/// line describing the currently open segment (or none), and the connection ends - no
/// persistent session to keep alive or reconnect, which is what makes "the client stopped
/// polling" or "Revit closed mid-poll" a non-event instead of a state machine to get right.
///
/// NAMED PIPE, NOT A SOCKET. Local-machine-only by construction - NamedPipeServerStream never
/// listens on the network, so this cannot be reached from another machine and needs no
/// firewall rule. The pipe name is scoped to the current Windows user, and the ACL restricts
/// it to that same user, so another account on a shared or terminal-server machine cannot
/// even attempt to connect.
/// </summary>
internal static class LiveStatusServer
{
    private static CancellationTokenSource? _cts;
    private static Task? _loop;

    public static string PipeName => $"CdaRevitLiveStatus_{Environment.UserName}";

    /// <summary>Idempotent - a second Start() while already running is a no-op.</summary>
    public static void Start()
    {
        if (_loop is not null) return;

        try
        {
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => AcceptLoop(_cts.Token));
            Log.Info($"Live status server started on pipe '{PipeName}'.");
        }
        catch (Exception ex)
        {
            // A live-status server that fails to start must not cost the user their tracking -
            // the CSV path already works without it.
            Log.Warn($"Live status server could not be started: {ex.Message}");
        }
    }

    public static void Stop()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // Best effort - Revit is shutting down either way.
        }
        finally
        {
            _cts = null;
            _loop = null;
        }
    }

    private static async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));

                using var server = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.Out, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 0, outBufferSize: 4096, pipeSecurity: security);

                await server.WaitForConnectionAsync(ct);

                var bytes = Encoding.UTF8.GetBytes(BuildPayload());
                await server.WriteAsync(bytes, ct);
                server.WaitForPipeDrain();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single bad connection must not take the whole channel down - log it, wait
                // a beat so a persistent failure does not spin the loop, and accept the next
                // client.
                Log.Warn($"Live status server: a connection failed: {ex.Message}");
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// One snapshot of TimeTracker's in-memory state, same accessors the CSV writer itself
    /// uses - this can never disagree with what the ledger will eventually say, because it
    /// reads the same source of truth.
    /// </summary>
    private static string BuildPayload()
    {
        var open = TimeTracker.Open;

        var status = new
        {
            hasOpenSegment = open is not null,
            projectName = open?.ProjectName ?? string.Empty,
            projectNumber = open?.ProjectNumber ?? string.Empty,
            fileName = open?.FileName ?? string.Empty,
            viewName = open?.ViewName ?? string.Empty,
            taskPhase = TimeTracker.TaskPhase,
            username = TimeTracker.Username,
            elapsedSeconds = open is null ? 0 : TimeTracker.Elapsed.TotalSeconds,
            isIdle = TimeTrackingService.IsIdle,
            serverUtc = DateTime.UtcNow.ToString("o"),
        };

        return JsonSerializer.Serialize(status);
    }
}
