using System.Collections.Concurrent;
using System.Net;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using Serilog;
using SIPSorcery.Net;

namespace RemoteDesk.Transport.WebRtc;

public sealed class WebRtcTransport : ITransport
{
    private static readonly ILogger Logger = Log.ForContext<WebRtcTransport>();

    private readonly RTCPeerConnection _peerConnection;
    private readonly bool _isInitiator;
    private readonly bool _forceRelay;
    private TransportState _state = TransportState.New;
    private ConnectionPath _connectionPath = ConnectionPath.Unknown;
    private readonly ConcurrentDictionary<string, RTCDataChannel> _dataChannels = new();
    private readonly ConcurrentDictionary<string, bool> _openedChannels = new();
    private readonly ConcurrentDictionary<byte, string> _channelLabelByCode = new();

    private readonly List<RTCIceCandidateType> _localCandidateTypes = [];
    private readonly List<RTCIceCandidateType> _remoteCandidateTypes = [];
    private readonly List<string> _remoteCandidateAddresses = [];
    private readonly List<RTCIceCandidateInit> _pendingRemoteIceCandidates = [];
    private readonly object _iceCandidateLock = new();
    private bool _isRemoteDescriptionSet;

    // Video reassembly state
    private readonly ConcurrentDictionary<uint, VideoFrameAssembler> _pendingFrames = new();
    private uint _nextVideoFrameSeq;

    private const int MaxChunkSize = 32 * 1024; // 32 KB chunks to safely fit SCTP buffer

    public TransportState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                Logger.Information("WebRtcTransport state changed to {State}", value);
                StateChanged?.Invoke(value);

