# Libp2p Chat Application

A peer-to-peer chat application demonstrating the capabilities of the .NET Libp2p implementation. This sample showcases core Libp2p features including protocol negotiation, stream multiplexing, and secure communication, with a modern Terminal User Interface (TUI).

## Features

### Core Capabilities
- **Protocol Negotiation**: Uses Multistream protocol to negotiate communication protocols
- **Stream Multiplexing**: Yamux protocol for efficient stream management
- **Secure Communication**: TLS-based encryption for message security
- **Peer Discovery**: Identify protocol for peer information exchange
- **Bidirectional Communication**: Full-duplex chat capabilities

### User Interface
- **Multi-Panel TUI**: Separate views for chat, peers, and logs
- **Real-time Updates**: Live message and peer status updates
- **Status Bar**: Shows current Peer ID and connection status
- **Tab Navigation**: Easy switching between different views
- **Keyboard Shortcuts**: Quick access to common actions (Ctrl+X or ESC to exit)

### Protocol Stack
1. **Transport Layer**: TCP/IP for reliable data transfer
2. **Security Layer**: TLS for encrypted communication
3. **Multiplexing Layer**: Yamux for stream management
4. **Application Layer**: Custom chat protocol (`/chat/1.0.0`)

## Architecture

### Components

1. *ChatProtocol.cs*
   - Implements `SymmetricSessionProtocol`
   - Handles message encoding/decoding
   - Manages chat session lifecycle
   - Integrates with TUI for message display

2. *Program.cs*
   - Sets up Libp2p services
   - Configures logging
   - Handles peer creation and connection
   - Manages listener/dialer modes

3. *TerminalUI.cs*
   - Implements modern terminal interface
   - Manages chat, peer, and log views
   - Handles user input and keyboard shortcuts
   - Provides real-time status updates

## Testing

### Running the Application

1. **Start Listener**:
   ```bash
   dotnet run -sp <port>
   ```
   Example: `dotnet run -sp 9062`

2. **Connect to Listener**:
   ```bash
   dotnet run -d /ip4/127.0.0.1/tcp/<port>/p2p/<PEER_ID>
   ```
   Replace `<port>` and `<PEER_ID>` with values from the listener output

### Interface Navigation
- Use **Tab** to switch between Chat, Peers, and Logs views
- Press **Enter** to send messages
- Use **Ctrl+X** or **ESC** to exit
- Click buttons with mouse or use keyboard shortcuts

### Testing Scenarios

1. *Basic Chat*
   - Start listener and dialer instances
   - Exchange messages in the Chat view
   - Monitor connections in the Peers view
   - View protocol details in the Logs view

2. *Connection Management*
   - Monitor connection status in status bar
   - Track peer connections in Peers view
   - View detailed logs in Logs view

3. *Error Handling*
   - View connection errors in Logs view
   - Monitor peer disconnections
   - Test graceful application exit

## Logging

The application provides comprehensive logging through the TUI:
- Protocol negotiation steps in Logs view
- Connection establishment details
- Message exchange tracking
- Stream management information
- Error conditions and stack traces
- Real-time status updates

## Potential Improvements

### Feature Enhancements
1. *Message Persistence*
   - Add message history
   - Implement offline message support

2. *Group Chat*
   - Implement pubsub for group messaging
   - Add room management

3. *File Transfer*
   - Add file sharing capabilities
   - Implement chunked transfer

4. *UI Improvements*
   - Add message search functionality
   - Implement user mentions
   - Add emoji support
   - Add theme customization

### Technical Improvements
1. *Protocol Optimization*
   - Implement message batching
   - Add compression support
   - Optimize stream management

2. *Security Enhancements*
   - Add end-to-end encryption
   - Implement message signing
   - Add authentication mechanisms

3. *Reliability*
   - Add message acknowledgment
   - Implement retry mechanisms
   - Add connection recovery

## Leveraging the Code

### Integration Points
1. *Protocol Implementation*
   - Use `ChatProtocol` as a template for custom protocols
   - Extend `SymmetricSessionProtocol` for new features

2. *Connection Management*
   - Reuse peer creation logic
   - Adapt listener/dialer patterns

3. *UI Integration*
   - Extend `TerminalUI` for custom views
   - Add new message types
   - Implement custom visualizations

```
$ ./Chat
[...]info: Chat[0] Listener started at /ip4/0.0.0.0/tcp/58641/p2p/ABCDEF
```

```
$ ./Chat -d /ip4/127.0.0.1/58641
```