// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class RequestResponseTests
{
    [Test]
    public async Task Test_RequestReponse()
    {
        E2eTestSetup test = new();
        int request = 1;

        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);
        int response = await session.DialAsync<IncrementNumberTestProtocol, int, int>(1);

        Assert.That(response, Is.EqualTo(request + 1));
    }
}
