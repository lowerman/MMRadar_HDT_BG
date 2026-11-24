using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MMRadar.Util;
using Newtonsoft.Json.Linq;

namespace MMRadar.Standalone
{
    /// <summary>A newer app release found on GitHub.</summary>
    public sealed class UpdateInfo
    {
        public Version Version { get; set; }
        public string AssetUrl { get; set; }
        public string AssetName { get; set; }
        public long AssetSize { get; set; }
        /// <summary>Hex SHA-256 published by GitHub for the asset, when available.</summary>
        public string Sha256 { get; set; }
        /// <summary>The release page (notes + manual download).</summary>
        public string PageUrl { get; set; }
    }

    /// <summary>
    /// In-app updates from this repository's GitHub releases. Never silent: the
    /// user starts every update. The app ships as a flat zip; an update renames
    /// the running files to *.old (Windows allows renaming files in use),
    /// copies the new ones in, restarts, and the next start sweeps the *.old.
    /// </summary>
    public static class UpdateService
    {
        /// <summary>owner/name of the GitHub repository (renames are redirected by GitHub).</summary>
        public const string Repository = "lowerman/MMRadar_HDT_BG";

        public static string ReleasesPage => $"https://github.com/{Repository}/releases";

        /// <summary>The plugin keeps the 1.0.x line in the same repository;
        /// only releases from this version up are standalone-app builds.</summary>
        private static readonly Version FirstAppVersion = new Version(1, 1, 0);

        private static readonly HttpClient Http = CreateClient();

        public static Version CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        private static string AppDirectory =>
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Add("User-Agent", "MMRadar-App");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return http;
        }

        /// <summary>The newest app release above the running version, or null.</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            var json = await Http.GetStringAsync(
                $"https://api.github.com/repos/{Repository}/releases?per_page=30").ConfigureAwait(false);
            UpdateInfo best = null;
            foreach (var release in JArray.Parse(json).OfType<JObject>())
            {
                if ((bool?)release["draft"] == true || (bool?)release["prerelease"] == true)
                    continue;
                var version = ParseTag((string)release["tag_name"]);
                if (version == null || version < FirstAppVersion || version <= CurrentVersion)
                    continue;
                if (best != null && version <= best.Version)
                    continue;
                var assetName = $"MMRadar-v{version.ToString(3)}.zip";
                var asset = (release["assets"] as JArray)?.OfType<JObject>()
                    .FirstOrDefault(a => string.Equals((string)a["name"], assetName, StringComparison.OrdinalIgnoreCase));
                if (asset == null)
                    continue;
                var digest = (string)asset["digest"];
                best = new UpdateInfo
                {
                    Version = version,
                    AssetUrl = (string)asset["browser_download_url"],
                    AssetName = assetName,
                    AssetSize = (long?)asset["size"] ?? 0,
                    Sha256 = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest.Substring(7).ToLowerInvariant()
                        : null,
                    PageUrl = (string)release["html_url"] ?? ReleasesPage,
                };
            }
            return best;
        }

        private static Version ParseTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return null;
            tag = tag.Trim().TrimStart('v', 'V');
            return Version.TryParse(tag, out var v) && v.Build >= 0 ? new Version(v.Major, v.Minor, v.Build) : null;
        }

        /// <summary>True when the app folder can be written (e.g. not under Program Files).</summary>
        public static bool CanUpdateInPlace()
        {
            try
            {
                var probe = Path.Combine(AppDirectory, ".mmradar-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Downloads, verifies and installs the update. Returns the exe to restart.</summary>
        public static async Task<string> DownloadAndInstallAsync(UpdateInfo update, IProgress<string> progress)
        {
            var work = Path.Combine(Path.GetTempPath(), "MMRadar-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var zipPath = Path.Combine(work, update.AssetName);

            progress?.Report("Downloading…");
            using (var response = await Http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var target = File.Create(zipPath))
                    await source.CopyToAsync(target).ConfigureAwait(false);
            }

            progress?.Report("Verifying…");
            var size = new FileInfo(zipPath).Length;
            if (update.AssetSize > 0 && size != update.AssetSize)
                throw new InvalidDataException($"Download is incomplete ({size} of {update.AssetSize} bytes).");
            if (update.Sha256 != null)
            {
                var actual = Sha256Of(zipPath);
                if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Download is damaged (checksum mismatch).");
            }

            progress?.Report("Installing…");
            return InstallFromZip(zipPath, work);
        }

        /// <summary>
        /// Installs a release zip over the running app. Only a flat archive with
        /// MMRadar.exe at its root is accepted. Any failure rolls back.
        /// </summary>
        public static string InstallFromZip(string zipPath, string workDirectory = null)
        {
            var work = workDirectory ?? Path.Combine(Path.GetTempPath(), "MMRadar-update-" + Guid.NewGuid().ToString("N"));
            var extracted = Path.Combine(work, "files");
            Directory.CreateDirectory(extracted);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    // Flat archive only: no folders, no path tricks ("zip slip").
                    if (entry.FullName.Contains("/") || entry.FullName.Contains("\\") ||
                        entry.FullName.Contains("..") || string.IsNullOrEmpty(entry.Name))
                        throw new InvalidDataException("Unexpected archive layout: " + entry.FullName);
                    entry.ExtractToFile(Path.Combine(extracted, entry.Name), true);
                }
            }
            if (!File.Exists(Path.Combine(extracted, "MMRadar.exe")))
                throw new InvalidDataException("The archive does not contain MMRadar.exe.");

            var appDir = AppDirectory;
            var moved = new List<(string Target, string Backup)>();
            var added = new List<string>();
            try
            {
                foreach (var file in Directory.GetFiles(extracted))
                {
                    var target = Path.Combine(appDir, Path.GetFileName(file));
                    if (File.Exists(target))
                    {
                        var backup = target + "." + DateTime.UtcNow.Ticks + ".old";
                        File.Move(target, backup); // works even for the running exe
                        moved.Add((target, backup));
                    }
                    else
                    {
                        added.Add(target);
                    }
                    File.Copy(file, target);
                }
            }
            catch
            {
                // Put the previous version back exactly as it was.
                foreach (var path in added)
                    TryDelete(path);
                foreach (var (target, backup) in moved)
                {
                    TryDelete(target);
                    try { File.Move(backup, target); } catch { /* best effort */ }
                }
                throw;
            }
            Logger.Info($"Update installed ({moved.Count} replaced, {added.Count} added)");
            TryDeleteDirectory(work);
            return Path.Combine(appDir, "MMRadar.exe");
        }

        /// <summary>Removes the previous version's files left behind by an update.</summary>
        public static void CleanupOldFiles()
        {
            try
            {
                foreach (var old in Directory.GetFiles(AppDirectory, "*.old"))
                    TryDelete(old); // a file still locked is simply retried next start
            }
            catch (Exception ex)
            {
                Logger.Debug("Update cleanup: " + ex.Message);
            }
        }

        public static void OpenInBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("Could not open " + url, ex);
            }
        }

        private static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* retried later */ }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* temp folder */ }
        }
    }
}
