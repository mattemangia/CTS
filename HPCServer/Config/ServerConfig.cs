using System;

namespace ParallelComputingServer.Config
{
    public class ServerConfig
    {
        // Client connection settings
        public int ServerPort { get; set; } = 7000;

        // Beacon service settings
        public int BeaconPort { get; set; } = 7001;
        public int BeaconIntervalMs { get; set; } = 5000;

        // Endpoint connection settings
        public int EndpointPort { get; set; } = 7002;
    }
}