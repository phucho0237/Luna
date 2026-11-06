using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Luna.Core.Auth;
using Luna.Core.Dev;
using Luna.Core.Sessions;
using Luna.Core.Storage;
using Luna.Core.Throttling;
using Luna.Protocol;
using Luna.Protocol.Api;
using Luna.Protocol.Auth;
using Luna.Protocol.Diagnostics;
using Luna.Protocol.Events;
using Luna.Protocol.Messaging;
using Luna.Protocol.Realtime;

// --seed-history mode: OFFLINE. Writes FAKE messages into a local file; no server, no session, no API call. Handled first,
// before anything that could touch the saved session is built.
int seedIndex = Array.FindIndex(args, a => string.Equals(a, "--seed-history", StringComparison.OrdinalIgnoreCase));
if (seedIndex >= 0)
{
    await SeedHistoryAsync(args, seedIndex);
    return;
}

bool printShape = args.Contains("--shape", StringComparer.OrdinalIgnoreCase)
    || args.Contains("--inspect-shape", StringComparer.OrdinalIgnoreCase)
    || args.Contains("-s", StringComparer.OrdinalIgnoreCase);

if (printShape)
{
    Console.WriteLine("--shape mode: prints the shape (field names and data types) of real messages received.");
    Console.WriteLine("Never prints values or message content to the screen.");
}

// --events mode: routes through EventRouter, prints only the event type name and counts (no id, uid or content).
bool printEvents = args.Contains("--events", StringComparer.OrdinalIgnoreCase);
if (printEvents)
{
    Console.WriteLine("--events mode: prints only event type names and counts, no ids or content.");
}

var eventCounts = new Dictionary<string, int>();

// Shape counter for --shape: the key is a shape string (it contains no values).
var shapeCounts = new Dictionary<string, int>();
var shapeIds = new Dictionary<string, int>();

string qrPath = Path.Combine(Path.GetTempPath(), "luna-qr.png");
var store = new EncryptedFileSessionStore(EncryptedFileSessionStore.DefaultPath, new DpapiSecretProtector());
var auth = new AuthService(store, trace: Console.WriteLine);

// Remembers the cooldown after 429 and the getfriends call mark between runs (--friends-list); the file lives outside the repo.
var throttleGuard = new ApiThrottleGuard();

// Minimum interval between two runs of --send-cloud: shared by the check and the notice.
TimeSpan sendTextInterval = TimeSpan.FromMinutes(10);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// --friends-list mode: calls GetFriendListAsync EXACTLY ONCE (one getfriends request, large count) and exits; runs at most once every 10 minutes.
// Prints only counts and the FieldSurvey block (field name, type, count); never values, names, phone numbers, avatars or the response body.
if (args.Contains("--friends-list", StringComparer.OrdinalIgnoreCase))
{
    if (throttleGuard.MinutesRemaining() is { } blockedMinutes)
    {
        Console.WriteLine($"In cooldown after a 429, about {blockedMinutes} minutes left. Not running.");
        return;
    }

    if (throttleGuard.MinutesUntilAllowed("getfriends", TimeSpan.FromMinutes(10)) is { } waitMinutes)
    {
        Console.WriteLine($"Blocked: about {waitMinutes} minutes left.");
        return;
    }

    try
    {
        AuthSession? listSession = await auth.RestoreAsync(cts.Token);
        if (listSession is null)
        {
            Console.WriteLine("No saved session. Run the normal mode (QR login) once first, then run --friends-list again.");
            return;
        }

        Console.WriteLine("At most 1 getfriends request; runs only once every 10 minutes.");
        throttleGuard.RecordCall("getfriends"); // Recorded BEFORE the call: an error midway still counts as called.
        using var listClient = new ApiClient(
            listSession.Context, listSession.Info, new ApiClientOptions { Trace = Console.WriteLine });
        var survey = new FieldSurvey();
        FriendListResult list = await listClient.GetFriendListAsync(survey.Add, cts.Token);

        Console.WriteLine($"Total friends: {list.Friends.Count}");
        Console.WriteLine($"Skipped: {list.Skipped}");
        Console.WriteLine($"IsFriend: {list.Friends.Count(f => f.IsFriend)}");
        Console.WriteLine($"IsBlocked: {list.Friends.Count(f => f.IsBlocked)}");
        Console.WriteLine($"IsActive: {list.Friends.Count(f => f.IsActive)}");
        Console.WriteLine("Field survey (name: type x count):");
        foreach (string line in survey.Describe())
        {
            Console.WriteLine(line);
        }
    }
    catch (ApiException ex)
    {
        Console.WriteLine($"The server returned error {ex.ErrorCode}");
    }
    catch (RateLimitedException)
    {
        throttleGuard.RecordBlock();
        Console.WriteLine("429, stopping. Wait at least one hour before running again, do NOT run again right away.");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine(ex.StatusCode is { } status
            ? $"HTTP error {(int)status}"
            : $"Network error ({ex.GetType().Name})");
    }
    catch (InvalidDataException ex)
    {
        Console.WriteLine($"Malformed response ({ex.GetType().Name})");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Cancelled.");
    }

    return;
}

