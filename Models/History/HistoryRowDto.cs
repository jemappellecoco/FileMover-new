namespace FileMoverWeb.Models.History
{
    public sealed class HistoryRowDto
    {
        public int historyId { get; set; }
        public int fileId { get; set; }
       

        public string? programName { get; set; }   // 你要顯示 UserBit 就塞 UserBit
        public string? fileName { get; set; }
        public string? fromType { get; set; }
        public string? sourceStorage { get; set; }
        public string? FromGroup { get; set; }
        public string? destStorage { get; set; }
         public string? ToGroup { get; set; }
        public string? assignedNode { get; set; }
        public string? action { get; set; }

        public string? destType { get; set; }
        public int toStorageId { get; set; }

        public int status { get; set; }
        // public string? note { get; set; }        // 直接用 h.note
        public System.DateTime? updateTime { get; set; }
        
    }
    public sealed class HistoryRecentRowDto
    {
        public int historyId { get; set; }
        public int fileId { get; set; }
       

        public string? programName { get; set; }   // 你要顯示 UserBit 就塞 UserBit
        public string? fileName { get; set; }
        public string? fromType { get; set; }
        public string? sourceStorage { get; set; }
        public string? FromGroup { get; set; }
        public string? destStorage { get; set; }
         public string? ToGroup { get; set; }
        public string? assignedNode { get; set; }
        public string? action { get; set; }

        public string? destType { get; set; }
        public int toStorageId { get; set; }

        public int status { get; set; }
        // public string? note { get; set; }        // 直接用 h.note
        public System.DateTime? updateTime { get; set; }
        
    }
}