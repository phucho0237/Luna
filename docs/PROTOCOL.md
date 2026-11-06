# Web/PC protocol: consolidated knowledge

Confidence labels:

- **[Live]** verified on the live server with a secondary account, using this project's code.
- **[Source]** taken from the zca-js 2.2.0 or ctdozlite source, not yet run for real.

Reference sources: zca-js 2.2.0 (`npm pack zca-js@2.2.0`, the files `dist/apis/login.js`, `loginQR.js`, `listen.js`, `utils.js`) and `ctdoteam1110/ctdozlite` (Rust: `param_cipher.rs`, `socket.rs`, `session.rs`).

## 1. Common parameters

- `zpw_type=30`, `zpw_ver=694` **[Live]** (the server accepts them for `getLoginInfo` and the WebSocket). This number changes over time and lives in `ClientContext.ApiVersion`. To check the current number: open `chat.zalo.me` with a secondary account and look at the Network tab. zca-js defaults to 685.
- ctdozlite notes that the voicecall endpoints of the official desktop app use `zpw_ver=693&zpw_type=24` **[Source]**.
- Fixed headers: `Origin: https://chat.zalo.me`, `User-Agent` (kept by the context, **must stay the same between runs**).
- IMEI = `uuid` + `-` + `md5(userAgent)` (lowercase), generated once and then stored with the session **[Live]**.

## 2. Hosts

- Only `wpa.chat.zalo.me` (login) and `id.zalo.me` (QR) are fixed.
- Every other host is taken **dynamically** from `zpw_service_map_v3` in the `getLoginInfo` response **[Live]**: 45 services were seen, including `chat`, `group`, `group_poll`, `friend`, `profile`, `conversation`, `sticker`, `voice_call`, `zcloud`, `file`, `reaction`, `label`... Each service is an array of addresses. Do not hard-code hosts.
- `zpw_ws` is an array of 7 WebSocket addresses, for example `ws1-msg.chat.zalo.me` **[Live]**.

## 3. The `signkey` signature **[Live]**

`signkey = md5("zsecure" + tag + concat(values, in order of sorted key names, ordinal comparison))`, lowercase.

- `tag` = the command name in lowercase with hyphens removed (`getlogininfo`, `getserverinfo`).
- `getLoginInfo` is signed over exactly 6 fields: `client_version, enc_ver, params, type, zcid, zcid_ext`. `nretry`, `zpw_ver`, `zpw_type` are appended to the URL **after** signing and are not part of the signature.
- `getServerInfo` is signed over `imei, type, client_version, computer_name`, and does **not** include `zpw_ver/zpw_type`.

## 4. ParamCipher v2 (login, when there is no `zpw_enk` yet) **[Live]**

- `zcid` = uppercase hex of AES-256-CBC (16-byte zero IV, PKCS7) of `"{type},{imei},{firstLaunchTime}"`, with the static key being the ASCII string `3FC4F0D2AB50057BCE0D90D9187A22B1` (use all 32 characters as the key).
- `zcid_ext`: random hex, 6 to 12 characters long.
- `encrypt_key` (32 ASCII characters) = the first 8 characters of the even positions of uppercase `MD5(zcid_ext)` + the first 12 characters of the even positions of `zcid` + the first 12 characters of the odd positions of `zcid`, **reversed**.
- `params` = Base64(AES-256-CBC(encrypt_key UTF-8, IV 0, PKCS7)(JSON payload)). The `getLoginInfo` response is decrypted with this same `encrypt_key`, **not** with `zpw_enk`. The `data` field may be URL-encoded.
- `getLoginInfo` payload: `{computer_name:"Web", imei, language:"vi", ts}`.

## 5. QR login (`id.zalo.me/account`) **[Live]**

1. GET the login page and get the version with the regex `https://stc-zlogin\.zdn\.vn/main-([\d.]+)\.js`.
2. POST forms: `logininfo`, `verify-client` (`type=device`), `authen/qr/generate` (gives `data.code` and `data.image`, a PNG as a data URI).
3. Long-poll `authen/qr/waiting-scan` and then `waiting-confirm` (`gAction=CONFIRM_QR`, `gToken=""`). `error_code 8` = waiting, call again. `-13` = the user declined. The QR code lives for about 100 seconds. A successful `waiting-confirm` returns `data.chatUid`.
4. GET `checksession`. **You must follow the redirects yourself** (a chain of 302s leading to `chat.zalo.me/index.html`), because the session cookies are set along the way.
5. GET `https://jr.chat.zalo.me/jr/userinfo`, which needs `data.logged == true`.

**Cookies must be handled manually**: `zpw_sek` is set by `id.zalo.me` but declares `Domain=chat.zalo.me`. .NET's default cookie mechanism will drop it. The code reads `Set-Cookie` itself, accepts only service domains, and attaches the cookie to the declared domain. Cookies seen: `zpw_sek@chat.zalo.me`, `_zlang`, `zpsid`, `__zi` (both `zalo.me` and `zaloapp.com`), `zpdid` and `zlogin_session` (`.id.zalo.me`).

## 6. Session restore **[Live]**

