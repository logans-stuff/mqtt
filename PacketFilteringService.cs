using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Meshtastic.Mqtt.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace Meshtastic.Mqtt.Services
{
    /// <summary>
    /// Implements packet filtering based on topics, port numbers, and encryption status
    /// </summary>
    public class PacketFilteringService
    {
        private readonly PacketFilteringSettings _settings;
        private readonly ILogger<PacketFilteringService> _logger;
        private readonly List<Regex> _allowedTopicPatterns;
        
        private long _blockedPacketsCount = 0;
        private long _allowedPacketsCount = 0;

        public PacketFilteringService(PacketFilteringSettings settings, ILogger<PacketFilteringService> logger)
        {
            _settings = settings;
            _logger = logger;

            // Convert MQTT wildcard patterns to regex
            _allowedTopicPatterns = settings.AllowedTopics
                .Select(pattern => ConvertMqttPatternToRegex(pattern))
                .ToList();
        }

        /// <summary>
        /// Validate if a topic is allowed
        /// </summary>
        public bool IsTopicAllowed(string topic)
        {
            if (!_settings.BlockUnknownTopics)
            {
                return true;
            }

            // Check if topic matches any allowed pattern
            bool isAllowed = _allowedTopicPatterns.Any(pattern => pattern.IsMatch(topic));

            if (!isAllowed)
            {
                _logger.LogWarning("Blocked unknown topic: {Topic}", topic);
                _blockedPacketsCount++;
            }
            else
            {
                _allowedPacketsCount++;
            }

            return isAllowed;
        }

        /// <summary>
        /// Validate if a port number is allowed
        /// </summary>
        public bool IsPortNumAllowed(int portNum)
        {
            // If we have a blocklist, check it first
            if (_settings.BlockedPortNums.Any() && _settings.BlockedPortNums.Contains(portNum))
            {
                _logger.LogDebug("Blocked packet with port number: {PortNum}", portNum);
                _blockedPacketsCount++;
                return false;
            }

            // If we have an allowlist, enforce it
            if (_settings.AllowedPortNums.Any())
            {
                bool isAllowed = _settings.AllowedPortNums.Contains(portNum);
                if (!isAllowed)
                {
                    _logger.LogDebug("Blocked packet with disallowed port number: {PortNum}", portNum);
                    _blockedPacketsCount++;
                }
                return isAllowed;
            }

            return true;
        }

        /// <summary>
        /// Check if a port number should have zero hop count
        /// </summary>
        public bool ShouldZeroHop(int portNum)
        {
            return _settings.ZeroHopPortNums.Contains(portNum);
        }

        /// <summary>
        /// Validate hop count
        /// </summary>
        public bool IsHopCountValid(int hopCount)
        {
            if (hopCount > _settings.MaxHopCount)
            {
                _logger.LogDebug("Blocked packet exceeding max hop count: {HopCount} > {Max}", 
                    hopCount, _settings.MaxHopCount);
                _blockedPacketsCount++;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Validate if a channel is allowed
        /// </summary>
        public bool IsChannelAllowed(string channelName)
        {
            if (!_settings.AllowedChannels.Any())
            {
                return true; // No restrictions
            }

            bool isAllowed = _settings.AllowedChannels.Contains(channelName, StringComparer.OrdinalIgnoreCase);
            
            if (!isAllowed)
            {
                _logger.LogDebug("Blocked packet from disallowed channel: {Channel}", channelName);
                _blockedPacketsCount++;
            }

            return isAllowed;
        }

        /// <summary>
        /// Check if undecryptable packets should be blocked
        /// </summary>
        public bool ShouldBlockUndecryptable()
        {
            return _settings.BlockUndecryptablePackets;
        }

        /// <summary>
        /// Validate an entire MQTT application message
        /// </summary>
        public ValidationResult ValidateMessage(MqttApplicationMessage message, MeshtasticPacketInfo packetInfo)
        {
            var result = new ValidationResult { IsValid = true };

            // Check topic
            if (!IsTopicAllowed(message.Topic))
            {
                result.IsValid = false;
                result.Reason = $"Unknown topic: {message.Topic}";
                return result;
            }

            // Check port number if available
            if (packetInfo.PortNum.HasValue && !IsPortNumAllowed(packetInfo.PortNum.Value))
            {
                result.IsValid = false;
                result.Reason = $"Blocked port number: {packetInfo.PortNum.Value}";
                return result;
            }

            // Check hop count
            if (packetInfo.HopCount.HasValue && !IsHopCountValid(packetInfo.HopCount.Value))
            {
                result.IsValid = false;
                result.Reason = $"Hop count exceeded: {packetInfo.HopCount.Value}";
                return result;
            }

            // Check channel
            if (!string.IsNullOrEmpty(packetInfo.Channel) && !IsChannelAllowed(packetInfo.Channel))
            {
                result.IsValid = false;
                result.Reason = $"Disallowed channel: {packetInfo.Channel}";
                return result;
            }

            // Check if packet is undecryptable
            if (packetInfo.IsEncrypted && !packetInfo.WasDecrypted && ShouldBlockUndecryptable())
            {
                result.IsValid = false;
                result.Reason = "Undecryptable packet from unknown channel";
                return result;
            }

            return result;
        }

        /// <summary>
        /// Convert MQTT wildcard pattern to regex
        /// + matches single level, # matches multiple levels
        /// </summary>
        private Regex ConvertMqttPatternToRegex(string mqttPattern)
        {
            // Escape special regex characters except MQTT wildcards
            string escaped = Regex.Escape(mqttPattern);
            
            // Replace MQTT wildcards with regex equivalents
            escaped = escaped.Replace(@"\+", "[^/]+");  // + matches any single topic level
            escaped = escaped.Replace(@"\#", ".*");     // # matches any remaining levels
            
            return new Regex($"^{escaped}$", RegexOptions.Compiled);
        }

        public Dictionary<string, object> GetStats()
        {
            return new Dictionary<string, object>
            {
                ["AllowedPackets"] = _allowedPacketsCount,
                ["BlockedPackets"] = _blockedPacketsCount,
                ["BlockRate"] = _allowedPacketsCount + _blockedPacketsCount > 0 
                    ? (double)_blockedPacketsCount / (_allowedPacketsCount + _blockedPacketsCount) 
                    : 0.0
            };
        }
    }

    /// <summary>
    /// Result of packet validation
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Information extracted from a Meshtastic packet
    /// </summary>
    public class MeshtasticPacketInfo
    {
        public string NodeId { get; set; }
        public int? PortNum { get; set; }
        public int? HopCount { get; set; }
        public string Channel { get; set; }
        public bool IsEncrypted { get; set; }
        public bool WasDecrypted { get; set; }
        public byte[] Payload { get; set; }
        
        /// <summary>
        /// Generate a hash for duplicate detection
        /// </summary>
        public string GetHash()
        {
            if (Payload == null || Payload.Length == 0)
                return $"{NodeId}_{PortNum}_{DateTime.UtcNow.Ticks}";
            
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(Payload);
            return Convert.ToBase64String(hash);
        }
    }
}
