using System.Collections.Concurrent;
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
    private TransportState _state = TransportState.New;
    private readonly ConcurrentDictionary<string, RTCDataChannel> _dataChannels = new();
    private readonly ConcurrentDictionary<byte, string> _channelLabelByCode = new();

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
            }
        }
    }

    public event Action<TransportState>? StateChanged;
    public event Action<byte, ReadOnlyMemory<byte>>? MessageReceived;
    public event Action<ReadOnlyMemory<byte>>? VideoPacketReceived;
    public event Action<string>? ErrorOccurred;

    public event Action<string, string, int>? IceCandidateGenerated;

    public WebRtcTransport(bool isInitiator, RTCConfiguration? config = null)
    {
        _isInitiator = isInitiator;

        config ??= new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new() { urls = "stun:stun.l.google.com:19302" }
            }
        };

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
            State = state switch
            {
                RTCPeerConnectionState.connecting => TransportState.Connecting,
                RTCPeerConnectionState.connected => TransportState.Connected,
                RTCPeerConnectionState.disconnected => TransportState.Disconnected,
                RTCPeerConnectionState.failed => TransportState.Failed,
                RTCPeerConnectionState.closed => TransportState.Closed,
                _ => TransportState.New
            };
        };

        _peerConnection.onicecandidate += (candidate) =>
        {
            if (candidate != null && !string.IsNullOrEmpty(candidate.candidate))
            {
                Logger.Debug("Generated ICE candidate: {Candidate} (mid={Mid}, line={Line})",
                    candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
                IceCandidateGenerated?.Invoke(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
            }
        };

        _peerConnection.ondatachannel += (channel) =>
        {
            Logger.Information("Remote data channel opened: {Label}", channel.label);
            RegisterDataChannel(channel);
        };
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

    private void RegisterDataChannel(RTCDataChannel channel)
    {
        _dataChannels[channel.label] = channel;

        channel.onopen += () =>
        {
            Logger.Information("Data channel opened: {Label}", channel.label);
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
        await Task.CompletedTask;
    }

    public void AddIceCandidate(string candidate, string sdpMid, int sdpMLineIndex)
    {
        _peerConnection.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = (ushort)sdpMLineIndex
        });
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