The stored cookies + IMEI + User-Agent are enough to call `getLoginInfo` again, with no QR scan. If the server rejects them, `getLoginInfo` throws `ApiException`.

## 7. WebSocket

- URL: `{zpw_ws[i]}` + `/` if the path is missing (the server returns 501 with an empty request line) + `?zpw_ver=..&zpw_type=..&t=<ms>` **[Live]**.
- Headers: `Origin`, `User-Agent`, `Cookie` (the cookie string of `https://chat.zalo.me`) **[Live]**.
- Binary frame: `[version u8][cmd u16 little-endian][subCmd u8][JSON UTF-8]` **[Live]**.
- The server speaks first: `cmd 1 / sub 1` with `{key, encrypt, data}`. `key` is the Base64 of a 32-byte AES-GCM key. `data` decrypts to `{error_code, data:{qCmds[], ctrl_qCmds[], bk_cmds}}` **[Live]**. `error_code != 0` means rejected.
- The `encrypt` field of every frame: 0 plain, 1 Base64 + compression, 2 Base64 (URL-decode first) + AES-GCM + compression, 3 like 2 but without compression **[Live]**.
- AES-GCM block = `iv(16) | aad(16) | ciphertext||tag(16)`, with a **16-byte** IV (.NET `AesGcm` cannot be used, BouncyCastle must be used) **[Live]**. Compression may be gzip, zlib or raw deflate and must be detected automatically.
- Ping: `cmd 2 / sub 1`, body `{eventId:<ms>}`, every 15 seconds after authentication. No disconnect has been seen during long continuous runs; the maximum endurance has not been tested separately.
- `cmd 3000 / sub 0`: the account is open elsewhere. The client closes with code 3000 and does **not** reconnect by itself **[Source]**. ctdozlite notes that `3002 NOT_FOUND_WORKER` means this shard does not hold the session and the address must be switched **[Source]**.

### Events seen live

All carry `data` as an object with the keys `lastActionId, more, msgs[], groupMsgs[], pageMsgs[], clearUnreads[], delivereds[], seens[], groupSeens[], queueStatus, eesession[]` (depending on the command, only some keys hold data):

| cmd | Meaning | Notes |
|---|---|---|
| 1 | authentication | **[Live]** |
| 501 | direct messages (`msgs`) | **[Live]** |
| 521 | group messages (`groupMsgs`) | **[Live]** |
| 502 | `delivereds` (direct): the message was delivered to the recipient; `seens`: the other person has seen it | **[Live]** |
| 522 | `delivereds` (group); `groupSeens`: someone in the group has seen it | **[Live]** |
| 504, 524, 613 | `clearUnreads`: the conversation is marked as read | **[Live]** (meaning inferred from the key name); the shape of 524 was collected on 2026-11-03 |
| 601 | `controls`: control events (group, friend, pin, mute, emoji status, reminders) | **[Live]**; for the content shape see the section "Contents of `controls`, `seens` and `groupSeens`"; `file_done` (upload finished) only per zca-js **[Source]** |
| 602 | `actions`: typing (`typing`, `gtyping`) | **[Live]** |
| 612 | `reacts`, `reactGroups`: reactions | **[Live]** |
| 621 | has `error_message`, `data`, `error_code` | meaning **unclear** |
| 610, 611 | reactions (per zca-js) | [Source], not seen |
| 510, 511 | message history (pull) | [Source] |

### Other event shapes (T2a, collected on the live server on 2026-11-02)

Only field names and types are recorded. Numeric or identifier-like keys are masked as `<key>`. Collected through these actions: receiving a direct message, sending a message from another device of the same account, a half-typed message, reactions (direct and group), group events, a friend request, reading a conversation.

Common fields in `data` of the events that have a queue (501, 601, 612, 613): `lastActionId: string`, `more: number` and `queueStatus: { <numeric key>: { ids: [string], lastId: string } }` (601 has two keys, one with an empty `ids`) **[Live]**. The events 502, 504, 522 have only `more: number` **[Live]**.

| cmd | Key in `data` | Element shape | Notes |
|---|---|---|---|
| 501 | `msgs` | see "Message model" | **[Live]** |
| 502 | `delivereds` | `{ msgId: string, seen: number, deliveredUids: [string], seenUids: [], realMsgId: string, mSTs: number }` | **[Live]** |
| 522 | `delivereds` | like 502 plus `groupId: string` | **[Live]** |
| 502 | `seens` | `{ idTo: string, msgId: string, realMsgId: string }` | **[Live]** (2026-11-03) |
| 522 | `groupSeens` | `{ msgId: string, groupId: string, seenUids: [string] }` | **[Live]** (2026-11-03); one event held 6 elements |
| 504 | `clearUnreads` | `{ idTo: string, lastMsgId: string, isGroup: number, type: number, ts: string }`, one variant additionally has `lastCliMsgId: string` and `sct: number` | **[Live]**, meaning inferred from the key names |
| 613 | `clearUnreads` | `{ idTo, lastMsgId, lastCliMsgId: string, isGroup, type: number, ts: string }` | **[Live]**, unclear how it differs from 504 |
| 524 | `clearUnreads` | `{ idTo: string, lastMsgId: string, isGroup: number, type: number, ts: string }` (without `lastCliMsgId` and `sct`) | **[Live]** (2026-11-03) |
| 602 | `actions` | `{ act_type: string, act: string, data: string }` | **[Live]**, for the `act_type` and `act` values see the table in the section below |
| 612 | `reacts`, `reactGroups` | `{ actionId, msgId, cliMsgId, msgType, uidFrom, idTo, content: string, ts: string, ttl: number }` | **[Live]**, `reactGroups` has the same shape, direct or group inferred from the key name |
| 601 | `controls` | `{ actionId: string, controlId: string, content: {...} }`, see below | **[Live]** |
| 621 | no array | `data` is an object `{ error_message: string, data: string, error_code: number }` | **[Live]**, meaning unclear |

