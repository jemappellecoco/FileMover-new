    namespace FileMoverWeb.Models.Node
    {
    // ✅ node「定期/上線」回報 free 的 DTO
    public sealed class NodeFreeReportDto
    {
        public string? Node { get; set; }
        public string? Role { get; set; }
        public string? Group { get; set; }
        public string? HostName { get; set; }
        public string? IpAddress { get; set; }

        public int MaxConcurrency { get; set; } = 1;
        public int FreeSlots { get; set; } = 0;
    }
    }