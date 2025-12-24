# Meshtastic MQTT Broker with Enhanced Security & Privacy

A feature-rich MQTT broker specifically designed for Meshtastic mesh networks with advanced packet filtering, rate limiting, privacy controls, and moderation capabilities.

## 🚀 Features

### Core Security
- **Fail2Ban Style Connection Moderation** - Automatically ban abusive clients
- **Rate Limiting** - Per-node and global packet rate limits with duplicate detection
- **Topic Filtering** - Whitelist/blacklist MQTT topics
- **Connection ACLs** - Allow/block specific clients

### Privacy Controls
- **Location Filtering** - Block or strip GPS data from POSITION packets
- **Position Precision Reduction** - Degrade location accuracy for privacy
- **Bitfield Stripping** - Remove "OK to MQTT" consent metadata
- **Selective Node Filtering** - Per-node location policies

### Packet Modification
- **Zero-Hopping** - Prevent specific packet types from being forwarded
- **Hop Count Limits** - Enforce maximum hop counts
- **Port Number Filtering** - Block/allow specific Meshtastic port numbers

### Monitoring
- **Real-time Statistics** - Track packets, bans, rate limits, and filters
- **Comprehensive Logging** - Serilog integration with configurable levels
- **Metrics Export** - Built-in statistics reporting

## 📋 Requirements

- .NET 9.0 SDK
- Docker (optional, for containerized deployment)

## 🔧 Quick Start

```bash
# Clone the repository
git clone https://github.com/yourusername/meshtastic-mqtt-broker.git
cd meshtastic-mqtt-broker

# Copy and customize configuration
cp appsettings.json appsettings.local.json

# Build and run
dotnet restore
dotnet build
dotnet run
```

## ⚙️ Configuration Example

```json
{
  "MqttBroker": {
    "Port": 8883,
    "UseSsl": true
  },
  "RateLimiting": {
    "Enabled": true,
    "PerNodePacketLimit": {
      "MaxPacketsPerMinute": 60
    }
  },
  "LocationFilter": {
    "ReducePositionPrecision": true,
    "TargetPrecisionBits": 12
  },
  "PacketFiltering": {
    "BlockUnknownTopics": true,
    "ZeroHopPortNums": [67]
  }
}
```

## 📚 Documentation

- **[Implementation Guide](docs/IMPLEMENTATION_GUIDE.md)** - Complete setup instructions
- **[Zero-Hopping Explained](docs/ZERO_HOPPING_EXPLAINED.md)** - How hop count modification works
- **[Protobuf Setup Guide](docs/PROTOBUF_SETUP_GUIDE.md)** - Adding Meshtastic protobuf support
- **[Bitfield Stripping Guide](docs/BITFIELD_STRIPPING_GUIDE.md)** - Removing "OK to MQTT" flags
- **[Location Blocking Guide](docs/LOCATION_BLOCKING_GUIDE.md)** - Privacy controls for GPS data
- **[Security Flow Diagrams](docs/SECURITY_FLOW_DIAGRAMS.md)** - Visual guide to security features

## 🎯 Common Use Cases

### Maximum Privacy
```json
{
  "LocationFilter": { "BlockPositionPackets": true },
  "PacketModification": { "StripOkToMqttBitfield": true }
}
```

### Public Network
```json
{
  "LocationFilter": {
    "ReducePositionPrecision": true,
    "TargetPrecisionBits": 12
  }
}
```

### Testing/Development
```json
{
  "RateLimiting": { "Enabled": false },
  "PacketFiltering": { "BlockUnknownTopics": false }
}
```

## 📊 Status

**Working without protobuf:**
- ✅ Rate limiting
- ✅ Fail2Ban
- ✅ Topic filtering
- ✅ Connection ACLs

**Requires protobuf setup** ([guide](docs/PROTOBUF_SETUP_GUIDE.md)):
- ⏳ Zero-hopping
- ⏳ Location stripping
- ⏳ Bitfield stripping
- ⏳ Precision reduction

## 📜 License

GPL-3.0 License - See [LICENSE](LICENSE) file

## 🙏 Acknowledgments

- Original boilerplate: [meshtastic/mqtt](https://github.com/meshtastic/mqtt)
- Meshtastic Project: [meshtastic.org](https://meshtastic.org)
- MQTTnet Library: [dotnet/MQTTnet](https://github.com/dotnet/MQTTnet)

---

Made with ❤️ for the Meshtastic community
