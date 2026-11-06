# Roadmap

Do the **first unfinished task** from the top down. When done, change `[ ]` to `[x]` and record the date. Every task must meet "Done when" in `AGENTS.md`. For any task that needs to call the live server, state that this part is **run by Luca** with `Luna.Playground` using a secondary account; the agent cannot do it and must not claim to have tried it.

## Done (verified on the live server with a secondary account)

- [x] Encryption and signing: `ParamsEncryptor`, `SignKey`, `ProtocolAes`, `SessionCipher` (matches zca-js)
- [x] `LoginApi`: `getServerInfo`, `getLoginInfo`, reading `zpw_service_map_v3`
- [x] `QrLoginClient`: QR login, cookies and redirects handled manually
- [x] `EncryptedFileSessionStore` (DPAPI) and `AuthService` (session restore, QR login)
- [x] `SocketClient`: WebSocket, authentication, ping, AES-GCM with a 16-byte IV, receiving `501` and `521`
- [x] `MessageParser` and the message model (T1, T1b, T1c)
- [x] `EventRouter`: routing socket events into typed events (T2a, T2b, T2)

## Next up

Current order: T7b-2 (home view and composition root), then T7c (chat pane). T2c, T4b, T5b and T5c each need live runs or research with the secondary account, so they are done when the interface needs them.

### [x] T1. Message model and parser (2026-11-02 - the live-server part was run by Luca with the Playground)

Folder `Luna.Protocol/Messaging/`.

- A `ChatMessage` type (record) for direct and group messages, with all fields from the "Message model" section of `PROTOCOL.md`. It has `ThreadType` (`User`, `Group`), `ThreadId`, `IsSelf`, `Mentions`, `Quote`, `MsgType`.
- `Content` distinguishes **text** (a string) from an **attachment** (an object), and keeps the original `JsonElement` for later use. It recognizes recalled messages (`content.deleteMsg`).
- `MessageParser` takes a `SocketEvent` (cmd 501 and 521) and returns a list of `ChatMessage`. The account's own messages must be recognized by `uidFrom == "0"`.
- Every id is a **string**. There is a test with numbers larger than `long.MaxValue`.
- Criteria: tests use fake JSON built from `PROTOCOL.md`, covering: incoming direct message, direct message sent by self, group message, with mention, with quote, attachment, recall, missing fields. Never print message content to a log.
- Luca's part: add a Playground command that prints the **shape** (field names and types, no values) of a real message to compare against the model. Report any fields that are not in the model.

### [x] T1b. Compare the shape of real messages against the model (2026-11-02 - Luca ran Playground --shape)

### [x] T1c. Message model following the real shape (2026-11-02)

- Add `StickerContent` (chosen by `msgType == "chat.sticker"`) and `UnknownContent` (an object with none of the attachment keys, keeping `Raw`).
- Add the `MessageTypes` constants and the `ChatMessage.PreviewThumb` field.
- `MessagePropertyExt.ToString()` does not print `Ext`.
- Tests that follow the real shape: sticker, an all-digit undo, a quote whose `ts` is a number, an image with `previewThumb`, group messages from self and from others.
- Playground `--shape` prints each shape only once, with the number of times it was seen.
- Update the "Message model" section in `PROTOCOL.md`.

### [x] T2a. Collect the shapes of the other socket events (2026-11-02 - Luca ran Playground --shape)

**Result:** run on the live server; the collected shapes are in `PROTOCOL.md` (section "Other event shapes"), and what has not been seen is also recorded there.

- `Luna.Protocol/Diagnostics/JsonShape.cs`: `JsonShape.Describe(JsonElement, int maxDepth = 6)` describes field names and types (string, number, boolean, null, object, array), takes only the first element of an array, and never prints values. Move the logic from the Playground's `GetShape` here (do not refactor `QrLoginClient.Shape`).
- Tests in `Luna.Protocol.Tests/Diagnostics/`: a fake secret value does not appear in the result; nested objects, empty array, multi-element array, depth limit, null, numbers and bool.
- Playground `--shape`: for each event (except cmd 1 and 2), walk `data.*`; for each property that is an array or object, print the shape of the first element (or of the object itself), deduplicate on the key `cmd|name|shape`, and print once with the count of times seen. A payload with no object-valued `data` (for example cmd 621) prints the shape of the whole payload. Keep the existing behavior for `msgs` and `groupMsgs`.
- `docs/PROTOCOL.md`: add the subsection "Other event shapes" recording the shapes collected for cmd 502, 504, 522, 524, 601, 602, 610, 611, 612, 621 (state clearly any cmd that has not been seen).
- Luca's part when it is done: run `--shape` and perform actions (seen, typing, reaction, group events, friend request, reading a conversation, a direct message sent from another device), then send the output.

