using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Meshtastic.Mqtt.Configuration;
using Microsoft.Extensions.Logging;

namespace Meshtastic.Mqtt.Services
{
    /// <summary>
    /// Implements fail2ban-style connection moderation
    /// Tracks failed authentication attempts and temporarily bans abusive clients
    /// </summary>
    public class Fail2BanService
    {
        private readonly Fail2BanSettings _settings;
        private readonly ILogger<Fail2BanService> _logger;
        
        // Track failed attempts: ClientId -> List of attempt timestamps
        private readonly ConcurrentDictionary<string, List<DateTime>> _failedAttempts = new();
        
        // Track banned clients: ClientId -> Ban expiration time
        private readonly ConcurrentDictionary<string, DateTime> _bannedClients = new();

        public Fail2BanService(Fail2BanSettings settings, ILogger<Fail2BanService> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public void RecordFailedAttempt(string clientId)
        {
            if (!_settings.Enabled) return;

            var now = DateTime.UtcNow;
            var cutoffTime = now.AddMinutes(-_settings.FindTimeMinutes);

            _failedAttempts.AddOrUpdate(
                clientId,
                new List<DateTime> { now },
                (key, existing) =>
                {
                    // Remove old attempts outside the find time window
                    existing.RemoveAll(t => t < cutoffTime);
                    existing.Add(now);
                    return existing;
                }
            );

            var recentAttempts = _failedAttempts[clientId].Count;
            
            if (recentAttempts >= _settings.MaxFailedAuthAttempts)
            {
                BanClient(clientId);
                _logger.LogWarning(
                    "Client {ClientId} banned for {Duration} minutes after {Attempts} failed attempts",
                    clientId, _settings.BanDurationMinutes, recentAttempts);
            }
        }

        public void BanClient(string clientId)
        {
            var banExpiry = DateTime.UtcNow.AddMinutes(_settings.BanDurationMinutes);
            _bannedClients[clientId] = banExpiry;
            
            // Clear failed attempts since client is now banned
            _failedAttempts.TryRemove(clientId, out _);
        }

        public bool IsClientBanned(string clientId)
        {
            if (!_settings.Enabled) return false;

            if (_bannedClients.TryGetValue(clientId, out var banExpiry))
            {
                if (DateTime.UtcNow < banExpiry)
                {
                    return true;
                }
                
                // Ban expired, remove it
                _bannedClients.TryRemove(clientId, out _);
                _logger.LogInformation("Ban expired for client {ClientId}", clientId);
            }

            return false;
        }

        public void ClearFailedAttempts(string clientId)
        {
            _failedAttempts.TryRemove(clientId, out _);
        }

        public Dictionary<string, object> GetStats()
        {
            var now = DateTime.UtcNow;
            return new Dictionary<string, object>
            {
                ["ActiveBans"] = _bannedClients.Count(kvp => kvp.Value > now),
                ["TotalBannedClients"] = _bannedClients.Count,
                ["ClientsWithFailedAttempts"] = _failedAttempts.Count
            };
        }
    }

    /// <summary>
    /// Implements rate limiting for packets
    /// Tracks duplicate packets and enforces per-node and global rate limits
    /// </summary>
    public class RateLimitingService
    {
        private readonly RateLimitingSettings _settings;
        private readonly ILogger<RateLimitingService> _logger;
        
        // Track packet hashes to detect duplicates: PacketHash -> First seen time
        private readonly ConcurrentDictionary<string, DateTime> _seenPackets = new();
        
        // Track packets per node: NodeId -> List of packet timestamps
        private readonly ConcurrentDictionary<string, List<DateTime>> _nodePackets = new();
        
        // Track banned nodes: NodeId -> Ban expiration time
        private readonly ConcurrentDictionary<string, DateTime> _bannedNodes = new();
        
        // Global packet counter
        private readonly List<DateTime> _globalPackets = new();
        private readonly object _globalLock = new();

