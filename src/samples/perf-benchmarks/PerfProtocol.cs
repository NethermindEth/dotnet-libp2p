// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Nethermind.Libp2p.Core;

namespace DataTransferBenchmark
{
    public class ReportingStream : Stream
    {
        private readonly Stream _innerStream;
        private ulong _lastReportRead;
        private DateTime _lastReportTime;

        public ReportingStream(Stream innerStream)
        {
            _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
            _lastReportTime = DateTime.UtcNow;
            _lastReportRead = 0;
        }

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => _innerStream.CanSeek;
        public override bool CanWrite => _innerStream.CanWrite;
        public override long Length => _innerStream.Length;

        public override long Position
        {
            get => _innerStream.Position;
            set => _innerStream.Position = value;
        }

        public override void Flush() => _innerStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = _innerStream.Read(buffer, offset, count);
            UpdateReport(bytesRead);
            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int bytesRead = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
            UpdateReport(bytesRead);
            return bytesRead;
        }

        private void UpdateReport(int bytesRead)
        {
            _lastReportRead += (ulong)bytesRead;
            var now = DateTime.UtcNow;
            var elapsedSeconds = (now - _lastReportTime).TotalSeconds;

            if (elapsedSeconds >= 1.0)
            {
                var result = new
                {
                    TimeSeconds = elapsedSeconds,
                    Type = "intermediary",
                    DownloadBytes = _lastReportRead
                };

                string jsonResult = JsonConvert.SerializeObject(result);
                Console.WriteLine(jsonResult);

                _lastReportTime = now;
                _lastReportRead = 0;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
        public override void SetLength(long value) => _innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public class Result
    {
        public double TimeSeconds { get; set; }
        public string Type { get; set; }
        public ulong DownloadBytes { get; set; }
        public ulong UploadBytes { get; set; }
    }

    public class PerfProtocol : IProtocol
    {
        private readonly ILogger<PerfProtocol> _logger;
        public string Id => "/perf/1.0.0";
        private PeerId peerId;
        private readonly PerfConfig _config;
        private Random rand = new();

        public PerfProtocol(ILogger<PerfProtocol> logger, PerfConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public async Task DialAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
        {
            byte[] u64Buf = new byte[8];
            using (Stream str = new ChannelStream(downChannel))
            {
                int bytesRead = await str.ReadAsync(u64Buf, 0, u64Buf.Length);
                if (bytesRead != u64Buf.Length)
                {
                    _logger.LogError("Could not read the required number of bytes");
                    str.Close();
                    return;
                }

                ulong bytesToSend = BitConverter.ToUInt64(u64Buf, 0);
                if (BitConverter.IsLittleEndian)
                {
                    bytesToSend = ReverseBytes(bytesToSend);
                }

                ulong bytesDrained = await DrainStreamAsync(str);
                if (bytesDrained == 0)
                {
                    _logger.LogError("Could not read the required number of bytes");
                    str.Close();
                    return;
                }

                if (!await SendBytesAsync(str, bytesToSend))
                {
                    _logger.LogError("Failed to drain the stream");
                    str.Close();
                    return;
                }

                str.Close();
            }
        }

        public async Task ListenAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
        {
            using (Stream str = new ChannelStream(downChannel))
            {
                byte[] sizeBuf = BitConverter.GetBytes(_config.BytesToRecv);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(sizeBuf);
                }

                await str.WriteAsync(sizeBuf, 0, sizeBuf.Length);

                if (!await SendBytesAsync(str, _config.BytesToSend))
                {
                    _logger.LogError("Failed to send bytes");
                    return;
                }

                ulong receivedBytes = await DrainStreamAsync(str);
                if (receivedBytes != _config.BytesToRecv)
                {
                    _logger.LogError($"Expected to receive {_config.BytesToRecv} bytes, but received {receivedBytes}");
                }
            }
        }

        public async Task<bool> SendBytesAsync(Stream stream, ulong bytesToSend)
        {
            byte[] buffer = new byte[64 * 1024];
            DateTime lastReportTime = DateTime.UtcNow;
            ulong lastReportWrite = 0;
            rand.NextBytes(buffer);

            while (bytesToSend > 0)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - lastReportTime).TotalSeconds >= 1.0)
                {
                    var result = new
                    {
                        TimeSeconds = (now - lastReportTime).TotalSeconds,
                        UploadBytes = lastReportWrite,
                        Type = "intermediary"
                    };
                    Console.WriteLine(JsonConvert.SerializeObject(result));

                    lastReportTime = now;
                    lastReportWrite = 0;
                }

                int toSend = buffer.Length;
                if (bytesToSend < (ulong)buffer.Length)
                {
                    toSend = (int)bytesToSend;
                }

                try
                {
                    await stream.WriteAsync(buffer, 0, toSend);
                }
                catch (Exception e)
                {
                    _logger.LogError($"Error writing to stream: {e.Message}");
                    return false;
                }

                bytesToSend -= (ulong)toSend;
                lastReportWrite += (ulong)toSend;
            }

            return true;
        }

        public async Task<ulong> DrainStreamAsync(Stream originalStream)
        {
            ulong totalReceived = 0;
            byte[] buffer = new byte[64 * 1024];

            using (var reportingStream = new ReportingStream(originalStream))
            {
                try
                {
                    int bytesRead;
                    while ((bytesRead = await reportingStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        totalReceived += (ulong)bytesRead;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError($"Error reading from stream: {e.Message}");
                }
            }

            return totalReceived;
        }

        private ulong ReverseBytes(ulong value)
        {
            return ((value & 0x00000000000000FF) << 56) |
                   ((value & 0x000000000000FF00) << 40) |
                   ((value & 0x0000000000FF0000) << 24) |
                   ((value & 0x00000000FF000000) << 8) |
                   ((value & 0x000000FF00000000) >> 8) |
                   ((value & 0x0000FF0000000000) >> 24) |
                   ((value & 0x00FF000000000000) >> 40) |
                   ((value & 0xFF00000000000000) >> 56);
        }
    }
}
