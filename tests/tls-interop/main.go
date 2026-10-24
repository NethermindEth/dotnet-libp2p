// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

// Exercises the pinned Go TLS/Yamux stack against samples/transport-interop.
package main

import (
	"context"
	"fmt"
	"os"
	"time"

	libp2p "github.com/libp2p/go-libp2p"
	"github.com/libp2p/go-libp2p/core/network"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/p2p/muxer/yamux"
	"github.com/libp2p/go-libp2p/p2p/protocol/ping"
	tlsSecurity "github.com/libp2p/go-libp2p/p2p/security/tls"
	"github.com/libp2p/go-libp2p/p2p/transport/tcp"
	ma "github.com/multiformats/go-multiaddr"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func check(conn network.Conn) error {
	state := conn.ConnState()
	if state.Security != tlsSecurity.ID || state.StreamMultiplexer != yamux.ID {
		return fmt.Errorf("unexpected negotiated stack: %+v", state)
	}
	id, err := peer.IDFromPublicKey(conn.RemotePublicKey())
	if err != nil || id != conn.RemotePeer() {
		return fmt.Errorf("authenticated peer identity mismatch: %v", err)
	}
	return nil
}

func run() error {
	ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
	defer cancel()
	h, err := libp2p.New(
		libp2p.ListenAddrStrings("/ip4/127.0.0.1/tcp/0"),
		libp2p.NoTransports,
		libp2p.Transport(tcp.NewTCPTransport),
		libp2p.Security(tlsSecurity.ID, tlsSecurity.New),
		libp2p.Muxer(yamux.ID, yamux.DefaultTransport),
		libp2p.DisableRelay(),
	)
	if err != nil {
		return err
	}
	defer h.Close()
	pingService := ping.NewPingService(h)
	if len(os.Args) == 2 {
		address, err := ma.NewMultiaddr(os.Args[1])
		if err != nil {
			return err
		}
		target, err := peer.AddrInfoFromP2pAddr(address)
		if err != nil {
			return err
		}
		if err := h.Connect(ctx, *target); err != nil {
			return err
		}
		conns := h.Network().ConnsToPeer(target.ID)
		if len(conns) != 1 {
			return fmt.Errorf("expected one authenticated connection, got %d", len(conns))
		}
		if err := check(conns[0]); err != nil {
			return err
		}
		select {
		case result := <-pingService.Ping(ctx, target.ID):
			if result.Error != nil {
				return result.Error
			}
			fmt.Printf("PASS Go dialer: TLS 1.3, %s, %s, authenticated %s, ping %s\n", tlsSecurity.ID, yamux.ID, target.ID, result.RTT)
			return nil
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	// Observe authentication before the .NET dialer can complete Ping and close.
	failures := make(chan error, 1)
	h.Network().Notify(&network.NotifyBundle{ConnectedF: func(_ network.Network, conn network.Conn) {
		if err := check(conn); err != nil {
			select {
			case failures <- err:
			default:
			}
			return
		}
		fmt.Printf("AUTHENTICATED Go listener: %s, %s, %s\n", tlsSecurity.ID, yamux.ID, conn.RemotePeer())
	}})
	fmt.Printf("LISTEN %s/p2p/%s\n", h.Addrs()[0], h.ID())
	select {
	case err := <-failures:
		return err
	case <-ctx.Done():
		return ctx.Err()
	}
}
