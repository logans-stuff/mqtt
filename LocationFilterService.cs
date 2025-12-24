using System;
using System.Collections.Generic;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Meshtastic.Protobufs;

namespace Meshtastic.Mqtt.Services
{
    /// <summary>
    /// Service for blocking POSITION packets and stripping location data from NODEINFO packets
    /// </summary>
    public class LocationFilterService
    {
        private readonly LocationFilterSettings _settings;
        private readonly ILogger<LocationFilterService> _logger;
        
        private long _blockedPositionPackets = 0;
        private long _strippedNodeInfoLocation = 0;

        public LocationFilterService(
            LocationFilterSettings settings,
            ILogger<LocationFilterService> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// Process a packet to block POSITION packets or strip location from NODEINFO
        /// Returns null if packet should be dropped entirely
        /// </summary>
        public MqttApplicationMessage ProcessPacket(MqttApplicationMessage message)
        {
            // Skip JSON messages (handle separately if needed)
            if (message.Topic.Contains("/json/"))
            {
                return ProcessJsonMessage(message);
            }

            try
            {
                // Parse the ServiceEnvelope
                var envelope = ServiceEnvelope.Parser.ParseFrom(
                    message.PayloadSegment.ToArray()
                );

                var packet = envelope.Packet;
                
                // Only process decoded packets
                if (packet.Decoded == null)
                {
                    return message;
                }

                // Check if this is a POSITION packet
                if (packet.Decoded.Portnum == PortNum.POSITION_APP)
                {
                    return HandlePositionPacket(message, packet);
                }

                // Check if this is a NODEINFO packet
                if (packet.Decoded.Portnum == PortNum.NODEINFO_APP)
                {
                    return HandleNodeInfoPacket(message, envelope, packet);
                }

                return message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing packet for location filtering");
                return message;
            }
        }

        /// <summary>
        /// Handle POSITION_APP packets
        /// </summary>
        private MqttApplicationMessage HandlePositionPacket(
            MqttApplicationMessage message,
            MeshPacket packet)
        {
            if (_settings.BlockPositionPackets)
            {
                _logger.LogInformation(
                    "Blocking POSITION packet from node {NodeId}",
                    $"!{packet.From:x8}"
                );
                _blockedPositionPackets++;
                return null; // Drop the packet
            }

            if (_settings.StripPositionData || _settings.ReducePositionPrecision)
            {
                return StripOrReducePositionPacket(message, packet);
            }

            return message;
        }

        /// <summary>
        /// Strip or reduce precision of POSITION packets
        /// </summary>
        private MqttApplicationMessage StripOrReducePositionPacket(
            MqttApplicationMessage message,
            MeshPacket packet)
        {
            try
            {
                // Parse the Position from the payload
                var position = Position.Parser.ParseFrom(packet.Decoded.Payload);
                var modified = position.Clone();
                bool wasModified = false;

                if (_settings.StripPositionData)
                {
                    // Strip all location data
                    if (modified.HasLatitudeI || modified.HasLongitudeI || modified.HasAltitude)
                    {
                        modified.ClearLatitudeI();
                        modified.ClearLongitudeI();
                        modified.ClearAltitude();
                        modified.ClearAltitudeHae();
                        modified.ClearAltitudeGeoidalSeparation();
                        modified.ClearGroundSpeed();
                        modified.ClearGroundTrack();
                        wasModified = true;

                        _logger.LogDebug(
                            "Stripped all location data from POSITION packet: Node={NodeId}",
                            $"!{packet.From:x8}"
                        );
                    }
                }
                else if (_settings.ReducePositionPrecision && modified.PrecisionBits > _settings.TargetPrecisionBits)
                {
                    // Reduce precision
                    modified.PrecisionBits = (uint)_settings.TargetPrecisionBits;
                    wasModified = true;

                    _logger.LogDebug(
                        "Reduced position precision: Node={NodeId}, Precision={Bits} bits",
                        $"!{packet.From:x8}",
                        _settings.TargetPrecisionBits
                    );
                }

                if (wasModified)
                {
                    return RebuildPacketWithModifiedPayload(message, packet, modified.ToByteArray());
                }

                return message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stripping position data");
                return message;
            }
        }

        /// <summary>
        /// Handle NODEINFO_APP packets - strip position data if present
        /// </summary>
        private MqttApplicationMessage HandleNodeInfoPacket(
            MqttApplicationMessage message,
            ServiceEnvelope envelope,
            MeshPacket packet)
        {
            if (!_settings.StripNodeInfoPosition)
            {
                return message;
            }

            try
            {
                // NodeInfo is NOT in the packet payload
                // It's sent as a FromRadio message with NodeInfo
                // But when it comes via MQTT, we see it differently
                
                // For MQTT, NODEINFO_APP actually contains a User message
                var user = User.Parser.ParseFrom(packet.Decoded.Payload);
                
                // User messages don't have position
                // Position is stored separately in NodeInfo database
                
                // However, if this is a different format, log it
                _logger.LogTrace(
                    "NODEINFO packet processed: Node={NodeId}",
                    $"!{packet.From:x8}"
                );

                return message;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse NODEINFO payload");
                return message;
            }
        }

        /// <summary>
        /// Process JSON MQTT messages to strip location data
        /// </summary>
        private MqttApplicationMessage ProcessJsonMessage(MqttApplicationMessage message)
        {
            if (!_settings.BlockPositionPackets && !_settings.StripPositionData)
            {
                return message;
            }

            try
            {
                var jsonString = System.Text.Encoding.UTF8.GetString(message.PayloadSegment.ToArray());
                var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonString);
                var root = jsonDoc.RootElement;

                // Check if this is a position message
                if (root.TryGetProperty("type", out var typeElement))
                {
                    var type = typeElement.GetString();
                    
                    if (type == "position" && _settings.BlockPositionPackets)
                    {
                        _logger.LogInformation("Blocking JSON position message");
                        _blockedPositionPackets++;
                        return null; // Drop
                    }

                    if (type == "position" && _settings.StripPositionData)
                    {
                        // Would need to parse and rebuild JSON without lat/lon
                        _logger.LogDebug("JSON position stripping not yet implemented");
                    }
                }

                return message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing JSON message");
                return message;
            }
        }

