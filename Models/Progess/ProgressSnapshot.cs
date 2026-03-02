using System;

namespace FileMoverWeb.Models.Progress
{
    /// <summary>
    /// Hub 內部保存的最新狀態（給 snapshot / speed 計算）
    /// </summary>
    public sealed class ProgressSnapshot
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