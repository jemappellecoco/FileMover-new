using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Channels;
using FileMoverWeb.Models.Progress;
using System.Linq;
namespace FileMoverWeb.Services
{
    public sealed class ProgressHub
    {
        private readonly ConcurrentDictionary<string, ProgressSnapshot> _latest = new();
        private readonly ConcurrentDictionary<Guid, Channel<ProgressEvent>> _subs = new();

        public IReadOnlyCollection<ProgressSnapshot> Snapshot()
                => _latest.Values.ToArray();   // 或 ToList()

        public ChannelReader<ProgressEvent> Subscribe(out Guid id)
        {
            id = Guid.NewGuid();
            var ch = Channel.CreateUnbounded<ProgressEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _subs[id] = ch;
            return ch.Reader;
        }

        public void Unsubscribe(Guid id)
        {
            if (_subs.TryRemove(id, out var ch))
                ch.Writer.TryComplete();
        }

        public ProgressEvent Publish(ProgressReportDto dto)
        {
            if (dto is null) throw new ArgumentNullException(nameof(dto));
            if (dto.HistoryId <= 0) throw new ArgumentException("HistoryId invalid", nameof(dto));

            var key = string.IsNullOrWhiteSpace(dto.Key) ? $"TO-{dto.HistoryId}" : dto.Key.Trim();
            var now = DateTimeOffset.UtcNow;

            _latest.TryGetValue(key, out var prev);

            long bytesDone = Math.Max(0, dto.BytesDone ?? 0);
            long bytesTotal = Math.Max(0, dto.BytesTotal ?? 0);

            int percent = dto.Percent.HasValue
                ? Clamp(dto.Percent.Value, 0, 100)
                : (bytesTotal > 0 ? Clamp((int)Math.Round(bytesDone * 100.0 / bytesTotal), 0, 100) : 0);

            double? speedBps = dto.SpeedBps;

            if (speedBps is null && prev is not null)
            {
                var dt = (now - prev.UpdatedAt).TotalSeconds;
                var db = bytesDone - prev.BytesDone;
                if (dt >= 0.20 && db >= 0) speedBps = db / dt;
            }

            var ev = new ProgressEvent
            {
                Key = key,
                HistoryId = dto.HistoryId,
                Node = (dto.Node ?? "").Trim(),
                Action = (dto.Action ?? "").Trim(),
                Percent = percent,
                BytesDone = bytesDone,
                BytesTotal = bytesTotal,
                SpeedBps = speedBps,
                FileName = dto.FileName,
                Message = dto.Message,
                UpdatedAt = now
            };

            _latest[key] = new ProgressSnapshot
            {
                Key = key,
                HistoryId = dto.HistoryId,
                Node = ev.Node,
                Action = ev.Action,
                Percent = ev.Percent,
                BytesDone = ev.BytesDone,
                BytesTotal = ev.BytesTotal,
                SpeedBps = ev.SpeedBps,
                FileName = ev.FileName,
                Message = ev.Message,
                UpdatedAt = now
            };

            foreach (var sub in _subs.Values)
                sub.Writer.TryWrite(ev);

            return ev;
        }

        private static int Clamp(int v, int min, int max)
            => v < min ? min : (v > max ? max : v);
    }
}