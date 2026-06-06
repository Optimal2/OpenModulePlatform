using System.Drawing;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.Bootstrapper;

internal static partial class Program
{
    private static int RunInstallerGui(CliOptions cli)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The graphical installer requires Windows.");
            return 1;
        }

        try
        {
            var configProfiles = ResolveGuiConfigProfiles(cli);
            var selectedConfigPath = ResolveGuiSelectedConfigPath(cli, configProfiles);
            var config = ReadJsonAsync<BootstrapConfig>(selectedConfigPath).GetAwaiter().GetResult();
            var payloadRoot = ResolvePayloadRoot(cli, selectedConfigPath);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(defaultValue: false);
            using var form = new InstallerForm(configProfiles, config, selectedConfigPath, payloadRoot, cli);
            Application.Run(form);
            return form.ExitCode;
        }
        catch (JsonException ex)
        {
            // Top-level WinForms boundary: keep startup failures in the installer UI instead of crashing without context.
            MessageBox.Show(
                ex.Message,
                "OpenModulePlatform installer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
        catch (SystemException ex)
        {
            // Top-level WinForms boundary: keep startup failures in the installer UI instead of crashing without context.
            MessageBox.Show(
                ex.Message,
                "OpenModulePlatform installer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static IReadOnlyList<BootstrapConfigProfile> ResolveGuiConfigProfiles(CliOptions cli)
    {
        var profiles = new Dictionary<string, BootstrapConfigProfile>(StringComparer.OrdinalIgnoreCase);
        void AddConfigFile(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return;
            }

            var config = ReadJsonAsync<BootstrapConfig>(fullPath).GetAwaiter().GetResult();
            var name = Path.GetFileNameWithoutExtension(fullPath)
                .Replace("bootstrap.", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(".sample", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace('.', ' ')
                .Replace('-', ' ')
                .Replace('_', ' ')
                .Trim();
            if (!string.IsNullOrWhiteSpace(config.Profile.DisplayName))
            {
                name = config.Profile.DisplayName.Trim();
            }

            profiles[fullPath] = new BootstrapConfigProfile(
                string.IsNullOrWhiteSpace(name) ? Path.GetFileName(fullPath) : name,
                fullPath,
                ResolveProfileMachineNames(config));
        }

        void AddConfigDirectory(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                return;
            }

            foreach (var configPath in Directory.EnumerateFiles(fullPath, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
            {
                AddConfigFile(configPath);
            }
        }

        void AddHostProfileRoot(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                return;
            }

            foreach (var profileDirectory in Directory.EnumerateDirectories(fullPath, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
            {
                var bootstrapPath = Path.Join(profileDirectory, "bootstrap.json");
                if (File.Exists(bootstrapPath))
                {
                    AddConfigFile(bootstrapPath);
                    continue;
                }

                AddConfigDirectory(profileDirectory);
            }
        }

        if (!string.IsNullOrWhiteSpace(cli.ConfigPath))
        {
            AddConfigFile(cli.ConfigPath);
        }

        foreach (var directory in EnumerateGuiConfigDirectories(cli))
        {
            AddConfigDirectory(directory);
        }

        foreach (var directory in EnumerateGuiHostProfileRoots(cli))
        {
            AddHostProfileRoot(directory);
        }

        if (profiles.Count == 0)
        {
            foreach (var path in EnumerateLegacyGuiConfigFiles())
            {
                AddConfigFile(path);
            }
        }

        return profiles.Values
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ConfigPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveGuiSelectedConfigPath(
        CliOptions cli,
        IReadOnlyList<BootstrapConfigProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            throw new FileNotFoundException(
                "No bootstrap configuration file was found. Create a machine-specific profile in 'hosts\\<profile>\\bootstrap.json' or in the package 'configs' folder before starting the installer.");
        }

        var localMachineNames = GetLocalMachineNames();
        var machineMatches = profiles
            .Where(profile => ProfileMatchesMachine(profile, localMachineNames))
            .ToArray();
        if (machineMatches.Length == 1)
        {
            return machineMatches[0].ConfigPath;
        }

        if (machineMatches.Length > 1)
        {
            throw new InvalidOperationException(
                "More than one bootstrap configuration matches this computer. Keep exactly one matching host profile, then start the installer again." + Environment.NewLine + Environment.NewLine
                + "Local computer names: " + string.Join(", ", localMachineNames.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)) + Environment.NewLine
                + "Matching configs: " + string.Join(", ", machineMatches.Select(static profile => profile.ConfigPath)));
        }

        throw new InvalidOperationException(
            "No bootstrap configuration matches this computer. The installer is locked to the config whose profile.machineNames, hostAgent.hostName, or hostAgent.hostKey matches the local computer name." + Environment.NewLine + Environment.NewLine
            + "Local computer names: " + string.Join(", ", localMachineNames.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)) + Environment.NewLine
            + "Profile folders: " + string.Join(Environment.NewLine + "  ", profiles.Select(static profile => Path.GetDirectoryName(profile.ConfigPath)).Distinct(StringComparer.OrdinalIgnoreCase)) + Environment.NewLine + Environment.NewLine
            + "Create or update a matching host profile, then start the installer again.");
    }

    private static IReadOnlyList<string> ResolveProfileMachineNames(BootstrapConfig config)
    {
        var names = new List<string>();
        names.AddRange(config.Profile.MachineNames);
        if (!string.IsNullOrWhiteSpace(config.HostAgent.HostName))
        {
            names.Add(config.HostAgent.HostName);
        }

        if (!string.IsNullOrWhiteSpace(config.HostAgent.HostKey))
        {
            names.Add(config.HostAgent.HostKey);
        }

        return names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlySet<string> GetLocalMachineNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddMachineName(names, Environment.MachineName);
        try
        {
            AddMachineName(names, Dns.GetHostName());
        }
        catch (System.Net.Sockets.SocketException)
        {
            // DNS lookups are not required for profile matching; the local
            // machine name above is enough for normal Windows installations.
        }

        return names;
    }

    private static void AddMachineName(HashSet<string> names, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        names.Add(normalized);
        var dotIndex = normalized.IndexOf('.');
        if (dotIndex > 0)
        {
            names.Add(normalized[..dotIndex]);
        }
    }

    private static bool ProfileMatchesMachine(
        BootstrapConfigProfile profile,
        IReadOnlySet<string> localMachineNames)
        => profile.MachineNames.Any(name => MachineNameMatches(name, localMachineNames));

    private static bool MachineNameMatches(string configuredName, IReadOnlySet<string> localMachineNames)
    {
        if (string.IsNullOrWhiteSpace(configuredName))
        {
            return false;
        }

        var normalized = configuredName.Trim();
        if (localMachineNames.Contains(normalized))
        {
            return true;
        }

        var dotIndex = normalized.IndexOf('.');
        return dotIndex > 0 && localMachineNames.Contains(normalized[..dotIndex]);
    }

    private static IEnumerable<string> EnumerateGuiConfigDirectories(CliOptions cli)
    {
        if (!string.IsNullOrWhiteSpace(cli.ConfigDirectory))
        {
            yield return cli.ConfigDirectory;
        }

        if (!string.IsNullOrWhiteSpace(cli.ConfigPath)
            && Path.GetDirectoryName(cli.ConfigPath) is { } explicitConfigDirectory)
        {
            yield return explicitConfigDirectory;
            yield return Path.Join(explicitConfigDirectory, "configs");
        }

        yield return Path.Join(Environment.CurrentDirectory, "configs");
        yield return Path.Join(AppContext.BaseDirectory, "configs");
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "configs"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "configs"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "configs"));
    }

    private static IEnumerable<string> EnumerateGuiHostProfileRoots(CliOptions cli)
    {
        if (!string.IsNullOrWhiteSpace(cli.ConfigDirectory))
        {
            yield return Path.Join(cli.ConfigDirectory, "hosts");
        }

        if (!string.IsNullOrWhiteSpace(cli.ConfigPath)
            && Path.GetDirectoryName(cli.ConfigPath) is { } explicitConfigDirectory)
        {
            yield return Path.Join(explicitConfigDirectory, "hosts");
            yield return Path.GetFullPath(Path.Join(explicitConfigDirectory, ".."));
            yield return Path.GetFullPath(Path.Join(explicitConfigDirectory, "..", "..", "hosts"));
        }

        yield return Path.Join(Environment.CurrentDirectory, "hosts");
        yield return Path.Join(AppContext.BaseDirectory, "hosts");
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "hosts"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "hosts"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "hosts"));
    }

    private static IEnumerable<string> EnumerateLegacyGuiConfigFiles()
    {
        yield return Path.Join(Environment.CurrentDirectory, "bootstrap.local.sample.json");
        yield return Path.Join(AppContext.BaseDirectory, "bootstrap.local.sample.json");
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "bootstrap.local.sample.json"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "bootstrap.local.sample.json"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "bootstrap.local.sample.json"));
    }

    private sealed record BootstrapConfigProfile(
        string DisplayName,
        string ConfigPath,
        IReadOnlyList<string> MachineNames);

    private static async Task<int> RunSyncPackageObjectsAsync(CliOptions cli)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Package-object sync currently uses the Windows installer profile loader.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(cli.ConfigPath))
        {
            WriteUsage();
            return 1;
        }

        var configPath = Path.GetFullPath(cli.ConfigPath);
        var config = await ReadJsonAsync<BootstrapConfig>(configPath);
        var payloadRoot = ResolvePayloadRoot(cli, configPath);
        var result = await SyncPackageObjectsIntoConfigAsync(cli, config, configPath, payloadRoot);
        return result.HasWarnings ? 1 : 0;
    }

    private static async Task<int> RunPackageObjectSyncForActionAsync(
        CliOptions cli,
        BootstrapConfig config,
        string configPath,
        string payloadRoot)
    {
        var result = await SyncPackageObjectsIntoConfigAsync(cli, config, configPath, payloadRoot);
        if (result.HasWarnings)
        {
            Console.WriteLine("> Package object sync had warnings. The main installation action was not started.");
            return 1;
        }

        Console.WriteLine("> Package object sync completed.");
        return 0;
    }

    private static async Task<DeveloperPackageObjectSyncResult> SyncPackageObjectsIntoConfigAsync(
        CliOptions cli,
        BootstrapConfig config,
        string configPath,
        string payloadRoot)
    {
        var logPath = Path.Join(
            Path.GetTempPath(),
            $"omp-installer-sync-{DateTime.UtcNow:yyyyMMddHHmmss}.log");

        void WriteProgress(string message)
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}";
            File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
            Console.WriteLine(message);
        }

        WriteProgress($"Sync log: {logPath}");
        var previousSynchronizationContext = SynchronizationContext.Current;
        using var form = new InstallerForm(
            [
                new BootstrapConfigProfile(
                    string.IsNullOrWhiteSpace(config.Profile.DisplayName)
                        ? Path.GetFileNameWithoutExtension(configPath)
                        : config.Profile.DisplayName,
                    configPath,
                    ResolveProfileMachineNames(config))
            ],
            config,
            configPath,
            payloadRoot,
            cli,
            initializeUi: false);

        try
        {
            // Creating WinForms controls installs a WindowsFormsSynchronizationContext.
            // The headless sync path has no message loop, so async continuations must
            // run on the thread pool instead of being posted back to WinForms.
            SynchronizationContext.SetSynchronizationContext(null);

            WriteProgress("> Sync installer package objects from source");
            var result = await form.SyncDeveloperPackageObjectsCoreAsync(WriteProgress);
            foreach (var line in result.Lines)
            {
                WriteProgress(line);
            }

            if (result.ConfigUpdated)
            {
                WriteProgress("Configuration targets were updated in memory for this sync run. Tracked host config files are not rewritten by package-object sync.");
            }

            return result;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }
    }

    private sealed class InstallerForm : Form
    {
        private readonly IReadOnlyList<BootstrapConfigProfile> _configProfiles;
        private BootstrapConfig _config;
        private readonly string _configPath;
        private string _payloadRoot;
        private readonly CliOptions _cli;
        private readonly string _payloadZipPath;
        private readonly Label _profileNameLabel = new()
        {
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
        };
        private readonly Label _configPathLabel = new()
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        };
        private readonly Label _payloadRootLabel = new()
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        };
        private readonly TextBox _logBox = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            MinimumSize = new Size(0, 120)
        };
        private readonly Label _statusLabel = new()
        {
            AutoSize = true,
            Text = "Ready."
        };
        private readonly ProgressBar _progressBar = new()
        {
            Dock = DockStyle.Fill,
            Height = 18,
            Style = ProgressBarStyle.Continuous
        };
        private readonly Button _installButton = new() { Text = "Install or update", AutoSize = true };
        private readonly Button _upgradeCompleteButton = new() { Text = "Upgrade / complete", AutoSize = true };
        private readonly Button _checkSourceButton = new() { Text = "Check source objects", AutoSize = true };
        private readonly Button _syncPackageObjectsButton = new() { Text = "Sync package objects", AutoSize = true };
        private readonly Button _refreshObjectArchiveButton = new() { Text = "Refresh object archive", AutoSize = true };
        private readonly Button _syncAllProfilePackageObjectsButton = new() { Text = "Prepare all host profiles", AutoSize = true };
        private readonly Button _importUniversalPackageButton = new() { Text = "Import universal package", AutoSize = true };
        private readonly Button _prunePackageArchiveButton = new() { Text = "Keep latest package objects", AutoSize = true };
        private readonly Button _createUniversalPackageButton = new() { Text = "Create universal package", AutoSize = true };
        private readonly Button _createUpdatedInstallerPackageButton = new() { Text = "Create updated installer package", AutoSize = true };
        private readonly Button _uninstallRuntimeButton = new() { Text = "Uninstall runtime", AutoSize = true };
        private readonly Button _cleanUninstallButton = new() { Text = "Clean uninstall", AutoSize = true };
        private readonly Button _fullUninstallButton = new() { Text = "Full uninstall", AutoSize = true, ForeColor = Color.DarkRed };
        private readonly Button _exitButton = new() { Text = "Exit", AutoSize = true };
        private readonly Button _reloadConfigButton = new() { Text = "Reload config", AutoSize = true };
        private readonly Label _installationStatusLabel = new()
        {
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
        };
        private readonly Label _primaryActionDescriptionLabel = new()
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0)
        };
        private readonly Button _primaryActionButton = new()
        {
            AutoSize = true,
            MinimumSize = new Size(260, 36)
        };
        private readonly CheckBox _refreshPackageBeforePrimaryAction = new()
        {
            AutoSize = true,
            Checked = true,
            Text = "Refresh installer package from source first"
        };
        private readonly Label _developerSourceStatusLabel = new()
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(850, 0)
        };
        private readonly CheckBox _showAdvancedActions = new()
        {
            AutoSize = true,
            Text = "Show other functions"
        };
        private readonly Panel _advancedActionsHost = new()
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Visible = false
        };
        private readonly CheckBox _runSql = new() { Text = "Run SQL bootstrap", Enabled = false };
        private readonly CheckBox _installHostAgent = new() { Text = "Install/update HostAgent", Enabled = false };
        private readonly CheckBox _deployWebApps = new() { Text = "Let HostAgent deploy web apps", Enabled = false };
        private readonly CheckBox _ensureIisSite = new() { Text = "Create/update IIS site and app pools", Enabled = false };
        private readonly CheckBox _includeExampleApps = new() { Text = "Install example apps and sample data", Enabled = false };
        private readonly Dictionary<string, TextBox> _fields = new(StringComparer.OrdinalIgnoreCase);
        private TabControl? _advancedActionTabs;
        private TabPage? _packageToolsTab;
        private bool _hasExistingInstallation;
        private bool _hasDeveloperSource;

        public InstallerForm(
            IReadOnlyList<BootstrapConfigProfile> configProfiles,
            BootstrapConfig config,
            string configPath,
            string payloadRoot,
            CliOptions cli,
            bool initializeUi = true)
        {
            _configProfiles = configProfiles;
            _config = config;
            _configPath = configPath;
            _payloadRoot = payloadRoot;
            _cli = cli;
            _payloadZipPath = cli.PayloadZipPath;
            ExitCode = 2;

            if (!initializeUi)
            {
                return;
            }

            Text = "OpenModulePlatform Installer";
            AutoScroll = true;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(960, 740);
            Size = new Size(1080, 820);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 7,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(CreateHeader(), 0, 0);
            root.Controls.Add(CreateSettingsTabs(), 0, 1);
            root.Controls.Add(CreateActionPanel(), 0, 2);
            root.Controls.Add(CreateStatusPanel(), 0, 3);

            _logBox.Font = new Font(FontFamily.GenericMonospace, 9);
            _logBox.Margin = new Padding(0, 10, 0, 6);
            root.Controls.Add(_logBox, 0, 4);

            var warning = new Label
            {
                AutoSize = true,
                ForeColor = IsWindowsAdministrator() ? SystemColors.ControlText : Color.DarkRed,
                Text = IsWindowsAdministrator()
                    ? "Review the settings and choose an action."
                    : "Run this installer as Administrator before installing Windows services or IIS settings."
            };
            root.Controls.Add(warning, 0, 5);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                AutoSize = true
            };
            buttons.Controls.Add(_exitButton);
            root.Controls.Add(buttons, 0, 6);

            LoadConfigProfiles();
            LoadValues();
            _primaryActionButton.Click += async (_, _) => await RunPrimaryActionAsync();
            _installButton.Click += async (_, _) => await InstallAsync();
            _upgradeCompleteButton.Click += async (_, _) => await UpgradeOrCompleteAsync();
            _checkSourceButton.Click += async (_, _) => await CheckDeveloperSourceAsync();
            _syncPackageObjectsButton.Click += async (_, _) => await SyncDeveloperPackageObjectsAsync();
            _refreshObjectArchiveButton.Click += async (_, _) => await SyncDeveloperPackageObjectsAsync();
            _syncAllProfilePackageObjectsButton.Click += async (_, _) => await SyncAllProfilePackageObjectsAsync();
            _importUniversalPackageButton.Click += async (_, _) => await ImportUniversalPackageIntoArchiveAsync();
            _prunePackageArchiveButton.Click += async (_, _) => await PrunePackageArchiveAsync();
            _createUniversalPackageButton.Click += async (_, _) => await CreateUniversalPackageAsync();
            _createUpdatedInstallerPackageButton.Click += async (_, _) => await CreateUpdatedInstallerPackageAsync();
            _uninstallRuntimeButton.Click += async (_, _) => await UninstallAsync(removeRuntimeFiles: false, removeDatabaseObjects: false);
            _cleanUninstallButton.Click += async (_, _) => await UninstallAsync(removeRuntimeFiles: true, removeDatabaseObjects: false);
            _fullUninstallButton.Click += async (_, _) => await UninstallAsync(removeRuntimeFiles: true, removeDatabaseObjects: true);
            _exitButton.Click += (_, _) => Close();
            _showAdvancedActions.CheckedChanged += (_, _) => _advancedActionsHost.Visible = _showAdvancedActions.Checked;
            _reloadConfigButton.Click += async (_, _) => await ReloadSelectedConfigAsync("Reloaded installation profile.");
        }

        public int ExitCode { get; private set; }

        private Control CreateHeader()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1,
                Padding = new Padding(0, 0, 0, 10)
            };
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
                Text = "OpenModulePlatform installer"
            });
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "The installer uses the configuration profile that matches this computer. Review the detected profile and run the recommended action."
            });

            var profileRow = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                Margin = new Padding(0, 8, 0, 0)
            };
            profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            profileRow.Controls.Add(new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Text = "Matched profile"
            }, 0, 0);
            profileRow.Controls.Add(_profileNameLabel, 1, 0);
            profileRow.Controls.Add(_reloadConfigButton, 2, 0);
            panel.Controls.Add(profileRow);

            panel.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Text = $"Local computer: {Environment.MachineName}"
            });
            panel.Controls.Add(_configPathLabel);
            panel.Controls.Add(_payloadRootLabel);
            return panel;
        }

        private void LoadConfigProfiles()
        {
            UpdateProfileLabels();
        }

        private async Task ReloadSelectedConfigAsync(string statusText)
        {
            try
            {
                await ReloadSelectedConfigCoreAsync();
                _logBox.Clear();
                SetReadyStatus(statusText);
            }
            catch (JsonException ex)
            {
                // Reload is a user-triggered operation; surface the exact configuration error and leave the current profile active.
                MessageBox.Show(
                    ex.Message,
                    "OpenModulePlatform installer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (SystemException ex)
            {
                // Reload is a user-triggered operation; surface the exact configuration error and leave the current profile active.
                MessageBox.Show(
                    ex.Message,
                    "OpenModulePlatform installer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private async Task ReloadSelectedConfigCoreAsync()
        {
            _config = await ReadJsonAsync<BootstrapConfig>(_configPath);
            _payloadRoot = ResolvePayloadRoot(_cli, _configPath);
            LoadValues();
            UpdateProfileLabels();
        }

        private void UpdateProfileLabels()
        {
            var profile = _configProfiles.FirstOrDefault(item =>
                item.ConfigPath.Equals(_configPath, StringComparison.OrdinalIgnoreCase));
            _profileNameLabel.Text = profile?.DisplayName ?? Path.GetFileNameWithoutExtension(_configPath);
            _configPathLabel.Text = $"Config: {_configPath}";
            _payloadRootLabel.Text = $"Payload: {_payloadRoot}";
        }

        private Control CreateActionPanel()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1,
                Padding = new Padding(0, 4, 0, 0)
            };

            panel.Controls.Add(_installationStatusLabel);
            panel.Controls.Add(CreatePrimaryActionPanel());

            _showAdvancedActions.Margin = new Padding(0, 8, 0, 4);
            panel.Controls.Add(_showAdvancedActions);
            _advancedActionsHost.Controls.Add(CreateAdvancedActionTabs());
            panel.Controls.Add(_advancedActionsHost);

            return panel;
        }

        private Control CreatePrimaryActionPanel()
        {
            var group = new GroupBox
            {
                Text = "Recommended action",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(12),
                Margin = new Padding(0, 8, 0, 0)
            };
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _primaryActionButton.Width = 260;
            _primaryActionButton.Margin = new Padding(0, 6, 12, 6);
            panel.Controls.Add(_primaryActionButton, 0, 0);
            panel.Controls.Add(_primaryActionDescriptionLabel, 1, 0);

            _refreshPackageBeforePrimaryAction.Margin = new Padding(0, 4, 12, 4);
            panel.Controls.Add(_refreshPackageBeforePrimaryAction, 0, 1);
            panel.Controls.Add(_developerSourceStatusLabel, 1, 1);

            _refreshObjectArchiveButton.Width = 260;
            _refreshObjectArchiveButton.Margin = new Padding(0, 4, 12, 4);
            panel.Controls.Add(_refreshObjectArchiveButton, 0, 2);
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(850, 0),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 7, 0, 4),
                Text = "Updates the local installer object archive from configured source repositories without starting an install."
            }, 1, 2);

            group.Controls.Add(panel);
            return group;
        }

        private Control CreateAdvancedActionTabs()
        {
            var tabs = new TabControl
            {
                Dock = DockStyle.Top,
                Height = 230
            };
            _advancedActionTabs = tabs;

            tabs.TabPages.Add(CreateActionTab(
                "Install / update",
                "Advanced actions for full bootstrap passes or package catch-up runs.",
                [
                    (_installButton, "Runs the full bootstrap again with the matched profile. Use this for deliberate reconfiguration or repair; existing artifact folders and HostAgent service configuration may be updated."),
                    (_upgradeCompleteButton, "Adds newer or missing module definitions and missing artifacts from this package. Existing artifact folders and an existing HostAgent service are left unchanged.")
                ]));

            _packageToolsTab = CreateActionTab(
                "Package tools",
                "Package helpers for refreshing the installer object archive and creating universal package zips.",
                [
                    (_checkSourceButton, "Compares package objects and installed database state with the configured source repository manifests."),
                    (_syncPackageObjectsButton, "Copies newer or missing module definitions and already-built artifact packages into this installer package. Missing .NET artifacts can be built selectively."),
                    (_syncAllProfilePackageObjectsButton, "Materializes host-specific host configs, config overlays, widgets, and widget data for every discovered host profile. Optional repository hooks run when source roots are available, but profile source links are not required."),
                    (_importUniversalPackageButton, "Imports a universal module package zip into this installer's object archive without touching the installed OMP runtime."),
                    (_prunePackageArchiveButton, "Deletes older module definitions and artifact package zips from this installer package while keeping the latest version for each module/app slot. Files referenced by the current profile are kept."),
                    (_createUniversalPackageButton, "Creates a universal module package zip from this installer's object archive. You can choose global objects and host-specific overlays for any available host profile."),
                    (_createUpdatedInstallerPackageButton, "Starts a separate refresh process that creates a fresh installer package from source and restarts the updated installer.")
                ]);
            tabs.TabPages.Add(_packageToolsTab);

            tabs.TabPages.Add(CreateActionTab(
                "Uninstall",
                "Removal actions are intentionally separate. Read each description carefully before running them.",
                [
                    (_uninstallRuntimeButton, "Stops and removes HostAgent/runtime Windows services plus the configured IIS site and app pools. Runtime files and database objects are kept."),
                    (_cleanUninstallButton, "Does the runtime uninstall and also removes configured runtime folders, ArtifactStore, web-app folders, and service folders. The database is kept."),
                    (_fullUninstallButton, "Does the clean uninstall and removes all user objects from the configured database. The database itself is never dropped.")
                ]));

            return tabs;
        }

        private static TabPage CreateActionTab(
            string title,
            string description,
            IReadOnlyList<(Button Button, string Description)> actions)
        {
            var page = new TabPage(title)
            {
                AutoScroll = true,
                Padding = new Padding(12)
            };
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1
            };
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(880, 0),
                ForeColor = SystemColors.GrayText,
                Text = description
            });

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Margin = new Padding(0, 8, 0, 0)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            foreach (var (button, actionDescription) in actions)
            {
                AddActionRow(grid, button, actionDescription);
            }

            panel.Controls.Add(grid);
            page.Controls.Add(panel);
            return page;
        }

        private Control CreateStatusPanel()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                Margin = new Padding(0, 10, 0, 0)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));

            _statusLabel.Anchor = AnchorStyles.Left;
            _progressBar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            panel.Controls.Add(_statusLabel, 0, 0);
            panel.Controls.Add(_progressBar, 1, 0);
            return panel;
        }

        private static void AddActionRow(TableLayoutPanel grid, Button button, string description)
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            button.Width = 240;
            button.Margin = new Padding(0, 4, 12, 4);
            grid.Controls.Add(button, 0, row);
            grid.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(780, 0),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 7, 0, 4),
                Text = description
            }, 1, row);
        }

        private Control CreateSettingsTabs()
        {
            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(CreateTab("SQL", [
                ("sql.server", "SQL Server"),
                ("sql.database", "Database"),
                ("sql.bootstrapPortalAdminPrincipal", "Bootstrap admin principal")
            ]));
            tabs.TabPages.Add(CreateTab("Paths", [
                ("artifactStoreRoot", "ArtifactStore root"),
                ("hostAgent.installPath", "HostAgent install path"),
                ("hostAgent.localArtifactCacheRoot", "Local artifact cache"),
                ("hostAgent.webAppsRoot", "Web apps root"),
                ("hostAgent.portalPhysicalPath", "Portal physical path"),
                ("hostAgent.servicesRoot", "Services root")
            ]));
            tabs.TabPages.Add(CreateTab("HostAgent", [
                ("hostAgent.serviceName", "Service name"),
                ("hostAgent.serviceAccountName", "Service account"),
                ("hostAgent.serviceAccountPassword", "Service password"),
                ("hostAgent.serviceAppUserName", "Default service-app account"),
                ("hostAgent.serviceAppPassword", "Default service-app password"),
                ("hostAgent.hostKey", "Host key"),
                ("hostAgent.hostName", "Host name")
            ]));
            tabs.TabPages.Add(CreateTab("IIS", [
                ("hostAgent.iisSiteName", "IIS site name"),
                ("hostAgent.iisBindingProtocol", "Binding protocol"),
                ("hostAgent.iisBindingPort", "Binding port"),
                ("hostAgent.iisBindingHostHeader", "Binding host header"),
                ("hostAgent.iisAppPoolNamePrefix", "App pool name prefix"),
                ("hostAgent.iisAppPoolUserName", "App pool account"),
                ("hostAgent.iisAppPoolPassword", "App pool password")
            ]));
            tabs.TabPages.Add(CreateTab("Developer", [
                ("developerSource.sourceRoot", "Source repository roots (; separated)"),
                ("developerSource.packageConfigPath", "Package config path"),
                ("developerSource.packageOutputRoot", "Temporary package output root")
            ]));

            var optionsPage = new TabPage("Options")
            {
                AutoScroll = true,
                Padding = new Padding(12)
            };
            var options = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoScroll = true,
                Padding = new Padding(0, 4, 0, 0)
            };
            options.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(820, 0),
                Text = "Choose what the bootstrapper should prepare now. HostAgent can deploy app updates later from artifacts."
            });
            options.Controls.Add(_runSql);
            options.Controls.Add(_installHostAgent);
            options.Controls.Add(_deployWebApps);
            options.Controls.Add(_ensureIisSite);
            options.Controls.Add(_includeExampleApps);
            optionsPage.Controls.Add(options);
            tabs.TabPages.Add(optionsPage);

            return tabs;
        }

        private TabPage CreateTab(string title, IReadOnlyList<(string Key, string Label)> fields)
        {
            var page = new TabPage(title)
            {
                AutoScroll = true,
                Padding = new Padding(12)
            };
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            foreach (var (key, label) in fields)
            {
                var row = grid.RowCount++;
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.Controls.Add(new Label
                {
                    Text = label,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(0, 8, 12, 4)
                }, 0, row);

                var box = new TextBox
                {
                    Anchor = AnchorStyles.Left | AnchorStyles.Right,
                    Width = 620,
                    ReadOnly = true,
                    Margin = new Padding(0, 4, 0, 4)
                };
                if (key.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    box.UseSystemPasswordChar = true;
                }

                _fields[key] = box;
                grid.Controls.Add(box, 1, row);
            }

            page.Controls.Add(grid);
            return page;
        }

        private void LoadValues()
        {
            Set("sql.server", _config.Sql.Server);
            Set("sql.database", _config.Sql.Database);
            Set("sql.bootstrapPortalAdminPrincipal", _config.Sql.BootstrapPortalAdminPrincipal);
            Set("artifactStoreRoot", _config.ArtifactStoreRoot);
            Set("hostAgent.serviceName", _config.HostAgent.ServiceName);
            Set("hostAgent.serviceAccountName", _config.HostAgent.ServiceAccountName);
            Set("hostAgent.serviceAccountPassword", _config.HostAgent.ServiceAccountPassword);
            Set("hostAgent.serviceAppUserName", _config.HostAgent.ServiceAppUserName);
            Set("hostAgent.serviceAppPassword", _config.HostAgent.ServiceAppPassword);
            Set("hostAgent.installPath", _config.HostAgent.InstallPath);
            Set("hostAgent.localArtifactCacheRoot", _config.HostAgent.LocalArtifactCacheRoot);
            Set("hostAgent.hostKey", _config.HostAgent.HostKey);
            Set("hostAgent.hostName", _config.HostAgent.HostName);
            Set("hostAgent.iisSiteName", _config.HostAgent.IisSiteName);
            Set("hostAgent.iisBindingProtocol", _config.HostAgent.IisBindingProtocol);
            Set("hostAgent.iisBindingPort", _config.HostAgent.IisBindingPort.ToString());
            Set("hostAgent.iisBindingHostHeader", _config.HostAgent.IisBindingHostHeader);
            Set("hostAgent.webAppsRoot", _config.HostAgent.WebAppsRoot);
            Set("hostAgent.portalPhysicalPath", _config.HostAgent.PortalPhysicalPath);
            Set("hostAgent.iisAppPoolNamePrefix", _config.HostAgent.IisAppPoolNamePrefix);
            Set("hostAgent.iisAppPoolUserName", _config.HostAgent.IisAppPoolUserName);
            Set("hostAgent.iisAppPoolPassword", _config.HostAgent.IisAppPoolPassword);
            Set("hostAgent.servicesRoot", _config.HostAgent.ServicesRoot);
            Set(
                "developerSource.sourceRoot",
                string.IsNullOrWhiteSpace(_config.DeveloperSource.SourceRoot)
                    ? ResolveDeveloperSourceRoot(throwIfMissing: false) ?? string.Empty
                    : _config.DeveloperSource.SourceRoot);
            Set("developerSource.packageConfigPath", _config.DeveloperSource.PackageConfigPath);
            Set("developerSource.packageOutputRoot", _config.DeveloperSource.PackageOutputRoot);

            _runSql.Checked = _config.Sql.Enabled;
            _installHostAgent.Checked = _config.HostAgent.Enabled;
            _deployWebApps.Checked = _config.HostAgent.DeployWebApps;
            _ensureIisSite.Checked = _config.HostAgent.EnsureIisSite;
            _includeExampleApps.Checked = _config.IncludeExampleApps;

            RefreshInstallerState();
        }

        private void ApplyValues()
        {
            _config.Sql.Server = Get("sql.server");
            _config.Sql.Database = Get("sql.database");
            _config.Sql.BootstrapPortalAdminPrincipal = Get("sql.bootstrapPortalAdminPrincipal");
            _config.ArtifactStoreRoot = Get("artifactStoreRoot");
            _config.HostAgent.ServiceName = Get("hostAgent.serviceName");
            _config.HostAgent.ServiceAccountName = Get("hostAgent.serviceAccountName");
            _config.HostAgent.ServiceAccountPassword = Get("hostAgent.serviceAccountPassword");
            _config.HostAgent.ServiceAppUserName = Get("hostAgent.serviceAppUserName");
            _config.HostAgent.ServiceAppPassword = Get("hostAgent.serviceAppPassword");
            _config.HostAgent.InstallPath = Get("hostAgent.installPath");
            _config.HostAgent.LocalArtifactCacheRoot = Get("hostAgent.localArtifactCacheRoot");
            _config.HostAgent.HostKey = Get("hostAgent.hostKey");
            _config.HostAgent.HostName = Get("hostAgent.hostName");
            _config.HostAgent.IisSiteName = Get("hostAgent.iisSiteName");
            _config.HostAgent.IisBindingProtocol = Get("hostAgent.iisBindingProtocol");
            _config.HostAgent.IisBindingPort = int.TryParse(Get("hostAgent.iisBindingPort"), out var port) ? port : 80;
            _config.HostAgent.IisBindingHostHeader = Get("hostAgent.iisBindingHostHeader");
            _config.HostAgent.WebAppsRoot = Get("hostAgent.webAppsRoot");
            _config.HostAgent.PortalPhysicalPath = Get("hostAgent.portalPhysicalPath");
            _config.HostAgent.IisAppPoolNamePrefix = Get("hostAgent.iisAppPoolNamePrefix");
            _config.HostAgent.IisAppPoolUserName = Get("hostAgent.iisAppPoolUserName");
            _config.HostAgent.IisAppPoolPassword = Get("hostAgent.iisAppPoolPassword");
            _config.HostAgent.ServicesRoot = Get("hostAgent.servicesRoot");
            _config.DeveloperSource.SourceRoot = Get("developerSource.sourceRoot");
            _config.DeveloperSource.PackageConfigPath = Get("developerSource.packageConfigPath");
            _config.DeveloperSource.PackageOutputRoot = Get("developerSource.packageOutputRoot");

            _config.Sql.Enabled = _runSql.Checked;
            _config.HostAgent.Enabled = _installHostAgent.Checked;
            _config.HostAgent.DeployWebApps = _deployWebApps.Checked;
            _config.HostAgent.EnsureIisSite = _ensureIisSite.Checked;
            _config.IncludeExampleApps = _includeExampleApps.Checked;
        }

        private void RefreshInstallerState()
        {
            _hasExistingInstallation = DetectExistingInstallation(out var installationDetails);
            _hasDeveloperSource = HasDeveloperSourceAvailable(out var developerSourceDetails);

            if (_hasExistingInstallation)
            {
                _installationStatusLabel.ForeColor = Color.DarkGreen;
                _installationStatusLabel.Text = "Current status: an existing OpenModulePlatform installation was detected.";
                _primaryActionButton.Text = "Upgrade existing installation";
                _primaryActionDescriptionLabel.Text =
                    "Applies newer or missing module packages, artifact packages and host-role topology data from this installer package. HostAgent is installed only if the configured service is missing.";
            }
            else
            {
                _installationStatusLabel.ForeColor = Color.DarkOrange;
                _installationStatusLabel.Text = "Current status: no existing OpenModulePlatform installation was detected.";
                _primaryActionButton.Text = "Install OpenModulePlatform";
                _primaryActionDescriptionLabel.Text =
                    "Runs the initial SQL bootstrap, prepares ArtifactStore, standard host roles, package-library objects, and HostAgent for this computer.";
            }

            if (!string.IsNullOrWhiteSpace(installationDetails))
            {
                _primaryActionDescriptionLabel.Text += Environment.NewLine + installationDetails;
            }

            _developerSourceStatusLabel.Text = _hasDeveloperSource
                ? "Developer source repositories are available. The checked option refreshes the installer package before the main action."
                : "Developer source repositories are not available for this profile. Package refresh options are disabled.";
            _refreshPackageBeforePrimaryAction.Visible = _hasDeveloperSource;
            _refreshPackageBeforePrimaryAction.Enabled = _hasDeveloperSource;
            _refreshPackageBeforePrimaryAction.Checked = _hasDeveloperSource;
            _refreshObjectArchiveButton.Visible = _hasDeveloperSource;
            _refreshObjectArchiveButton.Enabled = _hasDeveloperSource;
            _developerSourceStatusLabel.ForeColor = _hasDeveloperSource ? Color.DarkGreen : SystemColors.GrayText;
            _developerSourceStatusLabel.Text += string.IsNullOrWhiteSpace(developerSourceDetails)
                ? string.Empty
                : Environment.NewLine + developerSourceDetails;

            UpdatePackageToolsVisibility();
        }

        private void UpdatePackageToolsVisibility()
        {
            if (_advancedActionTabs is null || _packageToolsTab is null)
            {
                return;
            }

            var containsPackageTools = _advancedActionTabs.TabPages.Contains(_packageToolsTab);
            if (!containsPackageTools)
            {
                var insertIndex = Math.Min(1, _advancedActionTabs.TabPages.Count);
                _advancedActionTabs.TabPages.Insert(insertIndex, _packageToolsTab);
            }

            _checkSourceButton.Enabled = _hasDeveloperSource;
            _syncPackageObjectsButton.Enabled = _hasDeveloperSource;
            _syncAllProfilePackageObjectsButton.Enabled = _configProfiles.Count > 0;
            _importUniversalPackageButton.Enabled = true;
            _prunePackageArchiveButton.Enabled = HasPackageArchivePruneRoots();
            _createUpdatedInstallerPackageButton.Enabled = _hasDeveloperSource;
            _createUniversalPackageButton.Enabled = HasUniversalPackageCandidates();
        }

        private bool DetectExistingInstallation(out string details)
        {
            var findings = new List<string>();

            if (OperatingSystem.IsWindows()
                && !string.IsNullOrWhiteSpace(_config.HostAgent.ServiceName)
                && ServiceExists(_config.HostAgent.ServiceName))
            {
                findings.Add($"HostAgent service '{_config.HostAgent.ServiceName}' exists.");
            }

            if (!string.IsNullOrWhiteSpace(_config.HostAgent.PortalPhysicalPath))
            {
                var portalDll = Path.Join(
                    Path.GetFullPath(_config.HostAgent.PortalPhysicalPath),
                    "OpenModulePlatform.Portal.dll");
                if (File.Exists(portalDll))
                {
                    findings.Add($"Portal files exist at '{_config.HostAgent.PortalPhysicalPath}'.");
                }
            }

            if (OperatingSystem.IsWindows()
                && !string.IsNullOrWhiteSpace(_config.HostAgent.IisSiteName)
                && IisSiteExists(_config.HostAgent.IisSiteName))
            {
                findings.Add($"IIS site '{_config.HostAgent.IisSiteName}' exists.");
            }

            details = findings.Count == 0
                ? string.Empty
                : "Detected: " + string.Join(" ", findings);
            return findings.Count > 0;
        }

        private static bool IisSiteExists(string siteName)
        {
            var appCmdPath = TryGetAppCmdPath();
            return !string.IsNullOrWhiteSpace(appCmdPath)
                && RunProcess(appCmdPath, ["list", "site", $"/name:{siteName.Trim()}"], throwOnFailure: false).ExitCode == 0;
        }

        private bool HasDeveloperSourceAvailable(out string details)
        {
            try
            {
                var roots = ResolveDeveloperSourceRoots(throwIfMissing: false);
                details = roots.Count == 0
                    ? string.Empty
                    : "Source roots: " + string.Join("; ", roots);
                return roots.Count > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                details = ex.Message;
                return false;
            }
        }

        private async Task RunPrimaryActionAsync()
        {
            ApplyValues();
            RefreshInstallerState();

            var actionName = _hasExistingInstallation
                ? "upgrade the existing OpenModulePlatform installation"
                : "install OpenModulePlatform";
            var refreshPackage = _hasDeveloperSource && _refreshPackageBeforePrimaryAction.Checked;
            var packageWarnings = refreshPackage
                ? []
                : GetPackageReadinessWarnings();
            if (!ConfirmPrimaryAction(actionName, refreshPackage, packageWarnings))
            {
                return;
            }

            await RunGuiOperationAsync(
                _hasExistingInstallation
                    ? "Upgrading existing OpenModulePlatform installation..."
                    : "Installing OpenModulePlatform...",
                _hasExistingInstallation ? "Upgrade completed." : "Installation completed.",
                _hasExistingInstallation ? "Upgrade did not complete." : "Installation did not complete.",
                _hasExistingInstallation ? "Upgrade failed." : "Installation failed.",
                async () =>
                {
                    if (refreshPackage)
                    {
                        var refreshExitCode = await RefreshInstallerPackageObjectsForPrimaryActionAsync();
                        if (refreshExitCode != 0)
                        {
                            return refreshExitCode;
                        }
                    }

                    return _hasExistingInstallation
                        ? await RunUpgradeOrCompleteAsync(
                            _config,
                            _configPath,
                            _payloadRoot,
                            _payloadZipPath)
                        : await RunBootstrapAsync(
                            _config,
                            _configPath,
                            _payloadRoot,
                            _payloadZipPath,
                            yes: true);
                });

            RefreshInstallerState();
        }

        private async Task<int> RefreshInstallerPackageObjectsForPrimaryActionAsync()
        {
            Console.WriteLine("> Refresh installer package objects from source");
            var result = await SyncDeveloperPackageObjectsCoreAsync();
            foreach (var line in result.Lines)
            {
                Console.WriteLine(line);
            }

            if (result.ConfigUpdated)
            {
                Console.WriteLine("> Configuration targets were updated in memory for this run. Package-object sync does not rewrite tracked host config files.");
            }

            if (result.HasWarnings)
            {
                Console.WriteLine("> Package object refresh had warnings. The main installation action was not started.");
                return 1;
            }

            Console.WriteLine("> Package object refresh completed.");
            return 0;
        }

        private bool ConfirmPrimaryAction(
            string actionName,
            bool refreshPackage,
            IReadOnlyList<string> packageWarnings)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"This will {actionName} using the matched profile.");
            builder.AppendLine();
            builder.AppendLine($"Profile: {(_configProfiles.FirstOrDefault(profile => profile.ConfigPath.Equals(_configPath, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? Path.GetFileName(_configPath))}");
            builder.AppendLine($"Computer: {Environment.MachineName}");
            builder.AppendLine($"Config: {_configPath}");
            builder.AppendLine($"SQL target: {_config.Sql.Server}/{_config.Sql.Database}");
            builder.AppendLine($"ArtifactStore: {ValueOrPlaceholder(_config.ArtifactStoreRoot)}");
            builder.AppendLine($"HostAgent service: {ValueOrPlaceholder(_config.HostAgent.ServiceName)}");
            builder.AppendLine($"HostAgent install path: {ValueOrPlaceholder(_config.HostAgent.InstallPath)}");
            builder.AppendLine($"IIS site: {ValueOrPlaceholder(_config.HostAgent.IisSiteName)}");
            builder.AppendLine($"Portal path: {ValueOrPlaceholder(_config.HostAgent.PortalPhysicalPath)}");
            builder.AppendLine();
            builder.AppendLine(refreshPackage
                ? "Before the main action, the installer package will be refreshed from the configured source repositories. Warnings stop the main action so package problems can be reviewed first."
                : "The installer package will be used as-is.");
            if (packageWarnings.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Package readiness warning:");
                foreach (var warning in packageWarnings.Take(12))
                {
                    builder.AppendLine("  - " + warning);
                }

                if (packageWarnings.Count > 12)
                {
                    builder.AppendLine($"  - ...and {packageWarnings.Count - 12} more missing package file(s).");
                }

                builder.AppendLine();
                builder.AppendLine("Enable package refresh from source on developer machines, or use a full package that already contains the generated files.");
            }
            builder.AppendLine();
            builder.Append("Continue?");

            return MessageBox.Show(
                builder.ToString(),
                "Confirm OpenModulePlatform action",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private IReadOnlyList<string> GetPackageReadinessWarnings()
        {
            var warnings = new List<string>();
            void AddMissingPackageFile(string label, string relativePath)
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    return;
                }

                try
                {
                    var path = ResolvePackagePath(relativePath);
                    if (!File.Exists(path))
                    {
                        warnings.Add($"{label} is missing: {relativePath}");
                    }
                }
                catch (InvalidOperationException ex)
                {
                    warnings.Add($"{label} cannot be resolved: {ex.Message}");
                }
            }

            if (_config.Sql.Enabled)
            {
                foreach (var script in _config.Sql.Scripts.Where(static script => script.Enabled))
                {
                    AddMissingPackageFile("SQL script", script.Path);
                }
            }

            foreach (var artifact in _config.Artifacts)
            {
                AddMissingPackageFile("Artifact package", artifact.Source);
            }

            if (_config.HostAgent.Enabled)
            {
                AddMissingPackageFile("HostAgent package", _config.HostAgent.PackagePath);
            }

            return warnings;
        }

        private async Task InstallAsync()
        {
            ApplyValues();
            await RunGuiOperationAsync(
                "Installing or updating OpenModulePlatform...",
                "Installation completed.",
                "Installation did not complete.",
                "Installation failed.",
                () => RunBootstrapAsync(
                    _config,
                    _configPath,
                    _payloadRoot,
                    _payloadZipPath,
                    yes: true));
        }

        private async Task UpgradeOrCompleteAsync()
        {
            ApplyValues();
            var confirmation = MessageBox.Show(
                "This adds newer or missing module definitions and missing artifact folders from the selected package. It does not run the full SQL bootstrap and it does not reinstall artifact folders or HostAgent when they already exist. Continue?",
                "Upgrade / complete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            await RunGuiOperationAsync(
                "Upgrading or completing OpenModulePlatform package objects...",
                "Upgrade/complete completed.",
                "Upgrade/complete did not complete.",
                "Upgrade/complete failed.",
                () => RunUpgradeOrCompleteAsync(
                    _config,
                    _configPath,
                    _payloadRoot,
                    _payloadZipPath));
        }

        private async Task CheckDeveloperSourceAsync()
        {
            ApplyValues();
            await RunGuiOperationAsync(
                "Checking developer source objects...",
                "Developer source check completed. Review the log for details.",
                "Developer source check did not complete.",
                "Developer source check failed.",
                async () =>
                {
                    var result = await CheckDeveloperSourceStatusAsync();
                    foreach (var line in result.Lines)
                    {
                        Console.WriteLine(line);
                    }

                    return 0;
                });
        }

        private async Task SyncDeveloperPackageObjectsAsync()
        {
            ApplyValues();
            var confirmation = MessageBox.Show(
                "This updates this installer package with newer or missing module definition JSON files and artifact package zips. Existing package zips are reused first. If a .NET artifact package is missing, only that component project is published and packaged. Non-.NET packages still need to exist in ArtifactArchive or the source artifacts folder. Continue?",
                "Sync package objects",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            await RunGuiOperationAsync(
                "Syncing developer package objects...",
                "Package object sync completed.",
                "Package object sync completed with warnings. Review the log for details.",
                "Package object sync failed.",
                async () =>
                {
                    var result = await SyncDeveloperPackageObjectsCoreAsync();
                    foreach (var line in result.Lines)
                    {
                        Console.WriteLine(line);
                    }

                    if (result.ConfigUpdated)
                    {
                        Console.WriteLine("> Configuration targets were updated in memory for this run. Package-object sync does not rewrite tracked host config files.");
                    }

                    return result.HasWarnings ? 1 : 0;
                });
        }

        private async Task SyncAllProfilePackageObjectsAsync()
        {
            ApplyValues();
            var confirmation = MessageBox.Show(
                "This prepares host-specific package objects for every discovered host profile in this installer package. Profiles do not need source repository links. If local source roots are available, only optional host-profile object hooks are run; shared package objects are not refreshed by this action. Continue?",
                "Prepare all host profiles",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            await RunGuiOperationAsync(
                "Preparing package objects for all host profiles...",
                "All host profiles prepared.",
                "Host profile preparation completed with warnings. Review the log for details.",
                "Host profile preparation failed.",
                async () =>
                {
                    var result = await SyncAllProfilePackageObjectsCoreAsync(Console.WriteLine);
                    foreach (var line in result.Lines)
                    {
                        Console.WriteLine(line);
                    }

                    return result.HasWarnings ? 1 : 0;
                });
        }

        private async Task ImportUniversalPackageIntoArchiveAsync()
        {
            ApplyValues();
            using var dialog = new OpenFileDialog
            {
                AddExtension = true,
                CheckFileExists = true,
                DefaultExt = "zip",
                Filter = "Universal package (*.zip)|*.zip|All files (*.*)|*.*",
                Title = "Import universal package into installer object archive"
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var packagePath = dialog.FileName;
            await RunGuiOperationAsync(
                "Importing universal module package into installer object archive...",
                "Universal module package imported into installer object archive.",
                "Universal module package import completed with warnings. Review the log for details.",
                "Universal module package import failed.",
                () =>
                {
                    var result = ImportUniversalPackageIntoArchiveCore(packagePath);
                    foreach (var line in result.Lines)
                    {
                        Console.WriteLine(line);
                    }

                    return Task.FromResult(result.HasWarnings ? 1 : 0);
                });
        }

        private async Task PrunePackageArchiveAsync()
        {
            ApplyValues();

            var plan = await BuildPackageArchivePrunePlanAsync();
            if (plan.DeleteCandidates.Count == 0)
            {
                var message = plan.HasWarnings
                    ? "No old package object files can be removed. Review the log for skipped files."
                    : "No old package object files were found.";
                _logBox.Clear();
                foreach (var line in plan.Lines)
                {
                    _logBox.AppendText(line + Environment.NewLine);
                }

                MessageBox.Show(
                    message,
                    "Keep latest package objects",
                    MessageBoxButtons.OK,
                    plan.HasWarnings ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                return;
            }

            var confirmation = MessageBox.Show(
                BuildPackageArchivePruneConfirmation(plan),
                "Keep latest package objects",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            await RunGuiOperationAsync(
                "Removing older package objects...",
                "Old package objects removed.",
                "Old package object cleanup completed with warnings. Review the log for details.",
                "Old package object cleanup failed.",
                () =>
                {
                    var result = PrunePackageArchiveCore(plan);
                    foreach (var line in result.Lines)
                    {
                        Console.WriteLine(line);
                    }

                    return Task.FromResult(result.HasWarnings ? 1 : 0);
                });
        }

        private async Task CreateUpdatedInstallerPackageAsync()
        {
            ApplyValues();
            var confirmation = MessageBox.Show(
                "This will close the installer, build a fresh HostAgent-first package from the configured source repository, replace this installer package when the running EXE is no longer locked, and then start the updated installer. Continue?",
                "Create updated installer package",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            try
            {
                await SaveCurrentConfigAsync();
                StartDetachedInstallerPackageRefresh();
                ExitCode = 0;
                Close();
            }
            catch (SystemException ex)
            {
                // This operation hands off to a detached process, so any launch/setup failure must be shown before the form closes.
                MessageBox.Show(
                    ex.Message,
                    "OpenModulePlatform installer",
                    MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            }
        }

        private async Task CreateUniversalPackageAsync()
        {
            ApplyValues();
            using var form = new UniversalPackageBuilderForm(_payloadRoot, _configProfiles, _configPath);
            if (form.ShowDialog(this) != DialogResult.OK || form.Request is null)
            {
                return;
            }

            var request = form.Request;
            await RunGuiOperationAsync(
                "Creating universal module package...",
                "Universal module package created.",
                "Universal module package was created with warnings. Review the log for details.",
                "Universal module package creation failed.",
                () =>
                {
                    var result = CreateUniversalPackageZip(request);
                    Console.WriteLine($"> Universal module package: {result.PackagePath}");
                    Console.WriteLine($"> Package key: {request.PackageKey}");
                    Console.WriteLine($"> Package version: {request.PackageVersion}");
                    Console.WriteLine($"> Host profile: {request.HostDisplayName}");
                    Console.WriteLine($"> Items: {result.ItemCount}");
                    foreach (var item in request.Items)
                    {
                        Console.WriteLine($"  {item.Kind,-18} {item.PackagePath}");
                    }

                    return Task.FromResult(0);
                });
        }

        private bool HasPackageArchivePruneRoots()
            => Directory.Exists(ResolvePackageModuleDefinitionsRoot(_payloadRoot))
                || Directory.Exists(ResolvePackageArtifactsRoot(_payloadRoot));

        private async Task<PackageArchivePrunePlan> BuildPackageArchivePrunePlanAsync()
        {
            var lines = new List<string>
            {
                "Package archive cleanup plan:",
                $"  Installer package: {_payloadRoot}"
            };
            var candidates = new List<PackageArchivePruneCandidate>();
            var warningCount = 0;

            warningCount += await AddModuleDefinitionPruneCandidatesAsync(candidates, lines);
            warningCount += AddArtifactPackagePruneCandidates(candidates, lines);

            var protectedPaths = GetConfiguredPackageObjectPaths();
            var olderCandidates = SelectOlderPackageArchiveCandidates(candidates);
            var protectedCandidates = olderCandidates
                .Where(candidate => protectedPaths.Contains(Path.GetFullPath(candidate.Path)))
                .OrderBy(static candidate => candidate.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var deleteCandidates = olderCandidates
                .Where(candidate => !protectedPaths.Contains(Path.GetFullPath(candidate.Path)))
                .OrderBy(static candidate => candidate.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var latestKeptCount = candidates.Count - olderCandidates.Count;

            lines.Add($"  Candidate files: {candidates.Count}");
            lines.Add($"  Latest-version files kept: {latestKeptCount}");
            lines.Add($"  Older files kept because the current profile references them: {protectedCandidates.Length}");
            lines.Add($"  Older files to delete: {deleteCandidates.Length}");
            if (warningCount > 0)
            {
                lines.Add($"  Warnings: {warningCount}");
            }

            if (protectedCandidates.Length > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Configured older files kept:");
                foreach (var candidate in protectedCandidates.Take(20))
                {
                    lines.Add($"  KEEP   {candidate.DisplayName}: {GetDisplayPath(_payloadRoot, candidate.Path)}");
                }

                if (protectedCandidates.Length > 20)
                {
                    lines.Add($"  ...and {protectedCandidates.Length - 20} more configured older file(s).");
                }
            }

            return new PackageArchivePrunePlan(
                deleteCandidates,
                protectedCandidates,
                latestKeptCount,
                warningCount,
                lines);
        }

        private async Task<int> AddModuleDefinitionPruneCandidatesAsync(
            List<PackageArchivePruneCandidate> candidates,
            List<string> lines)
        {
            var root = ResolvePackageModuleDefinitionsRoot(_payloadRoot);
            if (!Directory.Exists(root))
            {
                return 0;
            }

            var warningCount = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                         .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var definition = await ReadModuleDefinitionAsync(path);
                    candidates.Add(new PackageArchivePruneCandidate(
                        "module definition",
                        definition.ModuleKey,
                        definition.DefinitionVersion,
                        path,
                        $"{definition.ModuleKey} {definition.DefinitionVersion}"));
                }
                catch (SystemException ex)
                {
                    warningCount++;
                    lines.Add($"  WARN   module-definition skipped {GetDisplayPath(_payloadRoot, path)}: {ex.Message}");
                }
                catch (JsonException ex)
                {
                    warningCount++;
                    lines.Add($"  WARN   module-definition skipped {GetDisplayPath(_payloadRoot, path)}: {ex.Message}");
                }
            }

            return warningCount;
        }

        private int AddArtifactPackagePruneCandidates(
            List<PackageArchivePruneCandidate> candidates,
            List<string> lines)
        {
            var root = ResolvePackageArtifactsRoot(_payloadRoot);
            if (!Directory.Exists(root))
            {
                return 0;
            }

            var warningCount = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories)
                         .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
            {
                var identity = ParseArtifactPackageIdentity(path);
                if (identity is null)
                {
                    warningCount++;
                    lines.Add($"  WARN   artifact package skipped {GetDisplayPath(_payloadRoot, path)}: file name does not follow the OMP artifact package identity format.");
                    continue;
                }

                candidates.Add(new PackageArchivePruneCandidate(
                    "artifact package",
                    string.Join('\u001f', identity.ModuleKey, identity.AppKey, identity.PackageType, identity.TargetName),
                    identity.Version,
                    path,
                    $"{identity.ModuleKey}/{identity.AppKey}/{identity.PackageType}/{identity.TargetName} {identity.Version}"));
            }

            return warningCount;
        }

        private static IReadOnlyList<PackageArchivePruneCandidate> SelectOlderPackageArchiveCandidates(
            IReadOnlyList<PackageArchivePruneCandidate> candidates)
        {
            var latestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in candidates.GroupBy(static candidate => candidate.GroupKey, StringComparer.OrdinalIgnoreCase))
            {
                var latestVersion = group
                    .Select(static candidate => candidate.Version)
                    .Aggregate((current, next) => CompareVersionText(next, current) > 0 ? next : current);
                foreach (var candidate in group.Where(candidate => CompareVersionText(candidate.Version, latestVersion) == 0))
                {
                    latestPaths.Add(Path.GetFullPath(candidate.Path));
                }
            }

            return candidates
                .Where(candidate => !latestPaths.Contains(Path.GetFullPath(candidate.Path)))
                .ToArray();
        }

        private HashSet<string> GetConfiguredPackageObjectPaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddPath(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                try
                {
                    var resolved = Path.IsPathRooted(path)
                        ? Path.GetFullPath(path)
                        : ResolvePackagePath(path);
                    paths.Add(Path.GetFullPath(resolved));
                }
                catch (SystemException)
                {
                    // Invalid configured paths are reported by the install readiness checks; pruning should not fail because of them.
                }
            }

            foreach (var artifact in _config.Artifacts)
            {
                AddPath(artifact.Source);
            }

            if (_config.HostAgent.Enabled)
            {
                AddPath(_config.HostAgent.PackagePath);
            }

            return paths;
        }

        private string BuildPackageArchivePruneConfirmation(PackageArchivePrunePlan plan)
        {
            var builder = new StringBuilder();
            builder.AppendLine("This will delete older package object files from this installer package.");
            builder.AppendLine();
            builder.AppendLine("The cleanup keeps:");
            builder.AppendLine("- the latest module definition version for each module");
            builder.AppendLine("- the latest artifact package version for each module/app/package slot");
            builder.AppendLine("- older files still referenced by the current host profile");
            builder.AppendLine();
            builder.AppendLine($"Files to delete: {plan.DeleteCandidates.Count}");
            builder.AppendLine($"Latest files kept: {plan.LatestKeptCount}");
            builder.AppendLine($"Configured older files kept: {plan.ProtectedCandidates.Count}");
            if (plan.WarningCount > 0)
            {
                builder.AppendLine($"Skipped/warning files: {plan.WarningCount}");
            }

            builder.AppendLine();
            builder.AppendLine("First files to delete:");
            foreach (var candidate in plan.DeleteCandidates.Take(12))
            {
                builder.AppendLine($"- {GetDisplayPath(_payloadRoot, candidate.Path)}");
            }

            if (plan.DeleteCandidates.Count > 12)
            {
                builder.AppendLine($"- ...and {plan.DeleteCandidates.Count - 12} more file(s).");
            }

            builder.AppendLine();
            builder.Append("Continue?");
            return builder.ToString();
        }

        private PackageArchivePruneResult PrunePackageArchiveCore(PackageArchivePrunePlan plan)
        {
            var lines = new List<string>(plan.Lines)
            {
                string.Empty,
                "Deleting older package object files:"
            };
            var warningCount = plan.WarningCount;
            var deletedCount = 0;

            foreach (var candidate in plan.DeleteCandidates)
            {
                try
                {
                    File.Delete(candidate.Path);
                    deletedCount++;
                    lines.Add($"  DELETE {candidate.DisplayName}: {GetDisplayPath(_payloadRoot, candidate.Path)}");
                }
                catch (SystemException ex)
                {
                    warningCount++;
                    lines.Add($"  WARN   could not delete {GetDisplayPath(_payloadRoot, candidate.Path)}: {ex.Message}");
                }
            }

            lines.Add(string.Empty);
            lines.Add($"Summary: {deletedCount} deleted, {plan.LatestKeptCount} latest kept, {plan.ProtectedCandidates.Count} configured older kept, {warningCount} warning(s).");
            return new PackageArchivePruneResult(warningCount > 0, lines);
        }

        private void StartDetachedInstallerPackageRefresh()
        {
            var currentExecutable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not resolve the running installer executable path.");
            var runnerRoot = Path.Join(
                Path.GetTempPath(),
                "omp-installer-refresh-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runnerRoot);

            var runnerExecutable = Path.Join(runnerRoot, Path.GetFileName(currentExecutable));
            CopyInstallerRunnerFiles(currentExecutable, runnerRoot);

            var process = Process.Start(new ProcessStartInfo(runnerExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = runnerRoot,
                ArgumentList =
                {
                    "--refresh-installer-package",
                    "--config",
                    _configPath,
                    "--payload-root",
                    _payloadRoot,
                    "--parent-process-id",
                    Environment.ProcessId.ToString(),
                    "--restart-gui"
                }
            });

            if (process is null)
            {
                throw new InvalidOperationException("Could not start the installer package refresh process.");
            }
        }

        private bool HasUniversalPackageCandidates()
            => CollectUniversalPackageCandidates(
                _payloadRoot,
                hostChoice: null,
                includeGlobal: true,
                includeHostSpecific: false).Count > 0
                || _configProfiles
                    .Select(CreateUniversalPackageHostChoice)
                    .Any(hostChoice => CollectUniversalPackageCandidates(
                        _payloadRoot,
                        hostChoice,
                        includeGlobal: false,
                        includeHostSpecific: true).Count > 0);

        private static UniversalPackageBuildResult CreateUniversalPackageZip(UniversalPackageBuildRequest request)
        {
            var outputPath = Path.GetFullPath(request.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
            var items = new JsonArray();
            var usedPackagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in request.Items.OrderBy(static item => item.PackagePath, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(item.SourcePath))
                {
                    throw new FileNotFoundException("A selected universal package item no longer exists.", item.SourcePath);
                }

                if (!usedPackagePaths.Add(item.PackagePath))
                {
                    throw new InvalidOperationException($"Duplicate universal package item path '{item.PackagePath}'.");
                }

                archive.CreateEntryFromFile(
                    item.SourcePath,
                    item.PackagePath,
                    CompressionLevel.Optimal);
                items.Add(new JsonObject
                {
                    ["kind"] = item.Kind,
                    ["path"] = item.PackagePath
                });
            }

            var manifest = new JsonObject
            {
                ["formatVersion"] = 1,
                ["objectType"] = "universal-module-package",
                ["packageKey"] = request.PackageKey,
                ["packageVersion"] = request.PackageVersion,
                ["displayName"] = request.DisplayName,
                ["description"] = request.Description,
                ["targetHostProfile"] = request.HostKey,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["items"] = items
            };

            var manifestEntry = archive.CreateEntry(
                UniversalModulePackageReader.ManifestEntryName,
                CompressionLevel.Optimal);
            using var manifestStream = manifestEntry.Open();
            using var manifestWriter = new StreamWriter(manifestStream, new UTF8Encoding(false));
            manifestWriter.Write(manifest.ToJsonString(JsonOptions));
            return new UniversalPackageBuildResult(outputPath, request.Items.Count);
        }

        private UniversalPackageArchiveImportResult ImportUniversalPackageIntoArchiveCore(string packagePath)
        {
            var fullPackagePath = Path.GetFullPath(packagePath);
            var extractionRoot = Path.Join(
                Path.GetTempPath(),
                "omp-installer-universal-import-" + Guid.NewGuid().ToString("N"));
            var lines = new List<string>
            {
                "Universal package import into installer object archive:",
                $"  Package: {fullPackagePath}",
                $"  Installer archive: {_payloadRoot}"
            };
            var warnings = 0;
            var imported = 0;
            var updated = 0;
            var skipped = 0;

            try
            {
                var package = new UniversalModulePackageReader().ExtractToDirectory(fullPackagePath, extractionRoot);
                lines.Add($"  Package key: {package.PackageKey ?? "(none)"}");
                lines.Add($"  Package version: {package.PackageVersion ?? "(none)"}");
                lines.Add($"  Target host profile: {package.TargetHostProfile ?? "(none)"}");
                lines.Add($"  Items: {package.Items.Count}");
                lines.Add(string.Empty);

                var knownHostKeys = BuildHostChoices(_configProfiles)
                    .Select(static choice => choice.HostKey)
                    .Where(static hostKey => !string.IsNullOrWhiteSpace(hostKey))
                    .Select(static hostKey => hostKey!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var item in package.Items)
                {
                    if (!TryResolveUniversalPackageArchiveImportTarget(
                            package,
                            item,
                            knownHostKeys,
                            out var targetPath,
                            out var targetDisplayPath,
                            out var warning))
                    {
                        warnings++;
                        lines.Add($"  WARN   {item.Path}: {warning}");
                        continue;
                    }

                    var existed = File.Exists(targetPath);
                    if (!CopyFileIfDifferent(item.ExtractedPath, targetPath))
                    {
                        skipped++;
                        lines.Add($"  SKIP   {item.Kind,-18} {item.Path} -> {targetDisplayPath} (identical)");
                        continue;
                    }

                    if (existed)
                    {
                        updated++;
                        lines.Add($"  UPDATE {item.Kind,-18} {item.Path} -> {targetDisplayPath}");
                    }
                    else
                    {
                        imported++;
                        lines.Add($"  ADD    {item.Kind,-18} {item.Path} -> {targetDisplayPath}");
                    }
                }
            }
            finally
            {
                DeleteDirectoryBestEffort(extractionRoot);
            }

            lines.Add(string.Empty);
            lines.Add($"Summary: {imported} added, {updated} updated, {skipped} identical skipped, {warnings} warning(s).");
            return new UniversalPackageArchiveImportResult(warnings > 0, lines);
        }

        private bool TryResolveUniversalPackageArchiveImportTarget(
            PortableUniversalModulePackage package,
            PortableUniversalModulePackageItem item,
            IReadOnlySet<string> knownHostKeys,
            out string targetPath,
            out string targetDisplayPath,
            out string warning)
        {
            targetPath = string.Empty;
            targetDisplayPath = string.Empty;
            warning = string.Empty;

            if (!File.Exists(item.ExtractedPath))
            {
                warning = "extracted item file is missing";
                return false;
            }

            var path = NormalizeUniversalPackagePath(item.Path);
            var rootRelativePath = GetUniversalPackageItemRelativePath(path, item.Kind);
            if (string.IsNullOrWhiteSpace(rootRelativePath))
            {
                warning = "item path does not contain a file name";
                return false;
            }

            var targetRoot = item.Kind switch
            {
                UniversalModulePackageItemKind.ModuleDefinition => ResolvePackageModuleDefinitionsRoot(_payloadRoot),
                UniversalModulePackageItemKind.ArtifactPackage => ResolvePackageArtifactsRoot(_payloadRoot),
                UniversalModulePackageItemKind.HostConfiguration => ResolveHostAwareArchiveImportRoot(
                    package,
                    rootRelativePath,
                    knownHostKeys,
                    "host-configs",
                    ResolvePackageHostConfigurationsRoot(_payloadRoot),
                    out rootRelativePath),
                UniversalModulePackageItemKind.ConfigOverlay => ResolveHostAwareArchiveImportRoot(
                    package,
                    rootRelativePath,
                    knownHostKeys,
                    "config-overlays",
                    ResolvePackageConfigOverlaysRoot(_payloadRoot),
                    out rootRelativePath),
                UniversalModulePackageItemKind.DashboardWidget => ResolveHostAwareArchiveImportRoot(
                    package,
                    rootRelativePath,
                    knownHostKeys,
                    "widgets",
                    ResolvePackageWidgetsRoot(_payloadRoot),
                    out rootRelativePath),
                UniversalModulePackageItemKind.WidgetRuntimeData => ResolveHostAwareArchiveImportRoot(
                    package,
                    rootRelativePath,
                    knownHostKeys,
                    "widget-data",
                    ResolvePackageWidgetDataRoot(_payloadRoot),
                    out rootRelativePath),
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                warning = $"unsupported universal package item kind '{item.Kind}'";
                return false;
            }

            targetPath = CombineUnderRoot(targetRoot, rootRelativePath);
            targetDisplayPath = GetDisplayPath(_payloadRoot, targetPath);
            return true;
        }

        private string ResolveHostAwareArchiveImportRoot(
            PortableUniversalModulePackage package,
            string relativePath,
            IReadOnlySet<string> knownHostKeys,
            string archiveFolder,
            string globalRoot,
            out string adjustedRelativePath)
        {
            adjustedRelativePath = relativePath;
            if (TrySplitHostSpecificUniversalPath(
                    package,
                    relativePath,
                    knownHostKeys,
                    out var hostKey,
                    out var hostRelativePath))
            {
                adjustedRelativePath = hostRelativePath;
                return Path.Join(_payloadRoot, "data", "hosts", hostKey, archiveFolder);
            }

            return globalRoot;
        }

        private static bool TrySplitHostSpecificUniversalPath(
            PortableUniversalModulePackage package,
            string relativePath,
            IReadOnlySet<string> knownHostKeys,
            out string hostKey,
            out string hostRelativePath)
        {
            hostKey = string.Empty;
            hostRelativePath = relativePath;
            var segments = NormalizeUniversalPackagePath(relativePath)
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                return false;
            }

            var firstSegment = segments[0];
            var targetHostProfile = string.IsNullOrWhiteSpace(package.TargetHostProfile)
                ? string.Empty
                : SanitizeUniversalPackagePathSegment(package.TargetHostProfile);
            if (!firstSegment.Equals(targetHostProfile, StringComparison.OrdinalIgnoreCase)
                && !knownHostKeys.Contains(firstSegment))
            {
                return false;
            }

            hostKey = firstSegment;
            hostRelativePath = string.Join('/', segments.Skip(1));
            return true;
        }

        private static string GetUniversalPackageItemRelativePath(
            string packagePath,
            UniversalModulePackageItemKind kind)
        {
            var normalized = NormalizeUniversalPackagePath(packagePath);
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return string.Empty;
            }

            var rootAliases = kind switch
            {
                UniversalModulePackageItemKind.ModuleDefinition => new[] { "module-definition", "module-definitions" },
                UniversalModulePackageItemKind.ArtifactPackage => new[] { "artifact", "artifacts" },
                UniversalModulePackageItemKind.HostConfiguration => new[] { "host-config", "host-configs", "host-configuration", "host-configurations" },
                UniversalModulePackageItemKind.ConfigOverlay => new[] { "config-overlay", "config-overlays" },
                UniversalModulePackageItemKind.DashboardWidget => new[] { "widget", "widgets", "dashboard-widgets" },
                UniversalModulePackageItemKind.WidgetRuntimeData => new[] { "widget-data", "widget-runtime-data" },
                _ => []
            };
            if (segments.Length > 1 && rootAliases.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
            {
                return string.Join('/', segments.Skip(1));
            }

            return string.Join('/', segments);
        }

        private static void CopyInstallerRunnerFiles(string currentExecutable, string runnerRoot)
        {
            var executableDirectory = Path.GetDirectoryName(currentExecutable)
                ?? throw new InvalidOperationException("Could not resolve the running installer directory.");
            var baseName = Path.GetFileNameWithoutExtension(currentExecutable);
            var hasFrameworkDependentFiles =
                File.Exists(Path.Join(executableDirectory, baseName + ".deps.json"))
                || File.Exists(Path.Join(executableDirectory, baseName + ".runtimeconfig.json"));

            if (!hasFrameworkDependentFiles)
            {
                File.Copy(currentExecutable, Path.Join(runnerRoot, Path.GetFileName(currentExecutable)), overwrite: true);
                return;
            }

            CopyDirectoryRecursive(executableDirectory, runnerRoot);
        }

        private async Task<DeveloperSourceCheckResult> CheckDeveloperSourceStatusAsync()
        {
            var lines = new List<string>();
            var sourceRoots = ResolveDeveloperSourceRoots(throwIfMissing: true);
            var primarySourceRoot = ResolveDeveloperSourceRoot(throwIfMissing: true)!;

            lines.Add($"Primary source root: {primarySourceRoot}");
            lines.Add("Source roots:");
            foreach (var sourceRoot in sourceRoots)
            {
                lines.Add($"  {sourceRoot}");
            }

            lines.Add($"Installer package: {_payloadRoot}");
            lines.Add(string.Empty);

            var manifests = await ReadDeveloperManifestsAsync(sourceRoots);
            lines.Add("Source manifests:");
            foreach (var manifest in manifests)
            {
                lines.Add($"  {manifest.RepositoryKey}: {manifest.ManifestPath}");
            }

            lines.Add(string.Empty);

            var sourceDefinitions = manifests
                .SelectMany(manifest => ReadManifestModuleDefinitions(manifest.Json, manifest.SourceRoot, manifest.RepositoryKey))
                .ToArray();
            var sourceComponents = manifests
                .SelectMany(manifest => ReadManifestComponents(manifest.Json, manifest.SourceRoot, manifest.RepositoryKey))
                .ToArray();
            var hasPackageUpdates = false;

            lines.Add("Module definitions:");
            foreach (var definition in sourceDefinitions)
            {
                var sourcePath = Path.Join(definition.SourceRoot, definition.Path);
                var sourceDocument = await ReadModuleDefinitionAsync(sourcePath);
                var packagePath = FindPackageModuleDefinitionPath(definition);
                if (!File.Exists(packagePath))
                {
                    lines.Add($"  UPDATE  {definition.ModuleKey}: package file is missing ({Path.GetFileName(definition.Path)}).");
                    hasPackageUpdates = true;
                    continue;
                }

                var packageDocument = await ReadModuleDefinitionAsync(packagePath);
                var versionComparison = CompareVersionText(sourceDocument.DefinitionVersion, packageDocument.DefinitionVersion);
                if (versionComparison > 0)
                {
                    lines.Add($"  UPDATE  {definition.ModuleKey}: package {packageDocument.DefinitionVersion}, source {sourceDocument.DefinitionVersion}.");
                    hasPackageUpdates = true;
                }
                else if (versionComparison < 0)
                {
                    lines.Add($"  DIFF    {definition.ModuleKey}: package {packageDocument.DefinitionVersion}, source {sourceDocument.DefinitionVersion}.");
                    hasPackageUpdates = true;
                }
                else if (versionComparison == 0 && !string.Equals(sourceDocument.DefinitionSha256, packageDocument.DefinitionSha256, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add($"  UPDATE  {definition.ModuleKey}: same version {sourceDocument.DefinitionVersion}, different content hash.");
                    hasPackageUpdates = true;
                }
                else
                {
                    lines.Add($"  OK      {definition.ModuleKey}: package library {packageDocument.DefinitionVersion}, source {sourceDocument.DefinitionVersion}.");
                }
            }

            lines.Add(string.Empty);
            lines.Add("Artifact package entries:");
            foreach (var component in sourceComponents)
            {
                var expectedTarget = component.RelativePathTemplate.Replace("{version}", component.Version, StringComparison.OrdinalIgnoreCase);
                var current = FindConfiguredArtifact(component);
                if (current is null)
                {
                    var availablePath = FindAvailableArtifactPackage(component);
                    if (!string.IsNullOrWhiteSpace(availablePath))
                    {
                        lines.Add($"  OK      {component.ComponentKey}: package library artifact is available ({Path.GetFileName(availablePath)}).");
                        continue;
                    }

                    lines.Add($"  UPDATE  {component.ComponentKey}: package has no configured or library artifact for {component.ModuleKey}/{component.AppKey}/{component.PackageType}/{component.TargetName}.");
                    hasPackageUpdates = true;
                    continue;
                }

                if (!string.Equals(NormalizePathForMatch(current.Target), NormalizePathForMatch(expectedTarget), StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add($"  UPDATE  {component.ComponentKey}: package target {current.Target}, source target {expectedTarget}.");
                    hasPackageUpdates = true;
                }
                else
                {
                    lines.Add($"  OK      {component.ComponentKey}: {expectedTarget}.");
                }
            }

            lines.Add(string.Empty);
            var hasInstalledUpdates = await AppendDatabaseStatusAsync(sourceDefinitions, sourceComponents, lines);

            lines.Add(string.Empty);
            lines.Add(hasPackageUpdates
                ? "Result: source contains module definitions or artifact entries that are newer/different than this installer package."
                : hasInstalledUpdates
                    ? "Result: this installer package matches the source manifest. The installed database has pending updates for configured modules or artifacts."
                    : "Result: this installer package matches the source manifest. Installed configured modules and artifacts are up to date.");

            return new DeveloperSourceCheckResult(hasPackageUpdates || hasInstalledUpdates, lines);
        }

        internal async Task<DeveloperPackageObjectSyncResult> SyncDeveloperPackageObjectsCoreAsync(Action<string>? progress = null)
        {
            void Report(string message) => progress?.Invoke(message);

            var lines = new List<string>();
            Report("Resolving developer source repositories...");
            var sourceRoots = ResolveDeveloperSourceRoots(throwIfMissing: true);
            Report($"Resolved {sourceRoots.Count} developer source root(s).");
            var warnings = 0;
            lines.Add("Source repository updates:");
            warnings += PullDeveloperSourceRepositories(
                sourceRoots,
                lines.Add,
                Report,
                throwOnFailure: false);
            lines.Add(string.Empty);
            if (warnings > 0)
            {
                lines.Add("Package object refresh stopped because one or more source repositories could not be updated.");
                lines.Add($"Summary: 0 updated, 0 already current, {warnings} warning(s).");
                return new DeveloperPackageObjectSyncResult(HasWarnings: true, ConfigUpdated: false, Lines: lines);
            }

            Report("Reading developer component manifests...");
            var manifests = await ReadDeveloperManifestsAsync(sourceRoots);
            Report($"Read {manifests.Count} developer component manifest(s).");
            var sourceDefinitions = manifests
                .SelectMany(manifest => ReadManifestModuleDefinitions(manifest.Json, manifest.SourceRoot, manifest.RepositoryKey))
                .ToArray();
            var sourceComponents = manifests
                .SelectMany(manifest => ReadManifestComponents(manifest.Json, manifest.SourceRoot, manifest.RepositoryKey))
                .ToArray();
            Report($"Found {sourceDefinitions.Length} module definition(s) and {sourceComponents.Length} artifact component(s).");
            var artifactSearchRoots = EnumerateArtifactPackageSearchRoots(sourceRoots).ToArray();
            var updated = 0;
            var unchanged = 0;
            var configUpdated = false;
            var builtPackages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            lines.Add($"Installer package: {_payloadRoot}");
            lines.Add("Artifact package search roots:");
            foreach (var root in artifactSearchRoots)
            {
                lines.Add($"  {root}");
            }

            lines.Add(string.Empty);
            lines.Add("Module definitions:");
            foreach (var definition in sourceDefinitions)
            {
                Report($"Syncing module definition {definition.ModuleKey} {definition.DefinitionVersion}...");
                var sourcePath = Path.Join(definition.SourceRoot, definition.Path);
                if (!File.Exists(sourcePath))
                {
                    lines.Add($"  WARN    {definition.ModuleKey}: source file was not found ({sourcePath}).");
                    warnings++;
                    continue;
                }

                var packagePath = FindPackageModuleDefinitionPath(definition);
                if (await CopyModuleDefinitionIfDifferentAsync(sourcePath, packagePath))
                {
                    lines.Add($"  UPDATED {definition.ModuleKey}: copied {definition.DefinitionVersion} to the package library.");
                    updated++;
                }
                else
                {
                    lines.Add($"  OK      {definition.ModuleKey}: package file already matches source.");
                    unchanged++;
                }
            }

            lines.Add(string.Empty);
            lines.Add("SQL scripts:");
            foreach (var script in _config.Sql.Scripts.Where(static script => script.Enabled))
            {
                if (string.IsNullOrWhiteSpace(script.Path))
                {
                    lines.Add("  WARN    SQL script entry has no path.");
                    warnings++;
                    continue;
                }

                Report($"Syncing SQL script {script.Path}...");
                var scriptFileName = Path.GetFileName(script.Path);
                var targetPath = ResolvePackagePath(script.Path);
                if (File.Exists(targetPath))
                {
                    lines.Add($"  OK      {script.Path}: package file is present.");
                    unchanged++;
                    continue;
                }

                var sourcePath = FindSourceSqlScript(script, sourceRoots);
                if (sourcePath is null)
                {
                    lines.Add($"  WARN    {script.Path}: package file is missing and no source SQL file was found.");
                    warnings++;
                    continue;
                }

                if (scriptFileName.Equals("bootstrap-local.sql", StringComparison.OrdinalIgnoreCase)
                    && Path.GetDirectoryName(sourcePath) is { } sourceSqlRoot
                    && Path.GetDirectoryName(targetPath) is { } targetSqlRoot)
                {
                    CopyDirectory(sourceSqlRoot, targetSqlRoot);
                    CopyCurrentOpenModulePlatformSqlTree(sourceRoots, targetSqlRoot);
                    lines.Add($"  UPDATED {script.Path}: restored missing SQL bootstrap tree.");
                }
                else
                {
                    CopyFileIfDifferent(sourcePath, targetPath);
                    lines.Add($"  UPDATED {script.Path}: restored missing SQL script.");
                }

                updated++;
            }

            lines.Add(string.Empty);
            lines.Add("Artifact packages:");
            foreach (var component in sourceComponents)
            {
                Report($"Syncing artifact package {component.ComponentKey} {component.Version}...");
                var packageName = GetArtifactPackageFileName(component);
                var expectedTarget = component.RelativePathTemplate.Replace("{version}", component.Version, StringComparison.OrdinalIgnoreCase);
                var sourcePackage = FindSourceArtifactPackage(packageName, artifactSearchRoots);
                var current = FindConfiguredArtifact(component);

                if (current is not null)
                {
                    var expectedSource = $"data/global/artifacts/{packageName}";
                    var payloadPath = ResolvePackagePath(expectedSource);
                    var targetMatches = string.Equals(
                        NormalizePathForMatch(current.Target),
                        NormalizePathForMatch(expectedTarget),
                        StringComparison.OrdinalIgnoreCase);

                    if (!targetMatches)
                    {
                        if (sourcePackage is null)
                        {
                            sourcePackage = ResolveOrBuildSourceArtifactPackage(
                                component,
                                packageName,
                                artifactSearchRoots,
                                builtPackages,
                                lines);
                            if (sourcePackage is null)
                            {
                                lines.Add($"  WARN    {component.ComponentKey}: package target is {current.Target}, source expects {expectedTarget}, but {packageName} was not found and could not be selectively built.");
                                warnings++;
                                continue;
                            }
                        }

                        CopyFileIfDifferent(sourcePackage, payloadPath);
                        CopyFileIfDifferent(sourcePackage, Path.Join(ResolvePackageArtifactsRoot(_payloadRoot), packageName));
                        current.Source = expectedSource;
                        current.Target = expectedTarget;
                        lines.Add($"  UPDATED {component.ComponentKey}: copied {packageName} and updated artifact target to {expectedTarget}.");
                        updated++;
                        configUpdated = true;
                        continue;
                    }

                    if (File.Exists(payloadPath))
                    {
                        // Existing same-target packages are treated as immutable. Refresh should add missing or newer
                        // versions, not rewrite bytes for an already versioned artifact and create noisy binary diffs.
                        if (!string.Equals(current.Source, expectedSource, StringComparison.OrdinalIgnoreCase))
                        {
                            current.Source = expectedSource;
                            configUpdated = true;
                            lines.Add($"  UPDATED {component.ComponentKey}: using package library artifact {expectedSource} for this run.");
                            updated++;
                        }
                        else
                        {
                            lines.Add($"  OK      {component.ComponentKey}: artifact package is present.");
                            unchanged++;
                        }

                        var mirroredLibraryPath = Path.Join(ResolvePackageArtifactsRoot(_payloadRoot), packageName);
                        if (!File.Exists(mirroredLibraryPath))
                        {
                            CopyFileIfDifferent(payloadPath, mirroredLibraryPath);
                        }

                        continue;
                    }

                    var currentSourcePath = ResolvePackagePath(current.Source);
                    if (File.Exists(currentSourcePath))
                    {
                        CopyFileIfDifferent(currentSourcePath, payloadPath);
                        CopyFileIfDifferent(currentSourcePath, Path.Join(ResolvePackageArtifactsRoot(_payloadRoot), packageName));
                        if (!string.Equals(current.Source, expectedSource, StringComparison.OrdinalIgnoreCase))
                        {
                            current.Source = expectedSource;
                            configUpdated = true;
                            lines.Add($"  UPDATED {component.ComponentKey}: moved configured artifact source to package library path {expectedSource} for this run.");
                            updated++;
                        }
                        else
                        {
                            lines.Add($"  OK      {component.ComponentKey}: configured artifact source is present ({current.Source}).");
                            unchanged++;
                        }

                        continue;
                    }

                    if (sourcePackage is null)
                    {
                        sourcePackage = ResolveOrBuildSourceArtifactPackage(
                            component,
                            packageName,
                            artifactSearchRoots,
                            builtPackages,
                            lines);
                        if (sourcePackage is null)
                        {
                            lines.Add($"  WARN    {component.ComponentKey}: configured artifact source is missing and {packageName} was not found and could not be selectively built.");
                            warnings++;
                            continue;
                        }
                    }

                    CopyFileIfDifferent(sourcePackage, payloadPath);
                    CopyFileIfDifferent(sourcePackage, Path.Join(ResolvePackageArtifactsRoot(_payloadRoot), packageName));
                    if (!string.Equals(current.Source, expectedSource, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(current.Target, expectedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        current.Source = expectedSource;
                        current.Target = expectedTarget;
                        configUpdated = true;
                    }

                    lines.Add($"  UPDATED {component.ComponentKey}: restored missing artifact package {packageName}.");
                    updated++;
                    continue;
                }

                var libraryPath = Path.Join(ResolvePackageArtifactsRoot(_payloadRoot), packageName);
                if (File.Exists(libraryPath))
                {
                    // Package library entries follow the same immutable-version rule as configured artifact packages.
                    lines.Add($"  OK      {component.ComponentKey}: package library artifact is present.");
                    unchanged++;

                    continue;
                }

                if (sourcePackage is null)
                {
                    sourcePackage = ResolveOrBuildSourceArtifactPackage(
                        component,
                        packageName,
                        artifactSearchRoots,
                        builtPackages,
                        lines);
                    if (sourcePackage is null)
                    {
                        lines.Add($"  WARN    {component.ComponentKey}: {packageName} was not found and could not be selectively built. Build the artifact first or use Create updated installer package.");
                        warnings++;
                        continue;
                    }
                }

                CopyFileIfDifferent(sourcePackage, libraryPath);
                lines.Add($"  UPDATED {component.ComponentKey}: copied package library artifact {packageName}.");
                updated++;
            }

            if (TryUpdateHostAgentPackagePathFromSyncedArtifacts(sourceComponents, lines))
            {
                updated++;
                configUpdated = true;
            }

            lines.Add(string.Empty);
            lines.Add($"Summary: {updated} updated, {unchanged} already current, {warnings} warning(s).");
            return new DeveloperPackageObjectSyncResult(warnings > 0, configUpdated, lines);
        }

        private Task<DeveloperPackageObjectSyncResult> SyncAllProfilePackageObjectsCoreAsync(Action<string>? progress = null)
        {
            void Report(string message) => progress?.Invoke(message);

            var hostProfiles = _configProfiles
                .Where(static profile => TryResolveInstallerHostProfileKey(profile.ConfigPath, out _))
                .ToList();
            var lines = new List<string>
            {
                "All-profile host package object preparation:",
                $"  Installer package: {_payloadRoot}",
                $"  Host profiles discovered: {hostProfiles.Count}",
                string.Empty
            };
            var warnings = 0;
            var synced = 0;
            var skipped = 0;
            var seenProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hookSourceRoots = Array.Empty<string>();
            var nonHostProfiles = _configProfiles.Count - hostProfiles.Count;
            if (nonHostProfiles > 0)
            {
                lines.Add($"  Non-host config profiles skipped: {nonHostProfiles}");
                lines.Add(string.Empty);
            }

            try
            {
                Report("Resolving optional developer source roots for host-profile object hooks...");
                hookSourceRoots = ResolveDeveloperSourceRoots(throwIfMissing: false).ToArray();
                if (hookSourceRoots.Length == 0)
                {
                    lines.Add("Host-profile hooks: skipped - no local developer source roots are available.");
                }
                else
                {
                    lines.Add("Host-profile hook source roots:");
                    foreach (var sourceRoot in hookSourceRoots)
                    {
                        lines.Add($"  {sourceRoot}");
                    }
                }
            }
            catch (SystemException ex)
            {
                warnings++;
                lines.Add($"WARN: Host-profile hooks are skipped because source roots could not be resolved: {ex.Message}");
            }

            lines.Add(string.Empty);

            foreach (var profile in hostProfiles.OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (!seenProfiles.Add(Path.GetFullPath(profile.ConfigPath)))
                {
                    continue;
                }

                Report($"Preparing host profile {profile.DisplayName}...");
                try
                {
                    var profilePayloadRoot = ResolvePayloadRoot(_cli, profile.ConfigPath);
                    lines.Add($"> Profile {profile.DisplayName}");
                    lines.Add($"  Config: {profile.ConfigPath}");
                    lines.Add($"  Package root: {profilePayloadRoot}");

                    warnings += SyncHostSpecificPackageObjectsCore(
                        hookSourceRoots,
                        lines,
                        profile.ConfigPath,
                        profilePayloadRoot,
                        Report);

                    synced++;
                    Report($"Prepared host profile {profile.DisplayName}.");
                    lines.Add(string.Empty);
                }
                catch (SystemException ex)
                {
                    warnings++;
                    skipped++;
                    lines.Add($"> Profile {profile.DisplayName}: skipped - {ex.Message}");
                    Report($"Skipped host profile {profile.DisplayName}: {ex.Message}");
                }
            }

            if (synced == 0)
            {
                warnings++;
                lines.Add("WARN: No readable host profiles were found.");
            }

            lines.Add($"Summary: {synced} profile(s) prepared, {skipped} skipped, {warnings} warning(s).");
            return Task.FromResult(new DeveloperPackageObjectSyncResult(warnings > 0, false, lines));
        }

        private int SyncHostSpecificPackageObjectsCore(
            IReadOnlyList<string> sourceRoots,
            List<string> lines,
            string? configPathOverride = null,
            string? payloadRootOverride = null,
            Action<string>? progress = null)
        {
            var warnings = 0;
            var copied = 0;
            var unchanged = 0;
            var configPath = configPathOverride ?? _configPath;
            var payloadRoot = payloadRootOverride ?? _payloadRoot;
            var targetHostProfile = TryResolveInstallerHostProfileKey(configPath, out var hostProfileKey)
                ? hostProfileKey
                : string.Empty;
            var profileDirectory = Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory;

            lines.Add("  Host-specific package objects:");
            if (string.IsNullOrWhiteSpace(targetHostProfile))
            {
                lines.Add("    WARN    could not resolve target host profile from config path.");
                return 1;
            }

            lines.Add($"    Target host profile: {targetHostProfile}");
            var hostArchiveRoot = Path.Join(payloadRoot, "data", "hosts", targetHostProfile);
            progress?.Invoke($"Preparing host archive {targetHostProfile}...");
            Directory.CreateDirectory(hostArchiveRoot);

            foreach (var folder in GetHostSpecificObjectFolders())
            {
                var sourceFolder = Path.Join(profileDirectory, folder.FolderName);
                if (!Directory.Exists(sourceFolder))
                {
                    continue;
                }

                CopyHostSpecificObjectDirectory(
                    sourceFolder,
                    Path.Join(hostArchiveRoot, folder.FolderName),
                    folder,
                    payloadRoot,
                    lines,
                    ref copied,
                    ref unchanged,
                    ref warnings);
            }

            if (File.Exists(configPath))
            {
                progress?.Invoke($"Reading host profile {targetHostProfile}...");
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                CopyHostSpecificObjectLists(
                    document.RootElement,
                    profileDirectory,
                    hostArchiveRoot,
                    "profile",
                    payloadRoot,
                    lines,
                    ref copied,
                    ref unchanged,
                    ref warnings);

                if (document.RootElement.TryGetProperty("modules", out var modules)
                    && modules.ValueKind == JsonValueKind.Object)
                {
                    foreach (var module in modules.EnumerateObject())
                    {
                        CopyHostSpecificObjectLists(
                            module.Value,
                            profileDirectory,
                            hostArchiveRoot,
                            $"module {module.Name}",
                            payloadRoot,
                            lines,
                            ref copied,
                            ref unchanged,
                            ref warnings);
                    }
                }
            }

            warnings += RunHostProfileObjectHooks(
                sourceRoots,
                targetHostProfile,
                hostArchiveRoot,
                configPath,
                payloadRoot,
                lines,
                ref copied,
                ref unchanged,
                progress);

            lines.Add($"    Summary: {copied} copied/updated, {unchanged} already current, {warnings} warning(s).");
            return warnings;
        }

        private static IReadOnlyList<HostSpecificObjectFolder> GetHostSpecificObjectFolders()
            =>
            [
                new("host-configs", "hostConfigurationFiles", IsJsonOrZipFile),
                new("config-overlays", "configOverlayFiles", IsJsonOrZipFile),
                new("widgets", "widgetFiles", static path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)),
                new("widget-data", "widgetDataFiles", static path => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ];

        private void CopyHostSpecificObjectDirectory(
            string sourceFolder,
            string targetFolder,
            HostSpecificObjectFolder folder,
            string payloadRoot,
            List<string> lines,
            ref int copied,
            ref int unchanged,
            ref int warnings)
        {
            foreach (var sourcePath in Directory.EnumerateFiles(sourceFolder, "*.*", SearchOption.AllDirectories)
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!folder.Filter(sourcePath))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourceFolder, sourcePath);
                CopyHostSpecificObjectFile(
                    sourcePath,
                    targetFolder,
                    relativePath,
                    $"{folder.FolderName}/{relativePath}",
                    payloadRoot,
                    lines,
                    ref copied,
                    ref unchanged,
                    ref warnings);
            }
        }

        private void CopyHostSpecificObjectLists(
            JsonElement profile,
            string profileDirectory,
            string hostArchiveRoot,
            string sourceLabel,
            string payloadRoot,
            List<string> lines,
            ref int copied,
            ref int unchanged,
            ref int warnings)
        {
            foreach (var folder in GetHostSpecificObjectFolders())
            {
                if (!profile.TryGetProperty(folder.ProfileListName, out var list)
                    || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in list.EnumerateArray())
                {
                    if (!TryResolveHostSpecificProfileEntry(
                            entry,
                            profileDirectory,
                            folder,
                            out var sourcePath,
                            out var destinationName,
                            out var warning))
                    {
                        warnings++;
                        lines.Add($"    WARN    {sourceLabel}/{folder.ProfileListName}: {warning}");
                        continue;
                    }

                    CopyHostSpecificObjectFile(
                        sourcePath,
                        Path.Join(hostArchiveRoot, folder.FolderName),
                        destinationName,
                        $"{sourceLabel}/{folder.ProfileListName}/{destinationName}",
                        payloadRoot,
                        lines,
                        ref copied,
                        ref unchanged,
                        ref warnings);
                }
            }
        }

        private static bool TryResolveHostSpecificProfileEntry(
            JsonElement entry,
            string profileDirectory,
            HostSpecificObjectFolder folder,
            out string sourcePath,
            out string destinationName,
            out string warning)
        {
            sourcePath = string.Empty;
            destinationName = string.Empty;
            warning = string.Empty;

            if (entry.ValueKind == JsonValueKind.String)
            {
                var value = entry.GetString() ?? string.Empty;
                var separatorIndex = value.IndexOf('=');
                if (separatorIndex >= 0)
                {
                    destinationName = value[..separatorIndex].Trim();
                    sourcePath = value[(separatorIndex + 1)..].Trim();
                }
                else
                {
                    sourcePath = value.Trim();
                    destinationName = Path.GetFileName(sourcePath);
                }
            }
            else if (entry.ValueKind == JsonValueKind.Object)
            {
                sourcePath = TryGetStringProperty(entry, "sourcePath");
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    sourcePath = TryGetStringProperty(entry, "path");
                }

                destinationName = TryGetStringProperty(entry, "destinationName");
            }
            else
            {
                warning = "entry must be a string or object.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                warning = "source path is empty.";
                return false;
            }

            sourcePath = ResolveProfileRelativePath(sourcePath, profileDirectory);
            if (!File.Exists(sourcePath))
            {
                warning = $"source file was not found: {sourcePath}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(destinationName))
            {
                destinationName = Path.GetFileName(sourcePath);
            }

            destinationName = NormalizeUniversalPackagePath(destinationName);
            if (string.IsNullOrWhiteSpace(destinationName))
            {
                warning = "destination name is empty.";
                return false;
            }

            if (!folder.Filter(destinationName))
            {
                warning = $"destination file type is not supported for {folder.FolderName}: {destinationName}";
                return false;
            }

            return true;
        }

        private static string TryGetStringProperty(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string ResolveProfileRelativePath(string path, string profileDirectory)
            => Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Join(profileDirectory, path.Replace('/', Path.DirectorySeparatorChar)));

        private void CopyHostSpecificObjectFile(
            string sourcePath,
            string targetRoot,
            string destinationName,
            string displayName,
            string payloadRoot,
            List<string> lines,
            ref int copied,
            ref int unchanged,
            ref int warnings)
        {
            try
            {
                var normalizedDestination = NormalizeUniversalPackagePath(destinationName);
                var targetPath = CombineUnderRoot(targetRoot, normalizedDestination);
                if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)
                    || !CopyFileIfDifferent(sourcePath, targetPath))
                {
                    unchanged++;
                    lines.Add($"    OK      {displayName}: already current.");
                    return;
                }

                copied++;
                lines.Add($"    UPDATED {displayName}: copied to {GetDisplayPath(payloadRoot, targetPath)}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                warnings++;
                lines.Add($"    WARN    {displayName}: {ex.Message}");
            }
        }

        private int RunHostProfileObjectHooks(
            IReadOnlyList<string> sourceRoots,
            string targetHostProfile,
            string hostArchiveRoot,
            string hostProfilePath,
            string payloadRoot,
            List<string> lines,
            ref int copied,
            ref int unchanged,
            Action<string>? progress = null)
        {
            var warnings = 0;
            foreach (var sourceRoot in sourceRoots.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                var hookPath = Path.Join(sourceRoot, "scripts", "omp", "build-host-profile-objects.ps1");
                if (!File.Exists(hookPath))
                {
                    continue;
                }

                var moduleKeys = ReadRepositoryModuleKeys(sourceRoot);
                if (moduleKeys.Count == 0)
                {
                    continue;
                }

                var tempRoot = Path.Join(
                    Path.GetTempPath(),
                    "omp-host-profile-objects-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(tempRoot);
                    var arguments = new List<string>
                    {
                        "-NoProfile",
                        "-File",
                        hookPath,
                        "-RepositoryRoot",
                        sourceRoot,
                        "-OutputRoot",
                        tempRoot,
                        "-HostProfilePath",
                        hostProfilePath,
                        "-TargetHostProfile",
                        targetHostProfile,
                        "-Configuration",
                        "Release",
                        "-ModuleKey"
                    };
                    arguments.AddRange(moduleKeys);

                    var result = RunProcess(
                        "powershell",
                        arguments,
                        throwOnFailure: false,
                        workingDirectory: sourceRoot,
                        timeout: TimeSpan.FromMinutes(5));
                    if (result.ExitCode != 0)
                    {
                        warnings++;
                        lines.Add($"    WARN    {Path.GetFileName(sourceRoot)} host-profile hook failed: {result.StdOut}{result.StdErr}");
                        continue;
                    }

                    foreach (var folder in GetHostSpecificObjectFolders())
                    {
                        var generatedRoot = Path.Join(tempRoot, folder.FolderName);
                        if (!Directory.Exists(generatedRoot))
                        {
                            continue;
                        }

                        CopyHostSpecificObjectDirectory(
                            generatedRoot,
                            Path.Join(hostArchiveRoot, folder.FolderName),
                            folder,
                            payloadRoot,
                            lines,
                            ref copied,
                            ref unchanged,
                            ref warnings);
                    }
                }
                finally
                {
                    DeleteDirectoryBestEffort(tempRoot);
                }
            }

            return warnings;
        }

        private static IReadOnlyList<string> ReadRepositoryModuleKeys(string sourceRoot)
        {
            var manifestPath = Path.Join(sourceRoot, "omp-components.json");
            if (!File.Exists(manifestPath))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.TryGetProperty("moduleDefinitions", out var definitions)
                && definitions.ValueKind == JsonValueKind.Array)
            {
                foreach (var moduleKey in definitions.EnumerateArray()
                    .Select(static definition => TryGetStringProperty(definition, "moduleKey"))
                    .Where(static moduleKey => !string.IsNullOrWhiteSpace(moduleKey)))
                {
                    keys.Add(moduleKey!.Trim());
                }
            }

            if (document.RootElement.TryGetProperty("components", out var components)
                && components.ValueKind == JsonValueKind.Array)
            {
                foreach (var moduleKey in components.EnumerateArray()
                    .Select(static component => TryGetStringProperty(component, "moduleKey"))
                    .Where(static moduleKey => !string.IsNullOrWhiteSpace(moduleKey)))
                {
                    keys.Add(moduleKey!.Trim());
                }
            }

            return keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private bool TryUpdateHostAgentPackagePathFromSyncedArtifacts(
            IReadOnlyList<ManifestComponent> sourceComponents,
            List<string> lines)
        {
            var component = sourceComponents.FirstOrDefault(static component =>
                component.PackageType.Equals("host-agent", StringComparison.OrdinalIgnoreCase));
            if (component is null)
            {
                return false;
            }

            var expectedSource = $"data/global/artifacts/{GetArtifactPackageFileName(component)}";
            var expectedPath = ResolvePackagePath(expectedSource);
            if (!File.Exists(expectedPath))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_config.HostAgent.PackagePath)
                && File.Exists(ResolvePackagePath(_config.HostAgent.PackagePath)))
            {
                return false;
            }

            _config.HostAgent.PackagePath = expectedSource;
            lines.Add($"  UPDATED HostAgent package path: using synced package {expectedSource} for this run.");
            return true;
        }

        private string? FindSourceSqlScript(SqlScriptOptions script, IReadOnlyList<string> sourceRoots)
        {
            var relativePath = script.Path.Replace('/', Path.DirectorySeparatorChar);
            var fileName = Path.GetFileName(relativePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_config.DeveloperSource.PackageConfigPath))
            {
                var packageConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(_config.DeveloperSource.PackageConfigPath));
                if (!string.IsNullOrWhiteSpace(packageConfigDirectory))
                {
                    candidates.Add(Path.Join(packageConfigDirectory, relativePath));
                    candidates.Add(Path.Join(packageConfigDirectory, "sql", fileName));
                    if (Directory.Exists(packageConfigDirectory))
                    {
                        candidates.AddRange(Directory
                            .EnumerateFiles(packageConfigDirectory, fileName, SearchOption.AllDirectories)
                            .Order(StringComparer.OrdinalIgnoreCase));
                    }
                }
            }

            foreach (var sourceRoot in sourceRoots)
            {
                candidates.Add(Path.Join(sourceRoot, relativePath));
                var artifactsRoot = Path.Join(sourceRoot, "artifacts");
                if (Directory.Exists(artifactsRoot))
                {
                    candidates.AddRange(Directory
                        .EnumerateFiles(artifactsRoot, fileName, SearchOption.AllDirectories)
                        .OrderByDescending(static path => path.Contains("hostagent-first-public", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(static path => path.Contains("hostagent-first-generic", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase));
                }
            }

            return candidates
                .Select(Path.GetFullPath)
                .FirstOrDefault(File.Exists);
        }

        private static void CopyCurrentOpenModulePlatformSqlTree(
            IReadOnlyList<string> sourceRoots,
            string targetSqlRoot)
        {
            var openModulePlatformRoot = sourceRoots.FirstOrDefault(IsDeveloperSourceRoot);
            if (string.IsNullOrWhiteSpace(openModulePlatformRoot))
            {
                return;
            }

            CopySqlFiles(Path.Join(openModulePlatformRoot, "sql"), Path.Join(targetSqlRoot, "OpenModulePlatform"));
            CopySqlDirectory(openModulePlatformRoot, "OpenModulePlatform.Auth", Path.Join(targetSqlRoot, "OpenModulePlatform.Auth"));
            CopySqlDirectory(openModulePlatformRoot, "OpenModulePlatform.Portal", Path.Join(targetSqlRoot, "OpenModulePlatform.Portal"));
            CopySqlDirectory(openModulePlatformRoot, "OpenModulePlatform.Web.ContentWebAppModule", Path.Join(targetSqlRoot, "OpenModulePlatform.Web.ContentWebAppModule"));
            CopySqlDirectory(openModulePlatformRoot, "OpenModulePlatform.Web.iFrameWebAppModule", Path.Join(targetSqlRoot, "OpenModulePlatform.Web.iFrameWebAppModule"));
            CopySqlDirectory(openModulePlatformRoot, Path.Join("examples", "WebAppModule"), Path.Join(targetSqlRoot, "examples", "WebAppModule"));
            CopySqlDirectory(openModulePlatformRoot, Path.Join("examples", "WebAppBlazorModule"), Path.Join(targetSqlRoot, "examples", "WebAppBlazorModule"));
            CopySqlDirectory(openModulePlatformRoot, Path.Join("examples", "ServiceAppModule"), Path.Join(targetSqlRoot, "examples", "ServiceAppModule"));
            CopySqlDirectory(openModulePlatformRoot, Path.Join("examples", "WorkerAppModule"), Path.Join(targetSqlRoot, "examples", "WorkerAppModule"));
        }

        private static void CopySqlDirectory(string sourceRoot, string projectRelativePath, string targetDirectory)
        {
            var sourceDirectory = Path.Join(sourceRoot, projectRelativePath, "sql");
            if (!Directory.Exists(sourceDirectory))
            {
                sourceDirectory = Path.Join(sourceRoot, projectRelativePath, "Sql");
            }

            CopySqlFiles(sourceDirectory, targetDirectory);
        }

        private static void CopySqlFiles(string sourceDirectory, string targetDirectory)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                return;
            }

            Directory.CreateDirectory(targetDirectory);
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*.sql", SearchOption.TopDirectoryOnly))
            {
                File.Copy(
                    sourceFile,
                    Path.Join(targetDirectory, Path.GetFileName(sourceFile)),
                    overwrite: true);
            }
        }

        private IEnumerable<string> EnumerateArtifactPackageSearchRoots(IReadOnlyList<string> sourceRoots)
        {
            var roots = new List<string>();
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddIfDirectory(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var fullPath = Path.GetFullPath(path);
                if (Directory.Exists(fullPath) && emitted.Add(fullPath))
                {
                    roots.Add(fullPath);
                }
            }

            if (!string.IsNullOrWhiteSpace(_config.ArtifactStoreRoot))
            {
                var artifactStoreRoot = Path.GetFullPath(_config.ArtifactStoreRoot);
                var runtimeRoot = Directory.GetParent(artifactStoreRoot)?.FullName;
                AddIfDirectory(Path.Join(runtimeRoot ?? string.Empty, "ArtifactArchive"));
                AddIfDirectory(Path.Join(artifactStoreRoot, "_available", "artifacts"));
            }

            foreach (var artifactPath in sourceRoots.Select(sourceRoot => Path.Join(sourceRoot, "artifacts")))
            {
                AddIfDirectory(artifactPath);
            }

            AddIfDirectory(ResolvePackageArtifactsRoot(_payloadRoot));

            return roots;
        }

        private static string? FindSourceArtifactPackage(
            string packageName,
            IReadOnlyList<string> artifactSearchRoots)
        {
            return artifactSearchRoots
                .Select(root => Path.Join(root, packageName))
                .FirstOrDefault(File.Exists);
        }

        private string? ResolveOrBuildSourceArtifactPackage(
            ManifestComponent component,
            string packageName,
            IReadOnlyList<string> artifactSearchRoots,
            Dictionary<string, string?> builtPackages,
            List<string> lines)
        {
            var sourcePackage = FindSourceArtifactPackage(packageName, artifactSearchRoots);
            if (sourcePackage is not null)
            {
                return sourcePackage;
            }

            if (builtPackages.TryGetValue(packageName, out var builtPackage))
            {
                return builtPackage;
            }

            builtPackage = TryBuildSourceArtifactPackage(component, packageName, lines);
            builtPackages[packageName] = builtPackage;
            return builtPackage;
        }

        private string? TryBuildSourceArtifactPackage(
            ManifestComponent component,
            string packageName,
            List<string> lines)
        {
            if (string.IsNullOrWhiteSpace(component.ProjectPath))
            {
                lines.Add($"  BUILD   {component.ComponentKey}: skipped selective build because the component manifest has no projectPath.");
                return null;
            }

            var outputRoot = ResolveSelectiveArtifactOutputRoot(component);
            var destination = Path.Join(outputRoot, packageName);
            var tempRoot = Path.Join(
                Path.GetTempPath(),
                "omp-selective-artifact-build-" + Guid.NewGuid().ToString("N"));
            var publishRoot = Path.Join(tempRoot, "publish");

            try
            {
                Directory.CreateDirectory(publishRoot);
                var projectFile = ResolveComponentProjectFile(component);
                if (projectFile is not null)
                {
                    lines.Add($"  BUILD   {component.ComponentKey}: publishing {GetDisplayPath(component.SourceRoot, projectFile)}.");
                    RunProcess(
                        "dotnet",
                        BuildDeterministicDotNetPublishArguments(component, projectFile, publishRoot),
                        workingDirectory: component.SourceRoot);
                }
                else
                {
                    var nodeProjectDirectory = ResolveComponentNodeProjectDirectory(component);
                    if (nodeProjectDirectory is null)
                    {
                        lines.Add($"  BUILD   {component.ComponentKey}: skipped selective build because projectPath '{component.ProjectPath}' does not resolve to one .NET project file or Node web project.");
                        return null;
                    }

                    BuildNodeWebArtifactPayload(component, nodeProjectDirectory, publishRoot, lines);
                }

                RemoveRuntimeConfigurationFiles(publishRoot);
                new ArtifactPackageWriter().CreateFromPayloadDirectory(
                    publishRoot,
                    destination,
                    [],
                    component.MinModuleDefinitionVersion);

                lines.Add($"  BUILD   {component.ComponentKey}: created {destination}.");
                return destination;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                lines.Add($"  BUILD   {component.ComponentKey}: selective build failed: {ex.Message}");
                return null;
            }
            finally
            {
                DeleteDirectoryBestEffort(tempRoot);
            }
        }

        private string ResolveSelectiveArtifactOutputRoot(ManifestComponent component)
        {
            if (!string.IsNullOrWhiteSpace(_config.ArtifactStoreRoot))
            {
                var artifactStoreRoot = Path.GetFullPath(_config.ArtifactStoreRoot);
                var runtimeRoot = Directory.GetParent(artifactStoreRoot)?.FullName;
                if (!string.IsNullOrWhiteSpace(runtimeRoot))
                {
                    return Path.Join(runtimeRoot, "ArtifactArchive");
                }
            }

            return Path.Join(component.SourceRoot, "artifacts");
        }

        private static string[] BuildDeterministicDotNetPublishArguments(
            ManifestComponent component,
            string projectFile,
            string publishRoot)
        {
            var sourceRoot = Path.GetFullPath(component.SourceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var pathMapRoot = "/_/" + SanitizeMsBuildPathMapSegment(component.RepositoryKey);

            return
            [
                "publish",
                projectFile,
                "-c",
                "Release",
                "-o",
                publishRoot,
                "--nologo",
                "--verbosity",
                "minimal",
                "-p:ContinuousIntegrationBuild=true",
                "-p:Deterministic=true",
                $"-p:PathMap={sourceRoot}={pathMapRoot}"
            ];
        }

        private static string SanitizeMsBuildPathMapSegment(string value)
        {
            var chars = value.Trim()
                .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '-')
                .ToArray();
            var sanitized = new string(chars).Trim('-');
            return string.IsNullOrWhiteSpace(sanitized) ? "repository" : sanitized;
        }

        private static string? ResolveComponentProjectFile(ManifestComponent component)
        {
            var projectPath = ResolveComponentProjectPath(component);

            if (File.Exists(projectPath)
                && projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return projectPath;
            }

            if (!Directory.Exists(projectPath))
            {
                return null;
            }

            var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectPath));
            if (!string.IsNullOrWhiteSpace(directoryName))
            {
                var preferredProject = Path.Join(projectPath, directoryName + ".csproj");
                if (File.Exists(preferredProject))
                {
                    return preferredProject;
                }
            }

            var projects = Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            return projects.Length == 1 ? projects[0] : null;
        }

        private static string? ResolveComponentNodeProjectDirectory(ManifestComponent component)
        {
            if (!component.PackageType.Equals("web-app", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var projectPath = ResolveComponentProjectPath(component);
            var projectDirectory = File.Exists(projectPath)
                ? Path.GetDirectoryName(projectPath)
                : projectPath;
            if (string.IsNullOrWhiteSpace(projectDirectory)
                || !Directory.Exists(projectDirectory)
                || !File.Exists(Path.Join(projectDirectory, "package.json")))
            {
                return null;
            }

            return projectDirectory;
        }

        private static string ResolveComponentProjectPath(ManifestComponent component)
        {
            var projectPath = Path.GetFullPath(Path.Join(
                component.SourceRoot,
                component.ProjectPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrChildPath(component.SourceRoot, projectPath))
            {
                throw new InvalidOperationException(
                    $"Component projectPath '{component.ProjectPath}' escapes source root '{component.SourceRoot}'.");
            }

            return projectPath;
        }

        private static void BuildNodeWebArtifactPayload(
            ManifestComponent component,
            string projectDirectory,
            string publishRoot,
            List<string> lines)
        {
            var packageLockPath = Path.Join(projectDirectory, "package-lock.json");
            if (File.Exists(packageLockPath))
            {
                lines.Add($"  BUILD   {component.ComponentKey}: restoring npm packages in {GetDisplayPath(component.SourceRoot, projectDirectory)}.");
                RunNpm(["ci"], workingDirectory: projectDirectory);
            }

            lines.Add($"  BUILD   {component.ComponentKey}: running npm build in {GetDisplayPath(component.SourceRoot, projectDirectory)}.");
            RunNpm(["run", "build"], workingDirectory: projectDirectory);

            var distPath = Path.Join(projectDirectory, "dist");
            if (!Directory.Exists(distPath))
            {
                throw new InvalidOperationException($"Node web project did not produce a dist folder: {distPath}");
            }

            CopyDirectory(distPath, publishRoot);
        }

        private static void RunNpm(
            IReadOnlyList<string> arguments,
            string workingDirectory)
        {
            if (OperatingSystem.IsWindows()
                && TryResolveNodeBackedNpm(out var nodePath, out var npmCliPath))
            {
                // npm.cmd probes project-local npm installations on Windows; invoking the global CLI through node avoids stale node_modules\npm shims.
                RunProcess(nodePath, [npmCliPath, .. arguments], workingDirectory: workingDirectory);
                return;
            }

            RunProcess(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", arguments, workingDirectory: workingDirectory);
        }

        private static bool TryResolveNodeBackedNpm(
            out string nodePath,
            out string npmCliPath)
        {
            nodePath = FindOnPath("node.exe") ?? string.Empty;
            npmCliPath = string.Empty;
            if (string.IsNullOrWhiteSpace(nodePath))
            {
                return false;
            }

            var nodeDirectory = Path.GetDirectoryName(nodePath);
            if (!string.IsNullOrWhiteSpace(nodeDirectory))
            {
                var siblingNpmCli = Path.Join(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(siblingNpmCli))
                {
                    npmCliPath = siblingNpmCli;
                    return true;
                }
            }

            foreach (var directory in GetPathDirectories())
            {
                var npmCli = Path.Join(directory, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(npmCli))
                {
                    npmCliPath = npmCli;
                    return true;
                }
            }

            return false;
        }

        private static string? FindOnPath(string fileName)
            => GetPathDirectories()
                .Select(directory => Path.Join(directory, fileName))
                .Where(File.Exists)
                .FirstOrDefault();

        private static IEnumerable<string> GetPathDirectories()
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                yield break;
            }

            foreach (var item in path
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Where(Directory.Exists))
            {
                yield return item;
            }
        }

        private static string GetDisplayPath(string root, string path)
        {
            try
            {
                return Path.GetRelativePath(root, path);
            }
            catch (ArgumentException)
            {
                return path;
            }
        }

        private string ResolvePackagePath(string packageRelativePath)
        {
            if (Path.IsPathRooted(packageRelativePath))
            {
                throw new InvalidOperationException("Package object sources must be relative to the installer package root.");
            }

            var direct = Path.GetFullPath(Path.Join(
                _payloadRoot,
                packageRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            return direct;
        }

        private static bool CopyFileIfDifferent(string sourcePath, string targetPath)
        {
            if (File.Exists(targetPath) && FilesHaveSameContent(sourcePath, targetPath))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
            File.Copy(sourcePath, targetPath, overwrite: true);
            return true;
        }

        private static async Task<bool> CopyModuleDefinitionIfDifferentAsync(string sourcePath, string targetPath)
        {
            if (File.Exists(targetPath))
            {
                var sourceDocument = await ReadModuleDefinitionAsync(sourcePath);
                var targetDocument = await ReadModuleDefinitionAsync(targetPath);
                if (string.Equals(
                    sourceDocument.DefinitionSha256,
                    targetDocument.DefinitionSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
            File.Copy(sourcePath, targetPath, overwrite: true);
            return true;
        }

        private static bool FilesHaveSameContent(string firstPath, string secondPath)
        {
            var first = new FileInfo(firstPath);
            var second = new FileInfo(secondPath);
            if (first.Length != second.Length)
            {
                return false;
            }

            using var firstStream = File.OpenRead(firstPath);
            using var secondStream = File.OpenRead(secondPath);
            var firstHash = SHA256.HashData(firstStream);
            var secondHash = SHA256.HashData(secondStream);
            return firstHash.SequenceEqual(secondHash);
        }

        private async Task<bool> AppendDatabaseStatusAsync(
            IReadOnlyList<ManifestModuleDefinition> sourceDefinitions,
            IReadOnlyList<ManifestComponent> sourceComponents,
            List<string> lines)
        {
            lines.Add("Installed database state:");
            var hasUpdates = false;
            try
            {
                await using var connection = new SqlConnection(BuildConnectionString(_config.Sql, _config.Sql.Database));
                await connection.OpenAsync();

                foreach (var definition in sourceDefinitions)
                {
                    var installedVersion = await QueryScalarStringAsync(
                        connection,
                        """
SELECT TOP (1) DefinitionVersion
FROM omp.ModuleDefinitionDocuments
WHERE ModuleKey = @moduleKey
  AND IsApplied = 1
ORDER BY AppliedUtc DESC, UpdatedUtc DESC, ModuleDefinitionDocumentId DESC;
""",
                        command => command.Parameters.AddWithValue("@moduleKey", definition.ModuleKey));
                    var status = CompareInstalledVersion(installedVersion, definition.DefinitionVersion);
                    hasUpdates |= IsDeveloperUpdateStatus(status);
                    lines.Add($"  {status,-7} module {definition.ModuleKey}: installed {installedVersion ?? "(missing)"}, source {definition.DefinitionVersion}.");
                }

                foreach (var component in sourceComponents)
                {
                    var isAvailableOnly = FindConfiguredArtifact(component) is null
                        && !string.IsNullOrWhiteSpace(FindAvailableArtifactPackage(component));
                    var installedVersion = await QueryScalarStringAsync(
                        connection,
                        """
SELECT TOP (1) ar.Version
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
WHERE m.ModuleKey = @moduleKey
  AND a.AppKey = @appKey
  AND ar.PackageType = @packageType
  AND ISNULL(ar.TargetName, N'') = @targetName
ORDER BY ar.ArtifactId DESC;
""",
                        command =>
                        {
                            command.Parameters.AddWithValue("@moduleKey", component.ModuleKey);
                            command.Parameters.AddWithValue("@appKey", component.AppKey);
                            command.Parameters.AddWithValue("@packageType", component.PackageType);
                            command.Parameters.AddWithValue("@targetName", component.TargetName);
                        });
                    var status = CompareInstalledVersion(installedVersion, component.Version);
                    if (string.IsNullOrWhiteSpace(installedVersion) && isAvailableOnly)
                    {
                        lines.Add($"  INFO    artifact {component.ComponentKey}: not installed, source {component.Version} is available for later import.");
                    }
                    else
                    {
                        hasUpdates |= IsDeveloperUpdateStatus(status);
                        lines.Add($"  {status,-7} artifact {component.ComponentKey}: installed {installedVersion ?? "(missing)"}, source {component.Version}.");
                    }
                }
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                lines.Add($"  SKIP    database check failed: {ex.Message}");
            }

            return hasUpdates;
        }

        private static string CompareInstalledVersion(string? installedVersion, string sourceVersion)
        {
            if (string.IsNullOrWhiteSpace(installedVersion))
            {
                return "UPDATE";
            }

            var comparison = CompareVersionText(sourceVersion, installedVersion);
            if (comparison == 0)
            {
                return "OK";
            }

            return comparison > 0 ? "UPDATE" : "DIFF";
        }

        private static bool IsDeveloperUpdateStatus(string status)
            => status is "UPDATE" or "DIFF";

        private ArtifactPayloadOptions? FindConfiguredArtifact(ManifestComponent component)
        {
            var expectedPrefix = NormalizePathForMatch(component.RelativePathTemplate)
                .Replace("{version}", string.Empty, StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/');

            return _config.Artifacts.FirstOrDefault(artifact =>
            {
                var target = NormalizePathForMatch(artifact.Target);
                return target.StartsWith(expectedPrefix + "/", StringComparison.OrdinalIgnoreCase)
                    || ParseArtifactPackageIdentity(artifact.Source) is { } identity
                    && string.Equals(identity.ModuleKey, component.ModuleKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(identity.AppKey, component.AppKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(identity.PackageType, component.PackageType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(identity.TargetName, component.TargetName, StringComparison.OrdinalIgnoreCase);
            });
        }

        private string FindPackageModuleDefinitionPath(ManifestModuleDefinition definition)
        {
            var fileName = Path.GetFileName(definition.Path);
            return Path.Join(ResolvePackageModuleDefinitionsRoot(_payloadRoot), fileName);
        }

        private string? FindAvailableArtifactPackage(ManifestComponent component)
        {
            var packageName = GetArtifactPackageFileName(component);
            var candidate = Path.Join(ResolvePackageArtifactsRoot(_payloadRoot), packageName);
            return File.Exists(candidate) ? candidate : null;
        }

        private static string GetArtifactPackageFileName(ManifestComponent component)
            => string.Join(
                "__",
                component.ModuleKey,
                component.AppKey,
                component.PackageType,
                component.TargetName,
                component.Version) + ".zip";

        private static ArtifactPackageIdentity? ParseArtifactPackageIdentity(string source)
        {
            var fileName = Path.GetFileNameWithoutExtension(source);
            var parts = fileName.Split(["__"], StringSplitOptions.None);
            return parts.Length == 5
                ? new ArtifactPackageIdentity(parts[0], parts[1], parts[2], parts[3], parts[4])
                : null;
        }

        private static async Task<string?> QueryScalarStringAsync(
            SqlConnection connection,
            string sql,
            Action<SqlCommand> bind)
        {
            await using var command = new SqlCommand(sql, connection);
            bind(command);
            var value = await command.ExecuteScalarAsync();
            return value is null or DBNull ? null : Convert.ToString(value);
        }

        private static async Task<JsonNode> ReadJsonNodeAsync(string path)
        {
            var text = await File.ReadAllTextAsync(path, Encoding.UTF8);
            return JsonNode.Parse(text)
                ?? throw new InvalidOperationException($"JSON file is empty: {path}");
        }

        private static async Task<IReadOnlyList<DeveloperManifest>> ReadDeveloperManifestsAsync(IReadOnlyList<string> sourceRoots)
        {
            var manifests = new List<DeveloperManifest>();
            foreach (var root in EnumerateDeveloperManifestRoots(sourceRoots))
            {
                var manifestPath = Path.Join(root, "omp-components.json");
                var json = await ReadJsonNodeAsync(manifestPath);
                var repositoryKey = GetJsonStringProperty(json, "repositoryKey");
                manifests.Add(new DeveloperManifest(
                    root,
                    manifestPath,
                    string.IsNullOrWhiteSpace(repositoryKey) ? Path.GetFileName(root) : repositoryKey,
                    json));
            }

            return manifests;
        }

        private static IEnumerable<string> EnumerateDeveloperManifestRoots(IReadOnlyList<string> sourceRoots)
        {
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourceRoot in sourceRoots.Where(emitted.Add))
            {
                yield return sourceRoot;
            }

            var primarySourceRoot = sourceRoots.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(primarySourceRoot))
            {
                yield break;
            }

            var workspaceRoot = Directory.GetParent(primarySourceRoot)?.FullName;
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                yield break;
            }

            var openDocViewerRoot = Path.Join(workspaceRoot, "OpenDocViewer");
            if (File.Exists(Path.Join(openDocViewerRoot, "omp-components.json")) && emitted.Add(openDocViewerRoot))
            {
                yield return openDocViewerRoot;
            }
        }

        private static IReadOnlyList<ManifestModuleDefinition> ReadManifestModuleDefinitions(
            JsonNode manifest,
            string sourceRoot,
            string repositoryKey)
        {
            if (GetJsonObjectProperty(manifest, "moduleDefinitions") is not JsonArray items)
            {
                return [];
            }

            return items
                .OfType<JsonObject>()
                .Select(item => new ManifestModuleDefinition(
                    sourceRoot,
                    repositoryKey,
                    GetJsonStringProperty(item, "moduleKey"),
                    GetJsonStringProperty(item, "definitionVersion"),
                    GetJsonStringProperty(item, "path")))
                .Where(item => !string.IsNullOrWhiteSpace(item.ModuleKey)
                    && !string.IsNullOrWhiteSpace(item.DefinitionVersion)
                    && !string.IsNullOrWhiteSpace(item.Path))
                .ToArray();
        }

        private static IReadOnlyList<ManifestComponent> ReadManifestComponents(
            JsonNode manifest,
            string sourceRoot,
            string repositoryKey)
        {
            if (GetJsonObjectProperty(manifest, "components") is not JsonArray items)
            {
                return [];
            }

            return items
                .OfType<JsonObject>()
                .Select(item => new ManifestComponent(
                    sourceRoot,
                    repositoryKey,
                    GetJsonStringProperty(item, "componentKey"),
                    GetJsonStringProperty(item, "moduleKey"),
                    GetJsonStringProperty(item, "appKey"),
                    GetJsonStringProperty(item, "packageType"),
                    GetJsonStringProperty(item, "targetName"),
                    GetJsonStringProperty(item, "version"),
                    GetJsonStringProperty(item, "relativePathTemplate"),
                    GetJsonStringProperty(item, "projectPath"),
                    GetJsonStringProperty(item, "packageFileTemplate"),
                    GetJsonStringProperty(item, "minModuleDefinitionVersion")))
                .Where(static item => item.HasCompleteArtifactIdentity)
                .ToArray();
        }

        private string? ResolveDeveloperSourceRoot(bool throwIfMissing)
        {
            var configuredRoot = ParseConfiguredDeveloperSourceRoots(_config.DeveloperSource.SourceRoot)
                .Select(Path.GetFullPath)
                .FirstOrDefault(IsDeveloperSourceRoot);
            if (configuredRoot is not null)
            {
                return configuredRoot;
            }

            var discoveredRoot = GetDeveloperSourceSearchStarts()
                .SelectMany(EnumerateSelfAndParents)
                .FirstOrDefault(IsDeveloperSourceRoot);
            if (discoveredRoot is not null)
            {
                return discoveredRoot;
            }

            if (throwIfMissing)
            {
                throw new DirectoryNotFoundException("No OpenModulePlatform source repository was found. Set Developer / Source repository root in the installer before using this action.");
            }

            return null;
        }

        private IReadOnlyList<string> ResolveDeveloperSourceRoots(bool throwIfMissing)
        {
            var configuredRoots = ParseConfiguredDeveloperSourceRoots(_config.DeveloperSource.SourceRoot)
                .Select(Path.GetFullPath)
                .ToArray();
            if (configuredRoots.Length > 0)
            {
                var validRoots = configuredRoots
                    .Where(root => File.Exists(Path.Join(root, "omp-components.json")))
                    .ToArray();
                if (validRoots.Any(IsDeveloperSourceRoot))
                {
                    return validRoots;
                }

                if (throwIfMissing)
                {
                    throw new DirectoryNotFoundException(
                        "Configured developer source roots must include an OpenModulePlatform source repository and may include additional repositories with omp-components.json.");
                }
            }

            var primarySourceRoot = ResolveDeveloperSourceRoot(throwIfMissing);
            return string.IsNullOrWhiteSpace(primarySourceRoot)
                ? []
                : [primarySourceRoot];
        }

        private static IEnumerable<string> ParseConfiguredDeveloperSourceRoots(string value)
            => value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => !string.IsNullOrWhiteSpace(item));

        private IEnumerable<string> GetDeveloperSourceSearchStarts()
        {
            yield return _payloadRoot;
            yield return Path.GetDirectoryName(_configPath) ?? Environment.CurrentDirectory;
            yield return AppContext.BaseDirectory;
            yield return Environment.CurrentDirectory;
        }

        private static IEnumerable<string> EnumerateSelfAndParents(string path)
        {
            var directory = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new DirectoryInfo(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            while (directory is not null)
            {
                yield return directory.FullName;
                directory = directory.Parent;
            }
        }

        private static bool IsDeveloperSourceRoot(string path)
            => File.Exists(Path.Join(path, "omp-components.json"))
                && File.Exists(Path.Join(path, "OpenModulePlatform.slnx"))
                && File.Exists(Path.Join(path, "scripts", "deployment", "package-hostagent-first.ps1"));

        internal async Task SaveCurrentConfigAsync()
        {
            var json = JsonSerializer.Serialize(_config, JsonOptions);
            await File.WriteAllTextAsync(_configPath, json + Environment.NewLine, Encoding.UTF8);
        }

        private static int CompareVersionText(string left, string right)
        {
            if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
            {
                return leftVersion.CompareTo(rightVersion);
            }

            var leftParts = left.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
            var rightParts = right.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
            var count = Math.Max(leftParts.Length, rightParts.Length);
            for (var index = 0; index < count; index++)
            {
                var leftPart = index < leftParts.Length ? leftParts[index] : "0";
                var rightPart = index < rightParts.Length ? rightParts[index] : "0";
                if (int.TryParse(leftPart, out var leftNumber) && int.TryParse(rightPart, out var rightNumber))
                {
                    var numberComparison = leftNumber.CompareTo(rightNumber);
                    if (numberComparison != 0)
                    {
                        return numberComparison;
                    }

                    continue;
                }

                var textComparison = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
                if (textComparison != 0)
                {
                    return textComparison;
                }
            }

            return 0;
        }

        private async Task UninstallAsync(bool removeRuntimeFiles, bool removeDatabaseObjects)
        {
            ApplyValues();
            if (!ConfirmUninstall(removeRuntimeFiles, removeDatabaseObjects))
            {
                return;
            }

            await RunGuiOperationAsync(
                "Uninstalling OpenModulePlatform...",
                "Uninstall completed.",
                "Uninstall did not complete.",
                "Uninstall failed.",
                () => RunUninstallAsync(
                    _config,
                    _configPath,
                    removeRuntimeFiles,
                    removeDatabaseObjects,
                    yes: true));
        }

        private async Task RunGuiOperationAsync(
            string runningText,
            string successText,
            string incompleteText,
            string failureText,
            Func<Task<int>> operation)
        {
            SetActionButtonsEnabled(false);
            _exitButton.Enabled = false;
            _logBox.Clear();
            SetBusyStatus(runningText);

            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var writer = new TextBoxWriter(_logBox);
            Console.SetOut(writer);
            Console.SetError(writer);

            try
            {
                ExitCode = await Task.Run(operation);
                SetReadyStatus(ExitCode == 0 ? successText : incompleteText);

                MessageBox.Show(
                    ExitCode == 0 ? successText : incompleteText,
                    "OpenModulePlatform installer",
                    MessageBoxButtons.OK,
                    ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (JsonException ex)
            {
                ExitCode = 1;
                // Action execution is the main GUI boundary; log and show the failure so the operator has both summary and details.
                writer.WriteLine(failureText);
                writer.WriteLine(ex.Message);
                SetReadyStatus(failureText);
                MessageBox.Show(
                    ex.Message,
                    "OpenModulePlatform installer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (SystemException ex)
            {
                ExitCode = 1;
                // Action execution is the main GUI boundary; log and show the failure so the operator has both summary and details.
                writer.WriteLine(failureText);
                writer.WriteLine(ex.Message);
                SetReadyStatus(failureText);
                MessageBox.Show(
                    ex.Message,
                    "OpenModulePlatform installer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                SetActionButtonsEnabled(true);
                _exitButton.Enabled = true;
                if (ExitCode == 2)
                {
                    SetReadyStatus("Operation did not complete.");
                }
            }
        }

        private bool ConfirmUninstall(bool removeRuntimeFiles, bool removeDatabaseObjects)
        {
            var message = BuildUninstallConfirmationMessage(removeRuntimeFiles, removeDatabaseObjects);
            var firstConfirmation = MessageBox.Show(
                message,
                "Confirm OpenModulePlatform uninstall",
                MessageBoxButtons.YesNo,
                removeDatabaseObjects ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (firstConfirmation != DialogResult.Yes)
            {
                return false;
            }

            if (!removeDatabaseObjects)
            {
                return true;
            }

            var databaseConfirmation = MessageBox.Show(
                $"This will remove all user objects from database '{_config.Sql.Database}' on '{_config.Sql.Server}'. The database itself will be kept. Continue?",
                "Confirm database object cleanup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            return databaseConfirmation == DialogResult.Yes;
        }

        private string BuildUninstallConfirmationMessage(bool removeRuntimeFiles, bool removeDatabaseObjects)
        {
            var builder = new StringBuilder();
            var additionalServiceNames = (_config.HostAgent.AdditionalServiceNamesToRemove ?? [])
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            builder.AppendLine("The uninstall action will affect:");
            builder.AppendLine();
            builder.AppendLine($"Windows services: {ValueOrPlaceholder(_config.HostAgent.ServiceName)} and services under {ValueOrPlaceholder(_config.HostAgent.ServicesRoot)}");
            if (additionalServiceNames.Length > 0)
            {
                builder.AppendLine($"Additional service names: {string.Join(", ", additionalServiceNames)}");
            }

            builder.AppendLine($"IIS site: {ValueOrPlaceholder(_config.HostAgent.IisSiteName)}");
            builder.AppendLine($"IIS app pools with prefix: {ValueOrPlaceholder(_config.HostAgent.IisAppPoolNamePrefix)}");

            if (removeRuntimeFiles)
            {
                builder.AppendLine();
                builder.AppendLine("Runtime folders will be removed:");
                builder.AppendLine($"HostAgent: {ValueOrPlaceholder(_config.HostAgent.InstallPath)}");
                builder.AppendLine($"Portal: {ValueOrPlaceholder(_config.HostAgent.PortalPhysicalPath)}");
                builder.AppendLine($"Web apps: {ValueOrPlaceholder(_config.HostAgent.WebAppsRoot)}");
                builder.AppendLine($"Services: {ValueOrPlaceholder(_config.HostAgent.ServicesRoot)}");
                builder.AppendLine($"ArtifactStore: {ValueOrPlaceholder(_config.ArtifactStoreRoot)}");
                builder.AppendLine($"Local artifact cache: {ValueOrPlaceholder(_config.HostAgent.LocalArtifactCacheRoot)}");
            }
            else
            {
                builder.AppendLine();
                builder.AppendLine("Runtime files will be kept.");
            }

            builder.AppendLine();
            if (removeDatabaseObjects)
            {
                builder.AppendLine($"Database objects will be removed from: {_config.Sql.Server}/{_config.Sql.Database}");
                builder.AppendLine("The database itself will not be dropped.");
            }
            else
            {
                builder.AppendLine($"Database objects will be kept in: {_config.Sql.Server}/{_config.Sql.Database}");
            }

            builder.AppendLine();
            builder.Append("Continue?");
            return builder.ToString();
        }

        private static string ValueOrPlaceholder(string value)
            => string.IsNullOrWhiteSpace(value) ? "(not configured)" : value.Trim();

        private void SetActionButtonsEnabled(bool enabled)
        {
            _primaryActionButton.Enabled = enabled;
            _refreshPackageBeforePrimaryAction.Enabled = enabled && _hasDeveloperSource;
            _refreshObjectArchiveButton.Enabled = enabled && _hasDeveloperSource;
            _showAdvancedActions.Enabled = enabled;
            _installButton.Enabled = enabled;
            _upgradeCompleteButton.Enabled = enabled;
            _checkSourceButton.Enabled = enabled && _hasDeveloperSource;
            _syncPackageObjectsButton.Enabled = enabled && _hasDeveloperSource;
            _syncAllProfilePackageObjectsButton.Enabled = enabled && _configProfiles.Count > 0;
            _importUniversalPackageButton.Enabled = enabled;
            _prunePackageArchiveButton.Enabled = enabled && HasPackageArchivePruneRoots();
            _createUniversalPackageButton.Enabled = enabled && HasUniversalPackageCandidates();
            _createUpdatedInstallerPackageButton.Enabled = enabled && _hasDeveloperSource;
            _uninstallRuntimeButton.Enabled = enabled;
            _cleanUninstallButton.Enabled = enabled;
            _fullUninstallButton.Enabled = enabled;
            _reloadConfigButton.Enabled = enabled;
        }

        private void SetBusyStatus(string text)
        {
            _statusLabel.Text = text;
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.MarqueeAnimationSpeed = 35;
        }

        private void SetReadyStatus(string text)
        {
            _statusLabel.Text = text;
            _progressBar.MarqueeAnimationSpeed = 0;
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
        }

        private void Set(string key, string value)
        {
            if (_fields.TryGetValue(key, out var box))
            {
                box.Text = value;
            }
        }

        private string Get(string key)
            => _fields.TryGetValue(key, out var box) ? box.Text.Trim() : string.Empty;
    }

    private sealed class TextBoxWriter : TextWriter
    {
        private readonly TextBox _target;

        public TextBoxWriter(TextBox target)
        {
            _target = target;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
            => Append(value.ToString());

        public override void Write(string? value)
            => Append(value ?? string.Empty);

        public override void WriteLine(string? value)
            => Append((value ?? string.Empty) + Environment.NewLine);

        private void Append(string text)
        {
            if (_target.IsDisposed)
            {
                return;
            }

            if (_target.InvokeRequired)
            {
                _target.BeginInvoke(() => Append(text));
                return;
            }

            _target.AppendText(text);
        }
    }

    private sealed record UniversalPackageBuildRequest(
        string PackageKey,
        string PackageVersion,
        string DisplayName,
        string Description,
        string? HostKey,
        string HostDisplayName,
        string OutputPath,
        IReadOnlyList<UniversalPackageCandidate> Items);

    private sealed record UniversalPackageBuildResult(
        string PackagePath,
        int ItemCount);

    private sealed record UniversalPackageCandidate(
        string Kind,
        string SourcePath,
        string PackagePath)
    {
        public string DisplayName => $"{Kind}: {PackagePath}";
    }

    private sealed record UniversalPackageHostChoice(
        string DisplayName,
        string? HostKey,
        string? ConfigPath,
        string? ExternalDataRoot)
    {
        public override string ToString() => DisplayName;
    }

    private sealed class UniversalPackageCandidateListItem
    {
        public UniversalPackageCandidateListItem(UniversalPackageCandidate candidate)
        {
            Candidate = candidate;
        }

        public UniversalPackageCandidate Candidate { get; }

        public override string ToString() => Candidate.DisplayName;
    }

    private sealed class UniversalPackageBuilderForm : Form
    {
        private readonly string _payloadRoot;
        private readonly IReadOnlyList<UniversalPackageHostChoice> _hostChoices;
        private readonly ComboBox _hostBox = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 360
        };
        private readonly CheckBox _includeGlobalObjects = new()
        {
            AutoSize = true,
            Checked = true,
            Text = "Include global package objects"
        };
        private readonly CheckBox _includeHostObjects = new()
        {
            AutoSize = true,
            Checked = true,
            Text = "Include host-specific package objects"
        };
        private readonly CheckBox _includeHistoricalArtifacts = new()
        {
            AutoSize = true,
            Checked = false,
            Text = "Include older artifact versions"
        };
        private readonly TextBox _packageKeyBox = new() { Width = 220 };
        private readonly TextBox _packageVersionBox = new() { Width = 160 };
        private readonly TextBox _displayNameBox = new() { Width = 420 };
        private readonly TextBox _descriptionBox = new() { Width = 580 };
        private readonly TextBox _outputPathBox = new()
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        private readonly CheckedListBox _itemList = new()
        {
            CheckOnClick = true,
            Dock = DockStyle.Fill
        };
        private readonly Label _itemSummaryLabel = new()
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        };
        private readonly Button _selectAllButton = new()
        {
            AutoSize = true,
            Text = "Select all"
        };
        private readonly Button _selectNoneButton = new()
        {
            AutoSize = true,
            Text = "Select none"
        };
        private readonly Button _browseButton = new()
        {
            AutoSize = true,
            Text = "Browse..."
        };
        private readonly Button _createButton = new()
        {
            AutoSize = true,
            Text = "Create"
        };

        private bool _outputPathIsAutomatic = true;
        private bool _updatingOutputPath;

        public UniversalPackageBuilderForm(
            string payloadRoot,
            IReadOnlyList<BootstrapConfigProfile> configProfiles,
            string currentConfigPath)
        {
            _payloadRoot = payloadRoot;
            _hostChoices = BuildHostChoices(configProfiles);

            Text = "Create universal module package";
            AutoScroll = true;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(900, 620);
            Size = new Size(980, 700);

            _packageKeyBox.Text = "omp-universal";
            _packageVersionBox.Text = DateTime.Now.ToString("yyyyMMdd-HHmm");
            _displayNameBox.Text = "OpenModulePlatform universal package";

            BuildLayout();
            PopulateHostChoices(currentConfigPath);
            RefreshCandidateList();
            UpdateDefaultOutputPath(force: true);
            UpdateHostObjectState();

            _hostBox.SelectedIndexChanged += (_, _) =>
            {
                UpdateHostObjectState();
                RefreshCandidateList();
                UpdateDefaultOutputPath(force: false);
            };
            _includeGlobalObjects.CheckedChanged += (_, _) => RefreshCandidateList();
            _includeHostObjects.CheckedChanged += (_, _) => RefreshCandidateList();
            _includeHistoricalArtifacts.CheckedChanged += (_, _) => RefreshCandidateList();
            _packageKeyBox.TextChanged += (_, _) => UpdateDefaultOutputPath(force: false);
            _packageVersionBox.TextChanged += (_, _) => UpdateDefaultOutputPath(force: false);
            _displayNameBox.TextChanged += (_, _) => UpdateCreateButtonState();
            _descriptionBox.TextChanged += (_, _) => UpdateCreateButtonState();
            _outputPathBox.TextChanged += (_, _) =>
            {
                if (!_updatingOutputPath)
                {
                    _outputPathIsAutomatic = false;
                }

                UpdateCreateButtonState();
            };
            _itemList.ItemCheck += (_, _) => BeginInvoke(UpdateCreateButtonState);
            _selectAllButton.Click += (_, _) => SetAllItemsChecked(checkedState: true);
            _selectNoneButton.Click += (_, _) => SetAllItemsChecked(checkedState: false);
            _browseButton.Click += (_, _) => BrowseOutputPath();
            _createButton.Click += (_, _) => TryCreateRequest();
        }

        public UniversalPackageBuildRequest? Request { get; private set; }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(12),
                RowCount = 7
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(880, 0),
                Text = "Create a universal package zip from the installer's object archive. The zip uses the same folders as the local archive: module-definitions, artifacts, host-configs, config-overlays, widgets, and widget-data."
            }, 0, 0);

            var identityGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 4,
                Margin = new Padding(0, 12, 0, 0)
            };
            identityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            identityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            identityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            identityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            identityGrid.Controls.Add(CreateFieldLabel("Package key"), 0, 0);
            identityGrid.Controls.Add(_packageKeyBox, 1, 0);
            identityGrid.Controls.Add(CreateFieldLabel("Version"), 2, 0);
            identityGrid.Controls.Add(_packageVersionBox, 3, 0);
            identityGrid.Controls.Add(CreateFieldLabel("Display name"), 0, 1);
            identityGrid.SetColumnSpan(_displayNameBox, 3);
            identityGrid.Controls.Add(_displayNameBox, 1, 1);
            identityGrid.Controls.Add(CreateFieldLabel("Description"), 0, 2);
            identityGrid.SetColumnSpan(_descriptionBox, 3);
            identityGrid.Controls.Add(_descriptionBox, 1, 2);
            root.Controls.Add(identityGrid, 0, 1);

            var hostGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Margin = new Padding(0, 10, 0, 0)
            };
            hostGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            hostGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            hostGrid.Controls.Add(CreateFieldLabel("Target host"), 0, 0);
            hostGrid.Controls.Add(_hostBox, 1, 0);

            var includePanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(110, 4, 0, 0)
            };
            includePanel.Controls.Add(_includeGlobalObjects);
            includePanel.Controls.Add(_includeHostObjects);
            includePanel.Controls.Add(_includeHistoricalArtifacts);
            hostGrid.SetColumnSpan(includePanel, 2);
            hostGrid.Controls.Add(includePanel, 0, 1);
            root.Controls.Add(hostGrid, 0, 2);

            var outputGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                Margin = new Padding(0, 10, 0, 0)
            };
            outputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            outputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            outputGrid.Controls.Add(CreateFieldLabel("Output zip"), 0, 0);
            outputGrid.Controls.Add(_outputPathBox, 1, 0);
            outputGrid.Controls.Add(_browseButton, 2, 0);
            root.Controls.Add(outputGrid, 0, 3);

            var itemToolbar = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0, 12, 0, 4)
            };
            itemToolbar.Controls.Add(_itemSummaryLabel);
            itemToolbar.Controls.Add(_selectAllButton);
            itemToolbar.Controls.Add(_selectNoneButton);
            root.Controls.Add(itemToolbar, 0, 4);
            root.Controls.Add(_itemList, 0, 5);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Margin = new Padding(0, 10, 0, 0)
            };
            var cancelButton = new Button
            {
                AutoSize = true,
                DialogResult = DialogResult.Cancel,
                Text = "Cancel"
            };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(_createButton);
            root.Controls.Add(buttons, 0, 6);

            AcceptButton = _createButton;
            CancelButton = cancelButton;
            Controls.Add(root);
        }

        private static Label CreateFieldLabel(string text)
            => new()
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 5, 8, 5),
                Text = text
            };

        private void PopulateHostChoices(string currentConfigPath)
        {
            _hostBox.Items.Add(new UniversalPackageHostChoice(
                "No target host (global package)",
                null,
                null,
                null));

            foreach (var choice in _hostChoices)
            {
                _hostBox.Items.Add(choice);
            }

            var selected = _hostChoices.FirstOrDefault(choice =>
                !string.IsNullOrWhiteSpace(choice.ConfigPath)
                && choice.ConfigPath.Equals(currentConfigPath, StringComparison.OrdinalIgnoreCase));
            _hostBox.SelectedItem = selected ?? _hostBox.Items[0];
        }

        private void UpdateHostObjectState()
        {
            var hostChoice = SelectedHostChoice;
            var hasHost = !string.IsNullOrWhiteSpace(hostChoice?.HostKey);
            _includeHostObjects.Enabled = hasHost;
            if (!hasHost)
            {
                _includeHostObjects.Checked = false;
            }
        }

        private void RefreshCandidateList()
        {
            var previouslyChecked = _itemList.CheckedItems
                .OfType<UniversalPackageCandidateListItem>()
                .Select(item => item.Candidate.PackagePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hadSelection = _itemList.Items.Count > 0;

            _itemList.BeginUpdate();
            _itemList.Items.Clear();
            var candidates = CollectUniversalPackageCandidates(
                _payloadRoot,
                SelectedHostChoice,
                _includeGlobalObjects.Checked,
                _includeHostObjects.Checked);
            if (!_includeHistoricalArtifacts.Checked)
            {
                candidates = FilterLatestUniversalPackageArtifacts(candidates);
            }

            foreach (var candidate in candidates)
            {
                var item = new UniversalPackageCandidateListItem(candidate);
                var isChecked = !hadSelection || previouslyChecked.Contains(candidate.PackagePath);
                _itemList.Items.Add(item, isChecked);
            }

            _itemList.EndUpdate();
            _itemSummaryLabel.Text = $"{candidates.Count} package object(s)";
            UpdateCreateButtonState();
        }

        private void SetAllItemsChecked(bool checkedState)
        {
            for (var index = 0; index < _itemList.Items.Count; index++)
            {
                _itemList.SetItemChecked(index, checkedState);
            }

            UpdateCreateButtonState();
        }

        private void BrowseOutputPath()
        {
            using var dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = "zip",
                Filter = "Universal package (*.zip)|*.zip|All files (*.*)|*.*",
                FileName = Path.GetFileName(_outputPathBox.Text),
                InitialDirectory = ResolveInitialOutputDirectory()
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            _outputPathIsAutomatic = false;
            SetOutputPath(dialog.FileName);
        }

        private string ResolveInitialOutputDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_outputPathBox.Text)
                && Path.GetDirectoryName(_outputPathBox.Text) is { } currentDirectory
                && Directory.Exists(currentDirectory))
            {
                return currentDirectory;
            }

            var exportsRoot = Path.Join(_payloadRoot, "exports");
            Directory.CreateDirectory(exportsRoot);
            return exportsRoot;
        }

        private void UpdateDefaultOutputPath(bool force)
        {
            if (!force && !_outputPathIsAutomatic)
            {
                return;
            }

            var packageKey = SanitizeUniversalPackageFilePart(_packageKeyBox.Text);
            var packageVersion = SanitizeUniversalPackageFilePart(_packageVersionBox.Text);
            var hostChoice = SelectedHostChoice;
            var hostPart = string.IsNullOrWhiteSpace(hostChoice?.HostKey)
                ? "global"
                : SanitizeUniversalPackageFilePart(hostChoice.HostKey);
            var fileName = $"{packageKey}__{hostPart}__{packageVersion}.zip";
            SetOutputPath(Path.Join(_payloadRoot, "exports", fileName));
            _outputPathIsAutomatic = true;
        }

        private void SetOutputPath(string path)
        {
            _updatingOutputPath = true;
            _outputPathBox.Text = path;
            _updatingOutputPath = false;
        }

        private void UpdateCreateButtonState()
        {
            _createButton.Enabled =
                !string.IsNullOrWhiteSpace(_packageKeyBox.Text)
                && !string.IsNullOrWhiteSpace(_packageVersionBox.Text)
                && !string.IsNullOrWhiteSpace(_outputPathBox.Text);
        }

        private void TryCreateRequest()
        {
            var selectedItems = _itemList.CheckedItems
                .OfType<UniversalPackageCandidateListItem>()
                .Select(item => item.Candidate)
                .ToArray();
            if (selectedItems.Length == 0)
            {
                var createEmpty = MessageBox.Show(
                    this,
                    "No package objects are selected. Create a manifest-only universal package?",
                    "Create universal module package",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (createEmpty != DialogResult.Yes)
                {
                    return;
                }
            }

            var outputPath = _outputPathBox.Text.Trim();
            if (File.Exists(outputPath))
            {
                var overwrite = MessageBox.Show(
                    this,
                    $"The output file already exists:{Environment.NewLine}{outputPath}{Environment.NewLine}{Environment.NewLine}Replace it?",
                    "Create universal module package",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (overwrite != DialogResult.Yes)
                {
                    return;
                }
            }

            var hostChoice = SelectedHostChoice;
            Request = new UniversalPackageBuildRequest(
                _packageKeyBox.Text.Trim(),
                _packageVersionBox.Text.Trim(),
                string.IsNullOrWhiteSpace(_displayNameBox.Text)
                    ? _packageKeyBox.Text.Trim()
                    : _displayNameBox.Text.Trim(),
                _descriptionBox.Text.Trim(),
                hostChoice?.HostKey,
                hostChoice?.DisplayName ?? "No target host",
                outputPath,
                selectedItems);
            DialogResult = DialogResult.OK;
            Close();
        }

        private UniversalPackageHostChoice? SelectedHostChoice
            => _hostBox.SelectedItem as UniversalPackageHostChoice;
    }

    private static UniversalPackageHostChoice? CreateUniversalPackageHostChoice(BootstrapConfigProfile profile)
    {
        if (!TryResolveInstallerHostProfileKey(profile.ConfigPath, out var hostKey))
        {
            return null;
        }

        var externalDataRoot = Path.GetDirectoryName(profile.ConfigPath);
        var displayName = string.Equals(profile.DisplayName, hostKey, StringComparison.OrdinalIgnoreCase)
            ? profile.DisplayName
            : $"{profile.DisplayName} ({hostKey})";
        return new UniversalPackageHostChoice(
            displayName,
            hostKey,
            profile.ConfigPath,
            externalDataRoot);
    }

    private static IReadOnlyList<UniversalPackageHostChoice> BuildHostChoices(
        IReadOnlyList<BootstrapConfigProfile> configProfiles)
    {
        var choices = new Dictionary<string, UniversalPackageHostChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in configProfiles
                     .Select(CreateUniversalPackageHostChoice)
                     .Where(static choice => choice is not null && !string.IsNullOrWhiteSpace(choice.HostKey))
                     .Select(static choice => choice!))
        {
            choices.TryAdd(choice.HostKey!, choice);
        }

        return choices.Values
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<UniversalPackageCandidate> CollectUniversalPackageCandidates(
        string payloadRoot,
        UniversalPackageHostChoice? hostChoice,
        bool includeGlobal,
        bool includeHostSpecific)
    {
        var candidates = new Dictionary<string, UniversalPackageCandidate>(StringComparer.OrdinalIgnoreCase);

        if (includeGlobal)
        {
            AddUniversalPackageCandidates(
                candidates,
                ResolvePackageModuleDefinitionsRoot(payloadRoot),
                "module-definitions",
                "module-definition",
                "*.json");
            AddUniversalPackageCandidates(
                candidates,
                ResolvePackageArtifactsRoot(payloadRoot),
                "artifacts",
                "artifact",
                "*.zip");
            AddUniversalPackageCandidates(
                candidates,
                ResolvePackageHostConfigurationsRoot(payloadRoot),
                "host-configs",
                "host-config",
                "*.*",
                IsJsonOrZipFile);
            AddUniversalPackageCandidates(
                candidates,
                ResolvePackageConfigOverlaysRoot(payloadRoot),
                "config-overlays",
                "config-overlay",
                "*.*",
                IsJsonOrZipFile);
            AddUniversalPackageCandidates(
                candidates,
                ResolvePackageWidgetsRoot(payloadRoot),
                "widgets",
                "dashboard-widget",
                "*.json");
            AddUniversalPackageCandidates(
                candidates,
                ResolvePackageWidgetDataRoot(payloadRoot),
                "widget-data",
                "widget-data",
                "*.zip");
        }

        if (includeHostSpecific && !string.IsNullOrWhiteSpace(hostChoice?.HostKey))
        {
            var hostSegment = SanitizeUniversalPackagePathSegment(hostChoice.HostKey);
            foreach (var root in EnumerateUniversalPackageHostDataRoots(payloadRoot, hostChoice))
            {
                AddUniversalPackageCandidates(
                    candidates,
                    Path.Join(root, "host-configs"),
                    $"host-configs/{hostSegment}",
                    "host-config",
                    "*.*",
                    IsJsonOrZipFile);
                AddUniversalPackageCandidates(
                    candidates,
                    Path.Join(root, "config-overlays"),
                    $"config-overlays/{hostSegment}",
                    "config-overlay",
                    "*.*",
                    IsJsonOrZipFile);
                AddUniversalPackageCandidates(
                    candidates,
                    Path.Join(root, "widgets"),
                    $"widgets/{hostSegment}",
                    "dashboard-widget",
                    "*.json");
                AddUniversalPackageCandidates(
                    candidates,
                    Path.Join(root, "widget-data"),
                    $"widget-data/{hostSegment}",
                    "widget-data",
                    "*.zip");
            }
        }

        return candidates.Values
            .OrderBy(static item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<UniversalPackageCandidate> FilterLatestUniversalPackageArtifacts(
        IReadOnlyList<UniversalPackageCandidate> candidates)
    {
        var latestByIdentity = new Dictionary<string, (UniversalPackageCandidate Candidate, string Version)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!TryParseUniversalPackageArtifactIdentity(candidate, out var identity))
            {
                continue;
            }

            var identityKey = string.Join(
                "__",
                identity.ModuleKey,
                identity.AppKey,
                identity.PackageType,
                identity.TargetName);
            if (!latestByIdentity.TryGetValue(identityKey, out var current)
                || CompareUniversalPackageVersionText(identity.Version, current.Version) > 0)
            {
                latestByIdentity[identityKey] = (candidate, identity.Version);
            }
        }

        var latestArtifactPaths = latestByIdentity.Values
            .Select(static item => item.Candidate.PackagePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Where(candidate => !TryParseUniversalPackageArtifactIdentity(candidate, out _)
                || latestArtifactPaths.Contains(candidate.PackagePath))
            .ToArray();
    }

    private static bool TryParseUniversalPackageArtifactIdentity(
        UniversalPackageCandidate candidate,
        out ArtifactPackageIdentity identity)
    {
        identity = default!;
        if (!candidate.Kind.Equals("artifact", StringComparison.OrdinalIgnoreCase)
            || !candidate.PackagePath.StartsWith("artifacts/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(candidate.PackagePath);
        var parts = fileName.Split(["__"], StringSplitOptions.None);
        if (parts.Length != 5)
        {
            return false;
        }

        identity = new ArtifactPackageIdentity(parts[0], parts[1], parts[2], parts[3], parts[4]);
        return true;
    }

    private static int CompareUniversalPackageVersionText(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        var leftParts = left.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var index = 0; index < count; index++)
        {
            var leftPart = index < leftParts.Length ? leftParts[index] : "0";
            var rightPart = index < rightParts.Length ? rightParts[index] : "0";
            if (int.TryParse(leftPart, out var leftNumber) && int.TryParse(rightPart, out var rightNumber))
            {
                var numberComparison = leftNumber.CompareTo(rightNumber);
                if (numberComparison != 0)
                {
                    return numberComparison;
                }

                continue;
            }

            var textComparison = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            if (textComparison != 0)
            {
                return textComparison;
            }
        }

        return 0;
    }

    private static void AddUniversalPackageCandidates(
        Dictionary<string, UniversalPackageCandidate> candidates,
        string sourceRoot,
        string packageFolder,
        string kind,
        string searchPattern,
        Func<string, bool>? filter = null)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, searchPattern, SearchOption.AllDirectories)
                     .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
        {
            if (filter is not null && !filter(sourcePath))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var packagePath = NormalizeUniversalPackagePath(Path.Join(packageFolder, relativePath));
            if (packagePath.Equals(UniversalModulePackageReader.ManifestEntryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            candidates.TryAdd(packagePath, new UniversalPackageCandidate(
                kind,
                sourcePath,
                packagePath));
        }
    }

    private static IEnumerable<string> EnumerateUniversalPackageHostDataRoots(
        string payloadRoot,
        UniversalPackageHostChoice hostChoice)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void YieldIfUnique(string? path, List<string> result)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var fullPath = Path.GetFullPath(path);
            if (yielded.Add(fullPath))
            {
                result.Add(fullPath);
            }
        }

        var roots = new List<string>();
        YieldIfUnique(hostChoice.ExternalDataRoot, roots);
        if (!string.IsNullOrWhiteSpace(hostChoice.HostKey))
        {
            YieldIfUnique(Path.Join(payloadRoot, "data", "hosts", hostChoice.HostKey), roots);
            YieldIfUnique(Path.Join(payloadRoot, "data", "profiles", hostChoice.HostKey), roots);
        }

        return roots;
    }

    private static bool IsJsonOrZipFile(string path)
        => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static string ResolveUniversalPackageHostKey(string configPath)
    {
        if (Path.GetFileName(configPath).Equals("bootstrap.json", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(configPath) is { } parent
            && !string.IsNullOrWhiteSpace(Path.GetFileName(parent)))
        {
            return SanitizeUniversalPackagePathSegment(Path.GetFileName(parent));
        }

        return SanitizeUniversalPackagePathSegment(Path.GetFileNameWithoutExtension(configPath));
    }

    private static bool TryResolveInstallerHostProfileKey(string configPath, out string hostProfileKey)
    {
        hostProfileKey = string.Empty;
        var fullPath = Path.GetFullPath(configPath);
        var profileDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            return false;
        }

        var hostsDirectory = Directory.GetParent(profileDirectory);
        if (hostsDirectory is null
            || !hostsDirectory.Name.Equals("hosts", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        hostProfileKey = SanitizeUniversalPackagePathSegment(Path.GetFileName(profileDirectory));
        return !string.IsNullOrWhiteSpace(hostProfileKey);
    }

    private static string NormalizeUniversalPackagePath(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("Universal package object paths must be relative paths.");
        }

        var invalid = Path.GetInvalidFileNameChars();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(static segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalid) >= 0))
        {
            throw new InvalidOperationException("Universal package object paths contain an invalid path segment.");
        }

        return string.Join('/', segments);
    }

    private static string SanitizeUniversalPackagePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(['/', '\\', ':'])
            .ToHashSet();
        var chars = value.Trim()
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(chars).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "host" : sanitized;
    }

    private static string SanitizeUniversalPackageFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(['/', '\\', ':'])
            .ToHashSet();
        var chars = value.Trim()
            .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(chars).Trim('.', ' ', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "package" : sanitized;
    }

    private sealed record HostSpecificObjectFolder(
        string FolderName,
        string ProfileListName,
        Func<string, bool> Filter);

    private sealed record DeveloperSourceCheckResult(
        bool HasUpdates,
        IReadOnlyList<string> Lines);

    private sealed record DeveloperPackageObjectSyncResult(
        bool HasWarnings,
        bool ConfigUpdated,
        IReadOnlyList<string> Lines);

    private sealed record DeveloperManifest(
        string SourceRoot,
        string ManifestPath,
        string RepositoryKey,
        JsonNode Json);

    private sealed record ManifestModuleDefinition(
        string SourceRoot,
        string RepositoryKey,
        string ModuleKey,
        string DefinitionVersion,
        string Path);

    private sealed record ManifestComponent(
        string SourceRoot,
        string RepositoryKey,
        string ComponentKey,
        string ModuleKey,
        string AppKey,
        string PackageType,
        string TargetName,
        string Version,
        string RelativePathTemplate,
        string ProjectPath,
        string PackageFileTemplate,
        string MinModuleDefinitionVersion)
    {
        public bool HasCompleteArtifactIdentity
            => !string.IsNullOrWhiteSpace(ComponentKey)
                && !string.IsNullOrWhiteSpace(ModuleKey)
                && !string.IsNullOrWhiteSpace(AppKey)
                && !string.IsNullOrWhiteSpace(PackageType)
                && !string.IsNullOrWhiteSpace(TargetName)
                && !string.IsNullOrWhiteSpace(Version)
                && !string.IsNullOrWhiteSpace(RelativePathTemplate);
    }

    private sealed record ArtifactPackageIdentity(
        string ModuleKey,
        string AppKey,
        string PackageType,
        string TargetName,
        string Version);

    private sealed record PackageArchivePruneCandidate(
        string Kind,
        string GroupKey,
        string Version,
        string Path,
        string DisplayName);

    private sealed record PackageArchivePrunePlan(
        IReadOnlyList<PackageArchivePruneCandidate> DeleteCandidates,
        IReadOnlyList<PackageArchivePruneCandidate> ProtectedCandidates,
        int LatestKeptCount,
        int WarningCount,
        IReadOnlyList<string> Lines)
    {
        public bool HasWarnings => WarningCount > 0;
    }

    private sealed record PackageArchivePruneResult(
        bool HasWarnings,
        IReadOnlyList<string> Lines);

    private sealed record UniversalPackageArchiveImportResult(
        bool HasWarnings,
        IReadOnlyList<string> Lines);
}
