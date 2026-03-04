namespace FileMoverWeb.Models.Archive
{
    public sealed class ArchiveRowDto
    {
        public int historyId { get; set; }
        public int fileId { get; set; }
        public string? programName { get; set; }
        public string? fileName { get; set; }
        public string? sourceStorage { get; set; }
        public string? destStorage { get; set; }
        public string? assignedNode { get; set; }
        public DateTime? updateTime { get; set; }
    }
}