                if (value == TransportState.Connected)
                {
                    EvaluateConnectionPath();
                }
            }
        }
    }

    public ConnectionPath ConnectionPath
    {
        get => _connectionPath;
        private set
        {
            if (_connectionPath != value)
            {
                _connectionPath = value;
                Logger.Information("WebRtcTransport connection path detected: {Path}", value);
                ConnectionPathChanged?.Invoke(value);
            }
        }
    }

    public event Action<TransportState>? StateChanged;
    public event Action<ConnectionPath>? ConnectionPathChanged;
    public event Action<byte, ReadOnlyMemory<byte>>? MessageReceived;
    public event Action<ReadOnlyMemory<byte>>? VideoPacketReceived;
    public event Action<string>? ErrorOccurred;

    public event Action<string, string, int>? IceCandidateGenerated;

    public WebRtcTransport(bool isInitiator, RTCConfiguration? config)
        : this(isInitiator, null, false, config)
    {
    }

    public WebRtcTransport(
        bool isInitiator,
        IEnumerable<IceServerConfig>? iceServers = null,
        bool forceRelay = false,
        RTCConfiguration? config = null)
    {
        _isInitiator = isInitiator;
        _forceRelay = forceRelay;

        if (forceRelay)
        {
            _connectionPath = ConnectionPath.TurnRelayed;
        }

        if (config == null)
        {
            config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>()
            };

            if (iceServers != null)
            {
                foreach (var server in iceServers)
                {
                    foreach (var url in server.Urls)
                    {
                        var rtcServer = new RTCIceServer { urls = url };
                        if (!string.IsNullOrEmpty(server.Credential))
                        {
                            rtcServer.username = server.Username;
                            rtcServer.credential = server.Credential;
                            rtcServer.credentialType = RTCIceCredentialType.password;
                        }
                        config.iceServers.Add(rtcServer);
                    }
                }
            }

            // Fallback default Anycast STUN if no servers provided
            if (config.iceServers.Count == 0)
            {
                config.iceServers.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
                config.iceServers.Add(new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" });
            }
        }

        if (forceRelay)
        {
            config.iceTransportPolicy = RTCIceTransportPolicy.relay;
            Logger.Information("WebRtcTransport forced relay policy enabled.");
        }

        _peerConnection = new RTCPeerConnection(config);

        _channelLabelByCode[DataChannels.Control] = "control";
        _channelLabelByCode[DataChannels.Input] = "input";
        _channelLabelByCode[DataChannels.Clipboard] = "clipboard";
        _channelLabelByCode[DataChannels.Files] = "files";
        _channelLabelByCode[6] = "video";

        WireEvents();
    }

    private void WireEvents()
    {
        _peerConnection.onconnectionstatechange += (state) =>
        {
            Logger.Information("PeerConnection state changed: {State}", state);
            switch (state)
            {
                case RTCPeerConnectionState.connecting:
                    if (State != TransportState.Connected)
                    {
                        State = TransportState.Connecting;
                    }
                    break;
                case RTCPeerConnectionState.connected:
                    TryPromoteConnected();
                    break;
                case RTCPeerConnectionState.disconnected:
                    State = TransportState.Disconnected;
                    break;
                case RTCPeerConnectionState.failed:
                    State = TransportState.Failed;
                    break;
                case RTCPeerConnectionState.closed:
                    State = TransportState.Closed;
                    break;
            }
        };

        _peerConnection.oniceconnectionstatechange += (iceState) =>
        {
            Logger.Information("PeerConnection ICE state changed: {State}", iceState);
            if (iceState == RTCIceConnectionState.connected)
            {
                TryPromoteConnected();
            }
            else if (iceState == RTCIceConnectionState.failed)
            {
                Logger.Warning("WebRTC ICE state failed.");
                State = TransportState.Failed;
            }
        };

        _peerConnection.onicecandidate += (candidate) =>
        {
            if (candidate != null && !string.IsNullOrEmpty(candidate.candidate))
            {
                lock (_localCandidateTypes)
                {
                    _localCandidateTypes.Add(candidate.type);
                }

                Logger.Debug("Generated ICE candidate: {Candidate} (type={Type}, mid={Mid}, line={Line})",
                    candidate.candidate, candidate.type, candidate.sdpMid, candidate.sdpMLineIndex);
                IceCandidateGenerated?.Invoke(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
            }
        };

        _peerConnection.ondatachannel += (channel) =>
        {
            Logger.Information("Remote data channel opened: {Label}", channel.label);
            RegisterDataChannel(channel);
        };
    }

    private void EvaluateConnectionPath()
    {
        if (_forceRelay)
        {
            ConnectionPath = ConnectionPath.TurnRelayed;
            return;
        }

        lock (_remoteCandidateTypes)
        {
            // If any relay candidates were generated or used and no host candidates matched
            if (_localCandidateTypes.Contains(RTCIceCandidateType.relay) || _remoteCandidateTypes.Contains(RTCIceCandidateType.relay))
            {
                if (!_localCandidateTypes.Contains(RTCIceCandidateType.host) && !_localCandidateTypes.Contains(RTCIceCandidateType.srflx))
                {
                    ConnectionPath = ConnectionPath.TurnRelayed;
                    return;
                }
            }

            // Check if remote candidates are public STUN reflexive
            var hasPublicSrflx = _remoteCandidateTypes.Contains(RTCIceCandidateType.srflx) ||
                                 _remoteCandidateTypes.Contains(RTCIceCandidateType.prflx);

            var allPrivate = _remoteCandidateAddresses.Count > 0 &&
                             _remoteCandidateAddresses.All(IsPrivateOrLoopbackAddress);

            if (hasPublicSrflx && !allPrivate)
            {
                ConnectionPath = ConnectionPath.DirectStunHolePunch;
            }
            else
            {
                ConnectionPath = ConnectionPath.DirectLocal;
            }
        }
    }

    private static bool IsPrivateOrLoopbackAddress(string address)
    {
        if (IPAddress.TryParse(address, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;
            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (bytes[0] == 10) return true;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                if (bytes[0] == 169 && bytes[1] == 254) return true;
            }
        }
        return false;
    }

    public async Task InitializeChannelsAsync()
    {
        if (_isInitiator)
        {
            Logger.Information("Initiator creating standard data channels...");
            var control = await _peerConnection.createDataChannel("control", new RTCDataChannelInit { id = 1 });
            RegisterDataChannel(control);

            var input = await _peerConnection.createDataChannel("input", new RTCDataChannelInit { id = 2 });
            RegisterDataChannel(input);

            var clipboard = await _peerConnection.createDataChannel("clipboard", new RTCDataChannelInit { id = 3 });
            RegisterDataChannel(clipboard);

            var files = await _peerConnection.createDataChannel("files", new RTCDataChannelInit { id = 4 });
            RegisterDataChannel(files);

            var video = await _peerConnection.createDataChannel("video", new RTCDataChannelInit { id = 6 });
            RegisterDataChannel(video);
        }
    }

    private void TryPromoteConnected()
    {
        if (State == TransportState.Connected) return;

        bool IsChannelReady(string label) =>
            _dataChannels.TryGetValue(label, out var ch) &&
            (ch.readyState == RTCDataChannelState.open || _openedChannels.ContainsKey(label));

        if (IsChannelReady("control") && IsChannelReady("video"))
        {
            Logger.Information("Required WebRTC data channels (control, video) are open; promoting transport State to Connected.");
            State = TransportState.Connected;
        }
    }

    private void RegisterDataChannel(RTCDataChannel channel)
    {
        _dataChannels[channel.label] = channel;

        channel.onopen += () =>
        {
            Logger.Information("Data channel opened: {Label}", channel.label);
            _openedChannels[channel.label] = true;
            TryPromoteConnected();
        };

        channel.onclose += () =>
        {
            Logger.Information("Data channel closed: {Label}", channel.label);
        };

        channel.onerror += (err) =>
        {
            Logger.Error("Data channel [{Label}] error: {Error}", channel.label, err);
            ErrorOccurred?.Invoke($"DataChannel [{channel.label}] error: {err}");
        };

        channel.onmessage += (dc, protocol, data) =>
        {
            HandleChannelMessage(channel.label, data);
        };

        // If the remote channel arrived already open (e.g., via ondatachannel), mark and promote immediately
        if (channel.readyState == RTCDataChannelState.open)
        {
            _openedChannels[channel.label] = true;
            TryPromoteConnected();
        }
    }

    private void HandleChannelMessage(string label, byte[] data)
    {
        if (label == "video")
        {
            ProcessVideoPacket(data);
            return;
        }

        byte channelCode = label switch
        {
            "control" => DataChannels.Control,
            "input" => DataChannels.Input,
            "clipboard" => DataChannels.Clipboard,
            "files" => DataChannels.Files,
            _ => 0
        };

        if (channelCode != 0)
        {
            MessageReceived?.Invoke(channelCode, data);
        }
        else
        {
            Logger.Warning("Received message on unknown data channel: {Label}", label);
        }
    }

    private void ProcessVideoPacket(byte[] data)
    {
        if (data.Length < 1) return;

        byte packetType = data[0];
        if (packetType == 1) // Full frame unchunked
        {
            VideoPacketReceived?.Invoke(new ReadOnlyMemory<byte>(data, 1, data.Length - 1));
        }
        else if (packetType == 2 && data.Length >= 13) // Chunked: Type(1) + Seq(4) + ChunkIdx(2) + TotalChunks(2) + TotalSize(4)
        {
            uint seq = BitConverter.ToUInt32(data, 1);
            ushort chunkIdx = BitConverter.ToUInt16(data, 5);
            ushort totalChunks = BitConverter.ToUInt16(data, 7);
            uint totalSize = BitConverter.ToUInt32(data, 9);

            var assembler = _pendingFrames.GetOrAdd(seq, _ => new VideoFrameAssembler(seq, totalChunks, totalSize));
            var frame = assembler.AddChunk(chunkIdx, data.AsMemory(13));
            if (frame != null)
            {
                _pendingFrames.TryRemove(seq, out _);
                VideoPacketReceived?.Invoke(frame);
            }
        }
    }

    public async Task<string> CreateOfferAsync()
    {
        await InitializeChannelsAsync();
        var offer = _peerConnection.createOffer();
        await _peerConnection.setLocalDescription(offer);
        return offer.sdp;
    }

    public async Task<string> CreateAnswerAsync()
    {
        var answer = _peerConnection.createAnswer();
        await _peerConnection.setLocalDescription(answer);
        return answer.sdp;
    }

    public async Task SetRemoteDescriptionAsync(RTCSdpType type, string sdp)
    {
        var init = new RTCSessionDescriptionInit
        {
            type = type,
            sdp = sdp
        };
        var result = _peerConnection.setRemoteDescription(init);
        if (result != SetDescriptionResultEnum.OK)
        {
            Logger.Warning("setRemoteDescription returned: {Result}", result);
        }

        List<RTCIceCandidateInit> pending;
        lock (_iceCandidateLock)
        {
            _isRemoteDescriptionSet = true;
            pending = new List<RTCIceCandidateInit>(_pendingRemoteIceCandidates);
            _pendingRemoteIceCandidates.Clear();
        }

        foreach (var cand in pending)
        {
            Logger.Debug("Applying buffered ICE candidate: {Candidate}", cand.candidate);
            _peerConnection.addIceCandidate(cand);
        }

        await Task.CompletedTask;
    }

    public void AddIceCandidate(string candidate, string sdpMid, int sdpMLineIndex)
    {
        // Parse candidate type and address for path detection
        ParseCandidateInfo(candidate, out var candidateType, out var address);
        lock (_remoteCandidateTypes)
        {
            if (candidateType.HasValue) _remoteCandidateTypes.Add(candidateType.Value);
            if (!string.IsNullOrEmpty(address)) _remoteCandidateAddresses.Add(address);
        }

        var candidateInit = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = (ushort)sdpMLineIndex
        };

        lock (_iceCandidateLock)
        {
            if (!_isRemoteDescriptionSet)
            {
                Logger.Debug("Buffering early ICE candidate until remote description is set: {Candidate}", candidate);
                _pendingRemoteIceCandidates.Add(candidateInit);
                return;
            }
        }

        try
        {
            _peerConnection.addIceCandidate(candidateInit);
        }
        catch (Exception ex)
        {
            Logger.Warning("Error adding ICE candidate: {Message}", ex.Message);
        }
    }

    private static void ParseCandidateInfo(string candidateStr, out RTCIceCandidateType? type, out string? address)
    {
        type = null;
        address = null;

        var parts = candidateStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Equals("typ", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
            {
                type = parts[i + 1].ToLowerInvariant() switch
                {
                    "host" => RTCIceCandidateType.host,
                    "srflx" => RTCIceCandidateType.srflx,
                    "prflx" => RTCIceCandidateType.prflx,
                    "relay" => RTCIceCandidateType.relay,
                    _ => null
                };
            }
        }

        // Standard SDP candidate address is at index 4 (candidate:foundation component transport priority address port ...)
        if (parts.Length > 5)
        {
            address = parts[4];
        }
    }

    public ValueTask SendMessageAsync(byte channelId, ReadOnlyMemory<byte> payload, bool reliable = true, CancellationToken ct = default)
    {
        if (_channelLabelByCode.TryGetValue(channelId, out var label) && _dataChannels.TryGetValue(label, out var channel))
        {
            if (channel.readyState == RTCDataChannelState.open)
            {
                channel.send(payload.ToArray());
                return ValueTask.CompletedTask;
            }
            else
            {
                Logger.Warning("Data channel {Label} is not open (State: {State})", label, channel.readyState);
            }
        }
        else
        {
            Logger.Warning("No data channel found for code: {Code}", channelId);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendVideoPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        if (!_dataChannels.TryGetValue("video", out var videoChannel) || videoChannel.readyState != RTCDataChannelState.open)
        {
            return ValueTask.CompletedTask;
        }

        if (packet.Length <= MaxChunkSize)
        {
            // Send single unchunked packet: [0x01, payload...]
            var buf = new byte[1 + packet.Length];
            buf[0] = 1;
            packet.CopyTo(buf.AsMemory(1));
            videoChannel.send(buf);
        }
        else
        {
            // Chunked: [0x02, Seq(4), ChunkIdx(2), TotalChunks(2), TotalSize(4), payload...]
            uint seq = unchecked(++_nextVideoFrameSeq);
            int totalBytes = packet.Length;
            ushort totalChunks = (ushort)((totalBytes + MaxChunkSize - 1) / MaxChunkSize);

            for (ushort i = 0; i < totalChunks; i++)
            {
                int offset = i * MaxChunkSize;
                int count = Math.Min(MaxChunkSize, totalBytes - offset);

                var chunk = new byte[13 + count];
                chunk[0] = 2;
                BitConverter.TryWriteBytes(chunk.AsSpan(1, 4), seq);
                BitConverter.TryWriteBytes(chunk.AsSpan(5, 2), i);
                BitConverter.TryWriteBytes(chunk.AsSpan(7, 2), totalChunks);
                BitConverter.TryWriteBytes(chunk.AsSpan(9, 4), (uint)totalBytes);
                packet.Slice(offset, count).CopyTo(chunk.AsMemory(13));

                videoChannel.send(chunk);
            }
        }

        return ValueTask.CompletedTask;
    }

    public Task CloseAsync()
    {
        State = TransportState.Closed;
        _peerConnection.close();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }

    private sealed class VideoFrameAssembler
    {
        private readonly uint _seq;
        private readonly ushort _totalChunks;
        private readonly byte[] _buffer;
        private readonly bool[] _received;
        private int _receivedCount;
        private readonly object _lock = new();

        public VideoFrameAssembler(uint seq, ushort totalChunks, uint totalSize)
        {
            _seq = seq;
            _totalChunks = totalChunks;
            _buffer = new byte[totalSize];
            _received = new bool[totalChunks];
        }

        public byte[]? AddChunk(ushort chunkIdx, ReadOnlyMemory<byte> chunkData)
        {
            lock (_lock)
            {
                if (chunkIdx >= _totalChunks || _received[chunkIdx])
                    return null;

                int offset = chunkIdx * MaxChunkSize;
                if (offset + chunkData.Length <= _buffer.Length)
                {
                    chunkData.CopyTo(_buffer.AsMemory(offset));
                    _received[chunkIdx] = true;
                    _receivedCount++;

                    if (_receivedCount == _totalChunks)
                    {
                        return _buffer;
                    }
                }

                return null;
            }
        }
    }
}
