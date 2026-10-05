using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrivateCoin.Desktop
{
    [DataContract]
    internal sealed class UpdateManifest
    {
        [DataMember(Name = "version", IsRequired = true)] public string Version { get; set; }
        [DataMember(Name = "downloadUrl", IsRequired = true)] public string DownloadUrl { get; set; }
        [DataMember(Name = "sha256", IsRequired = true)] public string Sha256 { get; set; }
        [DataMember(Name = "releaseNotes", EmitDefaultValue = false)] public string ReleaseNotes { get; set; }
    }

    internal sealed class DesktopUpdater
    {
        private static readonly string[] ProtectedFiles = { "Blockchain.json", "wallets.dat", "peers.dat", "recovery.dat" };
        private readonly Uri manifestUri;

        public DesktopUpdater(string manifestUrl)
        {
            Uri uri;
            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("UpdateManifestUrl deve ser um endereço HTTPS válido.");
            manifestUri = uri;
        }

        public async Task<UpdateManifest> CheckAsync()
        {
            using (var client = CreateClient())
            using (Stream stream = await client.GetStreamAsync(manifestUri).ConfigureAwait(false))
            {
                var manifest = (UpdateManifest)new DataContractJsonSerializer(typeof(UpdateManifest)).ReadObject(stream);
                ValidateManifest(manifest);
                Version available = ParseVersion(manifest.Version, "A versão publicada é inválida.");
                return available > Assembly.GetExecutingAssembly().GetName().Version ? manifest : null;
            }
        }

        public async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<int> progress)
        {
            ValidateManifest(manifest);
            string directory = Path.Combine(Path.GetTempPath(), "POVIX-Update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string archivePath = Path.Combine(directory, "update.zip");
            try
            {
                using (var client = CreateClient())
                using (HttpResponseMessage response = await client.GetAsync(new Uri(manifest.DownloadUrl), HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength.GetValueOrDefault(-1);
                    using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long received = 0;
                        int read;
                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        {
                            await destination.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                            received += read;
                            if (progress != null && total > 0) progress.Report((int)Math.Min(100, received * 100 / total));
                        }
                    }
                }
                VerifyHash(archivePath, manifest.Sha256);
                string payloadDirectory = Path.Combine(directory, "payload");
                ExtractSafely(archivePath, payloadDirectory);
                File.Delete(archivePath);
                return payloadDirectory;
            }
            catch
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                throw;
            }
        }

        public static void LaunchInstaller(string payloadDirectory)
        {
            string applicationDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string executable = Path.GetFileName(Application.ExecutablePath);
            string updateRoot = Directory.GetParent(payloadDirectory).FullName;
            string scriptPath = Path.Combine(updateRoot, "install.cmd");
            int processId = Process.GetCurrentProcess().Id;
            string script = "@echo off\r\nsetlocal\r\n:wait\r\n" +
                "tasklist /FI \"PID eq " + processId + "\" 2>NUL | find \"" + processId + "\" >NUL\r\n" +
                "if not errorlevel 1 (timeout /t 1 /nobreak >NUL & goto wait)\r\n" +
                "robocopy \"" + payloadDirectory + "\" \"" + applicationDirectory + "\" /E /R:3 /W:1 >NUL\r\n" +
                "if errorlevel 8 (msg * \"Nao foi possivel instalar a atualizacao do POVIX.\" & exit /b 1)\r\n" +
                "start \"\" \"" + Path.Combine(applicationDirectory, executable) + "\"\r\n" +
                "cd /d \"%TEMP%\"\r\nrmdir /s /q \"" + updateRoot + "\"\r\n";
            File.WriteAllText(scriptPath, script, Encoding.ASCII);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"\"" + scriptPath + "\"\"")
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = updateRoot });
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("POVIX-Desktop/" + Assembly.GetExecutingAssembly().GetName().Version);
            return client;
        }

        private static void ValidateManifest(UpdateManifest manifest)
        {
            if (manifest == null) throw new InvalidDataException("O manifesto de atualização está vazio.");
            ParseVersion(manifest.Version, "A versão publicada é inválida.");
            Uri downloadUri;
            if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("O pacote de atualização não usa HTTPS.");
            string hash = (manifest.Sha256 ?? string.Empty).Trim();
            if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("O SHA-256 do pacote é inválido.");
        }

        private static Version ParseVersion(string value, string error)
        {
            Version version;
            if (!Version.TryParse(value, out version)) throw new InvalidDataException(error);
            return version;
        }

        private static void VerifyHash(string path, string expected)
        {
            string actual;
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path)) actual = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A atualização foi descartada porque o SHA-256 não corresponde ao manifesto.");
        }

        private static void ExtractSafely(string archivePath, string destination)
        {
            Directory.CreateDirectory(destination);
            string destinationRoot = Path.GetFullPath(destination + Path.DirectorySeparatorChar);
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("O pacote contém um caminho de arquivo inválido.");
                    if (ProtectedFiles.Contains(Path.GetFileName(target), StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException("O pacote tentou substituir dados locais da carteira.");
                    if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(target);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(target)); entry.ExtractToFile(target, true); }
                }
            }
            if (!File.Exists(Path.Combine(destination, Path.GetFileName(Application.ExecutablePath))))
                throw new InvalidDataException("O pacote não contém o executável do POVIX Desktop.");
        }
    }
}
