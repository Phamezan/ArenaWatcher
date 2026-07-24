FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY ["DiscordBot.csproj", "./"]
RUN dotnet restore "DiscordBot.csproj"

# Copy remaining source files and publish
COPY . .
RUN dotnet publish "DiscordBot.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app

# Install font packages required by SixLabors.ImageSharp / SystemFonts
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        fontconfig \
        fonts-dejavu-core && \
    rm -rf /var/lib/apt/lists/*

# Prepare config and data directory targets for volume mounts
RUN mkdir -p /app/config /app/data

COPY --from=build /app/publish .

ENV ARENA_BOT_CONFIG=/app/config/appsettings.json

ENTRYPOINT ["dotnet", "DiscordBot.dll"]
