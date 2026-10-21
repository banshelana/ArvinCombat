@echo off
rem Publishes the ArvinCombat match service for Linux (framework-dependent, needs ASP.NET Core 8 runtime on the server)
cd /d "%~dp0"
dotnet publish ArvinCombat.MatchService.csproj -c Release -r linux-x64 --self-contained false -o "..\..\Builds\MatchService"
echo.
echo Output: %~dp0..\..\Builds\MatchService   (main file: ArvinCombat.MatchService.dll)
pause
