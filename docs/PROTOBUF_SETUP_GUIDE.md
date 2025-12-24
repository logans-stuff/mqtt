# Adding Protobuf Support for Zero-Hopping

This guide shows you exactly how to add Meshtastic protobuf support to enable actual packet modification.

## Step 1: Get Meshtastic Protobuf Definitions

### Option A: Clone the Repository

```bash
cd /path/to/your/project
git clone https://github.com/meshtastic/protobufs.git meshtastic-protobufs
```

### Option B: Add as Git Submodule

```bash
cd /path/to/your/project
git submodule add https://github.com/meshtastic/protobufs.git meshtastic-protobufs
git submodule update --init --recursive
```

## Step 2: Generate C# Code from Protobufs

### Install protoc (Protocol Buffer Compiler)

**On Ubuntu/Debian:**
```bash
sudo apt-get install protobuf-compiler
```

**On macOS:**
```bash
brew install protobuf
```

**On Windows:**
Download from: https://github.com/protocolbuffers/protobuf/releases

### Generate C# Classes

```bash
cd meshtastic-protobufs

# Generate C# code from all proto files
protoc --csharp_out=../Generated \
    --proto_path=. \
    mqtt.proto \
    mesh.proto \
    portnums.proto \
    telemetry.proto \
    config.proto \
    module_config.proto \
    deviceonly.proto \
    channel.proto \
    localonly.proto

# This creates files like:
# Generated/Mqtt.cs
# Generated/Mesh.cs
# Generated/Portnums.cs
# etc.
```

## Step 3: Update Your .csproj File

Add the protobuf package and include the generated files:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <!-- Existing packages -->
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
    
    <!-- Add protobuf support -->
    <PackageReference Include="Google.Protobuf" Version="3.25.1" />
    <PackageReference Include="Grpc.Tools" Version="2.60.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <!-- Include generated protobuf files -->
    <Compile Include="Generated/*.cs" />
  </ItemGroup>
</Project>
```

## Step 4: Implement the Packet Modification Service

Replace the placeholder code in `PacketModificationService.cs`:

```csharp
using System;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Meshtastic.Protobufs;  // The generated protobuf classes

namespace Meshtastic.Mqtt.Services
{
    public class PacketModificationService
    {
        private readonly ILogger<PacketModificationService> _logger;
        private readonly PacketFilteringService _filteringService;

        public PacketModificationService(
            PacketFilteringService filteringService,
            ILogger<PacketModificationService> logger)
        {
            _filteringService = filteringService;
            _logger = logger;
        }

        public MqttApplicationMessage ProcessPacket(
            MqttApplicationMessage message,
            MeshtasticPacketInfo packetInfo)
        {
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

                bool wasModified = false;
                var modifiedPacket = envelope.Packet.Clone();

                // Zero-hopping
                if (modifiedPacket.Decoded != null &&
                    _filteringService.ShouldZeroHop((int)modifiedPacket.Decoded.Portnum))
                {
                    var originalHops = modifiedPacket.HopLimit;
                    modifiedPacket.HopLimit = 0;
                    wasModified = true;

                    _logger.LogInformation(
                        "Zero-hopped packet: Port={Port}, From={From}, Hops {Original}→0",
                        modifiedPacket.Decoded.Portnum,
                        $"!{modifiedPacket.From:x8}",
                        originalHops
                    );
                }

                // Only rebuild if we modified something
                if (wasModified)
                {
                    var modifiedEnvelope = new ServiceEnvelope
                    {
                        Packet = modifiedPacket,
                        ChannelId = envelope.ChannelId,
                        GatewayId = envelope.GatewayId
                    };

                    var modifiedPayload = modifiedEnvelope.ToByteArray();

                    return new MqttApplicationMessageBuilder()
                        .WithTopic(message.Topic)
                        .WithPayload(modifiedPayload)
                        .WithQualityOfServiceLevel(message.QualityOfServiceLevel)
                        .WithRetainFlag(message.Retain)
                        .Build();
                }

                return message;
            }
            catch (InvalidProtocolBufferException ex)
            {
                _logger.LogWarning(ex, "Failed to parse protobuf message");
                return message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing packet");
                return message;
            }
        }

        /// <summary>
        /// Extract detailed packet information from protobuf
        /// </summary>
        public static MeshtasticPacketInfo ExtractPacketInfo(byte[] payload)
        {
            var info = new MeshtasticPacketInfo { Payload = payload };

            try
            {
                var envelope = ServiceEnvelope.Parser.ParseFrom(payload);
                var packet = envelope.Packet;

                info.NodeId = $"!{packet.From:x8}";
                info.HopCount = (int)packet.HopLimit;
                info.Channel = envelope.ChannelId;

                // Check encryption
                if (packet.PayloadVariantCase == MeshPacket.PayloadVariantOneofCase.Encrypted)
                {
                    info.IsEncrypted = true;
                    info.WasDecrypted = false;
                }
                else if (packet.PayloadVariantCase == MeshPacket.PayloadVariantOneofCase.Decoded)
                {
                    info.IsEncrypted = false;
                    info.WasDecrypted = true;
                    info.PortNum = (int)packet.Decoded.Portnum;
                }

                return info;
            }
            catch (Exception)
            {
                // If parsing fails, return basic info
                return info;
            }
        }
    }
}
```

## Step 5: Update Program.cs to Use the Service

```csharp
// In ConfigureServices method
services.AddSingleton<PacketModificationService>();

// In StartMqttBroker method
var packetModification = _serviceProvider.GetRequiredService<PacketModificationService>();

