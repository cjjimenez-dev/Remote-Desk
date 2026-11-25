# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY ["RemoteDesk.Server.Signaling/RemoteDesk.Server.Signaling.csproj", "RemoteDesk.Server.Signaling/"]
COPY ["RemoteDesk.Core/RemoteDesk.Core.csproj", "RemoteDesk.Core/"]

# Restore
RUN dotnet restore "RemoteDesk.Server.Signaling/RemoteDesk.Server.Signaling.csproj"

# Copy all source
COPY . .

# Build and publish
WORKDIR /src/RemoteDesk.Server.Signaling
RUN dotnet publish -c Release -r linux-x64 --self-contained false -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

# Run as non-root
USER app
EXPOSE 5000
ENTRYPOINT ["dotnet", "RemoteDesk.Server.Signaling.dll"]