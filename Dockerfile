FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Program.cs", "./"]
COPY ["*.csproj", "./"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY .env .
COPY addresses-groups.json .

# Set environment variables
ENV DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "ReceiptSenderToWhatsApp.dll"]