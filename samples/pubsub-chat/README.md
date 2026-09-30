# .NET libp2p PubSub Example

This sample demonstrates a peer-to-peer chat application built with .NET libp2p. It showcases how to implement basic P2P communication using publish-subscribe messaging patterns.

## Overview

The application implements a simple chat system over libp2p's pubsub protocols. It demonstrates:

- How peers discover and connect to each other
- How messages are published to topics and received by subscribers
- The underlying protocol handshakes and negotiations that happen in libp2p

## Features

- **Real-time chat messaging**: Using libp2p's pubsub (gossipsub/floodsub) implementation
- **Terminal-based GUI**: Interactive interface built with Terminal.Gui (gui.cs)
- **Headless mode**: Run as a background node without UI
- **Live peer monitoring**: View connected peers and their details
- **Protocol logging**: See the underlying libp2p protocol operations

## Operation Modes

### GUI Mode (Default)
```
dotnet run
```
Launches with an interactive terminal UI with tabs for:
- Messages: Chat history and interactions
- Logs: Protocol and connection debugging information
- Peers: Details of connected peers

### Headless Mode
```
dotnet run -- --headless
```
Runs without a UI, outputting logs to console. Useful for:
- Running background nodes to strengthen the network
- Debugging protocol interactions
- Testing connectivity between peers

## Using the Application

### GUI Navigation
- **F1**: Help menu
- **F2-F4**: Switch between tabs
- **Enter**: Send message
- **Esc**: Exit application

### Network Behavior

When a node starts:
1. It generates a peer ID and listens on a random port
2. It advertises itself using libp2p's discovery mechanisms
3. When connecting to another peer:
  - Negotiates encryption using Noise protocol
  - Establishes multiplexed streams with Yamux
  - Exchanges peer identification information
  - Subscribes to the chat topic via gossipsub/floodsub

### Chat Functionality

The chat functionality is built on top of libp2p's pubsub:
- Messages are broadcast to all peers subscribed to the same topic
- Each message includes the sender's nickname
- All connected peers receive messages in near real-time
- Message history is maintained locally during the session

## Technical Implementation

The sample consists of:

1. **Program.cs**: Entry point that parses command line args and starts appropriate mode
2. **ChatService.cs**: Core functionality for the libp2p node and pubsub messaging
3. **Gui.cs**: Terminal UI implementation for interactive mode
4. **MessageFormat.cs**: Defines the structure of chat messages

The libp2p stack demonstrates:
- Peer discovery and connection
- Protocol negotiation with multistream
- Secure communication with Noise protocol
- Stream multiplexing with Yamux
- Publish-subscribe messaging with gossipsub/floodsub
