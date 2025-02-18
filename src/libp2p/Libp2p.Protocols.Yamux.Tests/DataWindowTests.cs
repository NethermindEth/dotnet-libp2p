// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Yamux.Tests;

[TestFixture]
public class DataWindowTests
{
    [Test]
    public async Task Test_WindowExtendsProperly_WhenChannelWaitsForUpdate()
    {
        const int WindowSize = 100;

        for (int bytesToSendCase = 0; bytesToSendCase < 500; bytesToSendCase++)
        {
            int bytesToSend = bytesToSendCase;
            int windowUdatesNeeded = (bytesToSend - 1) / WindowSize;
            int finalAvailable = WindowSize - bytesToSend + WindowSize * windowUdatesNeeded;

            var w = new RemoteDataWindow(WindowSize);
            Task spendingTask = Task.Run(async () =>
            {
                await Task.Delay(bytesToSend % 3);
                while (bytesToSend != 0)
                {
                    bytesToSend -= await w.SpendOrWait(bytesToSend);
                }
            });

            await Task.Delay(bytesToSend % 5);
            for (int i = 0; i < windowUdatesNeeded; i++)
            {
                int val = w.Extend(WindowSize);
            }

            await spendingTask;
            Assert.That(w.Available, Is.EqualTo(finalAvailable));
        }
    }
}
