FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files for restore
COPY Interstellar.sln ./
COPY nuget.config ./
COPY Interstellar.Server/Interstellar.Server.csproj Interstellar.Server/
COPY Interstellar.Messages/Interstellar.Messages.csproj Interstellar.Messages/

RUN dotnet restore Interstellar.Server/Interstellar.Server.csproj

# Copy all source code (Server and Messages are the only projects needed for the server image)
COPY . .
RUN dotnet publish Interstellar.Server/Interstellar.Server.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false /p:LangVersion=preview

FROM mcr.microsoft.com/dotnet/runtime:8.0.8 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Best / optimal player count for the server dashboard.
# Set to a positive number (e.g. 12) so the dashboard shows a
# recommended lobby size.  Omit or set to 0 to disable.
ENV OPTIMAL_PLAYERS=0

# Run as non-root for better security
RUN chown -R 10001:10001 /app
USER 10001:10001

LABEL org.opencontainers.image.title="Interstellar.Server"

EXPOSE 22021

ENTRYPOINT ["dotnet", "Interstellar.Server.dll"]
CMD ["0.0.0.0:22021"]