### [x] T2b. Print safe classification values (act_type, act) and collect the remaining missing shapes (2026-11-02)

**Result:** run on the live server; the table of `act_type`/`act` values is in `PROTOCOL.md`. The detailed shape of `controls.content` per `act_type` and of `seens`/`groupSeens`: see T2c.

- `JsonShape.SafeToken(string)`: returns the string if it matches `^[a-z][a-z0-9_.-]{0,39}\z` and does not contain 5 consecutive digits, otherwise returns `<v>`.
- Playground `--shape`: for each element of `controls` (cmd 601) and `actions` (cmd 602), print one extra line `[cmd N] name.act_type=<SafeToken> act=<SafeToken>`, deduplicated on cmd|name|value. No other value is printed.
- Tests in `Luna.Protocol.Tests/Diagnostics/`: a valid value is kept as is; a value with uppercase letters, with diacritics, that looks like a phone number, longer than 40 characters or with 5 consecutive digits all become `<v>` (and must be present in the input data).
- Luca's part: run `--shape` again and perform actions (group events, friend request, a half-typed message, reading messages to produce `seens`), then send the output.

### [x] T2. Typed event routing (2026-11-02 - Luca ran Playground --events)

**Result:** run on the live server with Playground `--events` and a secondary account: no element was skipped; only cmd 522 (with `seens` or `groupSeens`) and cmd 621 are still `UnknownEvent`. Parsing the content of `controls`, `seens`, `groupSeens`: see T2c.

Folder `Luna.Protocol/Events/`. Based on the real shapes in `PROTOCOL.md` (sections "Other event shapes" and "act_type / act values").

- `ChatEvent` (abstract record) and its sealed subtypes: `MessageReceived` (holds a `ChatMessage`), `MessageUndone` (ThreadType, ThreadId, GlobalMsgId, CliMsgId, all strings), `DeliveryReceipt` (IsGroup, MsgId, RealMsgId, nullable GroupId; the list of uids that received it lives only in a property), `UnreadCleared` (IdTo, nullable LastMsgId, LastCliMsgId, IsGroup, Timestamp), `ReactionReceived` (ThreadType inferred from `reacts` or `reactGroups`, MsgId, CliMsgId, UidFrom, IdTo, RawContent as the original string, Timestamp), `TypingNotice` (IsGroup, Uid, nullable GroupId, taken from the JSON string in `data`; check models/Typing in zca-js with scripts/ref-grep.ps1), `ControlEvent` (ActType, Act, Content kept as the original JsonElement, Kind classified by ActType: Group, Friend, Mute, PinnedConversation, EmojiStatus, OneOne, FileDone, Other), `UnknownEvent` (Cmd, SubCmd, Payload).
- `EventRouter(string? selfUid = null, Action<string>? trace = null)` with `Route(SocketEvent)` returning `RouteResult(IReadOnlyList<ChatEvent> Events, int Skipped)`. Skip cmd 1 and 2. Use `MessageParser` for 501 and 521; a message with `IsUndo` emits `MessageUndone` instead of `MessageReceived`. Handle 502 and 522 (`delivereds`), 504, 524 and 613 (`clearUnreads`), 612 (`reacts`, `reactGroups`), 602 (`actions` whose act_type is typing), 601 (`controls`). Every other cmd, every array not yet understood (`seens`, `groupSeens`, `pageMsgs`, `eesession`) and cmd 621 go to `UnknownEvent`; it **never** throws or drops the connection.
- A bad element (missing field, wrong type) is skipped and counted in `Skipped`; catch only `InvalidOperationException`, `FormatException`, `JsonException`. Every id is read with `JsonFields.ReadScalar` (string, large numbers without losing precision).
- Every record that holds a uid, content or `Raw` must override `ToString()` so that it does not print them: print only the event kind, cmd, message id and the `JsonShape.SafeToken` of act_type and act.
- Tests: one test per event kind with fake JSON in the real shape; a batch mixing bad and good elements; an unknown cmd; `ToString` does not leak the uid (the fake key must be present in the input data); `selfUid` affects `IsSelf` of `MessageReceived`; no test touches the network.
- Playground `--events` (Luca runs it when this is done): print the event kind names and counts, never content or ids; `UnknownEvent` prints only the cmd and the key names through `JsonShape.SafeName`.
- Not part of T2: parsing the content of group and friend events (`controls.content`), `seens` and `groupSeens`, sending messages. See T2c.

