# 发布被控端(单文件自包含 win-x64)
dotnet publish Remot.Server/Remot.Server.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/server

# 发布 MCP(客户端,供 Claude Code 等 agent 调用)
dotnet publish Remot.Mcp/Remot.Mcp.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/mcp

Write-Host "发布完成:publish/{server,mcp}"
