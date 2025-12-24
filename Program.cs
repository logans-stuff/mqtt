using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Serilog;
using Meshtastic.Mqtt.Configuration;
using Meshtastic.Mqtt.Services;

namespace Meshtastic.Mqtt
{
    class Program
    {
        private static AppSettings _appSettings;
        private static IServiceProvider _serviceProvider;
        
        static async Task Main(string[] args)
        {
            // Load configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();

            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            try
            {
                Log.Information("Starting Meshtastic MQTT Broker");

                // Bind configuration
                _appSettings = new AppSettings();
                configuration.Bind(_appSettings);

                // Setup dependency injection
                var services = new ServiceCollection();
                ConfigureServices(services);
                _serviceProvider = services.BuildServiceProvider();

                // Start the MQTT broker
                await StartMqttBroker();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application terminated unexpectedly");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // Add logging
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog();
            });

            // Register configuration
            services.AddSingleton(_appSettings);

            // Register services
            services.AddSingleton(new Fail2BanService(
                _appSettings.ConnectionModeration.Fail2Ban,
                _serviceProvider?.GetService<ILogger<Fail2BanService>>()));
            
            services.AddSingleton(new RateLimitingService(
                _appSettings.RateLimiting,
                _serviceProvider?.GetService<ILogger<RateLimitingService>>()));
            
            services.AddSingleton(new PacketFilteringService(
                _appSettings.PacketFiltering,
                _serviceProvider?.GetService<ILogger<PacketFilteringService>>()));
        }

