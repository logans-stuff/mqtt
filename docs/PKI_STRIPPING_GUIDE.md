# Stripping PKI Fields and Handling MQTT Ignore Flags

## Overview

This guide explains how to strip PKI (Public Key Infrastructure) fields and handle "Ignore MQTT" preferences in Meshtastic packets.

## Why Strip PKI Fields?

### What are PKI Fields?

In Meshtastic, PKI fields in `MeshPacket` include:

```protobuf
message MeshPacket {
  // ... other fields ...
  
  bytes public_key = 16;        // Public key for encrypted DMs
  bool pki_encrypted = 17;      // Was packet encrypted using PKI?
  
  // ... other fields ...
}
```

### Why Remove Them?

**Reasons to strip PKI fields:**

1. **Privacy**: Public keys can be used to track users across the mesh
2. **Security**: Prevent key harvesting attacks
3. **Size**: PKI public keys are 32 bytes - saves bandwidth on MQTT
4. **Irrelevance**: MQTT consumers usually don't need PKI details
5. **Protocol Leakage**: Hide mesh internals from external systems

**When to strip:**
- Before forwarding to public MQTT brokers
- When bridging to external systems
- When aggregating packets for analytics
- For compliance with data minimization principles

## What is "Ignore MQTT"?

### The Meshtastic "Okay to MQTT" Setting

Meshtastic nodes have settings to control MQTT behavior:

**"Okay to MQTT"** (want_mqtt): User approves their packets being uploaded to MQTT
**"Ignore MQTT"** (ignore_mqtt): User wants to block packets that came via MQTT

### How It Works in Firmware

```
Node Settings:
├── mqtt.okay_to_mqtt = true/false    (should I send to MQTT?)
└── mqtt.ignore_mqtt = true/false     (should I ignore packets from MQTT?)
```

**The Problem:** 

The "Ignore MQTT" preference is stored in node settings, **not in the packet itself**. There's no field in `MeshPacket` that says "don't forward to MQTT."

**The Solution:**

The MQTT broker needs to:
1. Track which nodes have "Ignore MQTT" enabled
2. Block packets from those nodes before publishing to MQTT
3. Update the list when nodes change their preferences

## Implementation

### Fields to Strip from MeshPacket

```protobuf
message MeshPacket {
  uint32 from = 1;              // Keep - needed for routing
  uint32 to = 2;                // Keep - needed for routing
  uint32 channel = 3;           // Keep - needed for routing
  
  bytes encrypted = 4;          // Keep - actual payload
  Data decoded = 5;             // Keep - actual payload
  
  uint32 id = 6;                // Keep - needed for deduplication
  int32 rx_time = 7;            // Keep - useful timing info
  float rx_snr = 8;             // Keep - useful signal info
  int32 hop_limit = 9;          // Keep - useful for analytics
  bool want_ack = 10;           // Keep - protocol behavior
  Priority priority = 11;       // Keep - protocol behavior
  int32 rx_rssi = 12;           // Keep - useful signal info
  uint32 delayed = 13;          // Keep - protocol behavior
  bool want_response = 14;      // Keep - protocol behavior
  bytes payload_hash = 15;      // Keep - useful for dedup
  
  bytes public_key = 16;        // ⚠️ STRIP - privacy concern
  bool pki_encrypted = 17;      // ⚠️ STRIP - internal detail
  
  uint32 next_hop = 18;         // ⚠️ STRIP - internal routing
  uint32 relay_node = 19;       // ⚠️ STRIP - internal routing
  uint32 tx_after = 20;         // ⚠️ STRIP - internal timing
}
```

### Code Implementation

```csharp
public MqttApplicationMessage SanitizePacket(MqttApplicationMessage message)
{
    var envelope = ServiceEnvelope.Parser.ParseFrom(message.PayloadSegment.ToArray());
    var packet = envelope.Packet;
    var modified = packet.Clone();
    
    bool changed = false;
    
    // Strip PKI fields
    if (modified.PublicKey?.Length > 0)
    {
        _logger.LogInformation(
            "Stripping public_key: From={From}, Size={Size} bytes",
            $"!{modified.From:x8}",
            modified.PublicKey.Length
        );
        
        modified.PublicKey = ByteString.Empty;
        changed = true;
    }
    
    if (modified.PkiEncrypted)
    {
        _logger.LogDebug("Clearing pki_encrypted flag: From={From}", $"!{modified.From:x8}");
        modified.PkiEncrypted = false;
        changed = true;
    }
    
    // Strip internal routing fields
    if (modified.NextHop != 0 || modified.RelayNode != 0)
    {
        modified.NextHop = 0;
        modified.RelayNode = 0;
        modified.TxAfter = 0;
        changed = true;
    }
    
    if (!changed)
    {
        return message; // No changes needed
    }
    
    // Rebuild the envelope
    var newEnvelope = new ServiceEnvelope
    {
        Packet = modified,
        ChannelId = envelope.ChannelId,
        GatewayId = envelope.GatewayId
    };
    
    var newPayload = newEnvelope.ToByteArray();
    
    return new MqttApplicationMessageBuilder()
        .WithTopic(message.Topic)
        .WithPayload(newPayload)
        .WithQualityOfServiceLevel(message.QualityOfServiceLevel)
        .WithRetainFlag(message.Retain)
        .Build();
}
```

