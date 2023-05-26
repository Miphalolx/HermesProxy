dotnet publish ..\HermesProxy -p:PublishSingleFile=true  /p:PublishTrimmed=true -p:DebugType=None -p:DebugSymbols=false -r win-x64 /p:Configuration=Release /p:platform="x64" --self-contained
