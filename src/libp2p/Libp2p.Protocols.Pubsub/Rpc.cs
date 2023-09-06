// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf.Collections;

namespace Nethermind.Libp2p.Protocols.Pubsub.Dto;

public partial class Rpc
{
    public T Ensure<T>(Func<Rpc, T> accessor)
    {
        switch (accessor)
        {
            case Func<Rpc, ControlMessage> _:
            case Func<Rpc, RepeatedField<ControlPrune>> _:
            case Func<Rpc, RepeatedField<ControlGraft>> _:
            case Func<Rpc, RepeatedField<ControlIHave>> _:
            case Func<Rpc, RepeatedField<ControlIWant>> _:
                Control ??= new ControlMessage();
                break;
            default:
                throw new NotImplementedException($"No {nameof(Ensure)} for {nameof(T)}");
        }
        return accessor(this);
    }
}
