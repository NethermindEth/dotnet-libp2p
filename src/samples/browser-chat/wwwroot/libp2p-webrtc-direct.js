(function () {
  const UFRAG_PREFIX = 'libp2p+webrtc+v1/'
  const UFRAG_ALPHABET = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890'
  const DEFAULT_ICE_SERVERS = [
    'stun:stun.l.google.com:19302',
    'stun:global.stun.twilio.com:3478',
    'stun:stun.cloudflare.com:3478',
    'stun:stun.services.mozilla.com:3478'
  ]
  const MAX_MESSAGE_SIZE = 16 * 1024
  const connections = new Map()
  const channels = new Map()
  let nextConnectionId = 1
  let nextChannelId = 1

  function genUfrag (length = 32) {
    let value = UFRAG_PREFIX
    const bytes = new Uint8Array(length)
    crypto.getRandomValues(bytes)

    for (const byte of bytes) {
      value += UFRAG_ALPHABET[byte % UFRAG_ALPHABET.length]
    }

    return value
  }

  function parseMultiaddr (addr) {
    const parts = addr.split('/').filter(Boolean)
    const ip4 = parts.indexOf('ip4')
    const ip6 = parts.indexOf('ip6')
    const hostIndex = ip4 >= 0 ? ip4 : ip6
    const udp = parts.indexOf('udp')
    const certhash = parts.indexOf('certhash')

    if (hostIndex < 0 || udp < 0 || certhash < 0) {
      throw new Error(`Expected /ip4|ip6/.../udp/.../webrtc-direct/certhash/... multiaddr: ${addr}`)
    }

    return {
      type: ip4 >= 0 ? 'ip4' : 'ip6',
      host: parts[hostIndex + 1],
      port: Number(parts[udp + 1]),
      certhash: parts[certhash + 1]
    }
  }

  function decodeBase64UrlMultibase (value) {
    if (!value.startsWith('u')) {
      throw new Error(`Only base64url certhash values are supported: ${value}`)
    }

    let base64 = value.slice(1).replace(/-/g, '+').replace(/_/g, '/')
    while (base64.length % 4 !== 0) {
      base64 += '='
    }

    const binary = atob(base64)
    const bytes = new Uint8Array(binary.length)
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i)
    }
    return bytes
  }

  function readVarint (bytes, state) {
    let result = 0
    let shift = 0

    while (state.offset < bytes.length) {
      const value = bytes[state.offset++]
      result |= (value & 0x7f) << shift
      if ((value & 0x80) === 0) {
        return result
      }

      shift += 7
    }

    throw new Error('Truncated varint')
  }

  function certhashToFingerprint (certhash) {
    const multihash = decodeBase64UrlMultibase(certhash)
    const state = { offset: 0 }
    const code = readVarint(multihash, state)
    const length = readVarint(multihash, state)

    if (code !== 0x12 || length !== 32 || state.offset + length > multihash.length) {
      throw new Error(`Only sha2-256 certhash values are supported: ${certhash}`)
    }

    const digest = multihash.slice(state.offset, state.offset + length)
    const hex = Array.from(digest, b => b.toString(16).padStart(2, '0').toUpperCase()).join(':')
    return `sha-256 ${hex}`
  }

  function serverAnswerFromMultiaddr (remote, ufrag) {
    const ipVersion = remote.type === 'ip4' ? 4 : 6
    const fingerprint = certhashToFingerprint(remote.certhash)

    return {
      type: 'answer',
      sdp: `v=0
o=- 0 0 IN IP${ipVersion} ${remote.host}
s=-
t=0 0
a=ice-lite
m=application ${remote.port} UDP/DTLS/SCTP webrtc-datachannel
c=IN IP${ipVersion} ${remote.host}
a=mid:0
a=ice-options:ice2
a=ice-ufrag:${ufrag}
a=ice-pwd:${ufrag}
a=fingerprint:${fingerprint}
a=setup:passive
a=sctp-port:5000
a=max-message-size:${MAX_MESSAGE_SIZE}
a=candidate:1467250027 1 UDP 1467250027 ${remote.host} ${remote.port} typ host
a=end-of-candidates
`
    }
  }

  function munge (description, ufrag) {
    const lineBreak = description.sdp.includes('\r\n') ? '\r\n' : '\n'
    return {
      type: description.type,
      sdp: description.sdp
        .replace(/\na=ice-ufrag:[^\n]*\n/, `\na=ice-ufrag:${ufrag}${lineBreak}`)
        .replace(/\na=ice-pwd:[^\n]*\n/, `\na=ice-pwd:${ufrag}${lineBreak}`)
    }
  }

  function getFingerprintFromSdp (sdp) {
    const match = sdp.match(/^a=fingerprint:(\w+-[0-9]+)\s((?:[0-9a-fA-F]{2}:?)+)\r?$/m)
    if (match == null) {
      throw new Error('Could not find local WebRTC fingerprint in SDP')
    }

    return `${match[1]} ${match[2]}`
  }

  async function createCertificate () {
    return await RTCPeerConnection.generateCertificate({
      name: 'ECDSA',
      namedCurve: 'P-256'
    })
  }

  function invokeDotNet (dotNetRef, methodName, ...args) {
    try {
      const result = dotNetRef.invokeMethodAsync(methodName, ...args)
      if (result?.catch != null) {
        result.catch(() => {})
      }
    } catch {
    }
  }

  function registerChannel (channel, dotNetRef) {
    const channelId = `dc-${nextChannelId++}`
    channel.binaryType = 'arraybuffer'
    channels.set(channelId, { channel, dotNetRef })

    channel.addEventListener('open', () => {
      invokeDotNet(dotNetRef, 'OpenedFromJs')
    })

    channel.addEventListener('message', event => {
      if (event.data == null) {
        return
      }

      const bytes = event.data instanceof ArrayBuffer
        ? new Uint8Array(event.data)
        : new Uint8Array(event.data.buffer, event.data.byteOffset, event.data.byteLength)
      invokeDotNet(dotNetRef, 'ReceiveMessage', bytes)
    })

    channel.addEventListener('close', () => {
      invokeDotNet(dotNetRef, 'ClosedFromJs', null)
      channels.delete(channelId)
    })

    channel.addEventListener('error', event => {
      invokeDotNet(dotNetRef, 'ClosedFromJs', event.error?.message ?? 'RTC data channel error')
      channels.delete(channelId)
    })

    return channelId
  }

  function registerInboundChannel (channel, connectionDotNetRef) {
    const channelId = `dc-${nextChannelId++}`
    let opened = false
    channel.binaryType = 'arraybuffer'
    channels.set(channelId, { channel, connectionDotNetRef })

    const notifyOpen = () => {
      if (opened) {
        return
      }

      opened = true
      invokeDotNet(connectionDotNetRef, 'InboundChannelOpened', channelId)
    }

    channel.addEventListener('open', notifyOpen)
    if (channel.readyState === 'open') {
      notifyOpen()
    }

    channel.addEventListener('message', event => {
      if (event.data == null) {
        return
      }

      const bytes = event.data instanceof ArrayBuffer
        ? new Uint8Array(event.data)
        : new Uint8Array(event.data.buffer, event.data.byteOffset, event.data.byteLength)
      invokeDotNet(connectionDotNetRef, 'InboundChannelMessage', channelId, bytes)
    })

    channel.addEventListener('close', () => {
      invokeDotNet(connectionDotNetRef, 'InboundChannelClosed', channelId, null)
      channels.delete(channelId)
    })

    channel.addEventListener('error', event => {
      invokeDotNet(connectionDotNetRef, 'InboundChannelClosed', channelId, event.error?.message ?? 'RTC data channel error')
      channels.delete(channelId)
    })
  }

  function waitForOpen (channel) {
    if (channel.readyState === 'open') {
      return Promise.resolve()
    }

    return new Promise((resolve, reject) => {
      channel.addEventListener('open', resolve, { once: true })
      channel.addEventListener('close', () => reject(new Error('RTC data channel closed before opening')), { once: true })
      channel.addEventListener('error', event => reject(event.error ?? new Error('RTC data channel error')), { once: true })
    })
  }

  function createRelayedPeerConnection (connectionDotNetRef) {
    const peerConnection = new RTCPeerConnection({
      iceServers: DEFAULT_ICE_SERVERS.map(url => ({ urls: [url] }))
    })

    peerConnection.addEventListener('icecandidate', event => {
      if (peerConnection.connectionState === 'connected') {
        return
      }

      if (event.candidate == null || event.candidate.candidate === '') {
        return
      }

      invokeDotNet(connectionDotNetRef, 'LocalIceCandidate', JSON.stringify(event.candidate.toJSON()))
    })

    peerConnection.addEventListener('datachannel', event => {
      if (event.channel.label === 'init') {
        const close = () => {
          if (event.channel.readyState !== 'closed') {
            event.channel.close()
          }
        }

        event.channel.addEventListener('open', close, { once: true })
        if (event.channel.readyState === 'open') {
          close()
        }
        return
      }

      registerInboundChannel(event.channel, connectionDotNetRef)
    })

    return peerConnection
  }

  async function createRelayedOffer (connectionDotNetRef) {
    const peerConnection = createRelayedPeerConnection(connectionDotNetRef)
    const initChannel = peerConnection.createDataChannel('init')
    const offer = await peerConnection.createOffer()
    await peerConnection.setLocalDescription(offer)

    const connectionId = `pc-${nextConnectionId++}`
    connections.set(connectionId, { peerConnection, connectionDotNetRef, initChannel })

    return {
      connectionId,
      sdp: peerConnection.localDescription?.sdp ?? offer.sdp
    }
  }

  async function createRelayedAnswerer (connectionDotNetRef) {
    const peerConnection = createRelayedPeerConnection(connectionDotNetRef)
    const connectionId = `pc-${nextConnectionId++}`
    connections.set(connectionId, { peerConnection, connectionDotNetRef, initChannel: null })
    return connectionId
  }

  async function acceptRelayedOffer (connectionId, offerSdp) {
    const entry = connections.get(connectionId)
    if (entry == null) {
      throw new Error(`Unknown WebRTC connection: ${connectionId}`)
    }

    await entry.peerConnection.setRemoteDescription({ type: 'offer', sdp: offerSdp })
    const answer = await entry.peerConnection.createAnswer()
    await entry.peerConnection.setLocalDescription(answer)
    return entry.peerConnection.localDescription?.sdp ?? answer.sdp
  }

  async function setRelayedAnswer (connectionId, answerSdp) {
    const entry = connections.get(connectionId)
    if (entry == null) {
      throw new Error(`Unknown WebRTC connection: ${connectionId}`)
    }

    await entry.peerConnection.setRemoteDescription({ type: 'answer', sdp: answerSdp })
  }

  async function addRelayedIceCandidate (connectionId, candidateJson) {
    const entry = connections.get(connectionId)
    if (entry == null) {
      throw new Error(`Unknown WebRTC connection: ${connectionId}`)
    }

    const candidate = JSON.parse(candidateJson)
    if (candidate != null) {
      await entry.peerConnection.addIceCandidate(candidate)
    }
  }

  function waitRelayedConnected (connectionId) {
    const entry = connections.get(connectionId)
    if (entry == null) {
      throw new Error(`Unknown WebRTC connection: ${connectionId}`)
    }

    if (entry.peerConnection.connectionState === 'connected') {
      return Promise.resolve()
    }

    return new Promise((resolve, reject) => {
      const onState = () => {
        if (entry.peerConnection.connectionState === 'connected') {
          entry.peerConnection.removeEventListener('connectionstatechange', onState)
          resolve()
        } else if (entry.peerConnection.connectionState === 'failed' || entry.peerConnection.connectionState === 'closed') {
          entry.peerConnection.removeEventListener('connectionstatechange', onState)
          reject(new Error(`WebRTC connection state: ${entry.peerConnection.connectionState}`))
        }
      }

      entry.peerConnection.addEventListener('connectionstatechange', onState)
      onState()
    })
  }

  async function closeRelayedInitChannel (connectionId) {
    const entry = connections.get(connectionId)
    if (entry == null || entry.initChannel == null || entry.initChannel.readyState === 'closed') {
      return
    }

    const closed = entry.initChannel.readyState === 'closed'
      ? Promise.resolve()
      : new Promise(resolve => entry.initChannel.addEventListener('close', resolve, { once: true }))

    entry.initChannel.close()
    await closed
  }

  async function dial (handshakeDotNetRef, connectionDotNetRef, remoteAddr) {
    const remote = parseMultiaddr(remoteAddr)
    const ufrag = genUfrag()
    const certificate = await createCertificate()
    const peerConnection = new RTCPeerConnection({
      certificates: [certificate]
    })
    peerConnection.addEventListener('datachannel', event => {
      registerInboundChannel(event.channel, connectionDotNetRef)
    })

    const handshakeChannel = peerConnection.createDataChannel('', {
      negotiated: true,
      id: 0
    })
    const handshakeChannelId = registerChannel(handshakeChannel, handshakeDotNetRef)

    const offer = await peerConnection.createOffer()
    await peerConnection.setLocalDescription(munge(offer, ufrag))
    await peerConnection.setRemoteDescription(serverAnswerFromMultiaddr(remote, ufrag))
    await waitForOpen(handshakeChannel)

    const connectionId = `pc-${nextConnectionId++}`
    connections.set(connectionId, { peerConnection, connectionDotNetRef })

    return {
      connectionId,
      handshakeChannelId,
      localFingerprint: getFingerprintFromSdp(peerConnection.localDescription.sdp)
    }
  }

  async function openStream (connectionId, dotNetRef) {
    const connection = connections.get(connectionId)
    if (connection == null) {
      throw new Error(`Unknown WebRTC connection: ${connectionId}`)
    }

    const channel = connection.peerConnection.createDataChannel('')
    const channelId = registerChannel(channel, dotNetRef)
    await waitForOpen(channel)
    return channelId
  }

  function send (channelId, bytes) {
    const entry = channels.get(channelId)
    if (entry == null) {
      throw new Error(`Unknown WebRTC data channel: ${channelId}`)
    }

    if (entry.channel.readyState !== 'open') {
      throw new Error(`RTC data channel is ${entry.channel.readyState}`)
    }

    entry.channel.send(bytes)
  }

  function closeChannel (channelId) {
    const entry = channels.get(channelId)
    if (entry != null && entry.channel.readyState !== 'closed') {
      entry.channel.close()
    }
  }

  function closeConnection (connectionId) {
    const entry = connections.get(connectionId)
    if (entry != null) {
      entry.peerConnection.close()
      connections.delete(connectionId)
    }
  }

  window.nethermindLibp2pWebRtcDirect = {
    dial,
    createRelayedOffer,
    createRelayedAnswerer,
    acceptRelayedOffer,
    setRelayedAnswer,
    addRelayedIceCandidate,
    waitRelayedConnected,
    closeRelayedInitChannel,
    openStream,
    send,
    closeChannel,
    closeConnection
  }
})()
