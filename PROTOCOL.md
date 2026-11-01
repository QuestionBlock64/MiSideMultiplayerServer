# MiSide Direct Connect Relay

This is a standalone copy of the MiSide Multiplayer relay, built for Direct
Connect only. It is permanently locked to the Direct Connect session protocol.
It does not advertise, resolve, register, or join Public or Private lobbies.

## Run it

Start `MiSideDirectConnectRelay.exe` on the machine that will host the relay.
The default listener is TCP `0.0.0.0:24805`. The host selects Direct Connect in
the game and uses the local address `127.0.0.1:24805` unless they configured a
custom relay endpoint. Other players enter the host machine's reachable IP
address and the same port in Direct Connect.

To use another port, run:

```text
MiSideDirectConnectRelay.exe --port 25565
```

To bind to one network interface instead of all interfaces, add `--bind`:

```text
MiSideDirectConnectRelay.exe --bind 192.168.1.20 --port 25565
```

Allow the relay through the host firewall for inbound TCP on the selected port.
For players joining over the internet, the host's router must forward that TCP
port to the machine running the relay. The game does not start the relay
process; the host runs this server separately and shares the reachable IP and
port with joiners.

## Fixed protocol behavior

- Host registration accepts only `visibility: 2`, room ID `default`, and an
  empty lobby code.
- Join requests are accepted only for an active Direct Connect lobby in room
  `default`.
- Session-list requests always return an empty list, and private-code
  resolution always fails. Direct Connect sessions cannot leak into
  Public/Private lobby discovery.
- The `directconnect` command confirms the fixed mode. The `normal` command
  reports that the operation is unavailable. No argument, including
  `--allow-mode-switch`, can enable Public/Private mode in this build.

The relay still routes the game's normal room traffic, loading barriers,
player departures, and host-authoritative state using the same protocol as the
regular relay. It does not run Unity or the game simulation.
