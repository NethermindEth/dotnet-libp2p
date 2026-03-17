// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

internal static class MultiaddressProtocolRegistration
{
    private static int _initialized;

    [ModuleInitializer]
    internal static void Register()
    {
        EnsureRegistered();
    }

    internal static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        RegisterIfMissing<WebrtcDirect>("webrtc-direct", 280, 0, false, _ => new WebrtcDirect());
        RegisterIfMissing<Webrtc>("webrtc", 281, 0, false, _ => new Webrtc());
        RegisterIfMissing<Certhash>("certhash", 466, -1, false, address => address is not null ? new Certhash((string)address) : new Certhash());
    }

    private static void RegisterIfMissing<TProtocol>(string name, int code, int size, bool path, Func<object?, MultiaddressProtocol> factory)
        where TProtocol : MultiaddressProtocol
    {
        Type multiaddressType = typeof(Multiaddress);
        MethodInfo? supportsMethod = multiaddressType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "SupportsProtocol" && m.GetParameters() is [{ ParameterType: var p0 }] && p0 == typeof(string));
        if (supportsMethod?.Invoke(null, [name]) is true)
        {
            return;
        }

        MethodInfo? setupMethod = multiaddressType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "Setup" && m.IsGenericMethodDefinition && m.GetParameters().Length == 5);
        if (setupMethod is null)
        {
            return;
        }

        MethodInfo genericSetup = setupMethod.MakeGenericMethod(typeof(TProtocol));
        genericSetup.Invoke(null, [name, code, size, path, factory]);
    }
}