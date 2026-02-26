namespace FileMoverWeb.Models
{
    public sealed class Phase2Row
    {
        public int HistoryId { get; set; }
        public int FileId { get; set; }
        public int FileStatus { get; set; }
        public string? filetype { get; set; }   // "PO" / "CM"

        public string? UserBit { get; set; }
        public string? FileName { get; set; }

        public string? TapeNo { get; set; }
        public string? TapeBakNo { get; set; }

        public string? FromName { get; set; }
        public string? ToName { get; set; }
        public string? FromGroup { get; set; }
        public string? ToGroup { get; set; }
    }
}