// --history-probe mode: LIVE server, human only. Requests old messages (frames 510/511) a few times and prints only counts,
// day ranges and shapes. Saves nothing; runs at most once every 30 minutes. See RunHistoryProbeAsync.
if (args.Contains("--history-probe", StringComparer.OrdinalIgnoreCase))
{
    await RunHistoryProbeAsync(auth, throttleGuard, cts.Token);
    return;
}

// --send-cloud mode: sends EXACTLY ONE fixed text message to "My Documents" (the account's own send2me_id), then exits.
// Sending is a write operation: no retry, no loop. Never prints msgId, uid or content; runs at most once every 10 minutes.
if (args.Contains("--send-cloud", StringComparer.OrdinalIgnoreCase))
{
    if (throttleGuard.MinutesRemaining() is { } cloudBlockedMinutes)
    {
        Console.WriteLine($"In cooldown after a 429, about {cloudBlockedMinutes} minutes left. Not running.");
        return;
    }

    if (throttleGuard.MinutesUntilAllowed("sendtext", sendTextInterval) is { } sendWaitMinutes)
    {
        Console.WriteLine($"Blocked: about {sendWaitMinutes} minutes left.");
        return;
    }

    bool requestSent = false;
    try
    {
        AuthSession? sendSession = await auth.RestoreAsync(cts.Token);
        if (sendSession is null)
        {
            Console.WriteLine("No saved session. Run the normal mode (QR login) once first, then run --send-cloud again.");
            return;
        }

        if (string.IsNullOrEmpty(sendSession.Info.Send2MeId))
        {
            Console.WriteLine("The login response has no send2me_id; nothing is sent.");
            return;
        }

        Console.WriteLine($"Sending exactly 1 message to My Documents; runs only once every {(int)sendTextInterval.TotalMinutes} minutes.");
        throttleGuard.RecordCall("sendtext"); // Recorded BEFORE sending: an error midway still counts as sent.
        using var sendClient = new ApiClient(
            sendSession.Context, sendSession.Info, new ApiClientOptions { Trace = Console.WriteLine });
        string text = "Luna test " + DateTime.UtcNow.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        requestSent = true;
        await sendClient.SendTextAsync(sendSession.Info.Send2MeId, text, cts.Token);

        Console.WriteLine("Sent: the server accepted it.");
    }
    catch (ApiException ex)
    {
        Console.WriteLine($"The server returned error {ex.ErrorCode}");
    }
    catch (RateLimitedException)
    {
        throttleGuard.RecordBlock();
        Console.WriteLine("429, stopping. Wait at least one hour before running again, do NOT run again right away.");
    }
    catch (HttpRequestException)
    {
        Console.WriteLine("Unknown whether the message was sent. Check My Documents before running again; do NOT run again by yourself.");
    }
    catch (InvalidDataException ex)
    {
        Console.WriteLine($"Malformed response ({ex.GetType().Name})");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine(requestSent
            ? "Unknown whether the message was sent. Check My Documents before running again; do NOT run again by yourself."
            : "Cancelled.");
    }

    return;
}

