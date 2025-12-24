# Stripping the "OK to MQTT" Bitfield Flag

## The Correct Field to Strip

Based on the Meshtastic protobuf definition, the "OK to MQTT" flag is stored in the **`Data` message's `bitfield`** (field 9), **NOT** in MeshPacket:

```protobuf
message Data {
  PortNum portnum = 1;
  bytes payload = 2;
  bool want_response = 3;
  fixed32 dest = 4;
  fixed32 source = 5;
  fixed32 request_id = 6;
  fixed32 reply_id = 7;
  fixed32 emoji = 8;
  
  // ⬇️ THIS IS THE FIELD WE NEED TO CLEAR
  optional uint32 bitfield = 9;  // Contains "OK to MQTT" approval flag
}
```

The comment in mesh.proto says:
> "Bitfield for extra flags. First use is to indicate that user approves the packet being uploaded to MQTT."

## Why Strip This Flag?

The "OK to MQTT" bitfield is **user consent** for their packet to be uploaded to MQTT. If this bit is set, it means:
- The user has approved this specific packet being forwarded to MQTT
- The user's device settings include "Okay to MQTT" = true

**We should strip it because:**
1. It's metadata about internal consent, not part of the actual message
2. External MQTT consumers don't need to know about internal permissions
3. Reduces packet size (saves 1-2 bytes per packet)
4. Prevents leaking internal policy decisions

## Implementation

### C# Code to Strip Bitfield

```csharp
using System;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Meshtastic.Protobufs;

namespace Meshtastic.Mqtt.Services
{
    public class BitfieldStripperService
    {
        private readonly ILogger<BitfieldStripperService> _logger;
        private readonly bool _stripBitfield;
        
        private long _strippedCount = 0;

        public BitfieldStripperService(bool stripBitfield, ILogger<BitfieldStripperService> logger)
        {
            _stripBitfield = stripBitfield;
            _logger = logger;
        }

        public MqttApplicationMessage StripBitfield(MqttApplicationMessage message)
        {
            if (!_stripBitfield)
            {
                return message;
            }

            // Skip JSON messages
            if (message.Topic.Contains("/json/"))
            {
                return message;
            }

            try
            {
                // Parse the ServiceEnvelope
                var envelope = ServiceEnvelope.Parser.ParseFrom(
                    message.PayloadSegment.ToArray()
                );

                var packet = envelope.Packet;
                
                // Only process if the packet has decoded data
                if (packet.Decoded == null || !packet.Decoded.Bitfield.HasValue)
                {
                    return message; // No bitfield to strip
                }

                var originalBitfield = packet.Decoded.Bitfield.Value;
                
                // Create a modified copy
                var modifiedPacket = packet.Clone();
                
                // Clear the bitfield
                modifiedPacket.Decoded.ClearBitfield();
                
                _logger.LogDebug(
                    "Stripped bitfield from packet: From={From}, Bitfield=0x{Bitfield:X}",
                    $"!{packet.From:x8}",
                    originalBitfield
                );
                
                _strippedCount++;
                
                // Rebuild the envelope
                var modifiedEnvelope = new ServiceEnvelope
                {
                    Packet = modifiedPacket,
                    ChannelId = envelope.ChannelId,
                    GatewayId = envelope.GatewayId
                };
                
                var modifiedPayload = modifiedEnvelope.ToByteArray();
                
                _logger.LogTrace(
                    "Packet size reduced: {Original} → {New} bytes",
                    message.PayloadSegment.Count,
                    modifiedPayload.Length
                );
                
                return new MqttApplicationMessageBuilder()
                    .WithTopic(message.Topic)
                    .WithPayload(modifiedPayload)
                    .WithQualityOfServiceLevel(message.QualityOfServiceLevel)
                    .WithRetainFlag(message.Retain)
                    .Build();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stripping bitfield");
                return message;
            }
        }

        public long GetStrippedCount()
        {
            return _strippedCount;
        }
    }
}
```

### Configuration

Add to `appsettings.json`:

```json
{
  "PacketModification": {
    "StripOkToMqttBitfield": true
  }
}
```

### Integration in Program.cs

```csharp
// In ConfigureServices
services.AddSingleton(new BitfieldStripperService(
    _appSettings.PacketModification.StripOkToMqttBitfield,
    _serviceProvider?.GetService<ILogger<BitfieldStripperService>>()
));

// In InterceptingPublishAsync handler
mqttServer.InterceptingPublishAsync += e =>
{
    var bitfieldStripper = _serviceProvider.GetRequiredService<BitfieldStripperService>();
    
    // Strip the bitfield
    e.ApplicationMessage = bitfieldStripper.StripBitfield(e.ApplicationMessage);
    
    return Task.CompletedTask;
};
```

## What Exactly Gets Stripped?

### Before Stripping

```protobuf
Data {
  portnum: TEXT_MESSAGE_APP
  payload: "Hello World"
  bitfield: 0x00000001    // ⬅️ "OK to MQTT" flag set
}
```

### After Stripping

```protobuf
Data {
  portnum: TEXT_MESSAGE_APP
  payload: "Hello World"
  // bitfield is removed entirely
}
```

## Bitfield Values

The bitfield is a uint32, but currently only bit 0 is used:

```
Bit 0 (0x00000001): OK to MQTT flag
  - 0 = User has NOT approved MQTT upload
  - 1 = User HAS approved MQTT upload

Bits 1-31: Reserved for future use
```

## Example: Full Packet Flow

### 1. Device Sends Packet

```
Node !12345678 creates a text message:
- User has "Okay to MQTT" enabled in settings
- Device sets bitfield = 0x00000001
```

### 2. Packet Arrives at Broker

