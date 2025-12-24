# Zero-Hopping in Meshtastic - Complete Explanation

## What is Hopping?

In mesh networks, packets "hop" from node to node to reach distant destinations.

```
Node A ──→ Node B ──→ Node C ──→ Node D
  (hop 0)   (hop 1)   (hop 2)   (hop 3)
```

Each time a packet is forwarded, the hop count increases. Meshtastic packets include:
- **hop_start**: Maximum hops allowed (set by sender)
- **hop_limit**: Remaining hops (decrements each hop)

## What is Zero-Hopping?

**Zero-hopping means setting hop_limit to 0**, which prevents the packet from being forwarded by any other nodes. The packet can only reach direct neighbors (one radio transmission away).

### Why Zero-Hop Certain Packets?

Some packet types should NOT be forwarded across the mesh:

1. **Telemetry (PortNum 67)** - Battery %, voltage, temperature
   - Only relevant to nearby nodes
   - Would flood the network if everyone forwarded it
   
2. **Environmental Metrics (PortNum 68)** - Weather data
   - Localized data, not useful far away
   
3. **Private Messages** - Sometimes you only want direct communication
   
4. **Network Congestion** - Reduce traffic by not forwarding less critical data

## How Meshtastic Packets Work

A simplified Meshtastic packet structure:

```protobuf
message MeshPacket {
  uint32 from = 1;        // Sender node ID
  uint32 to = 2;          // Recipient (or broadcast)
  uint32 id = 3;          // Unique packet ID
  
  // Hop control
  uint32 hop_limit = 4;   // How many more hops allowed
  uint32 hop_start = 11;  // Original hop limit
  
  // Content
  oneof payload_variant {
    Data decoded = 5;     // Decrypted data
    bytes encrypted = 6;  // Encrypted data
  }
}

message Data {
  PortNum portnum = 1;    // Type of packet (1=text, 3=position, 67=telemetry)
  bytes payload = 2;      // The actual data
}
```

### Example Packet

```json
{
  "from": 123456789,
  "to": 4294967295,        // Broadcast
  "id": 987654321,
  "hop_limit": 3,          // Can hop 3 more times
  "hop_start": 3,          // Started with 3 hops
  "decoded": {
    "portnum": 67,         // Telemetry
    "payload": "..."       // Battery data
  }
}
```

When Node B receives and forwards it:
```json
{
  "hop_limit": 2,          // Decremented from 3 → 2
  "hop_start": 3,          // Stays the same
  // ... rest unchanged
}
```

## How Zero-Hopping is Implemented

### Current Implementation (Basic)

In the provided code, zero-hopping is partially implemented:

```csharp
// In PacketFilteringService.cs
public bool ShouldZeroHop(int portNum)
{
    return _settings.ZeroHopPortNums.Contains(portNum);
}

// In Program.cs - InterceptingPublishAsync handler
if (packetInfo.PortNum.HasValue && 
    packetFiltering.ShouldZeroHop(packetInfo.PortNum.Value))
{
    logger.LogDebug("Zero-hopping packet with port {PortNum}", packetInfo.PortNum.Value);
    // Modify hop count to 0 (in real implementation, modify the protobuf)
}
```

**Problem**: This is a placeholder! To actually modify the hop count, we need to:
1. Decode the protobuf packet
2. Change hop_limit to 0
3. Re-encode it
4. Replace the message payload

### Complete Working Implementation

Here's how to actually implement zero-hopping:

