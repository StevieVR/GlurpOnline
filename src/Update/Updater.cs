using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using GlurpOnline.Net;
using GlurpOnline.UI;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;
using Version = System.Version;

namespace GlurpOnline.Update
{
    internal static class Updater
    {
        public static readonly Version Current = new Version(Plugin.Version);

        public static UpdatePackage Own { get; private set; }

        public static bool PeerUpdates => Plugin.PeerUpdates.Value;
        public static bool CanShare => Own != null && PeerUpdates;

        private sealed class Offer
        {
            public string Text;
            public UpdatePackage Package;
            public string DownloadUrl;
            public string Rejoin;
            public bool FromPeer => Package != null;
        }

        private static Offer offer;
        private static string installedVersion;
        private static bool busy;
        private static bool githubChecked;
        private static string pendingRejoin;

        private static string PluginPath => Plugin.Instance.Info.Location;
        private static string RejoinFile => Path.Combine(Paths.CachePath, "glurponline_rejoin.txt");

        public static void Init()
        {
            var own = UpdatePackage.FromFile(PluginPath);
            Own = own != null && own.Version == Current ? own : null;
            Plugin.Log.LogInfo(Own != null ? "Signed release build" : "Unsigned build: can receive updates but won't share them");
            var leftovers = Directory.GetFiles(Path.GetDirectoryName(PluginPath), Path.GetFileName(PluginPath) + ".old-*").ToList();
            leftovers.Add(PluginPath + ".download");
            foreach (var file in leftovers)
            {
                try { File.Delete(file); } catch { }
            }
            ReadRejoin();
        }

        public static bool IsNewer(string version) => Version.TryParse(version, out var v) && v > Current;

        public static string TakeRejoin()
        {
            var r = pendingRejoin;
            pendingRejoin = null;
            return r;
        }

        public static void OfferFromPeer(UpdatePackage package, string source, string rejoin)
        {
            if (package == null || package.Version <= Current || installedVersion != null || !PeerUpdates) return;
            offer = new Offer
            {
                Package = package,
                Rejoin = rejoin,
                Text = $"{source} has Glurp Online {package.VersionText} and you have {Plugin.Version}.\n"
                     + (rejoin != null ? "Update now? The game will restart and rejoin."
                        : Session.Current != null ? "Update now? Your lobby will close and the game will restart."
                        : "Update now? The game will restart."),
            };
        }

        public static void OnDownloadingFromPeer(string version) => Toasts.Show($"Getting Glurp Online {version} from the host…");

        public static void OnNewerPeer(string name, string version) => Toasts.Show($"{name} has Glurp Online {version}. Update to play together");

        public static void Update()
        {
            if (offer == null || busy || !OnlineUI.CanPrompt(allowInSession: offer.FromPeer)) return;
            var o = offer;
            offer = null;
            OnlineUI.Confirm(o.Text, "NOT NOW", "UPDATE", () => Plugin.Instance.StartCoroutine(Accept(o)));
        }

        private static IEnumerator Accept(Offer o)
        {
            busy = true;
            var package = o.Package;
            if (package == null)
            {
                Toasts.Show("Downloading update…");
                using (var req = UnityWebRequest.Get(o.DownloadUrl))
                {
                    req.timeout = 60;
                    yield return req.SendWebRequest();
                    if (!req.isNetworkError && !req.isHttpError) package = UpdatePackage.Parse(req.downloadHandler.data);
                    else Plugin.Log.LogWarning($"Update download failed: {req.responseCode} {req.error}");
                }
            }
            if (package == null || package.Version <= Current)
            {
                busy = false;
                OnlineUI.Info("Couldn't download the update. Check your connection and try again later.");
                yield break;
            }
            if (!Install(package, out string error))
            {
                busy = false;
                OnlineUI.Info("Couldn't install the update.\n" + error);
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.3f);
            Restart(o.Rejoin);
        }

        public static void OnMainMenu()
        {
            if (githubChecked || !Plugin.CheckForUpdates.Value) return;
            githubChecked = true;
            Plugin.Instance.StartCoroutine(CheckGitHub());
        }

