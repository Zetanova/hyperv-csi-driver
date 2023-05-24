FROM mcr.microsoft.com/dotnet/aspnet:6.0-focal AS base
RUN apt-get update && apt-get install -y \
	openssh-client \
	parted \
	udev \
	&& rm -rf /var/lib/apt/lists/*
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0-focal AS build
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