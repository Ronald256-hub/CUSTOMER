using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Pos.Launcher;

internal static class Program
{
    private const string ProductName = "Nexus POS";
    private const int DefaultPort = 8765;
    private static readonly Regex HostnamePattern = new(
        "^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        try
        {
            LauncherOptions options = LauncherOptions.Parse(args);
            Run(options);
            return 0;
        }
        catch (Exception exception)
        {
            TryLog("ERROR " + exception);
            MessageBox.Show(
                $"{ProductName} could not start.\r\n\r\n{exception.Message}\r\n\r\nRun Repair and Diagnose from the Start menu.",
                ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Run(LauncherOptions options)
    {
        string appRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar))?.FullName
            ?? throw new InvalidOperationException("The application folder could not be resolved.");

        string serverRoot = Path.Combine(appRoot, "runtime");
        string serverExe = Path.Combine(serverRoot, "Robo.Pos.Server.exe");
        string interfaceFile = Path.Combine(serverRoot, "wwwroot", "index.html");

        if (!File.Exists(serverExe) || !File.Exists(interfaceFile))
        {
            throw new FileNotFoundException(
                "The secure Nexus POS runtime is incomplete. Reinstall the application.",
                !File.Exists(serverExe) ? serverExe : interfaceFile);
        }

        string packageRoot = Directory.GetParent(appRoot)?.FullName ?? appRoot;
        string dataDirectory = options.DataDirectory ?? (options.Portable
            ? Path.Combine(packageRoot, "portable-data")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nexus POS",
                "Data"));

        string documentRoot = options.Portable
            ? Path.Combine(dataDirectory, "Audit Documents")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                "Nexus POS",
                "Audit Documents");

        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(documentRoot);
        CurrentLogPath = Path.Combine(dataDirectory, "launcher.log");

        string databaseFile = ResolveDatabaseFile(dataDirectory);
        string instanceId = ReadOrCreateInstanceId(dataDirectory);
        string credentialFile = Path.Combine(dataDirectory, "FIRST_LOGIN_CREDENTIALS.txt");
        bool createdCredentialFile = false;
        string administratorPassword;

        if (File.Exists(credentialFile))
        {
            administratorPassword = ReadCredential(credentialFile, "admin");
        }
        else if (!File.Exists(databaseFile))
        {
            administratorPassword = CreateTemporaryPassword();
            WriteCredentialFile(credentialFile, administratorPassword);
            ProtectCredentialFile(credentialFile);
            createdCredentialFile = true;
        }
        else
        {
            administratorPassword = CreateTemporaryPassword();
        }

        bool networkEnabled = File.Exists(Path.Combine(dataDirectory, "shop-network.enabled"));
        bool cloudflareEnabled = File.Exists(Path.Combine(dataDirectory, "cloudflare.enabled"));
        string cloudflareHost = string.Empty;

        var environment = new Dictionary<string, string?>
        {
            ["NEXUS_DATA_DIR"] = dataDirectory,
            ["NEXUS_DOCUMENT_ROOT"] = documentRoot,
            ["NEXUS_ADMIN_USERNAME"] = "admin",
            ["NEXUS_ADMIN_DISPLAY_NAME"] = "Business Owner",
            ["NEXUS_ADMIN_INITIAL_PASSWORD"] = administratorPassword,
            ["NEXUS_INSTANCE_ID"] = instanceId,
            ["ROBO_DATA_DIR"] = dataDirectory,
            ["ROBO_DOCUMENT_ROOT"] = documentRoot,
            ["ROBO_ADMIN_INITIAL_PASSWORD"] = administratorPassword,
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        };

