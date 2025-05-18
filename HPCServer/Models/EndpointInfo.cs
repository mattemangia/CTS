using System;

namespace ParallelComputingServer.Models
{
    public class EndpointInfo
    {
        public string EndpointIP { get; set; }
        public int EndpointPort { get; set; }
        public string Name { get; set; }
        public string HardwareInfo { get; set; }
        public bool GpuEnabled { get; set; }
        public DateTime ConnectedAt { get; set; }
        public string Status { get; set; } = "Available";
        public double CpuLoadPercent { get; set; }
        public string CurrentTask { get; set; } = "None";
    }
}