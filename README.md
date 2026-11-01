# MiSide Direct Connect Relay

This standalone relay is permanently limited to Direct Connect. It cannot be
switched to Public or Private mode, and it never contributes sessions to the
Public/Private lobby finder.

## Start

Run `MiSideDirectConnectRelay.exe` on the host's computer. It listens on TCP
port `24805` by default. The host selects Direct Connect in MiSide; their local
relay address is `127.0.0.1:24805`. Joiners enter the host computer's reachable
IP address and port in Direct Connect.

Optional arguments:

- `--port 25565` listens on TCP port 25565 instead.
- `--bind 192.168.1.20` listens only on that local network interface.
- `--verbose` enables detailed logs.

Allow the relay through the host firewall for inbound TCP on the chosen port.
For internet connections, forward that TCP port from the host's router to this
computer. The host must keep the relay running while the lobby is in use.

This published build uses the .NET 6 runtime. See `PROTOCOL.md` for protocol
details and the required Direct Connect room behavior.
