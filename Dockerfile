# Use the official .NET 10.0 SDK image to build the app
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy solution and project files
COPY McpServer.slnx ./
COPY src/McpServer/McpServer.csproj src/McpServer/

# Restore dependencies
RUN dotnet restore src/McpServer/McpServer.csproj

# Copy remaining source code
COPY . .
WORKDIR /app/src/McpServer
RUN dotnet publish McpServer.csproj -c Release -o /app/publish

# Use the ASP.NET runtime image for the final stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Expose ports
EXPOSE 80
EXPOSE 443

ENTRYPOINT ["dotnet", "McpServer.dll"]