try
{
    AuthSession? session = await auth.RestoreAsync(cts.Token);
    if (session is not null)
    {
        Console.WriteLine("Restored the saved session, no QR scan needed.");
    }
    else
    {
        Console.WriteLine("No valid session. Logging in with QR (SECONDARY ACCOUNT)...");
        session = await auth.LoginWithQrAsync(
            e =>
            {
                switch (e)
                {
                    case QrGenerated generated:
                        File.WriteAllBytes(qrPath, generated.PngImage);
                        Console.WriteLine($"Saved the QR code at {qrPath}. Scan it with the mobile app.");
                        Process.Start(new ProcessStartInfo(qrPath) { UseShellExecute = true });
                        break;
                    case QrScanned:
                        Console.WriteLine("The QR code was scanned. Tap confirm on the phone.");
                        break;
                    case QrExpired:
                        Console.WriteLine("The QR code expired, generating a new one...");
                        break;
                    case QrDeclined:
                        Console.WriteLine("You declined the login on the phone.");
                        break;
                }
            },
            cts.Token);
    }

    Console.WriteLine($"Login OK. WebSocket count: {session.Info.WebSocketUrls.Count}");
    Console.WriteLine("Connecting the WebSocket. Send a message to this account from another account. Ctrl+C to stop.");

    var messageParser = new MessageParser(session.Info.Uid);
    var eventRouter = new EventRouter(session.Info.Uid);
    var socket = new SocketClient(session.Context, session.Info.WebSocketUrls, Console.WriteLine);
    await socket.RunAsync(
        onEvent: e =>
        {
            if (e.Cmd != 2)
            {
                Console.WriteLine(Describe(e));
            }

            if (e.Cmd is MessageParser.CmdUserMessage or MessageParser.CmdGroupMessage)
            {
                ParseResult parseResult = messageParser.Parse(e);
                foreach (ChatMessage msg in parseResult.Messages)
                {
                    Console.WriteLine($"  -> [Parsed] {msg}");
                }

                if (parseResult.Skipped > 0)
                {
                    Console.WriteLine($"  -> [Skipped] skipped {parseResult.Skipped} invalid messages");
                }
            }

            if (printEvents)
            {
                RouteResult routed = eventRouter.Route(e);
                foreach (ChatEvent routedEvent in routed.Events)
                {
                    string typeName = routedEvent.GetType().Name;
                    eventCounts[typeName] = eventCounts.GetValueOrDefault(typeName) + 1;
                    // Prints only the type name and count; UnknownEvent prints the cmd and key names too (through SafeName, handled by ToString).
                    Console.WriteLine(routedEvent is UnknownEvent
                        ? $"  -> [Event] {routedEvent} (count {eventCounts[typeName]})"
                        : $"  -> [Event] {typeName} (count {eventCounts[typeName]})");
                }

                if (routed.Skipped > 0)
                {
                    Console.WriteLine($"  -> [Event] skipped {routed.Skipped} invalid elements");
                }
            }

            if (printShape && e.Cmd is not (1 or 2))
            {
                bool isMessageCmd = e.Cmd is MessageParser.CmdUserMessage or MessageParser.CmdGroupMessage;
                string? messageArray = !isMessageCmd ? null : e.Cmd == MessageParser.CmdGroupMessage ? "groupMsgs" : "msgs";
                var seenInEvent = new List<string>();

                if (e.Payload.ValueKind == JsonValueKind.Object
                    && e.Payload.TryGetProperty("data", out JsonElement dataObj)
                    && dataObj.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in dataObj.EnumerateObject())
                    {
                        JsonElement target = prop.Value;
                        if (target.ValueKind == JsonValueKind.Array)
                        {
                            // An empty array has nothing to describe: skipped.
                            if (target.GetArrayLength() == 0)
                            {
                                continue;
                            }

                            target = target.EnumerateArray().First();
                        }

                        // controls (601) and actions (602): prints only act_type/act that went through SafeToken.
                        if ((e.Cmd == 601 && prop.Name == "controls") || (e.Cmd == 602 && prop.Name == "actions"))
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement item in prop.Value.EnumerateArray())
                                {
                                    RecordTokens(e.Cmd, prop.Name, item, viaContent: e.Cmd == 601);
                                }
                            }
                        }

                        // For message cmds, each element of msgs/groupMsgs is described separately (keeps the old behavior).
                        if (prop.Name == messageArray && prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement item in prop.Value.EnumerateArray())
                            {
                                RecordShape(e.Cmd, prop.Name, JsonShape.Describe(item), seenInEvent);
                            }

                            continue;
                        }

                        // Scalar property: ONE line "  name: type", deduplicated by cmd|name|type, never prints values.
                        if (target.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                        {
                            string type = JsonShape.Describe(target);
                            RecordScalar(e.Cmd, prop.Name, prop.Value.ValueKind == JsonValueKind.Array ? $"[{type}]" : type);
                            continue;
                        }

                        // Object or array with elements: prints a multi-line block.
                        RecordShape(e.Cmd, prop.Name, JsonShape.Describe(target), seenInEvent);
                    }
                }
                else
                {
                    // No object-shaped data (for example cmd 621): describe the whole payload.
                    RecordShape(e.Cmd, "(payload)", JsonShape.Describe(e.Payload), seenInEvent);
                }

                // End of the event: how many times each shape appearing in this event has been seen (cumulative).
                foreach (string key in seenInEvent)
                {
                    Console.WriteLine($"  shape #{shapeIds[key]}: seen {shapeCounts[key]} times");
                }
            }
        },
        onState: s => Console.WriteLine($"[{s.State}] {s.Detail}"),
        cancellationToken: cts.Token);

    if (printEvents)
    {
        Console.WriteLine("Total events by type:");
        foreach ((string name, int count) in eventCounts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {name}: {count}");
        }
    }

    Console.WriteLine("Stopped.");
}
catch (ApiException ex)
{
    Console.WriteLine($"The server returned error {ex.ErrorCode}: {ex.ServerMessage}");
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"HTTP error: {ex.Message}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
}
finally
{
    if (File.Exists(qrPath))
    {
        File.Delete(qrPath);
    }
}

