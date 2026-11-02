using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ArvinCombat.EditorTools
{
    /// <summary>
    /// One click: builds everything the Linux server needs, next to the WebGL build:
    ///
    ///   Builds/Linux/game3/
    ///     matchmaker/   ArvinCombat.MatchService.dll + appsettings.json   (dotnet publish, linux-x64)
    ///     server/       ArvinCombatRoom.x86_64 + _Data                    (Unity Linux Dedicated Server)
    ///     logs/
    ///     start.sh  stop.sh  status.sh  run.sh  README.txt
    ///   Builds/ArvinCombat-Linux-game3.zip
    ///
    /// Upload the game3 folder to /arvinplay/games/game3/ and run:  bash start.sh
    /// </summary>
    public static class ArvinCombatServerPackage
    {
        const string ScenePath = "Assets/ArvinCombat/Scenes/Main.unity";
        const string PackageDir = "Builds/Linux/game3";
        const string ZipPath = "Builds/ArvinCombat-Linux-game3.zip";
        const string MatchProject = "Server/MatchService/ArvinCombat.MatchService.csproj";

        [MenuItem("ArvinCombat/Build Linux Server Package (matchmaker + room server)", priority = 12)]
        public static void BuildPackage()
        {
            if (!File.Exists(ScenePath)) { ArvinCombatSetup.SetupAll(); }
            string root = Path.GetFullPath(PackageDir);
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(Path.Combine(root, "logs"));

                // 1) matchmaker
                EditorUtility.DisplayProgressBar("ArvinCombat", "Publishing matchmaker (dotnet publish linux-x64)...", 0.1f);
                if (!PublishMatchmaker(Path.Combine(root, "matchmaker"), out string pubLog))
                {
                    EditorUtility.ClearProgressBar();
                    Debug.LogError("ArvinCombat: matchmaker publish failed. Install the .NET 8 SDK (https://dotnet.microsoft.com) and try again.\n" + pubLog);
                    EditorUtility.DisplayDialog("ArvinCombat", "Matchmaker publish failed.\nIs the .NET 8 SDK installed? See the Console for details.", "OK");
                    return;
                }

                // 2) room server
                EditorUtility.DisplayProgressBar("ArvinCombat", "Building room server (Linux Dedicated Server)...", 0.4f);
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.Mono2x);
                var opts = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = Path.Combine(root, "server", "ArvinCombatRoom.x86_64"),
                    target = BuildTarget.StandaloneLinux64,
                    subtarget = (int)StandaloneBuildSubtarget.Server,
                    options = BuildOptions.None
                };
                var report = BuildPipeline.BuildPlayer(opts);
                if (report.summary.result != BuildResult.Succeeded)
                {
                    EditorUtility.ClearProgressBar();
                    Debug.LogError("ArvinCombat: room server build failed: " + report.summary.result +
                                   " (Unity Hub > Installs > 6000.0.39f1 > Add modules > Linux Dedicated Server Build Support)");
                    return;
                }

                // 3) scripts
                EditorUtility.DisplayProgressBar("ArvinCombat", "Writing start scripts...", 0.9f);
                WriteScripts(root);

                // 4) zip
                string zip = Path.GetFullPath(ZipPath);
                if (File.Exists(zip)) File.Delete(zip);
                System.IO.Compression.ZipFile.CreateFromDirectory(root, zip, System.IO.Compression.CompressionLevel.Optimal, true);

                Debug.Log($"<b>ArvinCombat</b>: Linux server package ready -> {root}\nZIP: {zip}\nUpload the game3 folder to /arvinplay/games/game3/ and run:  bash start.sh");
                EditorUtility.RevealInFinder(zip);
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static bool PublishMatchmaker(string outDir, out string log)
        {
            var sb = new StringBuilder();
            string proj = Path.GetFullPath(MatchProject);
            if (!File.Exists(proj)) { log = "Missing " + proj; return false; }
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"publish \"{proj}\" -c Release -r linux-x64 --self-contained false -o \"{outDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(proj)
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(5 * 60 * 1000)) { p.Kill(); sb.AppendLine("timeout"); log = sb.ToString(); return false; }
                    p.WaitForExit();
                    log = sb.ToString();
                    return p.ExitCode == 0 && File.Exists(Path.Combine(outDir, "ArvinCombat.MatchService.dll"));
                }
            }
            catch (System.Exception e)
            {
                log = "Could not run 'dotnet': " + e.Message;
                return false;
            }
        }

        // Linux scripts must use \n line endings
        static void WriteUnix(string path, string text) => File.WriteAllText(path, text.Replace("\r\n", "\n"), new UTF8Encoding(false));

        static void WriteScripts(string root)
        {
            const string common = @"cd ""$(dirname ""$0"")""
ROOT=""$(pwd)""
";
            WriteUnix(Path.Combine(root, "start.sh"), @"#!/usr/bin/env bash
# ArvinCombat (game3) - starts the matchmaker in the background.
# The matchmaker starts one room server per match by itself (server/ArvinCombatRoom.x86_64).
" + common + @"
if ! command -v dotnet >/dev/null 2>&1; then
  echo ""dotnet not found. Install the ASP.NET Core 8 runtime, e.g.: sudo apt-get install -y aspnetcore-runtime-8.0""
  exit 1
fi
chmod +x ""$ROOT/server/ArvinCombatRoom.x86_64"" ""$ROOT""/*.sh 2>/dev/null
mkdir -p ""$ROOT/logs""

# shared secret between the matchmaker and its rooms (created once, kept in room-secret.txt)
if [ ! -s ""$ROOT/room-secret.txt"" ]; then
  (openssl rand -hex 24 2>/dev/null || head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n') > ""$ROOT/room-secret.txt""
  chmod 600 ""$ROOT/room-secret.txt""
fi
export ArvinCombat__RoomSecret=""$(cat ""$ROOT/room-secret.txt"")""
# paths follow the folder this package was unpacked to
export ArvinCombat__RoomServerPath=""$ROOT/server/ArvinCombatRoom.x86_64""
export ArvinCombat__LogFolder=""$ROOT/logs""

if [ -f ""$ROOT/matchmaker.pid"" ] && kill -0 ""$(cat ""$ROOT/matchmaker.pid"")"" 2>/dev/null; then
  echo ""Matchmaker already running (pid $(cat ""$ROOT/matchmaker.pid""))""
  exit 0
fi
cd ""$ROOT/matchmaker""
nohup dotnet ArvinCombat.MatchService.dll >> ""$ROOT/logs/matchmaker-console.log"" 2>&1 &
echo $! > ""$ROOT/matchmaker.pid""
sleep 2
echo ""Matchmaker started (pid $(cat ""$ROOT/matchmaker.pid"")).""
curl -s http://127.0.0.1:5080/status && echo
");

            WriteUnix(Path.Combine(root, "run.sh"), @"#!/usr/bin/env bash
# Same as start.sh but stays in the foreground (Ctrl+C to stop) - handy for testing.
" + common + @"
chmod +x ""$ROOT/server/ArvinCombatRoom.x86_64"" 2>/dev/null
mkdir -p ""$ROOT/logs""
if [ ! -s ""$ROOT/room-secret.txt"" ]; then (openssl rand -hex 24 2>/dev/null || head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n') > ""$ROOT/room-secret.txt""; fi
export ArvinCombat__RoomSecret=""$(cat ""$ROOT/room-secret.txt"")""
export ArvinCombat__RoomServerPath=""$ROOT/server/ArvinCombatRoom.x86_64""
export ArvinCombat__LogFolder=""$ROOT/logs""
cd ""$ROOT/matchmaker"" && exec dotnet ArvinCombat.MatchService.dll
");

            WriteUnix(Path.Combine(root, "stop.sh"), @"#!/usr/bin/env bash
# Stops the matchmaker and every running room server.
" + common + @"
if [ -f ""$ROOT/matchmaker.pid"" ]; then kill ""$(cat ""$ROOT/matchmaker.pid"")"" 2>/dev/null; rm -f ""$ROOT/matchmaker.pid""; fi
pkill -f ""$ROOT/server/ArvinCombatRoom.x86_64"" 2>/dev/null
echo ""Stopped.""
");

            WriteUnix(Path.Combine(root, "status.sh"), @"#!/usr/bin/env bash
" + common + @"
if [ -f ""$ROOT/matchmaker.pid"" ] && kill -0 ""$(cat ""$ROOT/matchmaker.pid"")"" 2>/dev/null; then echo ""matchmaker: running (pid $(cat ""$ROOT/matchmaker.pid""))""; else echo ""matchmaker: stopped""; fi
echo ""rooms running: $(pgrep -fc ""$ROOT/server/ArvinCombatRoom.x86_64"")""
curl -s http://127.0.0.1:5080/status && echo
");

            WriteUnix(Path.Combine(root, "README.txt"), @"ArvinCombat - Linux server package (game3)

  matchmaker/  ArvinCombat.MatchService.dll  (.NET 8, needs: ASP.NET Core 8 runtime)
  server/      ArvinCombatRoom.x86_64        (Unity Linux dedicated room server, started by the matchmaker)
  logs/        matchmaker + room logs

Install:
  1. Upload this folder to the Linux server, e.g. /arvinplay/games/game3/
  2. bash start.sh        (status: bash status.sh   stop: bash stop.sh   foreground test: bash run.sh)
  3. Check http://127.0.0.1:5080/status  ->  ""configured"": true

Web side (TLS on 443): route these to this machine -
  wss://mm3.arvinplay.com/match       -> http://127.0.0.1:5080/match
  https://mm3.arvinplay.com/status    -> http://127.0.0.1:5080/status
  wss://mm3.arvinplay.com/room/{port} -> ws://127.0.0.1:{port}   (ports 7800-7899)
  (ready nginx config: nginx-arvincombat.conf)

Settings: matchmaker/appsettings.json  (RoomPortRange, MaxRooms, AllowedOrigin, ...)
RoomSecret is created automatically in room-secret.txt by start.sh.
Optional auto-start at boot: arvincombat-matchmaker.service (systemd).
");

            // optional helpers
            foreach (var f in new[] { "Server/linux/nginx-arvincombat.conf", "Server/linux/arvincombat-matchmaker.service" })
                if (File.Exists(f)) File.Copy(f, Path.Combine(root, Path.GetFileName(f)), true);
        }
    }
}
