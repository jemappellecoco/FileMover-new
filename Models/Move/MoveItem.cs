namespace FileMoverWeb.Models.Move
{
    public sealed class MoveItem
    {
        public required int HistoryId { get; init; }

        public int? FileId { get; init; }
        public int? FromStorageId { get; init; }
        public int? ToStorageId { get; init; }

        public required string SourcePath { get; init; }
        public required string DestPath   { get; init; }

        public string? Action { get; set; }

        public string? UserBit   { get; set; }
        public string? ToName    { get; set; }
        public string? ToType    { get; set; }
        public string? FromName  { get; set; }
        public string? FromType  { get; set; }
        public string? Extension { get; set; }
    }
}