// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Extensions;

public static class EnumerableExtensions
{
    public static bool UnorderedSequenceEqual<T>(this IEnumerable<T> left, IEnumerable<T> right) => left.OrderBy(x => x).SequenceEqual(right.OrderBy(x => x));
}