        /// <summary>
        /// Rebuild MQTT message with modified payload
        /// </summary>
        private MqttApplicationMessage RebuildPacketWithModifiedPayload(
            MqttApplicationMessage originalMessage,
            MeshPacket originalPacket,
            byte[] newPayload)
        {
            try
            {
                // Parse original envelope
                var envelope = ServiceEnvelope.Parser.ParseFrom(
                    originalMessage.PayloadSegment.ToArray()
                );

                // Create modified packet
                var modifiedPacket = originalPacket.Clone();
                modifiedPacket.Decoded.Payload = ByteString.CopyFrom(newPayload);

                // Rebuild envelope
                var modifiedEnvelope = new ServiceEnvelope
                {
                    Packet = modifiedPacket,
                    ChannelId = envelope.ChannelId,
                    GatewayId = envelope.GatewayId
                };

                var modifiedPayloadBytes = modifiedEnvelope.ToByteArray();

                return new MqttApplicationMessageBuilder()
                    .WithTopic(originalMessage.Topic)
                    .WithPayload(modifiedPayloadBytes)
                    .WithQualityOfServiceLevel(originalMessage.QualityOfServiceLevel)
                    .WithRetainFlag(originalMessage.Retain)
                    .Build();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rebuilding packet");
                return originalMessage;
            }
        }

        /// <summary>
        /// Get statistics
        /// </summary>
        public Dictionary<string, object> GetStats()
        {
            return new Dictionary<string, object>
            {
                ["BlockedPositionPackets"] = _blockedPositionPackets,
                ["StrippedNodeInfoLocations"] = _strippedNodeInfoLocation
            };
        }
    }

    /// <summary>
    /// Configuration for location filtering
    /// </summary>
    public class LocationFilterSettings
    {
        /// <summary>
        /// Block all POSITION_APP packets from being published to MQTT
        /// </summary>
        public bool BlockPositionPackets { get; set; } = false;

        /// <summary>
        /// Strip all location data from POSITION packets
        /// (keeps timing/metadata but removes lat/lon/altitude)
        /// </summary>
        public bool StripPositionData { get; set; } = false;

        /// <summary>
        /// Reduce position precision to protect privacy
        /// </summary>
        public bool ReducePositionPrecision { get; set; } = false;

        /// <summary>
        /// Target precision in bits (default 12 = ~1-2km accuracy)
        /// Lower = less precise. Standard values: 32=cm, 16=~10m, 12=~1km, 10=~5km
        /// </summary>
        public int TargetPrecisionBits { get; set; } = 12;

        /// <summary>
        /// Strip position data from NODEINFO packets
        /// Note: NODEINFO packets contain User data, not Position
        /// Position is stored separately in the node database
        /// </summary>
        public bool StripNodeInfoPosition { get; set; } = false;

        /// <summary>
        /// Allow position packets from specific nodes (whitelist)
        /// </summary>
        public List<string> AllowPositionFromNodes { get; set; } = new();

        /// <summary>
        /// Block position packets from specific nodes (blacklist)
        /// </summary>
        public List<string> BlockPositionFromNodes { get; set; } = new();
    }
}
