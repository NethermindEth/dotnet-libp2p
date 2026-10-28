<!-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited -->
<!-- SPDX-License-Identifier: MIT -->

# Noise transport short-record failure: separate issue draft

Status: **not filed**. Issue publication and a GitHub issue URL are still required.
This draft records the separate follow-up requested in
[PR #416's review](https://github.com/NethermindEth/dotnet-libp2p/pull/416#pullrequestreview-5651700306).
It does not fix the runtime defect or resolve that review request.

## Proposed issue title

Undersized Noise transport records fault the receive pump without propagating failure

## Finding

At revision `29ec47562587b3bc87cdc0a3e9bd5cfeae72e053`, the post-handshake
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

## Reproduction and observed results

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

## Expected follow-up behavior

- Reject a ciphertext record shorter than its authentication tag before calculating
  or allocating its plaintext buffer, using the protocol's expected failure path.
- Observe pump failures and retain ownership of sibling work through shutdown;
  a fault must not become successful protocol completion or an unobserved exception.
- Add regressions for short lengths in both roles, complete and truncated bodies,
  and valid encrypted records, including a zero-length plaintext control.

The runtime correction belongs in a separate change. PR #416's handshake framing,
identity validation, and Windows key-lifetime behavior remain unchanged.
