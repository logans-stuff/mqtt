using System.Collections.Generic;

namespace Meshtastic.Mqtt.Configuration
{
    public class AppSettings
    {
        public MqttBrokerSettings MqttBroker { get; set; } = new();
        public RateLimitingSettings RateLimiting { get; set; } = new();
        public PacketFilteringSettings PacketFiltering { get; set; } = new();
        public ConnectionModerationSettings ConnectionModeration { get; set; } = new();
        public MetricsSettings Metrics { get; set; } = new();
    }

    public class MqttBrokerSettings
    {
        public int Port { get; set; } = 8883;
        public bool UseSsl { get; set; } = true;
        public string CertificatePath { get; set; } = "certificate.pfx";
        public string CertificatePassword { get; set; } = "";
        public int MaxPendingMessagesPerClient { get; set; } = 100;
        public int ConnectionBacklog { get; set; } = 10;
    }

    public class RateLimitingSettings
    {
        public bool Enabled { get; set; } = true;
        public int DuplicatePacketWindow { get; set; } = 300; // seconds
        public PerNodeLimit PerNodePacketLimit { get; set; } = new();
        public GlobalLimit GlobalPacketLimit { get; set; } = new();
    }

    public class PerNodeLimit
    {
        public int MaxPacketsPerMinute { get; set; } = 60;
        public int BanDurationMinutes { get; set; } = 30;
    }

    public class GlobalLimit
    {
        public int MaxPacketsPerMinute { get; set; } = 1000;
    }

    public class PacketFilteringSettings
    {
        public bool BlockUnknownTopics { get; set; } = true;
        public List<string> AllowedTopics { get; set; } = new();
        public bool BlockUndecryptablePackets { get; set; } = true;
        public List<string> AllowedChannels { get; set; } = new();
        public List<int> BlockedPortNums { get; set; } = new();
        public List<int> AllowedPortNums { get; set; } = new();
        public int MaxHopCount { get; set; } = 3;
        public List<int> ZeroHopPortNums { get; set; } = new();
    }

    public class ConnectionModerationSettings
    {
        public Fail2BanSettings Fail2Ban { get; set; } = new();
        public bool RequireAuthentication { get; set; } = false;
        public List<string> AllowedClients { get; set; } = new();
        public List<string> BlockedClients { get; set; } = new();
        public List<string> KnownBadActors { get; set; } = new();
    }

    public class Fail2BanSettings
    {
        public bool Enabled { get; set; } = true;
        public int MaxFailedAuthAttempts { get; set; } = 5;
        public int BanDurationMinutes { get; set; } = 60;
        public int FindTimeMinutes { get; set; } = 10;
    }

    public class MetricsSettings
    {
        public bool Enabled { get; set; } = true;
        public bool TrackPerNodeStats { get; set; } = true;
        public int ExportInterval { get; set; } = 60; // seconds
    }
}
