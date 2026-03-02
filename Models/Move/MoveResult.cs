namespace FileMoverWeb.Models.Move
{
    public sealed class MoveResult
    {
        public int HistoryId { get; set; }
        public bool Success { get; set; }
        public int? StatusCode { get; set; }
        public string? Error { get; set; }
    }
}