static void PrintHistoryProbeUsage()
{
    Console.WriteLine("--history-probe mode (LIVE server, human only):");
    Console.WriteLine("  Close the Luna app and every web session first, and use a SECONDARY account.");
    Console.WriteLine($"  Runs at most once per {ProbePlan.MinMinutesBetweenRuns} minutes. Requests at most {ProbePlan.MaxPagesPerScope} pages of old");
    Console.WriteLine("  private messages, then the same for group messages, at least 5 seconds apart; stops after 90 seconds.");
    Console.WriteLine("  Prints counts, day ranges and field names only: no message text, names, ids or exact times.");
    Console.WriteLine("  Nothing is saved. Any error, disconnect or missing answer stops the probe.");
}

// Probe of the old-message frames (T5b). Prints only counts, day ranges, field names and the kind of any anomaly.
// The order matters for account safety: guard check, restore the session, RecordCall BEFORE connecting, one socket,
// typed requests only, no retry, no loop beyond ProbePlan.
static async Task RunHistoryProbeAsync(AuthService auth, ApiThrottleGuard guard, CancellationToken ct)
{
    PrintHistoryProbeUsage();

    if (guard.MinutesRemaining() is { } blockedMinutes)
    {
        Console.WriteLine($"In cooldown after a 429, about {blockedMinutes} minutes left. Not running.");
        return;
    }

    if (guard.MinutesUntilAllowed(ProbePlan.GuardKey, TimeSpan.FromMinutes(ProbePlan.MinMinutesBetweenRuns)) is { } waitMinutes)
    {
        Console.WriteLine($"Blocked: about {waitMinutes} minutes left before the probe may run again.");
        return;
    }

    var total = new Stopwatch();
    string stop = "complete";
    int requestsSent = 0;
    int privatePages = 0;
    int groupPages = 0;
    int parsedTotal = 0;
    try
    {
        AuthSession? session = await auth.RestoreAsync(ct);
        if (session is null)
        {
            Console.WriteLine("No saved session. Run the normal mode (QR login) once first, then run --history-probe again.");
            return;
        }

        guard.RecordCall(ProbePlan.GuardKey); // Recorded BEFORE connecting: an error midway still counts as a run.
        total.Start();

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(TimeSpan.FromSeconds(ProbePlan.MaxRunSeconds));
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var anomaly = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responses = Channel.CreateUnbounded<SocketEvent>();

        // No trace action: the client prints nothing. The state detail (a free-text reason) is never printed either.
        var client = new SocketClient(session.Context, session.Info.WebSocketUrls);
        Task runTask = client.RunAsync(
            onEvent: e =>
            {
                if (e.Cmd is 510 or 511 && e.SubCmd == 1)
                {
                    responses.Writer.TryWrite(e);
                }
            },
            onState: s =>
            {
                switch (s.State)
                {
                    case SocketState.Connected:
                        connected.TrySetResult();
                        break;
                    case SocketState.DuplicateConnection:
                        anomaly.TrySetResult("duplicate-connection");
                        break;
                    case SocketState.Disconnected:
                        anomaly.TrySetResult(Volatile.Read(ref requestsSent) > 0 ? "disconnect-after-request" : "disconnect-before-request");
                        break;
                }
            },
            cancellationToken: runCts.Token);

        try
        {
            stop = await DriveAsync();
        }
        finally
        {
            await runCts.CancelAsync();
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
                // Stopped on purpose.
            }
        }

        string CancelKind() => ct.IsCancellationRequested ? "cancelled" : "time-limit";

        // Waits for the answer to the last request: cmd 510/511, subCmd 1. Gives up after ResponseTimeoutSeconds.
        async Task<(string? Problem, JsonElement Payload)> NextResponseAsync(ushort cmd)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(ProbePlan.ResponseTimeoutSeconds));
            try
            {
                while (true)
                {
                    Task<SocketEvent> read = responses.Reader.ReadAsync(timeout.Token).AsTask();
                    Task first = await Task.WhenAny(read, anomaly.Task);
                    if (first == anomaly.Task)
                    {
                        await timeout.CancelAsync();
                        try
                        {
                            await read;
                        }
                        catch (OperationCanceledException)
                        {
                            // The pending read was canceled on purpose.
                        }

                        return (await anomaly.Task, default);
                    }

                    SocketEvent e = await read;
                    if (e.Cmd == cmd && e.SubCmd == 1)
                    {
                        return (null, e.Payload);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return (runCts.IsCancellationRequested ? CancelKind() : "no-response", default);
            }
        }

        async Task<string> DriveAsync()
        {
            Task firstDone = await Task.WhenAny(
                connected.Task,
                anomaly.Task,
                Task.Delay(TimeSpan.FromSeconds(ProbePlan.ResponseTimeoutSeconds), runCts.Token));
            if (firstDone == anomaly.Task)
            {
                return await anomaly.Task;
            }

            if (firstDone != connected.Task)
            {
                return runCts.IsCancellationRequested ? CancelKind() : "no-connection";
            }

            Console.WriteLine($"Connected. Waiting {ProbePlan.SettleSecondsAfterConnect} seconds, then requesting old messages.");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ProbePlan.SettleSecondsAfterConnect), runCts.Token);
            }
            catch (OperationCanceledException)
            {
                return CancelKind();
            }

            Stopwatch? sinceLast = null;
            foreach (HistoryScope scope in new[] { HistoryScope.Private, HistoryScope.Group })
            {
                ushort responseCmd = scope == HistoryScope.Private ? (ushort)510 : (ushort)511;
                string scopeName = scope == HistoryScope.Private ? "private" : "group";
                HistoryProbeSummary? previous = null;
                int pagesDone = 0;
                while (true)
                {
                    double sinceSeconds = sinceLast?.Elapsed.TotalSeconds ?? double.PositiveInfinity;
                    if (!ProbePlan.ShouldRequestNext(
                            pagesDone, previous?.Parsed ?? 0, previous?.HasError ?? false, sinceSeconds, total.Elapsed.TotalSeconds))
                    {
                        string reason = previous is { HasError: true } ? "error"
                            : previous is { Parsed: 0 } ? "empty page"
                            : pagesDone >= ProbePlan.MaxPagesPerScope ? "page limit"
                            : "time limit";
                        Console.WriteLine($"Scope {scopeName}: {pagesDone} page(s), stopped by: {reason}.");
                        break;
                    }

                    string? lastId = null;
                    if (previous is not null)
                    {
                        if (!previous.TryGetNextLastId(out string nextId))
                        {
                            return "next-id-not-usable";
                        }

                        lastId = nextId;
                    }

                    double wait = ProbePlan.SecondsToWait(sinceSeconds);
                    if (wait > 0)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(wait), runCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return CancelKind();
                        }
                    }

                    Interlocked.Increment(ref requestsSent);
                    client.RequestOldMessages(scope, lastId);
                    sinceLast = Stopwatch.StartNew();

                    (string? problem, JsonElement payload) = await NextResponseAsync(responseCmd);
                    if (problem is not null)
                    {
                        return problem;
                    }

                    var summary = HistoryProbeSummary.From(payload, scope, session.Info.Uid, DateTimeOffset.UtcNow, previous);
                    pagesDone++;
                    parsedTotal += summary.Parsed;
                    if (scope == HistoryScope.Private)
                    {
                        privatePages++;
                    }
                    else
                    {
                        groupPages++;
                    }

                    Console.WriteLine($"[{scopeName} page {pagesDone}] {summary.Format()}");
                    Console.WriteLine("  shape: " + System.Text.RegularExpressions.Regex.Replace(summary.Shape, @"\s+", " ").Trim());
                    if (summary.HasError)
                    {
                        return "error-indicator";
                    }

                    previous = summary;
                }
            }

            return "complete";
        }
    }
    catch (ApiException ex)
    {
        stop = "api-error";
        Console.WriteLine($"The server returned error {ex.ErrorCode}");
    }
    catch (RateLimitedException)
    {
        stop = "rate-limited";
        guard.RecordBlock();
        Console.WriteLine("429, stopping. Wait at least one hour before running again, do NOT run again right away.");
    }
    catch (HttpRequestException ex)
    {
        stop = "http-error";
        Console.WriteLine(ex.StatusCode is { } status ? $"HTTP error {(int)status}" : $"Network error ({ex.GetType().Name})");
    }
    catch (InvalidDataException ex)
    {
        stop = "malformed-response";
        Console.WriteLine($"Malformed response ({ex.GetType().Name})");
    }
    catch (OperationCanceledException)
    {
        stop = "cancelled";
        Console.WriteLine("Cancelled.");
    }

    if (stop is "error-indicator" or "disconnect-after-request")
    {
        guard.RecordBlock(); // The cooldown protects the account: do not run again for an hour.
        Console.WriteLine("Anomaly: " + stop + ". Wait at least one hour before running again.");
    }
    else if (stop is not ("complete" or "time-limit" or "cancelled"))
    {
        Console.WriteLine("Anomaly: " + stop);
    }

    Console.WriteLine(
        $"Summary: stop={stop} requests={requestsSent} private_pages={privatePages} group_pages={groupPages} "
        + $"parsed={parsedTotal} seconds={(int)total.Elapsed.TotalSeconds}");
}

