using System;

namespace ParallelComputingServer.Models
{
    public class BeaconMessage
    {
        public string ServerName { get; set; }
        public string ServerIP { get; set; }
        public int ServerPort { get; set; }
        public int ClientsConnected { get; set; }
        public int EndpointsConnected { get; set; }
        public bool GpuEnabled { get; set; }
        public DateTime Timestamp { get; set; }
    }
}