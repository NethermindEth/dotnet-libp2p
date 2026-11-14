# Windows process-key lifetime and recovery

Windows TLS and QUIC certificates share a lazily created, process-owned named
P-256 CNG key. Certificate creation is serialized around its signing object.
Each returned certificate owns an independent certificate/private-key handle;
callers must dispose that certificate and must not explicitly delete the shared
key. Outside Windows, the supplied session key remains in use.

Normal process exit attempts to delete the exact retained key. Abrupt termination,
a busy signing thread or failed deletion can leave a persisted key. Recovery runs
before the next process key is created and only deletes keys whose marked owners
are proven dead.

## Ownership and recovery

New keys use the current-user Microsoft Software Key Storage Provider, P-256
ECDSA, and a fresh random name
`libp2p-process-v2-{nonce:N}`. Overwrite is disabled. No process adopts another
process's private key. Each separately loaded helper owns its own key.

The custom property `libp2p-process-owner` is exactly 56 bytes:

| Offset | Encoding | Value |
| --- | --- | --- |
| 0 | 8 bytes | ASCII `libp2p` followed by two zero bytes |
| 8 | little-endian Int32 | Version 2 |
| 12 | little-endian Int32 | Positive process ID |
| 16 | little-endian Int64 | Positive raw process creation FILETIME |
| 24 | 16 bytes, .NET Guid byte order | Nonempty nonce matching the complete key name |
| 40 | 16 bytes, .NET Guid byte order | MachineGuid; zero means unavailable and prevents recovery |

User keys are stored under `%APPDATA%\Microsoft\Crypto\Keys`
([Microsoft key-storage documentation](https://learn.microsoft.com/en-us/windows/win32/seccng/key-storage-and-retrieval)).
This location can be redirected or roam, and
[DPAPI supports cross-computer decryption with roaming profiles](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata#remarks).
A local PID lookup therefore proves nothing about a key created on another
machine. Before any process query, recovery requires a nonempty machine identity
matching the local installation. Both the verified-key recovery path and direct
owner queries enforce this rule.

The identity is read once from `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography\MachineGuid`,
using the operating system's registry view so 32-bit and 64-bit processes agree.
Missing, invalid, unreadable or inaccessible identity means unknown liveness:
keys remain retained and correctly marked creation can continue with a zero
machine field. Such keys cannot be recovered automatically. Normal-exit cleanup
still owns and deletes its exact created key. Version-1 names and properties are
never recovered or upgraded because they cannot establish the owning machine.
This uses Windows installation identity, so separately running cloned installations
must have distinct MachineGuid values; a changed installation identity retains
old keys rather than guessing their origin.

`CngKeyCreationParameters.Parameters` supplies this property with
`CustomProperty | Persist`. [.NET 10.0.12 creation source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/CngKey.Create.cs)
sets properties before finalization and closes failed creation handles. No
post-creation metadata write or sidecar file creates an ownership gap.

Once, before lazy process-key creation, recovery tries a current-user,
cross-session mutex without waiting. [.NET's named wait handle options](https://learn.microsoft.com/en-us/dotnet/api/system.threading.namedwaithandleoptions?view=net-10.0)
provide this scope. Contention or inaccessible synchronization skips recovery;
marked creation can continue. Creation never holds the mutex. Recovery remains
synchronous so release happens on the acquiring thread. Abandonment grants
exclusion only; every candidate is revalidated.

[Provider enumeration](https://learn.microsoft.com/en-us/windows/win32/api/ncrypt/nf-ncrypt-ncryptenumkeys)
collects candidate names completely and releases its native state before any key
is opened or deleted. Only names with the process-key prefix are retained in this
snapshot. Recovery then checks provider, user store, algorithm, exact
property format/version, nonce correspondence and machine identity. Only after
matching machine evidence does a retained local process handle
establish creation time and termination state. An existing matching incarnation
whose process object has not signaled is live. A signaled matching process,
a different creation time, or `OpenProcess`'s invalid-parameter result for a
validated positive PID proves the
original incarnation dead. Access denial, failed queries, invalid creation times
and unexpected wait results are unknown and retain the key.

Only a verified dead owner's key is deleted, through the same verified key
handle. Legacy `libp2p-*` keys, independent keys, missing or malformed properties,
version-1 or future properties, mismatched nonces, foreign or unknown machines,
live owners and unknown liveness are retained.
Deletion failure leaves ownership evidence for a later process to retry.
Recovery has no timer, background worker, public setting or transport-disposal
dependency. Normal exit attempts exact-handle cleanup without waiting for a busy
signing thread.

Recovery is opportunistic: contention, access restrictions or deletion failures
can defer cleanup until another initialization. Historical unmarked keys cannot
be reclaimed automatically. Residual keys can remain in these conditions.
Certificate-list validation has a separate
[limitation](secure-transport-validation.md#received-tls-certificate-lists).

## Regression coverage

`WindowsCertificateKeyTests` covers sequential and concurrent certificate leases,
independent disposal, signing after another lease is disposed, and normal-exit
cleanup without deleting a live parent's key. The busy shutdown path using
`Monitor.TryEnter` is not exercised by the normal-exit test.

`WindowsProcessKeyRecoveryTests` covers creation barriers, crash/restart,
live-owner retention, synchronization contention and abandonment, malformed
ownership, and failed-deletion retry. Portable ownership tests cover machine
identity, PID reuse, unknown liveness, and enumeration disposal before recovery.
Registered transport tests exercise recovery through the TLS and QUIC builders.

The [Test workflow](../../.github/workflows/test.yml) runs the Windows transport,
key-lifetime and recovery checks. Windows-only skips do not satisfy its execution
minimums.
