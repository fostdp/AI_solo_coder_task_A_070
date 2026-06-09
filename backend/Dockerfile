FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["AluminaDetection.Api/AluminaDetection.Api.csproj", "AluminaDetection.Api/"]
RUN dotnet restore "AluminaDetection.Api/AluminaDetection.Api.csproj"

COPY AluminaDetection.Api/ AluminaDetection.Api/
RUN dotnet publish "AluminaDetection.Api/AluminaDetection.Api.csproj" \
    -c Release -o /app/publish \
    --no-restore \
    /p:PublishTrimmed=false \
    /p:PublishSingleFile=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser

COPY --from=build /app/publish .

RUN mkdir -p /app/logs && chown -R appuser:appgroup /app/logs

USER appuser

EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

ENTRYPOINT ["dotnet", "AluminaDetection.Api.dll"]
