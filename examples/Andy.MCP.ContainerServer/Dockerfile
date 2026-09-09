FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish examples/Andy.MCP.ContainerServer/Andy.MCP.ContainerServer.csproj -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:3000
EXPOSE 3000
USER app
ENTRYPOINT ["dotnet", "Andy.MCP.ContainerServer.dll"]
