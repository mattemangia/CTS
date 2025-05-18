

using System;

namespace CTS
{
    /// <summary>
    /// Beacon message format for server discovery
    /// </summary>
    public class BeaconMessage
    {
        public string ServerName { get; set; }
        public string ServerIP { get; set; }
        public int ServerPort { get; set; }
        public int NodesConnected { get; set; }
        public bool GpuEnabled { get; set; }
        public DateTime Timestamp { get; set; }
    }
}