// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

public class TestSuite
{
    public static IPeerFactory CreateLibp2p(params Type[] appProcols)
    {
        return new TestBuilder().Build();
    }
}
