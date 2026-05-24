using System.Drawing;
using System.Diagnostics;
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

        if (!string.IsNullOrWhiteSpace(cli.ConfigPath))
        {
            AddConfigFile(cli.ConfigPath);
        }

        foreach (var directory in EnumerateGuiConfigDirectories(cli))
        {
            AddConfigDirectory(directory);
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
                "No bootstrap configuration file was found. Create a machine-specific config in the package 'configs' folder before starting the installer. The standalone config editor is available at tools\\bootstrap-config-editor\\index.html when included in the package.");
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
                "More than one bootstrap configuration matches this computer. Keep exactly one matching config file in the package 'configs' folder, then start the installer again." + Environment.NewLine + Environment.NewLine
                + "Local computer names: " + string.Join(", ", localMachineNames.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)) + Environment.NewLine
                + "Matching configs: " + string.Join(", ", machineMatches.Select(static profile => profile.ConfigPath)));
        }

        throw new InvalidOperationException(
            "No bootstrap configuration matches this computer. The installer is locked to the config whose profile.machineNames, hostAgent.hostName, or hostAgent.hostKey matches the local computer name." + Environment.NewLine + Environment.NewLine
            + "Local computer names: " + string.Join(", ", localMachineNames.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)) + Environment.NewLine
            + "Config folder: " + string.Join(Environment.NewLine + "  ", profiles.Select(static profile => Path.GetDirectoryName(profile.ConfigPath)).Distinct(StringComparer.OrdinalIgnoreCase)) + Environment.NewLine + Environment.NewLine
            + "Create or update a config file in the package 'configs' folder, then start the installer again. The standalone config editor is available at tools\\bootstrap-config-editor\\index.html when included in the package.");
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

            return result.HasWarnings ? 1 : 0;
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
                    (_installButton, "Runs the full bootstrap again with the matched profile. Use this for deliberate reconfiguration or repair."),
                    (_upgradeCompleteButton, "Adds newer or missing module definitions and missing artifacts from this package. Existing artifact folders and an existing HostAgent service are left unchanged.")
                ]));

            _packageToolsTab = CreateActionTab(
                "Package tools",
                "Developer-only helpers for checking and refreshing package objects from source repositories.",
                [
                    (_checkSourceButton, "Compares package objects and installed database state with the configured source repository manifests."),
                    (_syncPackageObjectsButton, "Copies newer or missing module definitions and already-built artifact packages into this installer package. Missing .NET artifacts can be built selectively."),
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
            var page = new TabPage(title) { Padding = new Padding(12) };
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
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

            var optionsPage = new TabPage("Options") { Padding = new Padding(12) };
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
            var page = new TabPage(title) { Padding = new Padding(12) };
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
            if (_hasDeveloperSource && !containsPackageTools)
            {
                var insertIndex = Math.Min(1, _advancedActionTabs.TabPages.Count);
                _advancedActionTabs.TabPages.Insert(insertIndex, _packageToolsTab);
            }
            else if (!_hasDeveloperSource && containsPackageTools)
            {
                _advancedActionTabs.TabPages.Remove(_packageToolsTab);
            }
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
            var warnings = 0;
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
                        lines.Add($"  OK      {component.ComponentKey}: artifact package is present.");
                        unchanged++;

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
                        lines.Add($"  OK      {component.ComponentKey}: configured artifact source is present ({current.Source}).");
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

            lines.Add(string.Empty);
            lines.Add($"Summary: {updated} updated, {unchanged} already current, {warnings} warning(s).");
            return new DeveloperPackageObjectSyncResult(warnings > 0, configUpdated, lines);
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
                        [
                            "publish",
                            projectFile,
                            "-c",
                            "Release",
                            "-o",
                            publishRoot,
                            "--nologo",
                            "--verbosity",
                            "minimal"
                        ],
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
                    []);

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
            var npm = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
            var packageLockPath = Path.Join(projectDirectory, "package-lock.json");
            if (File.Exists(packageLockPath))
            {
                lines.Add($"  BUILD   {component.ComponentKey}: restoring npm packages in {GetDisplayPath(component.SourceRoot, projectDirectory)}.");
                RunProcess(npm, ["ci"], workingDirectory: projectDirectory);
            }

            lines.Add($"  BUILD   {component.ComponentKey}: running npm build in {GetDisplayPath(component.SourceRoot, projectDirectory)}.");
            RunProcess(npm, ["run", "build"], workingDirectory: projectDirectory);

            var distPath = Path.Join(projectDirectory, "dist");
            if (!Directory.Exists(distPath))
            {
                throw new InvalidOperationException($"Node web project did not produce a dist folder: {distPath}");
            }

            CopyDirectory(distPath, publishRoot);
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

            using var sha256 = SHA256.Create();
            using var firstStream = File.OpenRead(firstPath);
            using var secondStream = File.OpenRead(secondPath);
            var firstHash = sha256.ComputeHash(firstStream);
            var secondHash = sha256.ComputeHash(secondStream);
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
                    GetJsonStringProperty(item, "packageFileTemplate")))
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
                if (validRoots.Any(root => IsDeveloperSourceRoot(root)))
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
            _showAdvancedActions.Enabled = enabled;
            _installButton.Enabled = enabled;
            _upgradeCompleteButton.Enabled = enabled;
            _checkSourceButton.Enabled = enabled && _hasDeveloperSource;
            _syncPackageObjectsButton.Enabled = enabled && _hasDeveloperSource;
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
        string PackageFileTemplate)
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
}
