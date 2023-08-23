// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Floodsub;

public interface ITopic
{
    event Action<byte[]>? OnMessage;
    void Publish(byte[] bytes);
}
