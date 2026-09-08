using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OmniBIM.Desktop.Models;

namespace OmniBIM.Desktop.Services;

/// <summary>
/// Connects to the Revit add-in's LiveStatusServer (a named pipe, this machine and this
/// Windows user only) for instant "what's happening right now" - the CSV ledger remains the
/// source for everything historical (weekly hours, task/project breakdowns); this is only for
/// the live indicator.
///
/// ONE CONNECTION PER POLL, deliberately mirroring the server's own one-reply-then-close
/// design - no persistent session to manage, no reconnect logic. Every failure mode (Revit
/// not running, no add-in loaded, tracking disabled, a stale pipe, a slow connect) surfaces
/// as null rather than an exception the caller has to know about - "no live status" is not
/// this app's problem to solve, it is a completely ordinary state: Revit is simply not open.
/// </summary>
public static class LiveStatusClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string PipeName => $"CdaRevitLiveStatus_{Environment.UserName}";

    public static async Task<LiveStatus?> TryGetStatusAsync(TimeSpan? timeout = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.In, PipeOptions.Asynchronous);

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMilliseconds(400));
            await client.ConnectAsync(cts.Token);

            using var reader = new StreamReader(client, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(cts.Token);

            if (string.IsNullOrWhiteSpace(json)) return null;

            var payload = JsonSerializer.Deserialize<Payload>(json, JsonOptions);
            if (payload is null) return null;

            return new LiveStatus
            {
                HasOpenSegment = payload.HasOpenSegment,
                ProjectName = payload.ProjectName ?? string.Empty,
                ProjectNumber = payload.ProjectNumber ?? string.Empty,
                FileName = payload.FileName ?? string.Empty,
                ViewName = payload.ViewName ?? string.Empty,
                TaskPhase = payload.TaskPhase ?? string.Empty,
                Username = payload.Username ?? string.Empty,
                ElapsedSeconds = payload.ElapsedSeconds,
                IsIdle = payload.IsIdle,
            };
        }
        catch
        {
            // Revit not running, add-in not loaded, tracking disabled, a stale/unresponsive
            // pipe, a timed-out connect - all ordinary "nothing live to show" states.
            return null;
        }
    }

    private sealed class Payload
    {
        public bool HasOpenSegment { get; set; }
        public string? ProjectName { get; set; }
        public string? ProjectNumber { get; set; }
        public string? FileName { get; set; }
        public string? ViewName { get; set; }
        public string? TaskPhase { get; set; }
        public string? Username { get; set; }
        public double ElapsedSeconds { get; set; }
        public bool IsIdle { get; set; }
    }
}
