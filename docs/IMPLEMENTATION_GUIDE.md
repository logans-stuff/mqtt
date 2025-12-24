# Meshtastic MQTT Broker Configuration Guide

## Overview

This guide explains how to configure and extend the Meshtastic MQTT broker with the following features:

- **Configuration System**: JSON-based configuration with hot-reload support
- **Rate Limiting**: Duplicate packet detection and per-node rate limiting
- **Fail2Ban**: Connection moderation with automatic banning
- **Packet Filtering**: Topic validation, port number filtering, and hop count limits
- **Metrics**: Statistics tracking and reporting

## Understanding the Current Implementation

The original project is a **boilerplate** - meaning it provides basic MQTT broker functionality but the advanced features like "fail2ban style connection moderation" and "blocking unknown topics" are listed as **IDEAS** for implementation, not actual working features.

### What the Features Mean

#### 1. **Fail2Ban Style Connection Moderation**

Fail2ban is a security tool that monitors connection attempts and temporarily bans clients that show suspicious behavior.

**How it works in this implementation:**
- Tracks failed authentication attempts per client
- If a client fails authentication X times within Y minutes, they get banned
- Ban lasts for a configurable duration
- After ban expires, client can try again

**Configuration:**
```json
"ConnectionModeration": {
  "Fail2Ban": {
    "Enabled": true,
    "MaxFailedAuthAttempts": 5,      // Ban after 5 failures
    "BanDurationMinutes": 60,         // Ban for 1 hour
    "FindTimeMinutes": 10             // Look back 10 minutes for attempts
  }
}
```

#### 2. **Blocking Unknown Topics**

MQTT uses a publish/subscribe model with topics. Meshtastic devices publish to specific topic patterns:
- `msh/US/2/e/LongFast/!12345678` - Encrypted packets
- `msh/US/2/json/mqtt/!12345678` - JSON packets

**How it works:**
- Maintains a whitelist of allowed topic patterns
- Uses MQTT wildcards: `+` (single level), `#` (multiple levels)
- Rejects any publish/subscribe to topics not matching the patterns

**Configuration:**
```json
"PacketFiltering": {
  "BlockUnknownTopics": true,
  "AllowedTopics": [
    "msh/+/2/e/+/+",      // Allow all encrypted packets
    "msh/+/2/json/+/+"    // Allow all JSON packets
  ]
}
```

#### 3. **Rate Limiting Duplicate Packets**

In mesh networks, the same packet can be received multiple times from different routes.

**How it works:**
- Generates a hash of each packet's content
- Stores hashes with timestamps
- If same hash appears within configured window, packet is dropped
- Prevents network flooding from packet loops

**Configuration:**
```json
"RateLimiting": {
  "DuplicatePacketWindow": 300  // Ignore duplicates within 5 minutes
}
```

#### 4. **Rate Limiting Per Node**

Prevents a single device from flooding the network.

**How it works:**
- Tracks packets sent per node per minute
- If node exceeds limit, temporarily ban them
- Ban duration is configurable

**Configuration:**
```json
"RateLimiting": {
  "PerNodePacketLimit": {
    "MaxPacketsPerMinute": 60,    // Max 1 packet per second
    "BanDurationMinutes": 30      // Ban for 30 minutes if exceeded
  }
}
```

#### 5. **Zero Hopping Certain Packets**

Some packet types shouldn't be forwarded across the mesh (hop count = 0).

**How it works:**
- Certain port numbers (packet types) are configured to not hop
- When detected, the broker sets hop count to 0
- Useful for telemetry that should stay local

**Configuration:**
```json
"PacketFiltering": {
  "ZeroHopPortNums": [67]  // Telemetry packets don't hop
}
```

Port numbers in Meshtastic:
- 1: TEXT_MESSAGE_APP
- 3: POSITION_APP  
- 4: NODEINFO_APP
- 67: TELEMETRY_APP

## Installation Steps

### 1. Update Project Dependencies

