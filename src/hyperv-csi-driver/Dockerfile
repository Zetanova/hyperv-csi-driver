#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-buster-slim AS base
RUN apt-get update && apt-get install -y \
	openssh-client
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/core/sdk:3.1-buster AS build
WORKDIR /src
COPY ["hyperv-csi-driver/HypervCsiDriver.csproj", "hyperv-csi-driver/"]
RUN dotnet restore "hyperv-csi-driver/HypervCsiDriver.csproj"
COPY . .
WORKDIR "/src/hyperv-csi-driver"
RUN dotnet build "HypervCsiDriver.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "HypervCsiDriver.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "HypervCsiDriver.dll"]