        private static async Task StartMqttBroker()
        {
            var logger = _serviceProvider.GetRequiredService<ILogger<Program>>();
            var fail2Ban = _serviceProvider.GetRequiredService<Fail2BanService>();
            var rateLimiting = _serviceProvider.GetRequiredService<RateLimitingService>();
            var packetFiltering = _serviceProvider.GetRequiredService<PacketFilteringService>();

            var mqttFactory = new MqttFactory();
            var mqttServerOptions = new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(_appSettings.MqttBroker.Port)
                .WithConnectionBacklog(_appSettings.MqttBroker.ConnectionBacklog)
                .WithMaxPendingMessagesPerClient(_appSettings.MqttBroker.MaxPendingMessagesPerClient);

            // Configure SSL if enabled
            if (_appSettings.MqttBroker.UseSsl)
            {
                var certPath = _appSettings.MqttBroker.CertificatePath;
                if (File.Exists(certPath))
                {
                    var certificate = string.IsNullOrEmpty(_appSettings.MqttBroker.CertificatePassword)
                        ? new X509Certificate2(certPath)
                        : new X509Certificate2(certPath, _appSettings.MqttBroker.CertificatePassword);

                    mqttServerOptions.WithEncryptedEndpoint()
                        .WithEncryptedEndpointPort(_appSettings.MqttBroker.Port)
                        .WithEncryptionCertificate(certificate.Export(X509ContentType.Pfx));

                    logger.LogInformation("SSL enabled using certificate: {Path}", certPath);
                }
                else
                {
                    logger.LogWarning("SSL enabled but certificate not found at: {Path}", certPath);
                }
            }

            var mqttServer = mqttFactory.CreateMqttServer(mqttServerOptions.Build());

            // Handle client connections
            mqttServer.ValidatingConnectionAsync += e =>
            {
                var clientId = e.ClientId;
                logger.LogInformation("Client connecting: {ClientId}", clientId);

                // Check fail2ban
                if (fail2Ban.IsClientBanned(clientId))
                {
                    logger.LogWarning("Rejected banned client: {ClientId}", clientId);
                    e.ReasonCode = MqttConnectReasonCode.NotAuthorized;
                    return Task.CompletedTask;
                }

                // Check blocklist
                if (_appSettings.ConnectionModeration.BlockedClients.Contains(clientId) ||
                    _appSettings.ConnectionModeration.KnownBadActors.Contains(clientId))
                {
                    logger.LogWarning("Rejected blocked client: {ClientId}", clientId);
                    e.ReasonCode = MqttConnectReasonCode.NotAuthorized;
                    return Task.CompletedTask;
                }

                // Check allowlist (if configured)
                if (_appSettings.ConnectionModeration.AllowedClients.Any() &&
                    !_appSettings.ConnectionModeration.AllowedClients.Contains(clientId))
                {
                    logger.LogWarning("Rejected client not on allowlist: {ClientId}", clientId);
                    e.ReasonCode = MqttConnectReasonCode.NotAuthorized;
                    return Task.CompletedTask;
                }

                // Authentication check
                if (_appSettings.ConnectionModeration.RequireAuthentication)
                {
                    if (string.IsNullOrEmpty(e.UserName) || string.IsNullOrEmpty(e.Password))
                    {
                        logger.LogWarning("Client {ClientId} rejected: authentication required", clientId);
                        fail2Ban.RecordFailedAttempt(clientId);
                        e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                        return Task.CompletedTask;
                    }

                    // TODO: Implement actual credential validation
                    // For now, just clear failed attempts on successful auth
                    fail2Ban.ClearFailedAttempts(clientId);
                }

                logger.LogInformation("Client connected: {ClientId}", clientId);
                e.ReasonCode = MqttConnectReasonCode.Success;
                return Task.CompletedTask;
            };

            // Handle subscriptions
            mqttServer.InterceptingSubscriptionAsync += e =>
            {
                var clientId = e.ClientId;
                var topic = e.TopicFilter.Topic;

                logger.LogDebug("Client {ClientId} subscribing to: {Topic}", clientId, topic);

                // Validate subscription topic
                if (!packetFiltering.IsTopicAllowed(topic))
                {
                    logger.LogWarning("Client {ClientId} blocked from subscribing to: {Topic}", clientId, topic);
                    e.Response.ReasonCode = MqttSubscribeReasonCode.TopicFilterInvalid;
                }

                return Task.CompletedTask;
            };

            // Handle incoming messages
            mqttServer.InterceptingPublishAsync += e =>
            {
                var clientId = e.ClientId;
                var topic = e.ApplicationMessage.Topic;

                // Extract packet info (simplified - in real implementation, decode protobuf)
                var packetInfo = ExtractPacketInfo(e.ApplicationMessage);

                // Check global rate limit
                if (!rateLimiting.CheckGlobalRateLimit())
                {
                    logger.LogWarning("Global rate limit exceeded, dropping packet");
                    e.ProcessPublish = false;
                    return Task.CompletedTask;
                }

                // Check for duplicate packets
                var packetHash = packetInfo.GetHash();
                if (rateLimiting.IsDuplicatePacket(packetHash))
                {
                    logger.LogDebug("Dropping duplicate packet from {NodeId}", packetInfo.NodeId);
                    e.ProcessPublish = false;
                    return Task.CompletedTask;
                }

                // Check per-node rate limit
                if (!string.IsNullOrEmpty(packetInfo.NodeId) && 
                    !rateLimiting.CheckNodeRateLimit(packetInfo.NodeId))
                {
                    logger.LogWarning("Node {NodeId} rate limited", packetInfo.NodeId);
                    e.ProcessPublish = false;
                    return Task.CompletedTask;
                }

                // Validate packet
                var validationResult = packetFiltering.ValidateMessage(e.ApplicationMessage, packetInfo);
                if (!validationResult.IsValid)
                {
                    logger.LogWarning("Packet validation failed: {Reason}", validationResult.Reason);
                    e.ProcessPublish = false;
                    return Task.CompletedTask;
                }

                // Check if we should zero-hop this packet
                if (packetInfo.PortNum.HasValue && 
                    packetFiltering.ShouldZeroHop(packetInfo.PortNum.Value))
                {
                    logger.LogDebug("Zero-hopping packet with port {PortNum}", packetInfo.PortNum.Value);
                    // Modify hop count to 0 (in real implementation, modify the protobuf)
                }

                logger.LogDebug("Processing packet from {ClientId} on topic {Topic}", clientId, topic);
                return Task.CompletedTask;
            };

            // Handle client disconnections
            mqttServer.ClientDisconnectedAsync += e =>
            {
                logger.LogInformation("Client disconnected: {ClientId}, Type: {Type}", 
                    e.ClientId, e.DisconnectType);
                return Task.CompletedTask;
            };

            // Start the server
            await mqttServer.StartAsync();
            logger.LogInformation("MQTT Broker started on port {Port} (SSL: {SSL})", 
                _appSettings.MqttBroker.Port, _appSettings.MqttBroker.UseSsl);

            // Print statistics periodically
            if (_appSettings.Metrics.Enabled)
            {
                _ = Task.Run(async () =>
                {
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_appSettings.Metrics.ExportInterval));
                        PrintStats(logger, fail2Ban, rateLimiting, packetFiltering);
                    }
                });
            }

            // Keep the application running
            await Task.Delay(Timeout.Infinite);
        }

        private static void PrintStats(
            ILogger logger,
            Fail2BanService fail2Ban,
            RateLimitingService rateLimiting,
            PacketFilteringService packetFiltering)
        {
            logger.LogInformation("=== Broker Statistics ===");
            
            var fail2BanStats = fail2Ban.GetStats();
            logger.LogInformation("Fail2Ban - Active Bans: {ActiveBans}, Total Banned: {TotalBanned}", 
                fail2BanStats["ActiveBans"], fail2BanStats["TotalBannedClients"]);

            var rateLimitStats = rateLimiting.GetStats();
            logger.LogInformation("Rate Limiting - Packets/min: {PacketsPerMin}, Banned Nodes: {BannedNodes}", 
                rateLimitStats["PacketsLastMinute"], rateLimitStats["BannedNodes"]);

            var filterStats = packetFiltering.GetStats();
            logger.LogInformation("Packet Filtering - Allowed: {Allowed}, Blocked: {Blocked}, Block Rate: {BlockRate:P2}", 
                filterStats["AllowedPackets"], filterStats["BlockedPackets"], filterStats["BlockRate"]);
        }

        private static MeshtasticPacketInfo ExtractPacketInfo(MqttApplicationMessage message)
        {
            // Simplified extraction - in real implementation, decode protobuf
            // This is just a placeholder showing the structure
            
            var info = new MeshtasticPacketInfo
            {
                Payload = message.PayloadSegment.Array ?? Array.Empty<byte>(),
                IsEncrypted = false,
                WasDecrypted = false
            };

            // Parse topic to extract channel info
            // Format: msh/REGION/2/e/CHANNEL/NODEID
            var parts = message.Topic.Split('/');
            if (parts.Length >= 6)
            {
                info.Channel = parts[4];
                info.NodeId = parts[5];
            }

            // In real implementation, decode the protobuf to get:
            // - PortNum
            // - HopCount
            // - Encryption status
            
            return info;
        }
    }
}