Add these NuGet packages to `Meshtastic.Mqtt.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="MQTTnet" Version="4.3.6.1152" />
  <PackageReference Include="Serilog" Version="3.1.1" />
  <PackageReference Include="Serilog.Extensions.Logging" Version="8.0.0" />
  <PackageReference Include="Serilog.Sinks.Console" Version="5.0.1" />
  <PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />
  <PackageReference Include="Serilog.Settings.Configuration" Version="8.0.0" />
  <PackageReference Include="Microsoft.Extensions.Configuration" Version="8.0.0" />
  <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.0" />
  <PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="8.0.0" />
  <PackageReference Include="Microsoft.Extensions.Configuration.CommandLine" Version="8.0.0" />
  <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />
</ItemGroup>
```

### 2. Add Configuration Files

Create `appsettings.json` in your project root (see the provided example).

### 3. Add Service Classes

Copy the provided files:
- `Configuration.cs` - Configuration models
- `RateLimitingServices.cs` - Rate limiting and fail2ban
- `PacketFilteringService.cs` - Packet validation
- `Program.cs` - Main application

### 4. Update Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["Meshtastic.Mqtt.csproj", "./"]
RUN dotnet restore "Meshtastic.Mqtt.csproj"
COPY . .
RUN dotnet build "Meshtastic.Mqtt.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Meshtastic.Mqtt.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=publish /app/publish .

# Copy configuration
COPY appsettings.json .

EXPOSE 8883
ENTRYPOINT ["dotnet", "Meshtastic.Mqtt.dll"]
```

## Configuration Examples

### Strict Security Setup

```json
{
  "MqttBroker": {
    "Port": 8883,
    "UseSsl": true
  },
  "RateLimiting": {
    "Enabled": true,
    "DuplicatePacketWindow": 300,
    "PerNodePacketLimit": {
      "MaxPacketsPerMinute": 30,
      "BanDurationMinutes": 60
    }
  },
  "PacketFiltering": {
    "BlockUnknownTopics": true,
    "BlockUndecryptablePackets": true,
    "AllowedTopics": [
      "msh/US/2/e/LongFast/+",
      "msh/US/2/e/ShortFast/+"
    ],
    "MaxHopCount": 3
  },
  "ConnectionModeration": {
    "Fail2Ban": {
      "Enabled": true,
      "MaxFailedAuthAttempts": 3,
      "BanDurationMinutes": 120,
      "FindTimeMinutes": 5
    },
    "RequireAuthentication": true,
    "AllowedClients": ["gateway1", "gateway2"]
  }
}
```

### Open Testing Setup

```json
{
  "MqttBroker": {
    "Port": 1883,
    "UseSsl": false
  },
  "RateLimiting": {
    "Enabled": true,
    "PerNodePacketLimit": {
      "MaxPacketsPerMinute": 120
    }
  },
  "PacketFiltering": {
    "BlockUnknownTopics": false,
    "BlockUndecryptablePackets": false
  },
  "ConnectionModeration": {
    "Fail2Ban": {
      "Enabled": false
    },
    "RequireAuthentication": false
  }
}
```

### Regional Mesh Network

```json
{
  "PacketFiltering": {
    "BlockUnknownTopics": true,
    "AllowedTopics": [
      "msh/US/2/e/+/+",
      "msh/CA/2/e/+/+"
    ],
    "AllowedChannels": ["LongFast", "MediumFast"],
    "MaxHopCount": 5,
    "ZeroHopPortNums": [67, 68],
    "BlockedPortNums": []
  }
}
```

## Environment Variables

Override configuration via environment variables:

```bash
# Docker
docker run -e MqttBroker__Port=1883 \
           -e RateLimiting__Enabled=true \
           -e ConnectionModeration__Fail2Ban__Enabled=true \
           meshtastic-mqtt-broker

# Docker Compose
environment:
  - MqttBroker__Port=1883
  - MqttBroker__UseSsl=false
  - RateLimiting__Enabled=true
