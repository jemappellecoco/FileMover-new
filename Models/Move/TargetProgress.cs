namespace FileMoverWeb.Models.Move
{
    public sealed class TargetProgress
    {
        public required string JobId { get; set; }
        public required string Key { get; set; }  // historyId.ToString()

        public long CopiedBytes { get; set; }
        public long TotalBytes  { get; set; }
        public string? CurrentFile { get; set; }

        public double Percent =>
            TotalBytes <= 0 ? 0
            : Math.Min(100.0, Math.Max(0.0,
                (double)CopiedBytes / TotalBytes * 100.0));
    }
}