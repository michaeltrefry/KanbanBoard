FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ./KanbanBoard.slnx ./
COPY ./KanbanBoard.Api/KanbanBoard.Api.csproj ./KanbanBoard.Api/
COPY ./KanbanBoard.Mcp/KanbanBoard.Mcp.csproj ./KanbanBoard.Mcp/
COPY ./KanbanBoard.Shared/KanbanBoard.Shared.csproj ./KanbanBoard.Shared/
RUN dotnet restore ./KanbanBoard.Api/KanbanBoard.Api.csproj

COPY . .
RUN dotnet publish ./KanbanBoard.Api/KanbanBoard.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8443
EXPOSE 8080
ENTRYPOINT ["dotnet", "KanbanBoard.Api.dll"]
