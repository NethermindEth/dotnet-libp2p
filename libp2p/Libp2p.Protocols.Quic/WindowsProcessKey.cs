// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Nethermind.Libp2p.Protocols.Quic;

// Only keys carrying this exact, versioned property are eligible for recovery.
// Names select candidates; they never establish ownership or process death.
internal static class WindowsProcessKey
{
    internal const string NamePrefix = "libp2p-process-v1-";
    internal const string PropertyName = "libp2p-process-owner";
    internal const CngPropertyOptions PropertyOptions = CngPropertyOptions.CustomProperty | CngPropertyOptions.Persist;
    internal const string RecoveryMutexName = "libp2p-process-key-recovery-v1";

    internal readonly record struct Ownership(int ProcessId, long CreationTime, Guid Nonce)
    {
        internal string KeyName => $"{NamePrefix}{Nonce:N}";

        internal byte[] Encode()
        {
            byte[] value = new byte[40];
            "libp2p\0\0"u8.CopyTo(value);
            BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(12), ProcessId);
            BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(16), CreationTime);
            Nonce.TryWriteBytes(value.AsSpan(24));
            return value;
        }
    }

    internal static bool TryParse(string name, ReadOnlySpan<byte> value, out Ownership owner)
    {
        owner = default;
        if (value.Length != 40 || !value[..8].SequenceEqual("libp2p\0\0"u8)
            || BinaryPrimitives.ReadInt32LittleEndian(value[8..]) != 1) return false;
        Ownership parsed = new(BinaryPrimitives.ReadInt32LittleEndian(value[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(value[16..]), new Guid(value[24..]));
        if (parsed.ProcessId <= 0 || parsed.CreationTime <= 0 || parsed.Nonce == Guid.Empty
            || name != parsed.KeyName) return false;
        owner = parsed;
        return true;
    }

    internal enum OwnerState { Unknown, Live, Dead }

    // Pure classification of native results also permits deterministic PID reuse/error tests.
    internal static OwnerState ClassifyOwner(long expectedTime, bool opened, int openError,
        bool queried, long actualTime, uint waitResult)
    {
        if (!opened) return openError == 87 ? OwnerState.Dead : OwnerState.Unknown; // ERROR_INVALID_PARAMETER; PID is positive
        if (!queried || actualTime <= 0) return OwnerState.Unknown;
        if (actualTime != expectedTime) return OwnerState.Dead;
        return waitResult switch
        {
            0 => OwnerState.Dead, // WAIT_OBJECT_0
            258 => OwnerState.Live, // WAIT_TIMEOUT
            _ => OwnerState.Unknown,
        };
    }

    [SupportedOSPlatform("windows")]
    internal static Ownership CurrentOwner()
    {
        using SafeProcessHandle process = Native.OpenProcess(0x1000 | 0x100000, false, Environment.ProcessId);
        if (process.IsInvalid || !Native.GetProcessTimes(process, out long created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (created <= 0) throw new CryptographicException("Invalid process creation time.");
        return new(Environment.ProcessId, created, Guid.NewGuid());
    }

    [SupportedOSPlatform("windows")]
    internal static CngKeyCreationParameters CreationParameters(Ownership owner)
    {
        CngKeyCreationParameters parameters = new()
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            ExportPolicy = CngExportPolicies.AllowPlaintextExport,
            KeyUsage = CngKeyUsages.AllUsages,
        };
        // CngKey.Create sets Parameters before NCryptFinalizeKey. The software KSP
        // persists only finalized keys, including when its creator is killed.
        parameters.Parameters.Add(new CngProperty(PropertyName, owner.Encode(), PropertyOptions));
        return parameters;
    }

    [SupportedOSPlatform("windows")]
    internal static OwnerState QueryOwner(Ownership owner)
    {
        // Retain one process handle for both queries: reopening by PID would race reuse.
        using SafeProcessHandle process = Native.OpenProcess(0x1000 | 0x100000, false, owner.ProcessId);
        int error = Marshal.GetLastPInvokeError();
        if (process.IsInvalid) return ClassifyOwner(owner.CreationTime, false, error, false, 0, uint.MaxValue);
        bool queried = Native.GetProcessTimes(process, out long created, out _, out _, out _);
        uint wait = Native.WaitForSingleObject(process, 0);
        return ClassifyOwner(owner.CreationTime, true, 0, queried, created, wait);
    }

    [SupportedOSPlatform("windows")]
    internal static Mutex OpenRecoveryMutex() => new(false, RecoveryMutexName,
        new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });

    [SupportedOSPlatform("windows")]
    internal static bool Recover(Action? acquired = null)
    {
        try
        {
            using Mutex mutex = OpenRecoveryMutex();
            bool held;
            try { held = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return false;
            try
            {
                // Synchronous throughout: release on the acquiring thread. Abandonment
                // is only mutual exclusion, never evidence that a key's owner died.
                acquired?.Invoke();
                foreach (string name in GetRecoveryCandidates(EnumerateKeyNames()))
                {
                    try
                    {
                        using CngKey key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent);
                        RecoverKey(key, QueryOwner);
                    }
                    catch (CryptographicException) { /* Inaccessible, raced deletion, or failed deletion: retain/retry next startup. */ }
                }
                return true;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception e) when (e is CryptographicException or UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Recovery is opportunistic. Creation still requires its own exact marker.
            return false;
        }
    }

    // Finish enumeration and release its native state before opening or deleting keys.
    internal static IEnumerable<string> GetRecoveryCandidates(IEnumerable<string> names)
        => names.Where(name => name.StartsWith(NamePrefix, StringComparison.Ordinal)).ToArray();

    [SupportedOSPlatform("windows")]
    internal static bool RecoverKey(CngKey key, Func<Ownership, OwnerState> queryOwner, Action<CngKey>? delete = null)
    {
        if (key.Provider != CngProvider.MicrosoftSoftwareKeyStorageProvider || key.IsMachineKey
            || key.Algorithm != CngAlgorithm.ECDsaP256 || key.KeyName is not { } name
            || !key.HasProperty(PropertyName, CngPropertyOptions.CustomProperty)
            || !TryParse(name, key.GetProperty(PropertyName, CngPropertyOptions.CustomProperty).GetValue(), out Ownership owner)
            || queryOwner(owner) != OwnerState.Dead) return false;
        // Delete via the verified handle, never a subsequent lookup of this name.
        if (delete is null) key.Delete();
        else delete(key);
        return true;
    }

    [SupportedOSPlatform("windows")]
    internal static IEnumerable<string> EnumerateKeyNames()
    {
        int opened = Native.NCryptOpenStorageProvider(out SafeNCryptProviderHandle provider, CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider, 0);
        using (provider)
        {
            Check(opened);
            IntPtr state = IntPtr.Zero;
            try
            {
                while (true)
                {
                    IntPtr entry = IntPtr.Zero;
                    int status = Native.NCryptEnumKeys(provider, null, out entry, ref state, 0);
                    try
                    {
                        if (status == unchecked((int)0x8009002A)) yield break; // NTE_NO_MORE_ITEMS
                        Check(status);
                        Native.KeyName item = Marshal.PtrToStructure<Native.KeyName>(entry);
                        if (Marshal.PtrToStringUni(item.Name) is { } name) yield return name;
                    }
                    finally { if (entry != IntPtr.Zero) Native.NCryptFreeBuffer(entry); }
                }
            }
            finally { if (state != IntPtr.Zero) Native.NCryptFreeBuffer(state); }
        }
    }

    internal static void Check(int status)
    {
        if (status != 0) throw new CryptographicException(status);
    }

    // Shared with the Windows creation tests so they exercise the same provider and ABI.
    [SupportedOSPlatform("windows")]
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyName { internal IntPtr Name, Algorithm; internal int Legacy, Flags; }

        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        internal static extern int NCryptOpenStorageProvider(out SafeNCryptProviderHandle provider, string name, int flags);
        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        internal static extern int NCryptEnumKeys(SafeNCryptProviderHandle provider, string? scope, out IntPtr name, ref IntPtr state, int flags);
        [DllImport("ncrypt.dll")]
        internal static extern int NCryptFreeBuffer(IntPtr buffer);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    }
}
