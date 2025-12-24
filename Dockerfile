FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["Meshtastic.Mqtt.csproj", "./"]
RUN dotnet restore "Meshtastic.Mqtt.csproj"

# Copy source code
COPY . .

# Build
RUN dotnet build "Meshtastic.Mqtt.csproj" -c Release -o /app/build

# Publish
FROM build AS publish
RUN dotnet publish "Meshtastic.Mqtt.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# Copy published application
COPY --from=publish /app/publish .

# Copy configuration (will be overridden by volume mount)
COPY appsettings.json .

# Expose MQTT ports
EXPOSE 1883
EXPOSE 8883

# Run the application
ENTRYPOINT ["dotnet", "Meshtastic.Mqtt.dll"]