// In InterceptingPublishAsync handler
mqttServer.InterceptingPublishAsync += e =>
{
    var clientId = e.ClientId;
    
    // Extract packet info using protobuf parsing
    var packetInfo = PacketModificationService.ExtractPacketInfo(
        e.ApplicationMessage.PayloadSegment.ToArray()
    );

    // ... rate limiting checks ...

    // ... validation checks ...

    // Modify packet if needed
    e.ApplicationMessage = packetModification.ProcessPacket(
        e.ApplicationMessage,
        packetInfo
    );

    return Task.CompletedTask;
};
```

## Step 6: Update ExtractPacketInfo in Program.cs

Replace the simplified version with the protobuf-aware version:

```csharp
private static MeshtasticPacketInfo ExtractPacketInfo(MqttApplicationMessage message)
{
    // For JSON messages
    if (message.Topic.Contains("/json/"))
    {
        var info = new MeshtasticPacketInfo { Payload = message.PayloadSegment.ToArray() };
        
        // Parse topic for basic info
        var parts = message.Topic.Split('/');
        if (parts.Length >= 6)
        {
            info.Channel = parts[4];
            info.NodeId = parts[5];
        }
        
        return info;
    }

    // For protobuf messages, use proper parsing
    return PacketModificationService.ExtractPacketInfo(
        message.PayloadSegment.ToArray()
    );
}
```

## Step 7: Test Your Implementation

### Create a Test Packet

```python
# test_zero_hop.py
import paho.mqtt.client as mqtt
import sys
sys.path.append('./meshtastic-protobufs/python')

from meshtastic import mesh_pb2, mqtt_pb2

# Create a telemetry packet
packet = mesh_pb2.MeshPacket()
packet.from_node = 0xAABBCCDD
packet.to = 0xFFFFFFFF  # Broadcast
packet.id = 123456
packet.hop_limit = 3  # Should be changed to 0
packet.hop_start = 3

# Add telemetry data
packet.decoded.portnum = mesh_pb2.PortNum.TELEMETRY_APP
packet.decoded.payload = b'{"battery_level": 85, "voltage": 4.1}'

# Wrap in ServiceEnvelope
envelope = mqtt_pb2.ServiceEnvelope()
envelope.packet.CopyFrom(packet)
envelope.channel_id = "LongFast"
envelope.gateway_id = "!12345678"

# Connect and publish
client = mqtt.Client()
client.connect("localhost", 8883)

topic = "msh/US/2/e/LongFast/!aabbccdd"
client.publish(topic, envelope.SerializeToString())
client.disconnect()

print("Test packet sent!")
```

### Run the Test

```bash
python test_zero_hop.py
```

### Expected Log Output

```
[INFO] Client connected: test_client
[INFO] Zero-hopped packet: Port=TELEMETRY_APP, From=!aabbccdd, Hops 3→0
[DEBUG] Modified packet published to topic: msh/US/2/e/LongFast/!aabbccdd
```

## Step 8: Verify the Modification

You can verify the packet was modified by subscribing to the topic:

```python
# verify_modification.py
import paho.mqtt.client as mqtt
import sys
sys.path.append('./meshtastic-protobufs/python')

from meshtastic import mqtt_pb2

def on_message(client, userdata, msg):
    envelope = mqtt_pb2.ServiceEnvelope.FromString(msg.payload)
    packet = envelope.packet
    
    print(f"Received packet from !{packet.from_node:x8}")
    print(f"Hop limit: {packet.hop_limit}")  # Should be 0!
    print(f"Port: {packet.decoded.portnum}")
    print()

client = mqtt.Client()
client.on_message = on_message
client.connect("localhost", 8883)
client.subscribe("msh/US/2/e/LongFast/#")
client.loop_forever()
```

## Troubleshooting

### Issue: "ServiceEnvelope not found"

Make sure you've:
1. Generated the C# code with protoc
2. Added `<Compile Include="Generated/*.cs" />` to your .csproj
3. Rebuilt your project

### Issue: "InvalidProtocolBufferException"

The packet might not be a valid protobuf. Check:
- Is it a JSON message? (topic contains "/json/")
- Is the payload corrupted?
- Is it using the correct protobuf version?

### Issue: Modifications not appearing

Check:
1. Are you returning the modified message? (`e.ApplicationMessage = modified`)
2. Are subscribers connecting after the modification?
3. Check logs for errors during parsing

## Alternative: Use NuGet Package (if available)

Some developers have created NuGet packages for Meshtastic protobufs:

```bash
dotnet add package Meshtastic.Protobufs
```

This saves you from generating the code yourself, but check that it's up to date with the latest Meshtastic firmware.

## Next Steps

Once you have protobuf support working, you can:

1. Implement adaptive hop reduction based on network congestion
2. Strip precise location data for privacy
3. Modify packet priority
4. Add custom packet transformations
5. Implement packet signing/validation

## Complete Example Project Structure

```
your-project/
├── Program.cs
├── Configuration.cs
├── RateLimitingServices.cs
├── PacketFilteringService.cs
├── PacketModificationService.cs
├── appsettings.json
├── Meshtastic.Mqtt.csproj
├── meshtastic-protobufs/          # Git submodule or clone
│   ├── mqtt.proto
│   ├── mesh.proto
│   ├── portnums.proto
│   └── ...
└── Generated/                      # Generated from protobufs
    ├── Mqtt.cs
    ├── Mesh.cs
    ├── Portnums.cs
    └── ...
```

Now you have real zero-hopping capability! 🎉
