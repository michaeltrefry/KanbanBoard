FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ./KanbanBoard.slnx ./
COPY ./KanbanBoard.Api/KanbanBoard.Api.csproj ./KanbanBoard.Api/
COPY ./KanbanBoard.Mcp/KanbanBoard.Mcp.csproj ./KanbanBoard.Mcp/
COPY ./KanbanBoard.Shared/KanbanBoard.Shared.csproj ./KanbanBoard.Shared/
RUN dotnet restore ./KanbanBoard.Mcp/KanbanBoard.Mcp.csproj

COPY . .
RUN dotnet publish ./KanbanBoard.Mcp/KanbanBoard.Mcp.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 3001
ENTRYPOINT ["dotnet", "KanbanBoard.Mcp.dll"]