## Handling "Ignore MQTT" Preferences

Since there's no packet field for "ignore MQTT," we need to track it separately.

### Method 1: Configuration File

```json
{
  "PacketSanitization": {
    "MqttIgnoreList": [
      "!12345678",
      "!aabbccdd",
      "!deadbeef"
    ]
  }
}
```

### Method 2: Dynamic Learning

Monitor MQTT config packets to detect when nodes set "ignore_mqtt":

```csharp
if (packet.Decoded?.Portnum == PortNum.ADMIN_APP)
{
    // Parse AdminMessage
    var admin = AdminMessage.Parser.ParseFrom(packet.Decoded.Payload);
    
    if (admin.SetConfig?.Mqtt != null)
    {
        var mqttConfig = admin.SetConfig.Mqtt;
        var nodeId = $"!{packet.From:x8}";
        
        // Check if they're disabling MQTT
        if (!mqttConfig.Enabled || !mqttConfig.OkToMqtt)
        {
            _sanitizer.AddToMqttIgnoreList(nodeId);
            _logger.LogInformation("Node {NodeId} disabled MQTT", nodeId);
        }
        else
        {
            _sanitizer.RemoveFromMqttIgnoreList(nodeId);
            _logger.LogInformation("Node {NodeId} enabled MQTT", nodeId);
        }
    }
}
```

### Method 3: External API

Expose an API endpoint for nodes to register their preference:

```csharp
[HttpPost("api/mqtt-ignore/{nodeId}")]
public IActionResult SetMqttIgnore(string nodeId, [FromBody] bool ignore)
{
    if (ignore)
    {
        _sanitizer.AddToMqttIgnoreList(nodeId);
    }
    else
    {
        _sanitizer.RemoveFromMqttIgnoreList(nodeId);
    }
    
    return Ok();
}
```

## Configuration

Add to `appsettings.json`:

```json
{
  "PacketSanitization": {
    "StripPkiFields": true,
    "StripInternalRoutingFields": true,
    "RespectMqttIgnore": true,
    "MqttIgnoreList": [
      "!node1",
      "!node2"
    ],
    "ReducePositionPrecision": false,
    "TargetPositionPrecisionBits": 12,
    "LogPkiStripping": true
  }
}
```

## Integration with Main Program

```csharp
// In Program.cs

// Configure services
services.AddSingleton(new PacketSanitizationService(
    _appSettings.PacketSanitization,
    _serviceProvider?.GetService<ILogger<PacketSanitizationService>>()
));

// In MQTT message handler
mqttServer.InterceptingPublishAsync += e =>
{
    var packetInfo = ExtractPacketInfo(e.ApplicationMessage);
    
    // Check if node is on ignore list
    if (sanitizer.ShouldBlockFromMqtt(packetInfo.NodeId))
    {
        _logger.LogInformation(
            "Blocking packet from {NodeId} - on MQTT ignore list",
            packetInfo.NodeId
        );
        e.ProcessPublish = false;
        return Task.CompletedTask;
    }
    
    // Sanitize the packet
    var sanitized = sanitizer.SanitizePacket(
        e.ApplicationMessage,
        packetInfo
    );
    
    if (sanitized == null)
    {
        // Packet was blocked
        e.ProcessPublish = false;
    }
    else if (sanitized != e.ApplicationMessage)
    {
        // Packet was modified
        e.ApplicationMessage = sanitized;
    }
    
    return Task.CompletedTask;
};
```

## Privacy and Security Considerations

### Why PKI Stripping Matters

**Scenario 1: Key Harvesting**

```
Attacker monitors public MQTT broker:
1. Collects public_key from every packet
2. Builds database of node_id → public_key
3. Can now track users across different mesh networks
4. Can attempt to decrypt intercepted mesh traffic
```

**Scenario 2: Traffic Analysis**

```
Without stripping PKI flags:
- pki_encrypted = true → "This is a DM"
- pki_encrypted = false → "This is a broadcast"

Result: Attacker knows communication patterns
```