`controls[].content` has 4 shapes seen: (a) `{ flag: number, act: string, data: string, ts: number, act_type: string }`; (b) `{ act: string, data: string, act_type: string, ts: number }`; (c) `{ act_type: string, act: string, ts: number, data: { ts_clicked: number } }` with `data` as an **object**; (d) `{ data: string, act_type: string, act: string }` without `ts`. So `content.data` can be a string or an object. The `act_type`/`act` pair of each kind is in the table in the section below (T2b); the detailed shape of `content.data` for each kind awaits T2c.

### act_type / act values (T2b, collected on the live server on 2026-11-02)

Printed only through `JsonShape.SafeToken`. Seen **[Live]**:

| cmd | act_type | act | When |
|---|---|---|---|
| 602 | `typing` | `typing` (direct chat), `gtyping` (group) | typing |
| 601 | `group` | `update`, `join`, `remove_member`, `update_avatar`, `update_board` | group renamed, member added, member removed, group photo changed; `update_board` comes with creating a reminder (inferred from the event order, not certain) |
| 601 | `fr` | `req_v2`, `undo_req`, `req`, `add`, `remove` | sent a request, cancelled a sent request, received a request from another account, became friends, unfriended |
| 601 | `oneone` | `remove_reminder11` | deleted a reminder in a direct chat |
| 601 | `emoji_status` | `add` | set an emoji status |
| 601 | `pinned_convers` | `update` | pinned a conversation |
| 601 | `mute` | `set`, `unset` | muted, unmuted |
| 601 | `contact_tab` | `clicked` | clicked the contacts tab (meaning inferred from the name, 2026-11-03) |

Not seen in the runs: `file_done`, `join_reject`, `update_setting` and the other group `act` values that zca-js lists **[Source]**. How to handle them: treat `act_type` and `act` as open keywords; an unknown pair is not classified and never causes an error.

### Contents of `controls`, `seens` and `groupSeens` (T2c, collected on the live server on 2026-11-03)

Only field names and types are recorded. `content.data` has **three forms**: an object, a string containing JSON (must be parsed a second time), or a plain string that is not a JSON object or array. Some ids in `data` are **JSON numbers** (for example `fr/req.uid`): they must be read with `GetRawText()` so no precision is lost.

| act_type | act | Outer shape of `content` | `data` is | Shape of `data` |
|---|---|---|---|---|
| `group` | `update`, `join`, `remove_member`, `update_avatar` | `{ flag: number, act: string, data: string, ts: number, act_type: string }` | JSON string | see below |
| `group` | `update_board` | `{ flag: number, act: string, data: object, act_type: string }` (without `ts`) | object | `{ sourceId: string, groupName: string, groupTopic: {...}, groupId: string, creatorId: string }` |
| `fr` | `req_v2` | `{ act: string, data: object, act_type: string, ts: number }` | object | `{ toUid: string, fromUid: string, src: number, message: string }` |
| `fr` | `undo_req` | like `req_v2` | object | `{ toUid: string, fromUid: string }` |
| `fr` | `req` | `{ act: string, data: string, act_type: string, ts: number }` | JSON string | `{ uid: number, src: number, message: string }` |
| `fr` | `add`, `remove` | like `req` | plain string | unclear (may be a bare uid) |
| `mute` | `set` | `{ data: string, act_type: string, act: string }` (without `ts`) | JSON string | `{ toId: string, muteType: number, duration: number, startTime: number, systemTime: number, advanced: { reminder: number } }` |
| `mute` | `unset` | like `set` | JSON string | like `set` but `advanced: null` |
| `pinned_convers` | `update` | `{ act: string, data: object, act_type: string }` (without `ts`) | object | `{ tab: number, action: number, pinIds: [string], ts: number }` |
| `contact_tab` | `clicked` | `{ act_type: string, act: string, ts: number, data: object }` | object | `{ ts_clicked: number }` |

The `data` shape of groups (`group/update`, `join`, `remove_member`, `update_avatar`) is cut at 300 characters, so **only the beginning is available**:

- Common: `subType: number, groupId: string, creatorId: string, groupName: string, sourceId: string, updateMembers: [...]`.
- `updateMembers` is empty for `update` and `update_avatar`; for `join` and `remove_member` it has elements `{ id: string, dName: string, avatar: string, type: number, avatar_25: string }`.
- `update`, `join` and `remove_member`: `groupSetting: { blockName, signAdminMsg, addMemberOnly, setTopicOnly, enableMsgHistory, joinAppr, lockCreatePost, lockCreatePoll, ... }` (numbers), the rest is cut off.
- `update_avatar`: `groupSetting`, `groupTopic`, `info`, `extraData` are all `null`; then `time: string, avt: string, fullAvt: string, isAdd: number, hideGroupInfo: number, version: string, groupType: number, tota...` (cut off).

`group/update_board`: `groupTopic: { editorId: string, emoji: string, color: number, groupId: string, creatorId: string, editTime: number, eventType: number, repeatRule: [], responseMem: { rejectMember: number, acceptMember: number, myResp: number }, params: string, type: number, onlyMe: number, duration: number, repeatData: [], createTime: number, repeat: number, startTime: number, id: string }`. `params` is a string (it may hold the reminder content).

`pinned_convers/update`: `action` is a number (value not collected; both pinning and unpinning produce this event).