```csharp
using Google.Protobuf;
using Meshtastic.Protobufs;  // You need the Meshtastic protobuf library

public class PacketModificationService
{
    private readonly ILogger<PacketModificationService> _logger;
    private readonly PacketFilteringService _filteringService;

    public MqttApplicationMessage ProcessAndModifyPacket(
        MqttApplicationMessage message, 
        MeshtasticPacketInfo packetInfo)
    {
        // Only modify protobuf messages (not JSON)
        if (message.Topic.Contains("/json/"))
        {
            return message; // JSON messages handled differently
        }

        try
        {
            // Decode the ServiceEnvelope (outer wrapper)
            var envelope = ServiceEnvelope.Parser.ParseFrom(message.PayloadSegment);
            
            // Extract the MeshPacket
            var meshPacket = envelope.Packet;
            
            // Check if we should zero-hop this packet
            if (meshPacket.Decoded != null && 
                _filteringService.ShouldZeroHop((int)meshPacket.Decoded.Portnum))
            {
                // Create a modified copy
                var modifiedPacket = meshPacket.Clone();
                
                // Set hop_limit to 0 (prevents forwarding)
                modifiedPacket.HopLimit = 0;
                
                _logger.LogInformation(
                    "Zero-hopped packet: PortNum={Port}, From={From}, OriginalHops={Original}",
                    meshPacket.Decoded.Portnum,
                    meshPacket.From,
                    meshPacket.HopLimit
                );
                
                // Create new envelope with modified packet
                var modifiedEnvelope = new ServiceEnvelope
                {
                    Packet = modifiedPacket,
                    ChannelId = envelope.ChannelId,
                    GatewayId = envelope.GatewayId
                };
                
                // Re-encode to bytes
                var modifiedPayload = modifiedEnvelope.ToByteArray();
                
                // Create new MQTT message with modified payload
                var modifiedMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(message.Topic)
                    .WithPayload(modifiedPayload)
                    .WithQualityOfServiceLevel(message.QualityOfServiceLevel)
                    .WithRetainFlag(message.Retain)
                    .Build();
                
                return modifiedMessage;
            }
            
            return message; // No modification needed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error modifying packet for zero-hop");
            return message; // Return original on error
        }
    }
}
```

### Integration into Program.cs

```csharp
// In InterceptingPublishAsync handler
mqttServer.InterceptingPublishAsync += e =>
{
    var clientId = e.ClientId;
    var topic = e.ApplicationMessage.Topic;

    // Extract packet info
    var packetInfo = ExtractPacketInfo(e.ApplicationMessage);

    // ... rate limiting checks ...

    // ... validation checks ...

    // Modify packet if needed (zero-hopping, etc.)
    if (packetInfo.PortNum.HasValue && 
        packetFiltering.ShouldZeroHop(packetInfo.PortNum.Value))
    {
        // Modify the packet
        var modifiedMessage = packetModificationService.ProcessAndModifyPacket(
            e.ApplicationMessage, 
            packetInfo
        );
        
        // Replace the message
        e.ApplicationMessage = modifiedMessage;
    }

    return Task.CompletedTask;
};
```

## Real-World Example

### Scenario: Telemetry Packet

Node A sends battery telemetry:

```
Original Packet from Node A:
{
  "from": "!a1b2c3d4",
  "to": 4294967295,          // Broadcast
  "hop_limit": 3,
  "hop_start": 3,
  "decoded": {
    "portnum": 67,           // TELEMETRY_APP
    "payload": {
      "device_metrics": {
        "battery_level": 85,
        "voltage": 4.1
      }
    }
  }
}
```

**Without Zero-Hopping:**
```
Node A ──→ Node B ──→ Node C ──→ Node D
  (3 hops)  (2 hops)  (1 hop)   (0 hops)

Result: All nodes receive telemetry, using network capacity
```

**With Zero-Hopping:**
```
MQTT Broker intercepts packet, modifies:
{
  "hop_limit": 0,            // CHANGED FROM 3 → 0
  "hop_start": 3,
  // ... rest same
}

Node A ──→ Node B
  (0 hops)     X (won't forward, hop_limit=0)

Result: Only Node B gets it, network traffic reduced
```

## Configuration Examples

### Zero-hop all telemetry
```json
{
  "PacketFiltering": {
    "ZeroHopPortNums": [67]  // TELEMETRY_APP
  }
}
```

### Zero-hop multiple packet types
```json
{
  "PacketFiltering": {
    "ZeroHopPortNums": [
      67,  // TELEMETRY_APP
      68,  // ENVIRONMENTAL_MEASUREMENT_APP  
      71   // PRIVATE_APP
    ]
  }
}
```

### Zero-hop nothing (default behavior)
```json
{
  "PacketFiltering": {
    "ZeroHopPortNums": []
  }
}
```

## Meshtastic Port Numbers Reference

```
Common PortNums:
0   UNKNOWN_APP
1   TEXT_MESSAGE_APP          - Chat messages (should hop)
3   POSITION_APP              - GPS coordinates (should hop)
4   NODEINFO_APP              - Node information (should hop)
5   ROUTING_APP               - Mesh routing (should hop)
67  TELEMETRY_APP             - Battery, temp (consider zero-hop)
68  ENVIRONMENTAL_APP         - Weather data (consider zero-hop)
69  RANGE_TEST_APP            - Range testing (should hop)
71  PRIVATE_APP               - Private messages (consider zero-hop)
256 ATAK_PLUGIN              - ATAK integration (should hop)
```

