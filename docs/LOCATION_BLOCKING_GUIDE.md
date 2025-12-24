# Blocking and Stripping Location Data from Meshtastic Packets

## Overview

Location data in Meshtastic comes through **two separate mechanisms**:

1. **POSITION_APP packets** (PortNum = 3) - Dedicated position updates
2. **NODEINFO messages** - Contains user info, position stored separately in NodeDB

This guide covers how to block POSITION packets entirely or strip location data from them.

## Understanding Location Data in Meshtastic

### 1. POSITION_APP Packets (PortNum 3)

These are dedicated packets containing GPS coordinates:

```protobuf
message Position {
  sfixed32 latitude_i = 1;          // Latitude * 1e7
  sfixed32 longitude_i = 2;         // Longitude * 1e7
  int32 altitude = 3;               // Altitude MSL (meters)
  int32 altitude_hae = 9;           // Height above ellipsoid
  int32 altitude_geoidal_separation = 10;
  fixed32 time = 4;                 // Unix timestamp
  fixed32 timestamp = 7;            // GPS timestamp
  uint32 precision_bits = 23;       // Location precision (privacy)
  uint32 ground_speed = 15;         // Speed in m/s
  uint32 ground_track = 16;         // Heading in degrees
  uint32 PDOP = 11;                 // Position DOP
  uint32 HDOP = 12;                 // Horizontal DOP
  uint32 VDOP = 13;                 // Vertical DOP
  uint32 sats_in_view = 19;         // Satellite count
  LocSource location_source = 5;    // GPS/Manual/etc
  AltSource altitude_source = 6;    // GPS/Manual/Barometric
  uint32 seq_number = 22;           // Sequence counter
  // ... more fields
}
```

**Example packet structure:**
```
MeshPacket {
  from: 0x12345678
  to: 0xFFFFFFFF (broadcast)
  decoded: {
    portnum: POSITION_APP (3)
    payload: <Position protobuf>
  }
}
```

### 2. NODEINFO_APP Packets (PortNum 4)

These contain **User info**, NOT position directly:

```protobuf
message User {
  string id = 1;                    // Node ID like "!12345678"
  string long_name = 2;             // "John Smith"
  string short_name = 3;            // "JS"
  HardwareModel hw_model = 5;       // HELTEC_V3, etc
  bool is_licensed = 6;             // Ham radio license
  Config.DeviceConfig.Role role = 7; // CLIENT, ROUTER, etc
  bytes public_key = 8;             // PKI public key
  // NO position field here!
}
```

**Important**: Position is stored in the **NodeInfo** message (used internally), not in the NODEINFO_APP packet:

```protobuf
message NodeInfo {
  uint32 num = 1;                   // Node number
  User user = 2;                    // User info (see above)
  Position position = 3;            // ⬅️ Position stored HERE
  float snr = 4;
  fixed32 last_heard = 5;
  // ... more fields
}
```

**NodeInfo is used for:**
- Internal node database on devices
- Phone app communication via Bluetooth/Serial
- NOT sent as MQTT packets

**NODEINFO_APP packets over MQTT contain User only**, not the full NodeInfo with position.

## What Can You Block/Strip?

### Option 1: Block All POSITION Packets

**What it does:** Drops all POSITION_APP packets before publishing to MQTT

**Use cases:**
- Privacy-focused networks
- Reduce MQTT traffic
- Comply with location privacy policies

**Configuration:**
```json
{
  "LocationFilter": {
    "BlockPositionPackets": true
  }
}
```

### Option 2: Strip Location Data from POSITION Packets

**What it does:** Keeps POSITION packets but removes lat/lon/altitude

**Keeps:**
- Timestamp
- GPS fix quality
- Satellite count
- DOP values
- Sequence numbers

**Removes:**
- latitude_i
- longitude_i
- altitude
- altitude_hae
- ground_speed
- ground_track

**Use cases:**
- Want timing data without location
- Network diagnostics without revealing positions
- Privacy with some metadata

**Configuration:**
```json
{
  "LocationFilter": {
    "StripPositionData": true
  }
}
```

### Option 3: Reduce Position Precision

**What it does:** Degrades GPS accuracy to protect privacy

**Precision levels:**
```
32 bits = centimeter accuracy (~0.01m)
16 bits = ~10 meter accuracy
12 bits = ~1-2 kilometer accuracy  ⬅️ RECOMMENDED for privacy
10 bits = ~5 kilometer accuracy
0 bits  = No location data
```