### [ ] T2c. Parse group, friend and seen events

- Real data was collected on 2026-11-03 and recorded in `PROTOCOL.md` (section "Contents of `controls`, `seens` and `groupSeens`"): `fr`, `mute`, `pinned_convers`, `contact_tab`, `seens`, `groupSeens` and cmd 524 now have full shapes; `group/*` has only the beginning of `data` (cut at 300 characters).
- [x] T2c-1 (done first; coded, tested and run on the live server with Playground `--events` on 2026-11-03, no element skipped): `seens` and `groupSeens`, `fr/*`, `mute/*`, `pinned_convers/update`.
- T2c-2 (done later): group events `group/*`. Needs the truncation limit of `--shape` for `controls` to be raised and a rerun to get the tail of `data`.
- Not yet seen: `emoji_status`, `oneone` (in the run of 3 Nov), `file_done`, `join_reject`, `update_setting`.

### [x] T3. Post-login API call framework (2026-11-03 - Luca ran `--friends`)

**Result:** `--friends` was run on the live server with a secondary account: one request (`count=50`, `page=1`) returned 50 elements, no error. The shape is in `PROTOCOL.md` section 8.

Folder `Luna.Protocol/Api/`.

- `ApiClient`: picks the host from `ServiceMap`, attaches encrypted parameters with `SessionCipher`, decrypts the response, maps `error_code` to `ApiException`. It can pick a fallback address when one host fails.
- Basic rate limiting (queue + minimum interval) and `429` handling.
- Criteria: tests use a fake `HttpMessageHandler`, with a reference vector from zca-js for parameter encryption. The first **read-only** endpoint to try: the friend list (`getfriends`). Luca runs the live trial.

### [x] T3b. Friend model, normalized host, pagination (2026-11-03 - Luca ran `--friends-all`)

**Result:** run on the live server: one `count=500` request returned 50 people; all elements have the same set of fields (`bizPkg.label` is null or an object). Page 2 of `count=50` returned error 112: see T3c.

- Build the URL from a normalized host (block paths, query, port, IP); the `Friend` model and `FriendParser`; `GetAllFriendsAsync` (stops when a page is empty, no new `userId` appears, or `maxPages` is reached); `FieldSurvey` to learn the real types of the fields.
- Playground `--friends-all` (removed in T3e, replaced by `--friends-list`).

### [x] T3c. Pagination: error 112 on a later page means the end of the list (closed: pagination was not fully verified and caused 429, replaced by T3e)

- `GetAllFriendsAsync` currently throws when a page from the 2nd on returns code 112 (seen on the live server with a list of exactly 50 people), so it cannot be used for a list whose last page is full. Treat 112 on page 2 or later as the end of the list (add a flag to the result); 112 on the first page and every other error code still throw.
- `Truncated` is set when it stops because of `maxPages` (a non-empty page followed by new `userId` values) without any end-of-list signal; page length is not a signal because the `count` ceiling is unknown.
- Code done (new `EndedByNoData` and `Truncated` flags). The live run on 2026-11-03 got a 429 on the third request (at both 1-second and 3-second intervals), so pagination was dropped: closed, see T3e.

### [x] T3d. Cooldown after 429

