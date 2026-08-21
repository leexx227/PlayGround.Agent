FROM mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.24 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/CodingAgent.Host/CodingAgent.Host.csproj -c Release -r linux-musl-x64 --self-contained false -p:PublishReadyToRun=true -o /app

FROM mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.24 AS final
RUN apk add --no-cache git
WORKDIR /app
COPY --from=build /app .
EXPOSE 8088
ENTRYPOINT ["dotnet", "CodingAgent.Host.dll"]