static void PrintSeedUsage()
{
    Console.WriteLine("Usage: --seed-history <file> <count> [--force]");
    Console.WriteLine("  OFFLINE: no server, no session, no API call. Writes <count> (1 to 1000000) FAKE messages and fake contact");
    Console.WriteLine("  names into a SQLite file.");
    Console.WriteLine("  The file must be outside %LOCALAPPDATA%\\Luna (the real history is never touched). An existing file is used");
    Console.WriteLine("  only with --force, and only when it holds nothing but fake conversations.");
    Console.WriteLine("  The file is for Debug runs of the app: set LUNA_HISTORY_DB to its path before starting the app.");
    Console.WriteLine("  WARNING: when the app runs on this file it ALSO saves real incoming messages and real friend names into");
    Console.WriteLine("  it. Delete the file afterwards.");
}

// Prints only counts and short reasons: never a message text, a name, an id or the file path.
static async Task SeedHistoryAsync(string[] args, int seedIndex)
{
    const int MaxSeedCount = 1_000_000;
    const int BatchSize = 500;

    bool force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
    string? file = seedIndex + 1 < args.Length ? args[seedIndex + 1] : null;
    string? countText = seedIndex + 2 < args.Length ? args[seedIndex + 2] : null;
    if (file is null
        || file.StartsWith("--", StringComparison.Ordinal)
        || countText is null
        || !int.TryParse(countText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int count)
        || count < 1
        || count > MaxSeedCount)
    {
        PrintSeedUsage();
        return;
    }

    SeedTargetCheck check = SyntheticHistory.CheckTarget(file, MessageStoreDefaults.DefaultPath, force, File.Exists);
    if (check != SeedTargetCheck.Ok)
    {
        Console.WriteLine(check switch
        {
            SeedTargetCheck.IsDefaultPath => "Refused: that is the real history database.",
            SeedTargetCheck.InsideDataFolder => "Refused: the path is inside the folder of the real history database.",
            SeedTargetCheck.ExistsWithoutForce => "Refused: the file already exists. Use --force to add to it.",
            _ => "Refused: not a usable file path.",
        });
        return;
    }

    string fullPath = Path.GetFullPath(file);
    try
    {
        using var seedStore = new SqliteMessageStore(fullPath);

        // An existing file (only possible with --force) must hold nothing but fake conversations before anything is saved.
        IReadOnlyList<ConversationSummary> existing = await seedStore.ListConversationsAsync(500);
        if (!SyntheticHistory.AreAllThreadsSynthetic(existing.Select(c => c.ThreadId)))
        {
            Console.WriteLine("Refused: the existing file holds conversations that are not fake. Nothing was written.");
            return;
        }

        IReadOnlyList<ChatMessage> history = SyntheticHistory.Build(count, DateTimeOffset.UtcNow);
        int inserted = 0;
        int recalled = 0;
        int skipped = 0;
        for (int offset = 0; offset < history.Count; offset += BatchSize)
        {
            StoreResult result = await seedStore.SaveAsync(history.Skip(offset).Take(BatchSize).ToList());
            inserted += result.Inserted;
            recalled += result.Recalled;
            skipped += result.Skipped;
        }

        await seedStore.ReplaceContactsAsync(SyntheticHistory.ContactNames);
        IReadOnlyList<ConversationSummary> conversations = await seedStore.ListConversationsAsync(500);

        Console.WriteLine($"Messages saved: {inserted}");
        Console.WriteLine($"Recall notices applied: {recalled}");
        Console.WriteLine($"Skipped: {skipped}");
        Console.WriteLine($"Conversations: {conversations.Count}");
        Console.WriteLine("Reminder: the app saves real messages and friend names into this file too. Delete it afterwards.");
    }
    catch (Exception ex)
    {
        // Only the exception type is printed: a message can carry a path or data.
        Console.WriteLine($"Failed ({ex.GetType().Name}). The file may be incomplete; delete it and try again.");
    }
}