- [Live] three `getfriends` requests 1 second apart: the third got 429. The default `MinInterval` changed from 1 second to 3 seconds.
- `ApiClient` goes into cooldown after a 429 (60 seconds, doubling up to 15 minutes, reset on success) and sends no request during the cooldown; `RateLimitedException.RetryAfter`.
- The Playground remembers a one-hour cooldown between runs (`ApiThrottleGuard`, which since T3e lives in `Luna.Core.Throttling` and has tests).
- Status: code done. Not verified on the live server: the threshold, the penalty time and the scope of the 429.

### [x] T3e. Friend list in one request and per-endpoint intervals (2026-11-03 - Luca ran `--friends-list`)

- `ApiClient.GetFriendListAsync`: exactly one `getfriends` request (page 1, count 20000) like zca-js and ctdozlite; `GetAllFriendsAsync` is for diagnostics only.
- `ApiClientOptions.EndpointMinIntervals`: a minimum interval per endpoint (default 10 minutes for getfriends); a call that comes too early throws `RateLimitedException` without sending anything.
- `ApiThrottleGuard` moved to `Luna.Core.Throttling` (with tests), with a `lastCallsUtc` marker added; the Playground keeps only `--friends-list`.
- **Result:** `--friends-list` was run on the live server: one request with `count=20000` returned 50 people, no error, no 429.

### [x] T4. Send a text message (2026-11-03 - Luca ran `--send-cloud`)

- **Result:** `--send-cloud` was run on the live server with a secondary account: one `sms` request was accepted, and the message appeared in "My Documents" (the new name of `Cloud của tôi`).
- Scope: direct messages only (`/api/message/sms`) into "My Documents" (`send2me_id`). `SendTextAsync` + the write path `PostEncryptedAsync` (no failover, no retry).
- Criteria: the payload test matches the crypto-js vector (passed). Luca tries it live with a secondary account, **one message into the account's own Cloud**, no bulk sending.

### T4b. Group send, quote, recall

- Group send (`/api/group/sendmsg`), quote (`/api/message/quote`), recall (`/api/message/undo`, `/api/group/undomsg`). Do this after T4 is confirmed on the live server.

### [x] T5a. Local message storage in SQLite (2026-11-04)

- Storage only: no network and no history sync. Package `Microsoft.Data.Sqlite` 10.0.13 in `Luna.Core`.
- API in `Luna.Core.Storage`: `IMessageStore` (`SaveAsync`, `GetPageAsync`, `ListConversationsAsync`), `SqliteMessageStore(string databasePath)` (`":memory:"` allowed for tests), `MessageStoreDefaults.DefaultPath` (`%LOCALAPPDATA%\Luna\history.db`), and the records `StoreResult`, `MessageCursor`, `StoredMessage`, `ConversationSummary` (the last two have a redacted `ToString`).
- `SaveAsync` is one transaction per batch (all or nothing). De-duplication is per thread by `msgId`, falling back to `c:` + `cliMsgId` when `msgId` is empty or `0`; the first copy wins. Recall notices are never stored: they mark the target message in the same thread as recalled and clear its text. A notice that arrives before the original message is lost (counted as skipped), and the matching rule is `[Source]`-level, not verified on the live server. Recalled rows keep only metadata (their text is cleared and their raw JSON is replaced by `{}`), and an original that arrives after its recall notice is inserted as a normal message (T5b must decide how to remember recalls).
- Paging is keyset-based on `(ts, id)` descending, limit 1 to 500; recalled messages are hidden unless requested. Conversations are grouped by `(thread type, thread id)` and counted without recalled messages.
- Decision (Luca): the database is **plain at rest** for now (no SQLCipher, no field encryption). It lives outside the repository, is ignored by `.gitignore` and is denied to agents. Message text, uids and thread ids never appear in exception messages, logs or `ToString`.

### [ ] T5b. Pull history from the server

- Status: probe coded (branch `t5b-history-probe`), waiting for the human run of `Luna.Playground -- --history-probe` with the secondary account. Nothing has been tried on the live server; the real pull is not started.
- Pull history over WebSocket (frames 510/511) or HTTP (`/api/group/history`); choose one and record the reason in `PROTOCOL.md`.
- Needs live shape runs with the secondary account first (run by Luca with `Luna.Playground`); the agent must not claim to have tried the live server.
- Feed the result into `IMessageStore.SaveAsync`; respect the per-endpoint intervals and the 429 cooldown from T3d and T3e.

### [ ] T5c. Message sync from the phone (research first)

