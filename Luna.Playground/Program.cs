using System.Diagnostics;
using System.Text.Json;
using Luna.Core.Auth;
using Luna.Core.Sessions;
using Luna.Core.Throttling;
using Luna.Protocol;
using Luna.Protocol.Api;
using Luna.Protocol.Auth;
using Luna.Protocol.Diagnostics;
using Luna.Protocol.Events;
using Luna.Protocol.Messaging;
using Luna.Protocol.Realtime;

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
