# 发布被控端(单文件自包含 win-x64)
dotnet publish Remot.Server/Remot.Server.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/server

# 发布 CLI
dotnet publish Remot.Cli/Remot.Cli.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o publish/cli

# 发布 MCP server
dotnet publish Remot.Mcp/Remot.Mcp.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o publish/mcp

Write-Host "发布完成:publish/{server,cli,mcp}"