- Goal (Luca): older messages that exist only on the phone appear in the desktop app.
- Two possible mechanisms: (1) pull what the server still keeps (T5b: the history frames and `/api/group/history`); (2) the official phone-to-desktop sync, which is undocumented and not in `docs/PROTOCOL.md`.
- First read zca-js and ctdozlite for any sync or backup call (`scripts/ref-grep.ps1`), write down what is found with its evidence marker (`[Source]`), and decide the mechanism with Luca. Nothing here has been tried on the live server, and the agent must not claim to have tried it.
- Before importing large histories decide two things with Luca: encryption at rest for `history.db` (today it is plain, see T5a) and the request budget (how many history requests per run and per hour, on top of the per-endpoint intervals and the 429 cooldown from T3d and T3e).
### [x] T6. Split out the WebSocket I/O so it can be tested

- Put `ClientWebSocket` behind an interface (`IWebSocketConnection`) to test the reconnect loop: backoff, address switching, stopping when opened twice (`3000`), clean cancellation, ping sent only after authentication.
- Criteria: tests use a fake connection, with no long real `Task.Delay` (inject time).
- Done: `SocketClient` takes an optional connection factory and delay; `ClientWebSocketConnection` is a pass-through adapter. `SocketClientReconnectTests` drive the loop with a scripted fake connection and a fake delay (backoff 2..30 s and its reset, endpoint rotation, ping, duplicate stop, cancellation, close, bad frames), checked with mutation runs.
- Not covered: the adapter `ClientWebSocketConnection` is the only untested pass-through, and a real-server run (Playground normal mode) is still the integration check.

### T7. WinUI 3 interface

