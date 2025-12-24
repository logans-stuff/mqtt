using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace Meshtastic.Mqtt.Services
{
    /// <summary>
    /// Service for stripping PKI fields and handling MQTT control flags
    /// </summary>
    public class PacketSanitizationService
    {
        private readonly PacketSanitizationSettings _settings;
        private readonly ILogger<PacketSanitizationService> _logger;
        
        // Track nodes that have requested "Ignore MQTT"
        private readonly HashSet<string> _mqttIgnoreList = new();
        
        private long _strippedPkiCount = 0;
        private long _blockedIgnoreMqttCount = 0;

        public PacketSanitizationService(
            PacketSanitizationSettings settings,
            ILogger<PacketSanitizationService> logger)
        {
            _settings = settings;
            _logger = logger;
            
            // Load initial ignore list if configured
            if (_settings.MqttIgnoreList != null)
            {
                foreach (var nodeId in _settings.MqttIgnoreList)
                {
                    _mqttIgnoreList.Add(nodeId);
                }
            }
        }

        /// <summary>
        /// Check if a node has requested to ignore MQTT
        /// </summary>
        public bool ShouldBlockFromMqtt(string nodeId)
        {
            if (!_settings.RespectMqttIgnore)
            {
                return false;
            }

            bool isBlocked = _mqttIgnoreList.Contains(nodeId);
            
            if (isBlocked)
            {
                _logger.LogDebug("Blocking packet from node {NodeId} (MQTT ignore list)", nodeId);
                _blockedIgnoreMqttCount++;
            }

            return isBlocked;
        }

        /// <summary>
        /// Add a node to the MQTT ignore list
        /// </summary>
        public void AddToMqttIgnoreList(string nodeId)
        {
            if (_mqttIgnoreList.Add(nodeId))
            {
                _logger.LogInformation("Added node {NodeId} to MQTT ignore list", nodeId);
            }
        }

        /// <summary>
        /// Remove a node from the MQTT ignore list
        /// </summary>
        public void RemoveFromMqttIgnoreList(string nodeId)
        {
            if (_mqttIgnoreList.Remove(nodeId))
            {
                _logger.LogInformation("Removed node {NodeId} from MQTT ignore list", nodeId);
            }
        }

        /// <summary>
        /// Process and sanitize a packet by stripping PKI and other sensitive fields
        /// </summary>
        public MqttApplicationMessage SanitizePacket(
            MqttApplicationMessage message,
            MeshtasticPacketInfo packetInfo)
        {
            // Skip JSON messages (they don't have protobuf fields to strip)
            if (message.Topic.Contains("/json/"))
            {
                return message;
            }

            // Check if we should block this packet entirely
            if (!string.IsNullOrEmpty(packetInfo.NodeId) && 
                ShouldBlockFromMqtt(packetInfo.NodeId))
            {
                return null; // Signal to drop the packet
            }

            try
            {
                // For actual implementation, you would:
                // 1. Parse the protobuf ServiceEnvelope
                // 2. Extract the MeshPacket
                // 3. Strip sensitive fields
                // 4. Re-serialize
                
                /*
                var envelope = Meshtastic.Protobufs.ServiceEnvelope.Parser.ParseFrom(
                    message.PayloadSegment.Array,
                    message.PayloadSegment.Offset,
                    message.PayloadSegment.Count
                );
                
                var originalPacket = envelope.Packet;
                var modifiedPacket = originalPacket.Clone();
                
                bool wasModified = false;
                
                // Strip PKI fields
                if (_settings.StripPkiFields)
                {
                    if (modifiedPacket.PublicKey != null && modifiedPacket.PublicKey.Length > 0)
                    {
                        modifiedPacket.PublicKey = Google.Protobuf.ByteString.Empty;
                        wasModified = true;
                        
                        _logger.LogDebug(
                            "Stripped public_key from packet: From={From}, Size={Size}",
                            $"!{originalPacket.From:x8}",
                            originalPacket.PublicKey.Length
                        );
                    }
                    
                    if (modifiedPacket.PkiEncrypted)
                    {
                        modifiedPacket.PkiEncrypted = false;
                        wasModified = true;
                        
                        _logger.LogDebug(
                            "Cleared pki_encrypted flag: From={From}",
                            $"!{originalPacket.From:x8}"
                        );
                    }
                    
                    if (wasModified)
                    {
                        _strippedPkiCount++;
                    }
                }
                
                // Strip other sensitive fields if configured
                if (_settings.StripInternalRoutingFields && wasModified)
                {
                    // Fields like next_hop, relay_node are internal
                    // These typically shouldn't be forwarded to MQTT anyway
                    modifiedPacket.NextHop = 0;
                    modifiedPacket.RelayNode = 0;
                    
                    _logger.LogTrace("Stripped internal routing fields");
                }
                
                // Only rebuild if we modified something
                if (wasModified)
                {
                    var modifiedEnvelope = new Meshtastic.Protobufs.ServiceEnvelope
                    {
                        Packet = modifiedPacket,
                        ChannelId = envelope.ChannelId,
                        GatewayId = envelope.GatewayId
                    };
                    
                    var modifiedPayload = modifiedEnvelope.ToByteArray();
                    
                    _logger.LogInformation(
                        "Sanitized packet: From={From}, PKI stripped, Size {Original}→{New}",
                        $"!{originalPacket.From:x8}",
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
                */

                // Placeholder logging
                if (_settings.StripPkiFields)
                {
                    _logger.LogDebug(
                        "Would strip PKI fields from packet: From={NodeId} (protobuf parsing not implemented)",
                        packetInfo.NodeId
                    );
                }

                return message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sanitizing packet");
                return message;
            }
        }

        /// <summary>
        /// Check if a packet contains PKI fields that would be stripped
        /// </summary>
        public bool ContainsPkiFields(byte[] payload)
        {
            try
            {
                // Placeholder for actual implementation
                /*
                var envelope = Meshtastic.Protobufs.ServiceEnvelope.Parser.ParseFrom(payload);
                var packet = envelope.Packet;
                
                return (packet.PublicKey != null && packet.PublicKey.Length > 0) || 
                       packet.PkiEncrypted;
                */
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get statistics about sanitization operations
        /// </summary>
        public Dictionary<string, object> GetStats()
        {
            return new Dictionary<string, object>
            {
                ["PacketsWithPkiStripped"] = _strippedPkiCount,
                ["PacketsBlockedByMqttIgnore"] = _blockedIgnoreMqttCount,
                ["NodesOnMqttIgnoreList"] = _mqttIgnoreList.Count
            };
        }

        /// <summary>
        /// Export the current MQTT ignore list
        /// </summary>
        public IReadOnlyCollection<string> GetMqttIgnoreList()
        {
            return _mqttIgnoreList.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Configuration for packet sanitization
    /// </summary>
    public class PacketSanitizationSettings
    {
        /// <summary>
        /// Strip PKI public_key and pki_encrypted fields
        /// </summary>
        public bool StripPkiFields { get; set; } = true;

        /// <summary>
        /// Strip internal routing fields (next_hop, relay_node)
        /// </summary>
        public bool StripInternalRoutingFields { get; set; } = true;

        /// <summary>
        /// Respect nodes' "Ignore MQTT" preferences
        /// </summary>
        public bool RespectMqttIgnore { get; set; } = true;

        /// <summary>
        /// List of node IDs that should not have packets forwarded to MQTT
        /// </summary>
        public List<string> MqttIgnoreList { get; set; } = new();

        /// <summary>
        /// Strip position precision for privacy
        /// (downgrade to lower precision)
        /// </summary>
        public bool ReducePositionPrecision { get; set; } = false;

        /// <summary>
        /// Target precision bits if reducing position precision
        /// </summary>
        public int TargetPositionPrecisionBits { get; set; } = 12;

        /// <summary>
        /// Log when PKI fields are stripped
        /// </summary>
        public bool LogPkiStripping { get; set; } = true;
    }
}
