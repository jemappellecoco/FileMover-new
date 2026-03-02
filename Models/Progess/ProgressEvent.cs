using System;

namespace FileMoverWeb.Models.Progress
{
    /// <summary>
    /// SSE 推給前端的事件資料
    /// </summary>
    public sealed class ProgressEvent
    {
        public string Key { get; set; } = "";
        public int HistoryId { get; set; }
        public string Node { get; set; } = "";
        public string Action { get; set; } = "";

        public int Percent { get; set; }
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }
        public double? SpeedBps { get; set; }

        public string? FileName { get; set; }
        public string? Message { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}