```

## Monitoring and Metrics

Enable metrics to see broker statistics:

```json
{
  "Metrics": {
    "Enabled": true,
    "TrackPerNodeStats": true,
    "ExportInterval": 60
  }
}
```

Statistics logged every 60 seconds:
- Active bans and banned clients
- Packets per minute
- Allowed vs blocked packets
- Block rate percentage

## Testing Your Configuration

### 1. Test Connection Blocking

```bash
# Should be rejected if not in AllowedClients
mosquitto_pub -h localhost -p 8883 -i "test_client" -t "test" -m "hello"
```

### 2. Test Topic Filtering

```bash
# Should be blocked if BlockUnknownTopics=true
mosquitto_pub -h localhost -p 8883 -t "invalid/topic" -m "test"

# Should work if in AllowedTopics
mosquitto_pub -h localhost -p 8883 -t "msh/US/2/e/LongFast/!12345678" -m "test"
```

### 3. Test Rate Limiting

```bash
# Send many packets rapidly
for i in {1..100}; do
  mosquitto_pub -h localhost -p 8883 -t "msh/US/2/e/test/!node1" -m "packet$i"
done
# Node should get rate limited after configured threshold
```

### 4. Test Fail2Ban

Try connecting with wrong credentials multiple times:
```bash
for i in {1..10}; do
  mosquitto_pub -h localhost -p 8883 -u "wrong" -P "wrong" -t "test" -m "hi"
done
# Client should get banned after MaxFailedAuthAttempts
```

## Advanced Customization

### Adding Custom Packet Validation

Extend `PacketFilteringService.cs`:

```csharp
public bool ValidateCustomRule(MeshtasticPacketInfo packet)
{
    // Example: Block packets from specific geographic regions
    if (packet.Latitude.HasValue && packet.Longitude.HasValue)
    {
        if (IsInRestrictedZone(packet.Latitude.Value, packet.Longitude.Value))
        {
            _logger.LogWarning("Blocked packet from restricted zone");
            return false;
        }
    }
    return true;
}
```

### Adding Blocklists from External Sources

```csharp
// Load bad actors list from file or API
public async Task LoadBadActorsList()
{
    var badActors = await File.ReadAllLinesAsync("bad_actors.txt");
    foreach (var actor in badActors)
    {
        _appSettings.ConnectionModeration.KnownBadActors.Add(actor);
    }
}
```

### Integrating with Prometheus

Add metrics export:

```csharp
// Install Prometheus.NET package
using Prometheus;

var packetsProcessed = Metrics.CreateCounter("mqtt_packets_processed", "Packets processed");
var packetsBlocked = Metrics.CreateCounter("mqtt_packets_blocked", "Packets blocked");

// In packet handling:
packetsProcessed.Inc();
if (!validationResult.IsValid)
{
    packetsBlocked.Inc();
}
```

## Troubleshooting

### Issue: All packets being blocked

Check:
- `BlockUnknownTopics` might be too restrictive
- `AllowedTopics` patterns are correct
- Topics match Meshtastic format: `msh/REGION/2/e/CHANNEL/NODEID`

### Issue: High CPU usage

Reduce:
- `DuplicatePacketWindow` to prevent storing too many hashes
- Enable periodic cleanup in `RateLimitingService`

### Issue: Legitimate clients getting banned

Adjust:
- Increase `MaxFailedAuthAttempts`
- Increase `FindTimeMinutes` window
- Reduce `BanDurationMinutes`

## Next Steps

1. Implement actual Meshtastic protobuf decoding
2. Add database persistence for bans and statistics
3. Create web dashboard for monitoring
4. Add webhook notifications for security events
5. Implement channel key management

## Resources

- [MQTTnet Documentation](https://github.com/dotnet/MQTTnet/wiki)
- [Meshtastic MQTT Documentation](https://meshtastic.org/docs/software/integrations/mqtt/)
- [Meshtastic Protobuf Definitions](https://github.com/meshtastic/protobufs)
