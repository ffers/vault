FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER root
WORKDIR /app

# Install Python 3 and pip
RUN apt-get update && apt-get install -y \
    python3 \
    python3-pip \
    python3-venv \
    && rm -rf /var/lib/apt/lists/*

USER $APP_UID

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["LanguageLab.TgBot/LanguageLab.TgBot.csproj", "LanguageLab.TgBot/"]
RUN dotnet restore "LanguageLab.TgBot/LanguageLab.TgBot.csproj"
COPY . .
WORKDIR "/src/LanguageLab.TgBot"
RUN dotnet build "./LanguageLab.TgBot.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./LanguageLab.TgBot.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Copy Python scripts and requirements
COPY process_fb2.py .
COPY requirements.txt .

# Install Python dependencies
USER root
RUN pip3 install --no-cache-dir -r requirements.txt
USER $APP_UID

ENTRYPOINT ["dotnet", "LanguageLab.TgBot.dll"]
