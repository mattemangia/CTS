using System;

namespace ParallelComputingServer.Models
{
    public class ClientInfo
    {
        public string ClientIP { get; set; }
        public int ClientPort { get; set; }
        public DateTime ConnectedAt { get; set; }
        public string Status { get; set; } = "Active";
    }
}