## When to Use Zero-Hopping

### ✅ Good Candidates for Zero-Hop

1. **High-frequency telemetry** - Sent often, only useful nearby
2. **Environmental data** - Weather sensors, only relevant locally
3. **Device diagnostics** - Internal health metrics
4. **Private communications** - Direct node-to-node only

### ❌ Don't Zero-Hop

1. **Text messages** - Users expect these to reach far nodes
2. **Position updates** - Important for tracking across mesh
3. **Node info** - Needed for network topology
4. **Routing packets** - Critical for mesh functionality

## Performance Impact

### Network Load Comparison

**Scenario**: 10 nodes, each sending telemetry every 5 minutes

**Without Zero-Hop:**
- Each packet hops up to 3 times
- 10 nodes × 12 packets/hour × 3 hops = **360 transmissions/hour**

**With Zero-Hop:**
- Each packet reaches only direct neighbors
- 10 nodes × 12 packets/hour × 1 hop = **120 transmissions/hour**
- **67% reduction** in telemetry traffic

## Testing Zero-Hop

### Test Script

```bash
#!/bin/bash
# Send telemetry packet to MQTT broker

# Install dependencies
pip install meshtastic paho-mqtt protobuf

# Python test script
cat > test_zero_hop.py << 'EOF'
import paho.mqtt.client as mqtt
from meshtastic import mesh_pb2

# Create a telemetry packet
packet = mesh_pb2.MeshPacket()
packet.from_node = 123456789
packet.to = 0xFFFFFFFF  # Broadcast
packet.hop_limit = 3
packet.hop_start = 3
packet.decoded.portnum = mesh_pb2.PortNum.TELEMETRY_APP
packet.decoded.payload = b'{"battery_level": 85}'

# Wrap in ServiceEnvelope
envelope = mesh_pb2.ServiceEnvelope()
envelope.packet.CopyFrom(packet)
envelope.channel_id = "LongFast"

# Publish to MQTT
client = mqtt.Client()
client.connect("localhost", 8883)
client.publish("msh/US/2/e/LongFast/!node1", envelope.SerializeToString())
client.disconnect()

print("Packet sent - check broker logs for zero-hop modification")
EOF

python test_zero_hop.py
```

### What to Look For in Logs

```
[INFO] Zero-hopped packet: PortNum=67, From=123456789, OriginalHops=3
[DEBUG] Modified packet hop_limit: 3 → 0
[DEBUG] Forwarding modified packet to subscribers
```

## Advanced: Selective Zero-Hopping

Sometimes you want to zero-hop based on multiple conditions:

```csharp
public bool ShouldZeroHop(MeshtasticPacketInfo packet)
{
    // Always zero-hop telemetry
    if (packet.PortNum == 67)
        return true;
    
    // Zero-hop position updates if battery is low
    if (packet.PortNum == 3 && packet.BatteryLevel < 20)
        return true;
    
    // Zero-hop if packet has already hopped 2+ times
    if (packet.HopCount >= 2)
        return true;
    
    // Zero-hop during high network congestion
    if (_rateLimiting.GetCurrentPacketsPerMinute() > 500)
        return true;
    
    return false;
}
```

## Required Dependencies

To implement zero-hopping properly, add:

```bash
# Get Meshtastic protobufs
git clone https://github.com/meshtastic/protobufs.git
cd protobufs
protoc --csharp_out=. *.proto

# Or use the NuGet package (if available)
dotnet add package Meshtastic.Protobufs
```

Add to your `.csproj`:
```xml
<PackageReference Include="Google.Protobuf" Version="3.25.1" />
<PackageReference Include="Grpc.Tools" Version="2.60.0" />
```

## Summary

**Zero-hopping** modifies packets so they don't get forwarded by other nodes. You do this by:

1. Decoding the Meshtastic protobuf packet
2. Setting `hop_limit = 0`
3. Re-encoding and republishing

This is essential for reducing network congestion from high-frequency, low-priority packets like telemetry and environmental data.

The basic implementation I provided earlier is a placeholder - you need the protobuf decoding/encoding to actually modify the hop count. This document shows you exactly how to do that!
