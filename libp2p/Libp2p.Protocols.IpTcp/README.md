# TCP/IP transport

Inbound TCP admission is shared across listeners of a local peer: 256 simultaneous
connections by default, with 32 per IPv4 address or native IPv6 /64. Under pressure,
sessions idle for 60 seconds without application operations may be disconnected.
Connections retain admission until both pumps and their protocol upgrades finish.
The `connected {endpoint}` event is exported on a per-connection activity, keeping
the listener's retained events bounded. See [session resource limits](../../docs/sessions.md#resource-limits-and-deadlines)
for configuration, deadlines, and built-in protocol stream allowances.
