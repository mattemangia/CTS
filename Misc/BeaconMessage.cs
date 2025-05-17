

namespace CTS
{
    /// <summary>
    /// Represents a message broadcast by compute nodes for discovery
    /// </summary>
    public class BeaconMessage
    {
        public string ServerName { get; set; }
        public string ServerIP { get; set; }
        public int ServerPort { get; set; }
        public int NodesConnected { get; set; }
        public bool GpuEnabled { get; set; }
        public System.DateTime Timestamp { get; set; }
    }
}