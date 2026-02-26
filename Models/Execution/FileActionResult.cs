namespace FileMoverWeb.Models.Execution
{
    public sealed class FileActionResult
    {
        public int HistoryId { get; set; }
        public bool Success { get; set; }

        // 成功 = 11 / 12 ...
        // 失敗 = 91x / 92x ...
        public int FileStatus { get; set; }

        public string? Error { get; set; }
    }
}