        if (cloudflareEnabled)
        {
            string hostFile = Path.Combine(dataDirectory, "cloudflare-host.txt");
            if (!File.Exists(hostFile))
            {
                throw new InvalidOperationException(
                    "Cloudflare mode is enabled but cloudflare-host.txt is missing.");
            }

            cloudflareHost = File.ReadLines(hostFile).FirstOrDefault()?.Trim().ToLowerInvariant()
                ?? string.Empty;

            if (!HostnamePattern.IsMatch(cloudflareHost) ||
                Uri.CheckHostName(cloudflareHost) != UriHostNameType.Dns)
            {
                throw new InvalidOperationException("The configured Cloudflare hostname is invalid.");
            }

            environment["NEXUS_TRUST_PROXY"] = "true";
            environment["NEXUS_FORCE_SECURE_COOKIES"] = "true";
            environment["NEXUS_ALLOWED_ORIGINS"] = $"https://{cloudflareHost}";
            environment["AllowedHosts"] = $"localhost;127.0.0.1;{cloudflareHost}";
        }
        else if (networkEnabled)
        {
            environment["AllowedHosts"] = "*";
        }
        else
        {
            environment["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
        }

        string listenHost = networkEnabled && !cloudflareEnabled
            ? "0.0.0.0"
            : "127.0.0.1";

        int preferredPort = ReadConfiguredPort(dataDirectory);
        IReadOnlyList<int> portCandidates = cloudflareEnabled
            ? [preferredPort]
            : [preferredPort, .. Enumerable.Range(DefaultPort, 11).Where(port => port != preferredPort)];

        int? selectedPort = null;
        bool existingServer = false;

        foreach (int candidate in portCandidates)
        {
            if (IsNexusServerReady(candidate, instanceId).GetAwaiter().GetResult())
            {
                selectedPort = candidate;
                existingServer = true;
                break;
            }

            if (!IsPortOpen(candidate))
            {
                selectedPort = candidate;
                break;
            }
        }

        if (selectedPort is null)
        {
            throw new InvalidOperationException(
                "No Nexus POS server port is available. Close the conflicting program or change server-port.txt.");
        }

        int port = selectedPort.Value;

        if (!existingServer)
        {
            StartServer(serverExe, serverRoot, listenHost, port, dataDirectory, environment);
            WaitForServer(port, instanceId);
        }

        string browserUrl = cloudflareEnabled
            ? $"https://{cloudflareHost}/"
            : $"http://127.0.0.1:{port}/";

        TryLog(
            $"Application opened. Url={browserUrl}; DataDir={dataDirectory}; Network={networkEnabled}; Cloudflare={cloudflareEnabled}; ListenHost={listenHost}; Port={port}");

        if (createdCredentialFile)
        {
            MessageBox.Show(
                "Strong temporary administrator credentials were created and will open in Notepad. Keep them private and change the password immediately after first login.",
                ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{credentialFile}\"")
            {
                UseShellExecute = true
            });
        }