- MVVM with `CommunityToolkit.Mvvm` (approved). Logic lives in view models in `Luna.Core/ViewModels` (namespace `Luna.Core.ViewModels`), free of any UI framework and of user-facing text; they expose states and flags and the view maps them to localized resources. They are tested in `Luna.Core.Tests`; XAML is only the interface.
- Split into steps:
  - [x] **T7a-1. Login view models (no UI).** Done: `IAuthService` extracted from `AuthService`, `IUiDispatcher`, `LoginViewModel` (restore, then QR login; states `LoginState`, failures `LoginFailure` with the numeric server code only; single-flight start, cancel, dispose) and `ShellViewModel` (Login page until signed in, then Home), with tests using a scripted fake service and immediate and queueing dispatchers.
  - [x] **T7a-2. Login screen.** Done (2026-11-05, checked by the maintainer with the secondary account: QR sign-in, cancel and retry, session restore, window size per page): QR login page presenting `LoginViewModel`, Vietnamese strings in `Luna.App/Strings/vi-VN/Resources.resw` with a guard test, `WinUiDispatcher`, `LoginText`, a bounded `crash.log`, and the composition root in `Luna.App` (`AuthService`, `EncryptedFileSessionStore`, `ShellViewModel`).
  - [x] **T7b-1. Home view models (no UI).** Done: `contacts` table (schema v2) with `IContactStore`; `FriendDirectory` (one guarded `getfriends` load: the call is recorded before the request, a 10-minute minimum interval and the 60-minute block after a 429 both live in `ApiThrottleGuard`, never retried) over `ApiFriendListSource`, whose client has no interval of its own so the guard is the only judge; `LiveSyncService` over `IRealtimeSource` (`SocketRealtimeSource` is the thin adapter over `SocketClient`): one connection at most, no automatic restart after a duplicate connection or a failed source (only an explicit `Reconnect`), the socket callback only queues a batch and one writer saves it, safe shutdown order; `ConversationItem`, `ConversationListViewModel` (version counter, one atomic assignment) and `HomeViewModel` (stored list first, then the live feed, then the guarded friend load); `ShellViewModel` builds and owns the home through a `Func<AuthSession, HomeViewModel>`. All covered by tests with fakes; the live server was not touched.
  - [x] **T7b-2. Home view.** Done except the maintainer's live check (not run by the agent): `ChatTimeFormat`, `HomeViewModel` owned resources, the home strings with a guard test for every sync and friends state, `HomeText`, `ConversationRow`, `HomeView` (two columns, selection kept across refreshes by conversation key, idempotent `Attach`/`Detach`), the composition root with one `SqliteMessageStore` (a failed open leaves the home page on its unavailable line) and the window closing the shell before the login and the store. The original scope was: the WinUI part of the conversation list: `HomeView` presenting `HomeViewModel` (it calls `Home.StartCommand` when it loads; the shell does not), Vietnamese strings in `Resources.resw` (friend and live-sync states, the unnamed-conversation fallback, the empty and error states), the composition root in `Luna.App` (`SqliteMessageStore` at `MessageStoreDefaults.DefaultPath`, `ApiThrottleGuard`, `ApiFriendListSource`, `SocketRealtimeSource`, the factory passed to `ShellViewModel`; the owner of the store and the friend source disposes them), and the maintainer's live check with the secondary account (the conversation list shows, a message sent to the account appears in the list, a second login elsewhere shows the duplicate-connection state and the reconnect button works, one `getfriends` request per run).
  - **T7c. Chat pane.** The message list must be **virtualized** and smooth with tens of thousands of messages, loading more when scrolling up. Support dark/light, DPI, Vietnamese input (IME). Split into two steps:
    - [x] **T7c-1. Chat pane, read only.** Done except the maintainer's live check (not run by the agent): it only READS the local database (no server call, no retry, no sending, no input box) and shows what is stored, that is, messages received while the app ran. `Luna.Core/Chat`: `MessageKind` and `MessageKinds.Classify`, `MessageItem`, `MessageKey` and the pure `ChatMerge` (Replace, Insert at the sorted `(timestamp, id)` place, Reset only for a possible gap), `ChatViewModel` (one `ObservableCollection` edited in place through the dispatcher, version-guarded results, single-flight open, refresh and load-older, the open conversation opened again is a no-op), `HomeViewModel.Chat` and `SelectConversationAsync` (the chat refreshes in the same loop as the list). `IMessageStore.GetPageAsync` got `includeRaw` (when false `raw_json` is never read). `Luna.App`: `ChatText`, `MessageRow`, `ChatView` (virtualized list, bubbles, a "load older messages" button, empty and error lines), Vietnamese strings with a guard test for every kind and state. Not verified without a window: scroll behavior when items are inserted above or below (`KeepLastItemInView`), bubble widths, selection restore, the light theme.
    - [x] **T7c-2a. Infinite scroll and test data.** Done except the maintainer's live check (not run by the agent): older messages load by themselves when the list is scrolled near the top (`ChatScrollPolicy` in `Luna.Core`, with a fuse against runaway automatic loads), replacing the button; a loading chip and a start-of-history line show the state; the Vietnamese strings are updated. For testing with a large list, `Luna.Core/Dev/SyntheticHistory` builds a fake history through the real `MessageParser`, the Playground mode `--seed-history <file> <count> [--force]` writes it to a file outside `%LOCALAPPDATA%\Luna` (offline, counts only), and Debug builds of the app open that file when `LUNA_HISTORY_DB` is set. Not verified without a window: scroll position when pages are added above, and the behavior of the fuse with a real scroll bar.
    - [ ] **T7c-2b. Chat pane, the rest.** Open: day separators, other message kinds (images, files, stickers shown for real, T9), a trimming policy for the older pages kept in memory, and the narrow master-detail mode (the right column is hidden below 700 px today). Vietnamese input (IME) belongs to the input box, which comes with sending.

### T8. Desktop experience

- Toast with quick reply, unread badge, taskbar flash, tray, paste images from the clipboard, drag and drop files, remember the window position.

### T9. Media

- Images, files, stickers (cache), upload and download.

### T10. Release

- MSIX or unpackaged packaging (ask Luca), code signing, auto-update, error reporting **only when the user agrees** and with secrets filtered out, a multi-hour soak test (RAM, handles), compare RAM against the official Electron app.
- Voice and video calls come **last** (the heaviest: Opus, WASAPI, ZRTP).

## Risks and unknowns

- An unofficial client: the account may be locked. All experiments use a secondary account.
- The protocol changes over time (especially `zpw_ver`) and needs periodic monitoring.
- The meaning of `cmd 621`, API rate limits, the behavior of switching WebSocket addresses: see section 10 of `PROTOCOL.md`.