        public RateLimitingService(RateLimitingSettings settings, ILogger<RateLimitingService> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// Check if a packet is a duplicate within the configured time window
        /// </summary>
        public bool IsDuplicatePacket(string packetHash)
        {
            if (!_settings.Enabled) return false;

            var now = DateTime.UtcNow;
            var cutoffTime = now.AddSeconds(-_settings.DuplicatePacketWindow);

            if (_seenPackets.TryGetValue(packetHash, out var firstSeen))
            {
                if (firstSeen > cutoffTime)
                {
                    _logger.LogDebug("Duplicate packet detected: {Hash}", packetHash);
                    return true;
                }
                
                // Packet is old, update timestamp
                _seenPackets[packetHash] = now;
            }
            else
            {
                _seenPackets[packetHash] = now;
            }

            // Clean up old entries periodically
            if (_seenPackets.Count > 10000)
            {
                CleanupOldPackets();
            }

            return false;
        }

        /// <summary>
        /// Check if a node is within its rate limit
        /// </summary>
        public bool CheckNodeRateLimit(string nodeId)
        {
            if (!_settings.Enabled) return true;

            // Check if node is banned
            if (_bannedNodes.TryGetValue(nodeId, out var banExpiry))
            {
                if (DateTime.UtcNow < banExpiry)
                {
                    _logger.LogDebug("Blocked packet from banned node: {NodeId}", nodeId);
                    return false;
                }
                
                // Ban expired
                _bannedNodes.TryRemove(nodeId, out _);
                _logger.LogInformation("Rate limit ban expired for node {NodeId}", nodeId);
            }

            var now = DateTime.UtcNow;
            var cutoffTime = now.AddMinutes(-1);

            _nodePackets.AddOrUpdate(
                nodeId,
                new List<DateTime> { now },
                (key, existing) =>
                {
                    existing.RemoveAll(t => t < cutoffTime);
                    existing.Add(now);
                    return existing;
                }
            );

            var packetsThisMinute = _nodePackets[nodeId].Count;
            
            if (packetsThisMinute > _settings.PerNodePacketLimit.MaxPacketsPerMinute)
            {
                // Ban the node
                var banDuration = TimeSpan.FromMinutes(_settings.PerNodePacketLimit.BanDurationMinutes);
                _bannedNodes[nodeId] = now.Add(banDuration);
                
                _logger.LogWarning(
                    "Node {NodeId} exceeded rate limit ({Count} packets/min) - banned for {Duration} minutes",
                    nodeId, packetsThisMinute, _settings.PerNodePacketLimit.BanDurationMinutes);
                
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check if global rate limit is exceeded
        /// </summary>
        public bool CheckGlobalRateLimit()
        {
            if (!_settings.Enabled) return true;

            lock (_globalLock)
            {
                var now = DateTime.UtcNow;
                var cutoffTime = now.AddMinutes(-1);
                
                _globalPackets.RemoveAll(t => t < cutoffTime);
                _globalPackets.Add(now);

                if (_globalPackets.Count > _settings.GlobalPacketLimit.MaxPacketsPerMinute)
                {
                    _logger.LogWarning("Global rate limit exceeded: {Count} packets/min", _globalPackets.Count);
                    return false;
                }
            }

            return true;
        }

        private void CleanupOldPackets()
        {
            var cutoffTime = DateTime.UtcNow.AddSeconds(-_settings.DuplicatePacketWindow);
            var keysToRemove = _seenPackets
                .Where(kvp => kvp.Value < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _seenPackets.TryRemove(key, out _);
            }

            _logger.LogDebug("Cleaned up {Count} old packet entries", keysToRemove.Count);
        }

        public Dictionary<string, object> GetStats()
        {
            var now = DateTime.UtcNow;
            lock (_globalLock)
            {
                var recentPackets = _globalPackets.Count(t => t > now.AddMinutes(-1));
                return new Dictionary<string, object>
                {
                    ["TrackedPackets"] = _seenPackets.Count,
                    ["TrackedNodes"] = _nodePackets.Count,
                    ["BannedNodes"] = _bannedNodes.Count(kvp => kvp.Value > now),
                    ["PacketsLastMinute"] = recentPackets
                };
            }
        }
    }
}