        private static IEnumerator CheckGitHub()
        {
            string json;
            using (var req = UnityWebRequest.Get($"https://api.github.com/repos/{Plugin.UpdateRepository}/releases/latest"))
            {
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 20;
                yield return req.SendWebRequest();
                if (req.isNetworkError || req.isHttpError)
                {
                    Plugin.Log.LogInfo($"Update check skipped: {req.responseCode} {req.error}");
                    yield break;
                }
                json = req.downloadHandler.text;
            }
            try { ReadRelease(json); }
            catch (Exception e) { Plugin.Log.LogWarning("Update check failed: " + e); }
        }

        private static void ReadRelease(string json)
        {
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
            var dll = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]+/GlurpOnline\\.dll)\"");
            if (!tag.Success || !dll.Success || !Version.TryParse(tag.Groups[1].Value, out var latest))
            {
                Plugin.Log.LogWarning("Update check: the latest release has no GlurpOnline.dll");
                return;
            }
            string version = tag.Groups[1].Value;
            if (latest <= Current)
            {
                Plugin.Log.LogInfo($"Up to date (latest release is {version})");
                return;
            }
            Plugin.Log.LogInfo($"Glurp Online {version} is available");
            if (offer != null || installedVersion != null) return;
            offer = new Offer
            {
                DownloadUrl = dll.Groups[1].Value,
                Text = $"Glurp Online {version} is out! You have {Plugin.Version}.\nUpdate now? The game will restart.",
            };
        }

        private static bool Install(UpdatePackage package, out string error)
        {
            string path = PluginPath, temp = path + ".download", previous = path + ".old-" + DateTime.UtcNow.Ticks;
            try
            {
                File.WriteAllBytes(temp, package.Bytes);
                File.Move(path, previous);
                try { File.Move(temp, path); }
                catch { File.Move(previous, path); throw; }
                installedVersion = package.VersionText;
                Plugin.Log.LogInfo("Installed Glurp Online " + package.VersionText);
                error = null;
                return true;
            }
            catch (Exception e)
            {
                try { File.Delete(temp); } catch { }
                Plugin.Log.LogError("Update install failed: " + e);
                error = e is UnauthorizedAccessException ? "Windows wouldn't let the game write to its plugins folder." : e.Message;
                return false;
            }
        }

        private static void Restart(string rejoin)
        {
            try
            {
                if (!string.IsNullOrEmpty(rejoin)) File.WriteAllText(RejoinFile, DateTime.UtcNow.Ticks + "\n" + rejoin);
                string script = $"Wait-Process -Id {Process.GetCurrentProcess().Id} -Timeout 60 -ErrorAction SilentlyContinue; "
                              + "Start-Sleep -Milliseconds 800; " + LaunchCommand();
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var name in psi.EnvironmentVariables.Keys.Cast<string>().Where(k => k.StartsWith("doorstop", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    psi.EnvironmentVariables.Remove(name);
                }
                Process.Start(psi);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Couldn't schedule a restart: " + e);
                busy = false;
                OnlineUI.Info($"Glurp Online {installedVersion} is installed. Restart the game to finish updating.");
                return;
            }
            Plugin.Log.LogInfo("Restarting to finish the update");
            Application.Quit();
        }

        private static string LaunchCommand()
        {
            string Quote(string s) => "'" + s.Replace("'", "''") + "'";
            if (Session.SteamAvailable && !File.Exists(Path.Combine(Paths.GameRootPath, "steam_appid.txt")))
            {
                return "Start-Process " + Quote("steam://rungameid/" + SteamUtils.GetAppID().m_AppId);
            }
            var args = new List<string>();
            var given = Environment.GetCommandLineArgs();
            for (int i = 1; i < given.Length; i++)
            {
                if (given[i].StartsWith("+")) { i++; continue; }
                args.Add(given[i]);
            }
            string command = $"Start-Process -FilePath {Quote(Paths.ExecutablePath)} -WorkingDirectory {Quote(Paths.GameRootPath)}";
            if (args.Count > 0) command += " -ArgumentList " + string.Join(",", args.Select(Quote).ToArray());
            return command;
        }

        private static void ReadRejoin()
        {
            try
            {
                if (!File.Exists(RejoinFile)) return;
                var lines = File.ReadAllLines(RejoinFile);
                File.Delete(RejoinFile);
                if (lines.Length >= 2 && long.TryParse(lines[0], out long ticks)
                    && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromMinutes(5))
                {
                    pendingRejoin = lines[1];
                    Plugin.Log.LogInfo("Will rejoin after update: " + pendingRejoin);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't read rejoin info: " + e.Message); }
        }
    }
}