**Example:**
```
Original: 37.7749° N, 122.4194° W (San Francisco exact location)
12-bit:   37.78° N, 122.42° W (general SF area)
```

**Configuration:**
```json
{
  "LocationFilter": {
    "ReducePositionPrecision": true,
    "TargetPrecisionBits": 12
  }
}
```

### Option 4: Selective Blocking (Per-Node)

**Allow specific nodes to share location:**
```json
{
  "LocationFilter": {
    "BlockPositionPackets": true,
    "AllowPositionFromNodes": [
      "!gateway1",
      "!fixed-station"
    ]
  }
}
```

**Block specific nodes from sharing location:**
```json
{
  "LocationFilter": {
    "BlockPositionFromNodes": [
      "!mobile-node",
      "!private-device"
    ]
  }
}
```

## Implementation Details

### Blocking POSITION Packets

```csharp
public MqttApplicationMessage ProcessPacket(MqttApplicationMessage message)
{
    var envelope = ServiceEnvelope.Parser.ParseFrom(message.PayloadSegment.ToArray());
    var packet = envelope.Packet;
    
    if (packet.Decoded?.Portnum == PortNum.POSITION_APP)
    {
        if (_settings.BlockPositionPackets)
        {
            _logger.LogInformation("Blocking POSITION packet from {NodeId}", 
                $"!{packet.From:x8}");
            return null; // Drop the packet
        }
    }
    
    return message;
}
```

### Stripping Location Data

```csharp
private MqttApplicationMessage StripPositionData(MqttApplicationMessage message, MeshPacket packet)
{
    // Parse the Position from payload
    var position = Position.Parser.ParseFrom(packet.Decoded.Payload);
    var modified = position.Clone();
    
    // Strip location fields
    modified.ClearLatitudeI();
    modified.ClearLongitudeI();
    modified.ClearAltitude();
    modified.ClearAltitudeHae();
    modified.ClearGroundSpeed();
    modified.ClearGroundTrack();
    
    // Keep timing and quality fields
    // - time
    // - timestamp
    // - PDOP, HDOP, VDOP
    // - sats_in_view
    // - fix_quality
    
    // Rebuild packet with modified position
    var modifiedPacket = packet.Clone();
    modifiedPacket.Decoded.Payload = modified.ToByteArray();
    
    // Rebuild envelope and MQTT message
    return RebuildMessage(message, modifiedPacket);
}
```

### Reducing Precision

```csharp
if (position.PrecisionBits > _settings.TargetPrecisionBits)
{
    position.PrecisionBits = (uint)_settings.TargetPrecisionBits;
    
    _logger.LogDebug("Reduced precision to {Bits} bits", _settings.TargetPrecisionBits);
}
```

## Full Configuration Example

```json
{
  "LocationFilter": {
    "BlockPositionPackets": false,
    "StripPositionData": false,
    "ReducePositionPrecision": true,
    "TargetPrecisionBits": 12,
    "StripNodeInfoPosition": false,
    "AllowPositionFromNodes": [],
    "BlockPositionFromNodes": []
  }
}
```

## Integration with Program.cs

```csharp
// In ConfigureServices
services.AddSingleton(new LocationFilterService(
    _appSettings.LocationFilter,
    _serviceProvider?.GetService<ILogger<LocationFilterService>>()
));

// In InterceptingPublishAsync handler
mqttServer.InterceptingPublishAsync += e =>
{
    var locationFilter = _serviceProvider.GetRequiredService<LocationFilterService>();
    
    // Process packet for location filtering
    var result = locationFilter.ProcessPacket(e.ApplicationMessage);
    
    if (result == null)
    {
        // Packet was blocked
        e.ProcessPublish = false;
    }
    else if (result != e.ApplicationMessage)
    {
        // Packet was modified
        e.ApplicationMessage = result;
    }
    
    return Task.CompletedTask;
};
```

## Packet Flow Examples

### Example 1: Block All Position Packets

```
Input: POSITION packet from !12345678
  → LocationFilter.ProcessPacket()
  → Check: portnum == POSITION_APP?  YES
  → Check: BlockPositionPackets?  YES
  → Result: return null (DROP)

Output: Packet not published to MQTT
```

### Example 2: Strip Location Data

```
Input: POSITION packet
  lat: 37.7749 * 1e7
  lon: -122.4194 * 1e7
  alt: 10m
  time: 1703123456
  sats_in_view: 12

Processing:
  → Parse Position from payload
  → Clear: latitude_i, longitude_i, altitude
  → Keep: time, sats_in_view, PDOP, etc
  → Re-encode and rebuild

Output: POSITION packet
  time: 1703123456
  sats_in_view: 12
  PDOP: 180
  // No lat/lon/alt
```

