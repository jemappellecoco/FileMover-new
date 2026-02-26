namespace FileMoverWeb.Models.Node
{
    public sealed class NodeStatusDto
    {
        public string NodeName { get; set; } = "";
        public string Role { get; set; } = "";
        public string Group { get; set; } = "";
        public string Status { get; set; } = "Offline";

        public int MaxConcurrency { get; set; } = 1;
        public int CurrentRunning { get; set; } = 0;

        public string? LastHeartbeat { get; set; }
        public string? HostName { get; set; }
        public string? IpAddress { get; set; }
    }
}