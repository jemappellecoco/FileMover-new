namespace FileMoverWeb.Models.Progress
{
    /// <summary>
    /// Slave -> Master 回報用 DTO
    /// </summary>
    public sealed class ProgressReportDto
    {
        public int HistoryId { get; set; }

        /// <summary>前端用的 key，建議：TO-{historyId}</summary>
        public string? Key { get; set; }

        public string? Node { get; set; }      // ex: "7F-S1"
        public string? Action { get; set; }    // copy/move/delete

        /// <summary>可選：不傳就用 BytesDone/BytesTotal 算</summary>
        public int? Percent { get; set; }

        public long? BytesDone { get; set; }
        public long? BytesTotal { get; set; }

        /// <summary>可選：bytes/sec；不傳 Hub 可用 bytes 差估算</summary>
        public double? SpeedBps { get; set; }

        /// <summary>可選：顯示在你 .progress-file 那行</summary>
        public string? FileName { get; set; }

        /// <summary>可選：例如 "copying" / "hashing" / "finalizing"</summary>
        public string? Message { get; set; }
    }
}