### Example 3: Reduce Precision

```
Input: POSITION packet
  lat: 37.774900 * 1e7 (exact)
  lon: -122.419400 * 1e7 (exact)
  precision_bits: 32

Processing:
  → Check: precision_bits (32) > target (12)?  YES
  → Set: precision_bits = 12
  → Coordinates stay same, but precision indicator changed

Output: POSITION packet
  lat: 37.774900 * 1e7 (still exact in proto)
  lon: -122.419400 * 1e7 (still exact in proto)
  precision_bits: 12
  // Clients should round based on precision_bits
```

**Note:** The actual coordinates aren't rounded by the broker - the `precision_bits` field tells clients how precise the data is. Clients should round appropriately.

To actually round coordinates:
```csharp
// Calculate precision mask based on bits
int shift = 32 - _settings.TargetPrecisionBits;
int mask = ~((1 << shift) - 1);

// Apply mask to coordinates
modified.LatitudeI = position.LatitudeI & mask;
modified.LongitudeI = position.LongitudeI & mask;
modified.PrecisionBits = (uint)_settings.TargetPrecisionBits;
```

## Testing

### Test 1: Verify Position Blocking

```bash
# Subscribe to MQTT
mosquitto_sub -h localhost -p 8883 -t 'msh/+/2/e/+/+' -v

# From a Meshtastic device, send position update
# Should NOT appear in MQTT if BlockPositionPackets=true
```

### Test 2: Verify Location Stripping

```python
import paho.mqtt.client as mqtt
from meshtastic import mesh_pb2, mqtt_pb2

def on_message(client, userdata, msg):
    envelope = mqtt_pb2.ServiceEnvelope.FromString(msg.payload)
    packet = envelope.packet
    
    if packet.decoded.portnum == mesh_pb2.PortNum.POSITION_APP:
        position = mesh_pb2.Position.FromString(packet.decoded.payload)
        
        # Verify location stripped
        assert not position.HasField('latitude_i'), "Latitude should be stripped!"
        assert not position.HasField('longitude_i'), "Longitude should be stripped!"
        assert not position.HasField('altitude'), "Altitude should be stripped!"
        
        # Verify timing kept
        assert position.time > 0, "Timestamp should be present"
        
        print("✓ Location data successfully stripped")

client = mqtt.Client()
client.on_message = on_message
client.connect("localhost", 8883)
client.subscribe("msh/US/2/e/LongFast/#")
client.loop_forever()
```

## Important Notes About NODEINFO

**NODEINFO_APP packets do NOT contain position data** when sent over MQTT. They only contain User information (name, hardware model, etc).

Position is stored in the NodeInfo message which is used:
- Internally in device node databases
- For phone/PC app communication
- NOT for MQTT packets

So you **don't need to strip position from NODEINFO** packets - there isn't any there!

## Performance Impact

### Blocking POSITION Packets

```
Blocked packets saved: ~100-200 bytes per position update
Frequency: Every 30-300 seconds per node (configurable)
Network with 10 nodes: Save ~4-40 KB/hour
```

### Stripping vs Blocking

```
Full POSITION packet: ~120 bytes
Stripped POSITION packet: ~40 bytes
Savings: ~80 bytes (67% reduction)

vs Blocking: 100% reduction

Use stripping when you want timing/quality data without location
Use blocking when you don't want any position packets at all
```

## Privacy Considerations

**Blocking is strongest privacy:**
- No location data at all
- No metadata about GPS state
- Simplest implementation

**Stripping is moderate privacy:**
- No exact location
- But reveals: GPS is active, fix quality, satellite count
- Someone could infer approximate activity

**Precision reduction is weakest:**
- Location still revealed (just less precise)
- 12-bit precision still shows ~1-2km area
- May not be sufficient for high-security scenarios

## Summary

| Feature | What Gets Removed | What Stays | Use Case |
|---------|------------------|------------|----------|
| Block Packets | Entire packet | Nothing | Maximum privacy |
| Strip Location | Lat/Lon/Alt/Speed | Time, GPS quality, sat count | Privacy + diagnostics |
| Reduce Precision | Exact coordinates | Approximate area (~1-2km) | Privacy-friendly mapping |

**Two separate things:**
1. **POSITION_APP packets** (PortNum 3) - Contain actual GPS coordinates
2. **NODEINFO_APP packets** (PortNum 4) - Contain user info only, no position

You need to filter POSITION packets, not NODEINFO!