// Prints only the event name and count, not the message content.
static string Describe(SocketEvent e)
{
    var parts = new List<string> { $"cmd={e.Cmd} sub={e.SubCmd}" };
    if (e.Payload.ValueKind == JsonValueKind.Object
        && e.Payload.TryGetProperty("data", out JsonElement data)
        && data.ValueKind == JsonValueKind.Object)
    {
        foreach (JsonProperty property in data.EnumerateObject())
        {
            parts.Add(property.Value.ValueKind == JsonValueKind.Array
                ? $"{JsonShape.SafeName(property.Name)}[{property.Value.GetArrayLength()}]"
                : JsonShape.SafeName(property.Name));
        }
    }

    return string.Join(' ', parts);
}

// Records a shape: key "cmd|name|shape" (contains no values), printed once, later occurrences only bump the counter.
void RecordShape(int cmd, string name, string shape, List<string> seenInEvent)
{
    name = JsonShape.SafeName(name);
    string key = $"{cmd}|{name}|{shape}";
    if (shapeCounts.TryGetValue(key, out int seen))
    {
        shapeCounts[key] = seen + 1;
    }
    else
    {
        shapeCounts[key] = 1;
        shapeIds[key] = shapeIds.Count + 1;
        Console.WriteLine($"--- NEW SHAPE #{shapeIds[key]} (cmd {cmd}, {name}) ---");
        Console.WriteLine(shape);
        Console.WriteLine("---------------------------------------------");
    }

    if (!seenInEvent.Contains(key))
    {
        seenInEvent.Add(key);
    }
}

