// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using NUnit.Framework;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

class TestPingProtocol : ISessionProtocol
{
    public string Id => "test-ping";

    public async Task DialAsync(IChannel downChannel, ISessionContext context)
    {
        string str = "hello";
        await downChannel.WriteLineAsync(str);
        string res = await downChannel.ReadLineAsync();
        Assert.That(res, Is.EqualTo(str + " there"));
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        string str = await downChannel.ReadLineAsync();
        await downChannel.WriteLineAsync(str + " there");
    }
}
