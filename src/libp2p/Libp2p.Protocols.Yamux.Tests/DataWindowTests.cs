// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Yamux.Tests;

[TestFixture]
public class DataWindowTests
{
    [Test]
    public void Test_LocalWindow_FixedExtension_WhenDynamicDisabled()
    {
        const int initial = 1000;
        var settings = new YamuxWindowSettings { InitialWindowSize = initial, UseDynamicWindow = false };
        var w = new LocalDataWindow(settings);

        Assert.That(w.Available, Is.EqualTo(initial));
        bool spent = w.TrySpend(600);
        Assert.That(spent, Is.True);
        Assert.That(w.Available, Is.EqualTo(400));

        int extended = w.ExtendIfNeeded();
        Assert.That(extended, Is.EqualTo(initial));
        Assert.That(w.Available, Is.EqualTo(400 + initial));
    }

    [Test]
    public void Test_LocalWindow_DynamicExtension_GrowsWithConsumption()
    {
        const int initial = 256 * 1024;
        const int maxWindow = 4 * 1024 * 1024;
        var settings = new YamuxWindowSettings
        {
            InitialWindowSize = initial,
            MaxWindowSize = maxWindow,
            UseDynamicWindow = true
        };
        var w = new LocalDataWindow(settings);

        w.TrySpend(initial - 1);
        Assert.That(w.Available, Is.EqualTo(1));

        w.RecordConsumed(initial - 1);
        int extended = w.ExtendIfNeeded();
        Assert.That(extended, Is.GreaterThanOrEqualTo(initial), "First extend gives at least initial size");
        Assert.That(extended, Is.LessThanOrEqualTo(maxWindow));
    }

    [Test]
    public void Test_LocalWindow_Throws_WhenInitialWindowSizeInvalid()
    {
        var settings = new YamuxWindowSettings { InitialWindowSize = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalDataWindow(settings));
    }

    [Test]
    public void Test_LocalWindow_Throws_WhenMaxWindowSizeLessThanInitial()
    {
        var settings = new YamuxWindowSettings { InitialWindowSize = 1000, MaxWindowSize = 500 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalDataWindow(settings));
    }

    [Test]
    public void Test_LocalWindow_NoExtension_WhenAboveHalf()
    {
        const int initial = 1000;
        var w = new LocalDataWindow(new YamuxWindowSettings { InitialWindowSize = initial });
        w.TrySpend(400);
        Assert.That(w.Available, Is.EqualTo(600));
        Assert.That(w.ExtendIfNeeded(), Is.EqualTo(0));
    }

    [Test]
    public async Task Test_WindowExtendsProperly_WhenChannelWaitsForUpdate()
    {
        const int WindowSize = 100;

        for (int bytesToSendCase = 0; bytesToSendCase < 500; bytesToSendCase++)
        {
            int bytesToSend = bytesToSendCase;
            int windowUpdatesNeeded = (bytesToSend - 1) / WindowSize;
            int finalAvailable = WindowSize - bytesToSend + WindowSize * windowUpdatesNeeded;

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
            for (int i = 0; i < windowUpdatesNeeded; i++)
            {
                int val = w.Extend(WindowSize);
            }

            await spendingTask;
            Assert.That(w.Available, Is.EqualTo(finalAvailable));
        }
    }
}