Direction of `fr/*` (confirmed on 2026-11-03 with a controlled sequence of actions between two accounts) **[Live]**: `req_v2` is a request sent by this account, `undo_req` is cancelling a sent request, `req` is a request sent to us by another account (`uid` is the sender's JSON number), `add` arrives when the two sides become friends, `remove` is unfriending. It is not yet possible to tell which side accepted when an `add` arrives. The meaning of `fromUid` and `toUid` follows the field names; the values were not cross-checked.

### Other findings and what has not been seen (T2a, T2b, T2)

**[Live]**:

- A direct message sent from another device of the same account gives `IsSelf = True` (it is not known whether `uidFrom` is `"0"` or the real uid). Seen in both the `--shape` and `--events` runs.
- `webchat` may have `content` as an attachment-style object (`title, description, href, thumb, childnumber, action, params, type`), not only a string.
- `msgType = chat.ecard` appears at the same time as friend events in a direct chat. The parser handles it (it does not fall into `[Skipped]`), and the `content` shape is the familiar attachment shape (`title, description, href, thumb, childnumber, action, params, type`) **[Live]**. On 2026-11-03 `chat.ecard` also appeared in a group with `IsSelf = True`, at the same time as `group/update_board` (inferred from the event order).
- `controls` (cmd 601) has at least 5 shape variants; the new variants are not yet recorded in detail (T2c).
- Cmd 621 always arrives right after the connection is established, before any other event. Its meaning is still unclear.
- Playground `--events` (T2): after a long sequence of actions only cmd 522 (with `seens` or `groupSeens`) and cmd 621 remain `UnknownEvent`; no element was skipped.

**Not seen**: cmd 610 and 611; no element of `pageMsgs` or `eesession` has been seen; `file_done`, `join_reject`, `update_setting`; in the run of 2026-11-03 `emoji_status` and `oneone` were not seen (they were seen on 2 Nov but have no shape yet). **No full shape yet**: the tail of `data` of the group events (see the section above).

### Typed event routing (`EventRouter`, T2)

`Luna.Protocol/Events/EventRouter.Route(SocketEvent)` returns `RouteResult(Events, Skipped)`. It skips cmd 1, 2. A bad element (missing field, wrong type) is dropped and counted in `Skipped`; it never throws. No event's `ToString()` prints a uid, conversation id or content.

| cmd | Array | Event |
|---|---|---|
| 501, 521 | `msgs`, `groupMsgs` | `MessageReceived` (the account's own messages from another device are also emitted, `IsSelf = true`); a recalled message becomes `MessageUndone` |
| 502, 522 | `delivereds` | `DeliveryReceipt` |
| 502, 522 | `seens` (502: `idTo`, `msgId`, `realMsgId`), `groupSeens` (522: `msgId`, `groupId`, `seenUids`) | `SeenReceipt` |
| 504, 524, 613 | `clearUnreads` | `UnreadCleared` (524 has the same shape as 504, collected on 2026-11-03) |
| 612 | `reacts` (direct), `reactGroups` (group) | `ReactionReceived`, `content` kept as a string |
| 602 | `actions` | `TypingNotice` when `act_type = typing` (`act` = `typing` or `gtyping`); any other action becomes `UnknownEvent` |
| 601 | `controls` | `FriendEvent` (`fr/*`), `MuteChanged` (`mute/set`, `mute/unset`), `PinnedConversationsChanged` (`pinned_convers/update`); the rest become `ControlEvent` (`Kind` by `act_type`, including `ContactTab`; `Content` kept as is). `group/*` is not parsed yet (T2c-2) |
| other, 621, `pageMsgs`, wrong-type arrays, `seens` on a cmd other than 502, `groupSeens` on a cmd other than 522 | | `UnknownEvent` (cmd, subcmd, payload) |

Cmd 601 (T2c-1): the typed event **replaces** `ControlEvent` when `data` can be parsed; unexpected `data` or missing fields still emit `ControlEvent` and are not counted in `Skipped`. `data` is read in three forms: an object, a string containing JSON (second parse), a plain string (only `fr/add` and `fr/remove` accept it, not parsed). A corrupt string (lone surrogate, nesting deeper than 64 levels) also falls back to `ControlEvent` and does not throw. `fr/req.uid` is a JSON number kept verbatim (even if larger than `long.MaxValue`). **[Live]** (2026-11-03, Playground `--events` with a secondary account): the friend sequence (send, cancel, receive, accept, unfriend) produced 5 `FriendEvent`, muting then unmuting produced 2 `MuteChanged`, pinning and unpinning produced 2 `PinnedConversationsChanged`, reading messages in a direct chat and a group produced 2 `SeenReceipt`; no element was skipped and `UnknownEvent` was left with only cmd 621. Not verified on the live server: the meaning of `tab` and `action` of `pinned_convers`, the unit of `duration` and `startTime` of `mute`, the accepting side of `fr/add`.

`TypingNotice`: `data` is a JSON string; zca-js wraps it as `JSON.parse("{" + data + "}")`. The router also accepts the form that already has braces. Confirmed **[Live]** (2026-11-03): `typing` has `uid`, `ts` (string), `isPC` (number); `gtyping` additionally has `gid`. The server does not send a typing event in a group of only two people **[Source]** (a note in zca-js).

### Message model (`msgs[]` and `groupMsgs[]`)

The first part (field list, `uidFrom` convention) follows zca-js **[Source]**; the "Shapes observed on the live server" part below is verified **[Live]**.

Fields: `actionId, msgId, cliMsgId, msgType, uidFrom, idTo, dName, ts, status, content, notify, ttl, userId, uin, topOut, topOutTimeOut, topOutImprTimeOut, propertyExt{color,size,type,subType,ext}, paramsExt{countUnread,containType,platformType}, cmd, st, at, realMsgId, quote`. Group messages additionally have `mentions[{uid,pos,len,type}]`.

- `content` is a string (text) **or** an object (attachment: `title, description, href, thumb, childnumber, action, params, type`, or another form). If `content.deleteMsg` is present, it means the message was **recalled** (undo).
- Direct message: `uidFrom == "0"` means the message was **sent by the account itself**; then the counterpart is `idTo`. `threadId` = `uidFrom == "0" ? idTo : uidFrom`. `idTo == "0"` means self.
- Group message: `threadId = idTo`; `uidFrom == "0"` is self.
- `quote`: `ownerId, cliMsgId, globalMsgId, cliMsgType, ts, msg, attach, fromD, ttl`.
- Every id is a large number: keep it as a string.

Shapes observed on the live server (T1b, data types only, no values) **[Live]**:

- `msgType` values seen: `webchat` (text), `chat.sticker`, `chat.photo`, `share.file`, `chat.undo` **[Live]**. The constants are in `MessageTypes`.
- Text message: `content` is a string; `propertyExt` is `{ color, size, type, subType: number; ext: string }`. Group messages additionally have `mentions[{ uid: string, pos, len, type: number }]` **[Live]**.
- `chat.sticker`: `content = { id: number, catId: number, type: number }`. The parser picks `StickerContent` by `msgType` and keeps the id as a string **[Live]**.
- `chat.undo`: `content = { globalMsgId, cliMsgId, deleteMsg, srcId, destId }`, **all numbers** (not strings). The parser reads them with `GetRawText()` so no precision is lost **[Live]**.
- `chat.photo` and `share.file`: `content = { title, description, href, thumb: string; childnumber: number; action, params, type: string }`. `chat.photo` also has an outer-level field `previewThumb: string` (outside `content`, mapped to `ChatMessage.PreviewThumb`) **[Live]**.
- `quote`: `{ ownerId, cliMsgId, globalMsgId: string; cliMsgType: number; ts: number; msg, attach, fromD: string; ttl: number }`. Note that `ts` is a **number**, not a string **[Live]**.
- An object-valued `content` that has none of the keys `title/description/href/thumb/params` (and is not a sticker or undo) is kept as `UnknownContent` with `Raw` intact; it is not guessed to be an attachment.
- Verified: a direct message sent by the account itself from another device gives `IsSelf = True` (it is not known whether `uidFrom` is `"0"` or the real uid). Not verified on the live server: voice messages, video, contact cards, location.

### Old messages (T5b) **[Source]**

**Not verified on the live server.** Everything here comes from zca-js 2.2.0 **[Source]**; the maintainer changes the marker to **[Live]** after running `Luna.Playground -- --history-probe`.

- Request: a WebSocket frame version 1, cmd `510` (private chats) or `511` (groups), subCmd `1`, JSON body `{"first": true, "lastId": <message id string or null>, "preIds": []}`. `SocketClient.RequestOldMessages(HistoryScope, lastMessageId)` builds it; `lastId` is null for the first page, otherwise 1 to 20 digits.
- Response: a frame cmd `510` or `511`, subCmd `1`; the decoded data has `msgs` (private) or `groupMsgs` (groups), shaped like the live messages above.
- The request carries no thread id, so it seems to return a stream of recent messages of the whole account, paged by `lastId` (the id of the oldest message of the previous page).
- Unknown until the probe runs: how far back the server keeps messages, the page size, whether `lastId` pages overlap or continue older, whether history messages use `uidFrom == "0"` or the real uid for the own account, and any rate limit. Treat these as open questions.
- The probe (`HistoryProbeSummary`, `ProbePlan`) asks at most 3 pages per scope, 5 seconds apart, stops after 90 seconds or on any error, and runs at most once per 30 minutes. It prints counts, day ranges and field names only; it saves nothing.
- Writes on the socket are serialized (one send at a time) and a queued request is dropped, never retried, when the connection drops.

## 8. Post-login API

- Parameters are encrypted with `SessionCipher` using `zpw_enk` (Base64 of an AES-128 key, IV 0). Response: `data` is decrypted with the same key (URL-decode first).
- Send a direct message: `{chat service}/api/message/sms`. Group message: `{group service}/api/group/sendmsg`. Quote: `/api/message/quote`. Recall: `/api/message/undo` (direct) and `/api/group/undomsg` (group). Friend list: `/api/social/friend/getfriends`. Last messages of conversations: `/api/preloadconvers/get-last-msgs` (per ctdozlite). Group history: `/api/group/history`. Sticker: `/api/message/sticker/...`.
- **Calling `getfriends` (`ApiClient`, run on the live server on 2026-11-03 with Playground `--friends` and a secondary account)** **[Live]**:
  - `GET {profile[i]}/api/social/friend/getfriends?params=<Base64 AES>&zpw_ver=..&zpw_type=..`, with **no** `signkey`. The host comes from `zpw_service_map_v3["profile"]`.
  - Payload (compact JSON, exact key order): `{"incInvalid":1,"page":P,"count":C,"avatar_size":A,"actiontime":0,"imei":"<imei>"}`. The reference vector from crypto-js matches byte for byte (see `GetFriendsTests`).
  - Two-layer response: the outer `{error_code, error_message, data}` with `data` being an encrypted string (may be URL-encoded); `error_code` can be a number or a string. Decrypting `data` gives an inner `{error_code, data}`; the result is the inner `data` field. A missing `error_code` at the inner layer throws `InvalidDataException`, like the outer layer. The live run had `error_code` at the inner layer (if it had been missing, an error would have been thrown) **[Live]**.
  - Before any network call, every host in the service must be an absolute `https` URL with no userinfo, whose host is exactly `zalo.me`/`zaloapp.com` or ends in `.zalo.me`/`.zaloapp.com`; otherwise `InvalidDataException` is thrown, with no call and no failover to another host.
  - Host URL building rules (T3b): the host must be a DNS name (no IP, no IPv6), default port, empty path or only `/`, no query, no fragment, no trailing dot. The URL that is sent is always built from the normalized form `https://` + lowercase host + path, never from the original string in the ServiceMap.
  - The `Friend` model (T3b, `Luna.Protocol/Api/Friend.cs`): `UserId` (string, required; accepts a number or a string, keeps the original text), `Username`, `DisplayName`, `ProfileName`, `Avatar`, `PhoneNumber`, `Status`, `Gender`, `IsFriend` (`isFr`=1), `IsBlocked`, `IsActive`, `LastActionTime`, `LastUpdateTime`, `CreatedTs`. Missing or wrongly-typed fields become null/false. `dob`, `sdob`, `globalId`, `userKey`, `cover`, `bgavatar` are not taken. `ToString()` prints only `IsFriend` and `IsBlocked`. An element that is not an object, lacks `userId` or has a corrupt string is skipped and counted in `Skipped`.
  - `GetAllFriendsAsync` pagination (T3b): calls sequentially from `page=1` and stops when (a) a page is empty, (b) a page has no new `userId` after de-duplicating by `UserId` (if the page's `userId` set equals the previous page exactly, `PageParameterIgnored` is set, meaning the server appears to ignore `page`), or (c) `maxPages` is reached (`Truncated` is set when it stops at a non-empty `maxPages` page that still has new `userId` values without any end-of-list signal, meaning some friends may not have been fetched, regardless of the length of the last page). It deliberately does not stop just because a page is shorter than `pageSize`, because the server's `count` ceiling is unknown. Live results: see the survey sections below.
  - Rule for 112 (T3c): a `ApiException` with code 112 on page 2 or later (at least one page already succeeded, received at either the outer or the inner layer) is treated as THE END OF THE LIST: stop, do not throw, keep the results of the earlier pages, set `FriendListResult.EndedByNoData = true`; `Pages` counts the request that returned 112 and `Truncated = false`. 112 on the first page and every other error code on any page (including 429 and network errors) still throw. `EndedByNoData` is an inference from code 112, not proof that the list is complete: treat it as complete only when the real friend count is also known. The **[Live]** level applies only to the case seen (page 2 of `count=50` returned 112 for an account with about 50 friends); the meaning "past the end of the list" is not verified on the live server for a page in the middle of the list (112 has not been seen on a middle page).
  - Failover only on a network error, timeout or HTTP 5xx; 4xx (except 429) throws immediately; 429 throws `RateLimitedException` and is not retried by itself. Every request, including an attempt on a fallback host, is at least `MinInterval` apart (default **3 seconds** since T3d, measured from the start).
  - Rate limiting and cooldown after 429 (T3d): **[Live]** (2026-11-03) three consecutive `getfriends` requests 1 second apart: the third got HTTP 429; two requests 1 second apart had worked before. Two hypotheses, not yet distinguished: (a) a burst limit (with three requests close together the third is rejected); (b) a quota that accumulates over time (about 8 to 9 `getfriends` requests that day across several runs). The real threshold, the penalty time and the scope (account or IP address) are **unknown**. Because they cannot be told apart, both the minimum interval and the total number of requests per run must be low, and after a 429 wait at least one hour. Rule in `ApiClient`: on a 429 set the blocked-until time = now + cooldown (60 seconds, doubling on each consecutive 429, at most 15 minutes; one success resets it to 60 seconds); during the cooldown every call throws `RateLimitedException` immediately (with `RetryAfter`) and sends NO request, including an attempt on a fallback host. The remaining time is clamped to [0, current cooldown] so a clock set backwards does not lengthen the cooldown. The figures 60 seconds / 15 minutes are the client's cautious choice, not measurements from the server. Playground `--friends` and `--friends-all` also remember a one-hour cooldown between runs (a file outside the repo, holding only the time the cooldown ends).
  - **[Live]** (2026-11-03, second run): `--friends-all` with `MinInterval` of 3 seconds (`pageSize` 25): the first two requests succeeded, and the third got 429 again, exactly like the run with a 1-second interval. So raising the interval from 1 second to 3 seconds does not avoid the 429. Both times it was the third request of a run; the earlier runs (one to two requests each, a few minutes apart) did not get a 429. The most reasonable reading: a limit over a short time window that allows about two requests in a window longer than about 6 seconds (the hypothesis of a daily accumulating quota is weakened). The real window, the penalty time and the scope (account or IP address) are unknown. Temporary rule for each live run: at most two requests, and wait at least one hour after every 429.
  - The `page` parameter **[Live, inferred]**: page 2 of `count=20` and of `count=25` returned new people (the pagination loop continued to page 3), so `page` has an effect. A last page returning 112 or an empty array has not been seen.
  - Live result **[Live]** (2026-11-03): one request with `count=50`, `page=1` returned 50 elements, no error. Shape of the first element (names and types only): `{ userId: string, username: string, displayName: string, zaloName: string, avatar: string, bgavatar: string, cover: string, gender: number, dob: number, sdob: string, status: string, phoneNumber: string, isFr: number, isBlocked: number, lastActionTime: number, lastUpdateTime: number, isActive: number, key: number, type: number, isActivePC: number, isActiveWeb: number, isValid: number, userKey: string, accountStatus: number, oaInfo: null, user_mode: number, globalId: string, bizPkg: { label: null, pkgId: number }, createdTs: number, isEnterpriseAccount: number, oa_status: null }`.
  - Notes on `getfriends`: the fields that hold personal information (`userId`, `username`, `displayName`, `zaloName`, `avatar`, `bgavatar`, `cover`, `phoneNumber`, `status`, `dob`, `sdob`, `globalId`, `userKey`) must be hidden from `ToString()` and logs. Only the first element has been seen (other elements may lack fields or differ in type); `oaInfo` and `oa_status` are `null`, so their real types are unknown. Types and list length: see the survey right below.
  - Live survey **[Live]** (2026-11-03, `--friends-all`, secondary account): one request with `count=500`, `page=1` returned **50** elements, no error. An earlier run (`count=50`): page 1 returned 50 elements, page 2 returned error **112**. The two results fit a list of exactly 50 people with 112 being how the server reports no more data (zca-js also records 112 as an error for an empty list in `getSentFriendRequest` **[Source]**); Luca confirmed the account has about 49-50 friends (not counting official accounts), so the hypothesis that the server caps `count` at 50 is weakened but not fully ruled out (not rerun: see T3e). The `profile` service has only one host.
  - Survey of the 50 elements: every element has the same set of fields as the first element's shape (no element lacks a field). There is only one type variant: `bizPkg.label` is `null` (45 elements) or an `object` (5 elements). `oaInfo` and `oa_status` are `null` in all 50 (the real type when not `null` is unknown). All 50 have `isFr = 1`, `isBlocked = 0`, `isActive = 1`.
- Friend list in one request (T3e) **[Source]**: zca-js and ctdozlite both fetch the friend list with ONE `getfriends` request with `page=1`, `count=20000` at startup or when the user presses refresh, with no pagination; ctdozlite records a 429 when `getfriends` is called densely. `ApiClient.GetFriendListAsync` does exactly that (payload `{"incInvalid":1,"page":1,"count":20000,"avatar_size":120,"actiontime":0,"imei":"<imei>"}`, crypto-js vector in `GetFriendListTests`). `GetAllFriendsAsync` is for diagnostics only and is not used in the app.
- Per-endpoint interval (T3e): `ApiClientOptions.EndpointMinIntervals`, by default `/api/social/friend/getfriends` = 10 minutes between two CALLS (checked and recorded on the first attempt; a failover within the same call is not blocked). This is a client choice, NOT a measurement from the server. A call that comes too early throws `RateLimitedException` without sending anything and does not count as a 429.
- **[Live]** (2026-11-03, Playground `--friends-list`, secondary account): `GetFriendListAsync` (one `getfriends` request, `page=1`, `count=20000`, `avatar_size=120`) succeeded, with no error and no 429, returning all **50** elements (matching the real friend count of about 49-50). Field survey is the same as the `count=500` run: every element has the same set of fields, `bizPkg.label` is `null` (45) or an `object` (5), `oaInfo` and `oa_status` are `null` in all 50, and all 50 have `isFr = 1`, `isBlocked = 0`, `isActive = 1`. The real `api-throttle.json` file holds both `blockedUntilUtc` (kept as is) and `lastCallsUtc.getfriends`. Unknown: behavior with a very long list (thousands of friends), large response size, and the real limit of `getfriends` (this run had only one request, more than an hour after the previous one).
- Send a direct text message (T4) **[Live]** (2026-11-03, see the result bullet below; the payload and key order follow zca-js **[Source]**). `ApiClient.SendTextAsync`:
  - `POST {chat[0]}/api/message/sms?nretry=0&zpw_ver=..&zpw_type=..`, body `application/x-www-form-urlencoded` with one field `params=<Base64 AES>`, with **no** `signkey`. Two-layer response like `getfriends`.
  - Payload (compact JSON, exact key order): `{"message":"..","clientId":<epoch milliseconds, a NUMBER>,"imei":"<imei>","ttl":0,"toid":"<id>"}`. The crypto-js vector matches byte for byte (see `SendTextTests`; fake key and imei, `clientId` 1700000000000).
  - `data.msgId` can be a 19-digit NUMBER or a STRING (the vector has both): read it verbatim (`GetRawText`/string), never convert to `double`. If `data` or `msgId` is missing, `MsgId` is null but it is still a success because `error_code` is 0.
  - WRITE rule (`PostEncryptedAsync`): exactly ONE attempt on the first host, NO failover, NO retry, no loop. A network error, timeout or 5xx is thrown as is and means it is NOT KNOWN whether the request reached the server: never resend it automatically. The host check, the cooldown after 429 and `MinInterval` are still used as on the read path.
  - Per-endpoint interval: `/api/message/sms` = **60 seconds** between two calls (a cautious client choice, not a measurement from the server). Playground `--send-cloud` additionally blocks 10 minutes between runs (key `sendtext`).
  - Input validation (client choice, not verified): `toId` is 1-64 characters from `[A-Za-z0-9_-]`; `text` is 1-2000 UTF-16 units, with no lone surrogate and no control character except LF, CR, tab. The 2000 ceiling is not verified against the server.
  - `send2me_id` in the login data is the id of "My Documents" (formerly `Cloud của tôi`) **[Live]**; `LoginInfo.Send2MeId` (null if missing, empty or of the wrong type). The session stores only cookie/IMEI/UA, so `LoginInfo` is rebuilt every time the session is restored.
  - **[Live]** (2026-11-03, Playground `--send-cloud`, secondary account): one request `POST {chat[0]}/api/message/sms?nretry=0&zpw_ver=..&zpw_type=..` (the first host in the `ServiceMap`, with no need to force a specific host as ctdozlite does) was accepted by the server, and the message `Luna test HH:mmZ` appeared in "My Documents" (the new name of the same place) of the secondary account itself. So: the real login response DOES have `send2me_id`; `sms` accepts it as `toid`; the payload `{message, clientId (number), imei, ttl:0, toid}` with `nretry=0` and no `signkey` is accepted. The real `api-throttle.json` file has `lastCallsUtc.sendtext` next to the old keys. **Unknown:** the real shape of `data` in the response (whether there is a `msgId`, number or string; the Playground deliberately does not print it), whether the message is pushed back over the WebSocket as an event, the real rate limit of `sms`, the 2000 UTF-16 unit ceiling, and the behavior when sending to someone else or to a group.
- After authentication ctdozlite also sends a queue-pull frame for each pair in `ctrl_qCmds` and `qCmds`: body `{first:true, reqId:"req_N", lastId:1, preIds:[]}`. Cmd `533` (HAS_MSG) signals that there are messages to fetch.

## 9. Known error codes

`18022` wrong signature. `18060` invalid encryption protocol. QR: `8` waiting, `-13` declined. The rest are unknown: when you meet an unknown code, record it here.

## 10. Things to verify further

Whether the server changes `zpw_ver` (check periodically). The meaning of `cmd 621`. The behavior when the WebSocket address is switched on reconnect. API rate limits (`429` in the ctdozlite docs). The tail of `data` of the group events (`group/update`, `join`, `remove_member`, `update_avatar`, cut at 300 characters), the meaning of the `action` values of `pinned_convers`, the difference between 504 and 613, `emoji_status`, `oneone`, `file_done`, `join_reject`, `update_setting`.

