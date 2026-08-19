# MiSide Multiplayer Relay — Protocol Contract

**Superseded 2026-07-09.** The original version of this document described a
`HELLO:`/`MITA:` line-prefix scheme that the relay expected but the mod never
implemented. Looking at the mod's actual `TcpRelayTransport.cs`/`RpcDispatcher.cs`,
the real protocol is a JSON envelope on every line — this document now matches
that instead. The relay (`Program.cs`) has been updated to parse envelopes
correctly; no mod-side changes were needed.

## Transport

- Raw TCP, one persistent connection per client.
- Newline-delimited (`\n`) UTF-8 text lines.
- `TcpClient.NoDelay = true` on both ends.
- The server forwards ordinary traffic only to *other* clients in the same
  established room. Command responses are delivered privately to their issuer.

## Wire format

Every line, with no exceptions, is a JSON object shaped like this
(`TcpRelayTransport.RelayEnvelope`):

```json
{"roomName":"default","senderId":"ASUS_QB_SecondEdition","eventName":"miside.player.state","payload":"{...}"}
```

| Field | Meaning |
|---|---|
| `roomName` | Client-side room filter (`RoomName` in the mod's `.cfg`). Clients only apply packets from their own room; the relay does not enforce this — it broadcasts to everyone connected, room-filtering happens client-side. |
| `senderId` | The sending client's stable identity — this is the mod's `Identity.LocalPlayerId` from its `.cfg`. This is the *only* identity string sent on the wire; there is no separate display-name field. |
| `eventName` | What kind of packet this is. See below. |
| `payload` | A JSON string (yes, double-encoded — `payload` is itself a JSON string containing more JSON) with the event-specific data. |

## Event names (`RpcDispatcher` constants)

| Constant | Value | Purpose |
|---|---|---|
| `PlayerStateEvent` | `miside.player.state` | Regular position/animation sync. |
| `PlayerLeftEvent` | `miside.player.left` | Sent when a player leaves. |
| `MitaStateEvent` | `miside.mita.state` | Mita-sync traffic. |
| `DoorStateEvent` | `miside.world.door` | World door state. |
| `StoryObjectStateEvent` | `miside.world.storyobj` | Story object state. |
| `TransportHelloEvent` | `miside.transport.hello` | Sent once immediately after connecting, before any other traffic. `payload` is just `"{}"`. |

## Identity / naming on the relay side

There's no dedicated display name in the protocol — `senderId` (the `.cfg`'s
`LocalPlayerId`) is what the relay uses for both identification and logging.
Concretely, in `Program.cs`:

**Current implementation note:** the historical text immediately below predates
chat support. The active protocol now carries a separate display name; the
current rules are specified in [Chat extension](#chat-extension).

- The relay parses every incoming line as a `RelayEnvelope`.
- On `eventName == "miside.transport.hello"`, it captures `senderId` as
  `ClientConnection.ClientId` (used for ID-based bans) and captures
  `displayName` for logging and player-facing messages.
- As a fallback, if a client's first real packet isn't preceded by a hello
  (or a build doesn't send one), the relay also opportunistically adopts
  `senderId` from the first envelope it can parse, so identification doesn't
  depend on the hello arriving.
- Once identified, logs read `Client #3 (ASUS_QB_SecondEdition, 192.168.1.42:51022)`
  instead of just `Client #3`.

If you want a nicer name than the raw `LocalPlayerId` (e.g. "DELL" from the
`DisplayName` field in the `.cfg` you shared), that field currently never
leaves the client — `TcpRelayTransport` doesn't include it in the envelope
anywhere. Adding it would mean adding a field to `RelayEnvelope` (e.g.
`displayName`) on the mod side and reading it in `HandleHello`/
`TryAdoptIdentity` on the relay side. Both sides would need the change; this
isn't done yet.

## Chat extension

The relay enforces rooms server-side: a connection room is established by its
hello envelope and subsequent traffic is forwarded only to other clients in
that room. Clients still apply their own room filter as a second check.

The following optional envelope fields are understood:

| Field | Used by | Purpose |
|---|---|---|
| `displayName` | hello and normal traffic | Current `Identity.DisplayName` from `MS_Multiplayer.cfg`. |
| `sceneName` | hello | Initial active scene for join notices. |

Additional event names:

| Event | Payload | Routing |
|---|---|---|
| `miside.chat.message` | `{ senderId, displayName, sceneName, position, text }` | Forwarded to other clients in the same room. Receivers discard another-scene message or one more than 30 units away. |
| `miside.chat.system` | `{ text, color, sceneName }` | Relay-generated join/leave notices, sent only to other clients in the same room and scene. |
| `miside.server.response` | `{ text, color }` | Private relay response, delivered only to the client that issued a command. |

A chat `text` beginning with `/` is not relayed as chat. The relay checks the
sender `LocalPlayerId` against `OppedIds`, executes the matching console command
when allowed, and returns captured output to that one client. The relay uses
`yellow` for warnings, `red`/`darkred` for errors, and `white` for normal
output.

## Bans

`BanManager.IsIdBanned` checks against `senderId` values (i.e. `LocalPlayerId`).
Since `LocalPlayerId` is player-editable in the `.cfg`, this is a "friction"
ban, not a hard one — a banned player can change the value in their `.cfg`
and reconnect. IP bans (`BanManager.IsIpBanned`, checked at connection accept
time) are the harder guarantee, though they're defeated by a changed IP
(different network, VPN, etc). Neither is unbeatable; that's inherent to a
protocol with no auth layer.

## What moved server-side (out of the mod's `.cfg`)

Unchanged from before — see the `[Networking]`/`[Identity]` sections of the
mod's `.cfg` for client-local settings (`ServerHost`, `ServerPort`, `RoomName`,
`LocalPlayerId`, `DisplayName`). Server policy (port/bind address, max clients,
bans, log verbosity, Discord webhook) lives in the relay's `settings.json`.
