// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Yamux.Tests;

[TestFixture]
public class DataWindowTests
{
    [Test]
    public async Task Test_WindowExtendsProperly_WhenChannelWaitsForUpdate()
    {
        int windowSize = 100;
        var w = new DataWindow(windowSize);
        Task spendingTask = Task.Run(async () =>
        {
            int bytesToSend = windowSize + 1;
            while (bytesToSend != 0)
            {
                bytesToSend -= await w.SpendWindowOrWait(bytesToSend);
            }
        });
        await Task.Delay(10);
        int val = w.ExtendWindowIfNeeded();

        await spendingTask;
        Assert.That(val, Is.EqualTo(windowSize));
        Assert.That(w.Available, Is.EqualTo(windowSize - 1));
    }
}