### Best Practices

1. **Always strip PKI fields** for public brokers
2. **Keep PKI fields** only for trusted, private brokers
3. **Log PKI stripping** for security auditing
4. **Rotate keys regularly** (even if stripped)
5. **Monitor for suspicious key patterns**

## Performance Impact

### Before Stripping (Typical Packet)

```
ServiceEnvelope
├── Packet (MeshPacket)
│   ├── from: 4 bytes
│   ├── to: 4 bytes
│   ├── id: 4 bytes
│   ├── public_key: 32 bytes  ⬅️ Can be stripped
│   ├── pki_encrypted: 1 byte ⬅️ Can be stripped
│   ├── encrypted: 200 bytes (payload)
│   └── ...
└── channel_id, gateway_id

Total size: ~250 bytes
```

### After Stripping

```
ServiceEnvelope
├── Packet (MeshPacket)
│   ├── from: 4 bytes
│   ├── to: 4 bytes
│   ├── id: 4 bytes
│   ├── encrypted: 200 bytes (payload)
│   └── ...
└── channel_id, gateway_id

Total size: ~217 bytes

Savings: 33 bytes (13% reduction)
```

### Impact on Network

```
Scenario: 1000 packets/hour with PKI fields

Before: 1000 × 250 bytes = 250 KB/hour
After:  1000 × 217 bytes = 217 KB/hour

Bandwidth saved: 33 KB/hour (13%)
```

## Testing

### Test 1: Verify PKI Stripping

```python
import paho.mqtt.client as mqtt
from meshtastic import mesh_pb2, mqtt_pb2

# Create a packet WITH PKI fields
packet = mesh_pb2.MeshPacket()
packet.from_node = 0x12345678
packet.id = 123456
packet.public_key = b'\\x00' * 32  # 32-byte public key
packet.pki_encrypted = True
packet.decoded.portnum = mesh_pb2.PortNum.TEXT_MESSAGE_APP
packet.decoded.payload = b'Hello World'

envelope = mqtt_pb2.ServiceEnvelope()
envelope.packet.CopyFrom(packet)
envelope.channel_id = "LongFast"

# Publish to broker
client = mqtt.Client()
client.connect("localhost", 8883)
client.publish("msh/US/2/e/LongFast/!12345678", envelope.SerializeToString())

# Subscribe and verify
def on_message(client, userdata, msg):
    received = mqtt_pb2.ServiceEnvelope.FromString(msg.payload)
    
    # Check if PKI fields were stripped
    assert len(received.packet.public_key) == 0, "public_key not stripped!"
    assert not received.packet.pki_encrypted, "pki_encrypted not cleared!"
    
    print("✓ PKI fields successfully stripped")

client.on_message = on_message
client.subscribe("msh/US/2/e/LongFast/#")
client.loop_forever()
```

### Test 2: Verify MQTT Ignore List

```csharp
// Add node to ignore list
sanitizer.AddToMqttIgnoreList("!aabbccdd");

// Try to publish from that node
var packet = CreateTestPacket(fromNode: 0xaabbccdd);
bool blocked = sanitizer.ShouldBlockFromMqtt("!aabbccdd");

Assert.IsTrue(blocked, "Packet should be blocked");
```

## Monitoring

Add metrics to track sanitization:

```csharp
public Dictionary<string, object> GetStats()
{
    return new Dictionary<string, object>
    {
        ["TotalPackets"] = _totalPackets,
        ["PacketsWithPkiStripped"] = _strippedPkiCount,
        ["PacketsBlockedByIgnoreList"] = _blockedCount,
        ["AverageSizeSaved"] = _totalBytesSaved / Math.Max(1, _strippedPkiCount),
        ["IgnoreListSize"] = _mqttIgnoreList.Count
    };
}
```

Log periodically:

```
[INFO] Sanitization Stats:
  - Packets processed: 1000
  - PKI fields stripped: 234 (23%)
  - Packets blocked (ignore list): 12 (1.2%)
  - Bandwidth saved: 7.8 KB
  - Nodes on ignore list: 5
```

## Summary

**PKI Stripping:**
- Removes `public_key` (32 bytes) and `pki_encrypted` (1 byte)
- Protects user privacy
- Reduces bandwidth by ~13%
- Prevents key harvesting attacks

**MQTT Ignore:**
- No packet field exists for this
- Must track node preferences separately
- Can learn from config packets or use API
- Blocks packets before MQTT publication

**Best Practice:**
- Always strip PKI on public brokers
- Respect user MQTT preferences
- Log all sanitization operations
- Monitor for suspicious patterns

Both features work together to create a privacy-respecting, efficient MQTT broker for Meshtastic networks!