        OpenApplicationWindow(browserUrl);
    }

    private static string ResolveDatabaseFile(string dataDirectory)
    {
        string current = Path.Combine(dataDirectory, "nexus-pos.db");
        string legacy = Path.Combine(dataDirectory, "robo-pos.db");
        return File.Exists(current) || !File.Exists(legacy) ? current : legacy;
    }

    private static string ReadOrCreateInstanceId(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, "instance-id.txt");
        if (File.Exists(path))
        {
            string existing = File.ReadLines(path).FirstOrDefault()?.Trim() ?? string.Empty;
            if (Guid.TryParseExact(existing, "N", out _))
            {
                return existing;
            }
        }

        string instanceId = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, instanceId, Encoding.ASCII);
        return instanceId;
    }

    private static int ReadConfiguredPort(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, "server-port.txt");
        if (!File.Exists(path))
        {
            return DefaultPort;
        }

        string text = File.ReadLines(path).FirstOrDefault()?.Trim() ?? string.Empty;
        if (!int.TryParse(text, out int port) || port is < 1024 or > 65535)
        {
            throw new InvalidOperationException("The configured Nexus POS server port is invalid.");
        }

        return port;
    }

    private static void StartServer(
        string serverExe,
        string serverRoot,
        string listenHost,
        int port,
        string dataDirectory,
        IReadOnlyDictionary<string, string?> environment)
    {
        string outputLog = Path.Combine(dataDirectory, "server-output.log");
        string errorLog = Path.Combine(dataDirectory, "server-error.log");
        string pidFile = Path.Combine(dataDirectory, "server.pid");

        var startInfo = new ProcessStartInfo
        {
            FileName = serverExe,
            Arguments = $"--urls \"http://{listenHost}:{port}\"",
            WorkingDirectory = serverRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach ((string name, string? value) in environment)
        {
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Nexus POS server process could not be created.");

        _ = PumpAsync(process.StandardOutput, outputLog);
        _ = PumpAsync(process.StandardError, errorLog);
        File.WriteAllText(pidFile, process.Id.ToString(), Encoding.ASCII);
    }

    private static async Task PumpAsync(StreamReader reader, string path)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            while (await reader.ReadLineAsync() is { } line)
            {
                await writer.WriteLineAsync($"{DateTimeOffset.Now:O} {line}");
                await writer.FlushAsync();
            }
        }
        catch
        {
        }
    }

    private static void WaitForServer(int port, string instanceId)
    {
        for (int attempt = 0; attempt < 80; attempt++)
        {
            Thread.Sleep(250);
            if (IsNexusServerReady(port, instanceId).GetAwaiter().GetResult())
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "The secure local server did not become ready. Review server-error.log in the Nexus POS data folder.");
    }

    private static async Task<bool> IsNexusServerReady(int port, string instanceId)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string response = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v3/health");
            return response.Contains("Nexus POS", StringComparison.Ordinal) &&
                   response.Contains("schemaVersion", StringComparison.Ordinal) &&
                   response.Contains(instanceId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            Task connection = client.ConnectAsync("127.0.0.1", port);
            return connection.Wait(TimeSpan.FromMilliseconds(350)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static void OpenApplicationWindow(string url)
    {
        string[] edgeCandidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe")
        ];

        string? edge = edgeCandidates.FirstOrDefault(File.Exists);
        if (edge is not null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = edge,
                Arguments = $"--app=\"{url}\" --start-maximized",
                UseShellExecute = true
            });
            return;
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string CreateTemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#%*-_+?";
        string all = upper + lower + digits + symbols;

        var characters = new List<char>
        {
            RandomCharacter(upper),
            RandomCharacter(lower),
            RandomCharacter(digits),
            RandomCharacter(symbols)
        };

        while (characters.Count < 20)
        {
            characters.Add(RandomCharacter(all));
        }

        for (int index = characters.Count - 1; index > 0; index--)
        {
            int swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (characters[index], characters[swapIndex]) = (characters[swapIndex], characters[index]);
        }

        return new string([.. characters]);
    }

    private static char RandomCharacter(string source) =>
        source[RandomNumberGenerator.GetInt32(source.Length)];

    private static void WriteCredentialFile(string path, string password)
    {
        string content = $"""
            NEXUS POS
            FIRST LOGIN CREDENTIALS

            Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}

            Administrator
            Username: admin
            admin={password}

            Change this temporary password immediately after first login.
            Add teller accounts from Administration > Teller Accounts.
            Keep this file private and delete it after the password is changed.
            """;

        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string ReadCredential(string path, string name)
    {
        string prefix = name + "=";
        string? line = File.ReadLines(path)
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(line))
        {
            throw new InvalidOperationException("The first-login credential file is incomplete.");
        }

        return line[prefix.Length..];
    }

    private static void ProtectCredentialFile(string path)
    {
        try
        {
            string identity = string.IsNullOrWhiteSpace(Environment.UserDomainName)
                ? Environment.UserName
                : $"{Environment.UserDomainName}\\{Environment.UserName}";

            Process.Start(new ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = $"\"{path}\" /inheritance:r /grant:r \"{identity}:(R,W)\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(5000);
        }
        catch
        {
            TryLog("Credential file permissions could not be restricted automatically.");
        }
    }

    private static string? CurrentLogPath { get; set; }

    private static void TryLog(string message)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(CurrentLogPath))
            {
                File.AppendAllText(
                    CurrentLogPath,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch
        {
        }
    }

    private sealed record LauncherOptions(bool Portable, string? DataDirectory)
    {
        public static LauncherOptions Parse(string[] args)
        {
            bool portable = false;
            string? dataDirectory = null;

            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index].ToLowerInvariant())
                {
                    case "--portable":
                    case "-portable":
                        portable = true;
                        break;
                    case "--data-dir":
                        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                        {
                            throw new ArgumentException("--data-dir requires a directory path.");
                        }
                        dataDirectory = Path.GetFullPath(args[index]);
                        break;
                }
            }

            return new LauncherOptions(portable, dataDirectory);
        }
    }
}
