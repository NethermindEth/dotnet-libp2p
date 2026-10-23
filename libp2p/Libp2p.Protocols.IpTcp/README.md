# TCP/IP transport

Inbound TCP admission is shared across listeners of a local peer: 256 simultaneous
connections by default, with 32 per source IP. Connections retain admission until
both pumps and their protocol upgrades finish. See [session resource limits](../../docs/sessions.md#resource-limits-and-deadlines)
for configuration, deadlines, and built-in protocol stream allowances.
