<!-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited -->
<!-- SPDX-License-Identifier: MIT -->

# Noise transport short-record failure: separate follow-up

Status: **fixed in this change** ([issue #431](https://github.com/NethermindEth/dotnet-libp2p/issues/431)).
This record describes the separate follow-up requested in
[PR #416's review](https://github.com/NethermindEth/dotnet-libp2p/pull/416#pullrequestreview-5651700306).
The runtime correction is separate from PR #416's handshake framing changes.

## Issue title

Undersized Noise transport records fault the receive pump without propagating failure

## Historical finding

At inspected candidate `6deef303642830456c3e6f46b9dc40117aea2a8a`, the post-handshake
`ExchangeData` receive loop reads a peer-controlled unsigned length and its body,
then allocates `new byte[length - 16]` without first checking the minimum size.
Lengths 0 through 15 produce a negative allocation length and an
`OverflowException`. This is separate from #371's signed handshake-prefix reads.

`ExchangeData` returns `Task.WhenAny(upToDown, downToUp)` as `Task`. Its caller
awaits the outer task without awaiting the completed pump. Consequently a pump
fault can appear as successful protocol completion. This matches the documented
[Task.WhenAny completion contract](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.whenany?view=net-10.0#remarks).
Both public Noise roles call this shared loop; the reproduction below exercises
the production listener after authenticating and publishing the expected peer ID.

The same receive-loop allocation and task-return behavior exist in parent
`0e731cc8b84ad51fd7af3bc31e58744678e0a427`.
The complete `NoiseProtocol.cs` is byte-identical between the reproduction's
revision `29ec47562587b3bc87cdc0a3e9bd5cfeae72e053` and the inspected candidate;
the dated reproduction below describes those revisions before the correction.

At this change's base, `d6d849b870aa5f92a951545b96183d4b97c8ed8b`, PR #414
has already added cancellation, channel shutdown, and joining both pumps. The
short-record allocation remains, but pump faults are now hidden by joining with
`ConfigureAwaitOptions.SuppressThrowing` without subsequently propagating them.

## Historical reproduction and observed results

A disposable probe used the existing `HandshakeFramingTests.HandshakePeer(2)`
helper to drive the real `NoiseProtocol.ListenAsync` and production `Channel`:

1. Complete the independent Noise XX handshake and verify the authenticated peer ID.
2. Wait for the listener's application-channel upgrade.
3. Observe first-chance exceptions while sending a two-byte big-endian prefix for
   each length from 0 through 15, followed by exactly that many zero bytes.
4. Confirm the exception originates in `ExchangeData` and await the public listener task.

On Linux with .NET SDK 10.0.402 and runtime 10.0.13, every one of the 16 cases
observed `OverflowException` from the receive pump while the public listener task
ended in `RanToCompletion`. The peer was authenticated and upgraded before each
malformed record. Representative output:

```text
wireLength=0; authenticated=true; upgraded=true; pump=OverflowException; publicListen=RanToCompletion
wireLength=1; authenticated=true; upgraded=true; pump=OverflowException; publicListen=RanToCompletion
wireLength=15; authenticated=true; upgraded=true; pump=OverflowException; publicListen=RanToCompletion
```

All 12 existing valid-frame controls in `AcceptsUnsignedHandshakeLength` passed
in the same run and delivered application data. The disposable probe was removed;
no test asserting that this defect remains present is retained in the suite.
This reproduction does not claim a process crash or an authentication bypass.

## Implemented behavior

- Both roles reject a ciphertext length below the 16-byte authentication tag with
  `Libp2pException` immediately after its prefix, before reading the body or
  calculating or allocating the plaintext buffer. A 16-byte record carrying
  authenticated empty plaintext remains valid.
- The existing shutdown cancels and joins both pumps. The first pump's failure
  propagates through `DialAsync` or `ListenAsync`; if it ends normally, a sibling
  failure still propagates. All pump exceptions are observed during shutdown.
- `HandshakeFramingTests` covers lengths 0, 1, and 15 in both roles, complete
  bodies and incomplete bodies on an open connection, tampered ciphertext,
  and an empty encrypted record followed by application data and orderly EOF.
  Controlled sibling reads also verify joining through shutdown and propagation
  when either the first pump or its sibling fails.

The [pinned libp2p Noise specification](https://github.com/libp2p/specs/blob/c750236de43cd8b246a824b377dfdb26bae686db/noise/README.md#encryption-and-io)
defines transport ciphertext as an encrypted payload plus 16 authentication bytes.
The [Go implementation at the referenced revision](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/p2p/security/noise/rw.go)
uses the same tag overhead and propagates decryption errors to its reader. These
sources were inspected; cross-client interoperability was not executed here.

PR #416's handshake framing, identity validation, and Windows key-lifetime
behavior remain unchanged.