// Prints act_type/act (only these two fields) through SafeToken, deduplicated by "tok|cmd|name|value".
void RecordTokens(int cmd, string name, JsonElement item, bool viaContent)
{
    JsonElement holder = item;
    if (viaContent
        && (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("content", out holder)))
    {
        return;
    }

    if (holder.ValueKind != JsonValueKind.Object)
    {
        return;
    }

    string actType = holder.TryGetProperty("act_type", out JsonElement t) && t.ValueKind == JsonValueKind.String
        ? JsonShape.SafeToken(t.GetString()) : "<none>";
    string act = holder.TryGetProperty("act", out JsonElement a) && a.ValueKind == JsonValueKind.String
        ? JsonShape.SafeToken(a.GetString()) : "<none>";
    name = JsonShape.SafeName(name);
    // Shape of data (a nested JSON string is parsed too): key names and types only, one line.
    string dataShape = holder.TryGetProperty("data", out JsonElement dataEl)
        ? JsonShape.DescribeEmbeddedOneLine(dataEl) : "<none>";
    // The dedup key uses the value that went through SafeToken, not the raw string.
    string key = $"tok|{cmd}|{name}|{actType}|{act}|{dataShape}";
    if (shapeCounts.TryAdd(key, 1))
    {
        Console.WriteLine($"[cmd {cmd}] {name}.{actType}/{act} data={dataShape}");
    }
    else
    {
        shapeCounts[key]++;
    }
}

// Scalar property: prints one line "  name: type" the first time a cmd|name|type pair is seen, no block, no recount.
void RecordScalar(int cmd, string name, string type)
{
    name = JsonShape.SafeName(name);
    string key = $"{cmd}|{name}|{type}";
    if (shapeCounts.TryAdd(key, 1))
    {
        Console.WriteLine($"  [cmd {cmd}] {name}: {type}");
    }
    else
    {
        shapeCounts[key]++;
    }
}