```
ServiceEnvelope {
  packet: {
    from: 0x12345678
    to: 0xFFFFFFFF (broadcast)
    decoded: {
      portnum: TEXT_MESSAGE_APP
      payload: "Hello World"
      bitfield: 0x00000001  ⬅️ Contains user consent
    }
  }
  channel_id: "LongFast"
  gateway_id: "!gateway1"
}
```

### 3. Broker Strips Bitfield

```csharp
var envelope = ServiceEnvelope.Parser.ParseFrom(rawBytes);
var packet = envelope.Packet;

if (packet.Decoded?.Bitfield.HasValue == true)
{
    _logger.LogDebug("Stripping bitfield: 0x{Value:X}", packet.Decoded.Bitfield.Value);
    packet.Decoded.ClearBitfield();
}
```

### 4. Modified Packet Published to MQTT

```
ServiceEnvelope {
  packet: {
    from: 0x12345678
    to: 0xFFFFFFFF
    decoded: {
      portnum: TEXT_MESSAGE_APP
      payload: "Hello World"
      // bitfield removed!
    }
  }
  channel_id: "LongFast"
  gateway_id: "!gateway1"
}
```

## Important Notes

### The Bitfield is in Data, Not MeshPacket

```protobuf
MeshPacket {
  from: 0x12345678
  to: 0xFFFFFFFF
  
  decoded: Data {           // ⬅️ Bitfield is HERE
    portnum: TEXT_MESSAGE_APP
    payload: "..."
    bitfield: 0x00000001   // ⬅️ In the Data message
  }
}
```

**Not here:**
```protobuf
MeshPacket {
  from: 0x12345678
  // NO bitfield at this level
  public_key: ...          // This is different (PKI field)
  pki_encrypted: ...       // This is different (PKI field)
}
```

### Don't Confuse with PKI Fields

These are **different** fields:

| Field | Location | Purpose |
|-------|----------|---------|
| `bitfield` | `Data.bitfield` (field 9) | User consent for MQTT |
| `public_key` | `MeshPacket.public_key` (field 16) | PKI public key |
| `pki_encrypted` | `MeshPacket.pki_encrypted` (field 17) | PKI encryption flag |

**You only want to strip `Data.bitfield`**, not the PKI fields.

## Performance Impact

### Size Savings

```
With bitfield:
- bitfield tag: 1 byte
- bitfield value: 1-5 bytes (varint encoded)
- Total: 2-6 bytes

Without bitfield:
- 0 bytes

Savings: 2-6 bytes per packet (~1-2% of typical packet)
```

### Encoding Details

Protobuf uses varint encoding for uint32:

```
bitfield = 0x00000001 → encoded as 0x48 0x01 (2 bytes)
bitfield = 0x00000080 → encoded as 0x48 0x80 0x01 (3 bytes)
bitfield = 0x00000000 → field omitted (0 bytes)
```

When we clear the bitfield, protobuf omits it entirely.

## Testing

### Test 1: Verify Bitfield is Stripped

```python
import paho.mqtt.client as mqtt
from meshtastic import mesh_pb2, mqtt_pb2

# Create packet WITH bitfield
data = mesh_pb2.Data()
data.portnum = mesh_pb2.PortNum.TEXT_MESSAGE_APP
data.payload = b'Hello World'
data.bitfield = 0x00000001  # Set OK to MQTT flag

packet = mesh_pb2.MeshPacket()
packet.from_node = 0x12345678
packet.to = 0xFFFFFFFF
packet.decoded.CopyFrom(data)

envelope = mqtt_pb2.ServiceEnvelope()
envelope.packet.CopyFrom(packet)

# Publish
client = mqtt.Client()
client.connect("localhost", 8883)
client.publish("msh/US/2/e/LongFast/!12345678", envelope.SerializeToString())

# Subscribe and verify
def on_message(client, userdata, msg):
    received = mqtt_pb2.ServiceEnvelope.FromString(msg.payload)
    
    # Bitfield should be cleared
    if received.packet.decoded.HasField('bitfield'):
        print("❌ FAIL: Bitfield not stripped!")
        print(f"   Bitfield value: 0x{received.packet.decoded.bitfield:X}")
    else:
        print("✓ PASS: Bitfield successfully stripped")

client.on_message = on_message
client.subscribe("msh/US/2/e/LongFast/#")
client.loop_forever()
```

### Test 2: Verify Payload Unchanged

```csharp
// Original packet
var originalData = new Data
{
    Portnum = PortNum.TEXT_MESSAGE_APP,
    Payload = ByteString.CopyFromUtf8("Test Message"),
    Bitfield = 0x00000001
};

// Process
var result = bitfieldStripper.StripBitfield(CreateMqttMessage(originalData));
var processedEnvelope = ServiceEnvelope.Parser.ParseFrom(result.PayloadSegment.ToArray());

// Verify
Assert.IsFalse(processedEnvelope.Packet.Decoded.HasBitfield, "Bitfield should be removed");
Assert.AreEqual("Test Message", 
    processedEnvelope.Packet.Decoded.Payload.ToStringUtf8(), 
    "Payload should be unchanged");
```

## Summary

**What to strip:** Only the `Data.bitfield` field (field 9 in the Data message)

**What NOT to strip:**
- ~~`MeshPacket.public_key`~~ (PKI field, keep it)
- ~~`MeshPacket.pki_encrypted`~~ (PKI flag, keep it)
- ~~Any other fields~~ (keep everything else)

**Why strip it:**
- It's internal consent metadata
- External MQTT consumers don't need it
- Saves 2-6 bytes per packet
- Prevents leaking policy decisions

**Implementation:**
1. Parse ServiceEnvelope
2. Check if `packet.Decoded.Bitfield` has a value
3. If yes, call `packet.Decoded.ClearBitfield()`
4. Re-serialize and publish

Simple, clean, and focused!
