using System.Drawing;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Data.SqlClient;

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
            var configPath = ResolveGuiConfigPath(cli);
            var config = ReadJsonAsync<BootstrapConfig>(configPath).GetAwaiter().GetResult();
            var payloadRoot = ResolvePayloadRoot(cli, configPath);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(defaultValue: false);
            using var form = new InstallerForm(config, configPath, payloadRoot, cli.PayloadZipPath);
            Application.Run(form);
            return form.ExitCode;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "OpenModulePlatform installer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string ResolveGuiConfigPath(CliOptions cli)
    {
        if (!string.IsNullOrWhiteSpace(cli.ConfigPath))
        {
            return Path.GetFullPath(cli.ConfigPath);
        }

        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "bootstrap.local.sample.json"),
            Path.Combine(AppContext.BaseDirectory, "bootstrap.local.sample.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "bootstrap.local.sample.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "bootstrap.local.sample.json"))
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "No bootstrap config was specified and bootstrap.local.sample.json could not be found near the installer.");
    }

    private sealed class InstallerForm : Form
    {
        private readonly BootstrapConfig _config;
        private readonly string _configPath;
        private readonly string _payloadRoot;
        private readonly string _payloadZipPath;
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
        private readonly Button _checkSourceButton = new() { Text = "Check source objects", AutoSize = true };
        private readonly Button _syncPackageObjectsButton = new() { Text = "Sync package objects", AutoSize = true };
        private readonly Button _createUpdatedInstallerPackageButton = new() { Text = "Create updated installer package", AutoSize = true };
        private readonly Button _uninstallRuntimeButton = new() { Text = "Uninstall runtime", AutoSize = true };
        private readonly Button _cleanUninstallButton = new() { Text = "Clean uninstall", AutoSize = true };
        private readonly Button _fullUninstallButton = new() { Text = "Full uninstall", AutoSize = true, ForeColor = Color.DarkRed };
        private readonly Button _exitButton = new() { Text = "Exit", AutoSize = true };
        private readonly CheckBox _runSql = new() { Text = "Run SQL bootstrap" };
        private readonly CheckBox _installHostAgent = new() { Text = "Install/update HostAgent" };
        private readonly CheckBox _deployWebApps = new() { Text = "Let HostAgent deploy web apps" };
        private readonly CheckBox _ensureIisSite = new() { Text = "Create/update IIS site and app pools" };
        private readonly CheckBox _includeExampleApps = new() { Text = "Install example apps and sample data" };
        private readonly Dictionary<string, TextBox> _fields = new(StringComparer.OrdinalIgnoreCase);

        public InstallerForm(
            BootstrapConfig config,
            string configPath,
            string payloadRoot,
            string payloadZipPath)
        {
            _config = config;
            _configPath = configPath;
            _payloadRoot = payloadRoot;
            _payloadZipPath = payloadZipPath;
            ExitCode = 2;

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
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
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

            LoadValues();
            _installButton.Click += async (_, _) => await InstallAsync();
            _checkSourceButton.Click += async (_, _) => await CheckDeveloperSourceAsync();
            _syncPackageObjectsButton.Click += async (_, _) => await SyncDeveloperPackageObjectsAsync();
            _createUpdatedInstallerPackageButton.Click += async (_, _) => await CreateUpdatedInstallerPackageAsync();
            _uninstallRuntimeButton.Click += async (_, _) => await UninstallAsync(removeRuntimeFiles: false, removeDatabaseObjects: false);
            _cleanUninstallButton.Click += async (_, _) => await UninstallAsync(removeRuntimeFiles: true, removeDatabaseObjects: false);
            _fullUninstallButton.Click += async (_, _) => await UninstallAsync(removeRuntimeFiles: true, removeDatabaseObjects: true);
            _exitButton.Click += (_, _) => Close();
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
                Text = "OpenModulePlatform HostAgent-first installation"
            });
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Review the installation settings before starting. Existing OMP artifacts can be upgraded later through HostAgent."
            });
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Text = $"Config: {_configPath}"
            });
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Text = $"Payload: {_payloadRoot}"
            });
            return panel;
        }

        private Control CreateActionPanel()
        {
            var group = new GroupBox
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Text = "Actions",
                Padding = new Padding(12)
            };

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddActionRow(
                grid,
                _installButton,
                "Runs the SQL bootstrap, prepares ArtifactStore, and installs or updates HostAgent using the settings above.");
            AddActionRow(
                grid,
                _checkSourceButton,
                "On a development machine, compares the installed/package module definitions and artifacts with the source repository manifest.");
            AddActionRow(
                grid,
                _syncPackageObjectsButton,
                "On a development machine, updates newer or missing module definition JSON files and already-built artifact packages without rebuilding the whole installer package.");
            AddActionRow(
                grid,
                _createUpdatedInstallerPackageButton,
                "On a development machine, starts a separate refresh process that rebuilds this installer package from source and restarts the updated installer.");
            AddActionRow(
                grid,
                _uninstallRuntimeButton,
                "Stops and removes HostAgent/runtime Windows services plus the configured IIS site and app pools. Runtime files and database objects are kept.");
            AddActionRow(
                grid,
                _cleanUninstallButton,
                "Does the runtime uninstall and also removes the configured runtime folders, ArtifactStore, web-app folders, and service folders. The database is kept.");
            AddActionRow(
                grid,
                _fullUninstallButton,
                "Does the clean uninstall and removes all user objects from the configured database. The database itself is never dropped.");

            group.Controls.Add(grid);
            return group;
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
                "This updates this installer package with newer or missing module definition JSON files and artifact package zips that already exist in the package, ArtifactStore _available library, RuntimeRoot\\ArtifactArchive, or source artifacts folders. It does not compile source projects. If a binary artifact package is missing everywhere, use Create updated installer package instead. Continue?",
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
                        await SaveCurrentConfigAsync();
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
            catch (Exception ex)
            {
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
            var runnerRoot = Path.Combine(
                Path.GetTempPath(),
                "omp-installer-refresh-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runnerRoot);

            var runnerExecutable = Path.Combine(runnerRoot, Path.GetFileName(currentExecutable));
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
                File.Exists(Path.Combine(executableDirectory, baseName + ".deps.json"))
                || File.Exists(Path.Combine(executableDirectory, baseName + ".runtimeconfig.json"));

            if (!hasFrameworkDependentFiles)
            {
                File.Copy(currentExecutable, Path.Combine(runnerRoot, Path.GetFileName(currentExecutable)), overwrite: true);
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
                var sourcePath = Path.Combine(definition.SourceRoot, definition.Path);
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
                    var location = IsAvailablePackagePath(packagePath) ? "available" : "initial";
                    lines.Add($"  OK      {definition.ModuleKey}: package {packageDocument.DefinitionVersion}, source {sourceDocument.DefinitionVersion} ({location}).");
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
                        lines.Add($"  OK      {component.ComponentKey}: available for later install ({Path.GetFileName(availablePath)}).");
                        continue;
                    }

                    lines.Add($"  UPDATE  {component.ComponentKey}: package has no initial or available artifact for {component.ModuleKey}/{component.AppKey}/{component.PackageType}/{component.TargetName}.");
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
                    ? "Result: this installer package matches the source manifest. The installed database has pending updates for initial modules or artifacts."
                    : "Result: this installer package matches the source manifest. Installed initial modules and artifacts are up to date.");

            return new DeveloperSourceCheckResult(hasPackageUpdates || hasInstalledUpdates, lines);
        }

        private async Task<DeveloperPackageObjectSyncResult> SyncDeveloperPackageObjectsCoreAsync()
        {
            var lines = new List<string>();
            var sourceRoots = ResolveDeveloperSourceRoots(throwIfMissing: true);
            var manifests = await ReadDeveloperManifestsAsync(sourceRoots);
            var sourceDefinitions = manifests
                .SelectMany(manifest => ReadManifestModuleDefinitions(manifest.Json, manifest.SourceRoot, manifest.RepositoryKey))
                .ToArray();
            var sourceComponents = manifests
                .SelectMany(manifest => ReadManifestComponents(manifest.Json, manifest.SourceRoot, manifest.RepositoryKey))
                .ToArray();
            var artifactSearchRoots = EnumerateArtifactPackageSearchRoots(sourceRoots).ToArray();
            var updated = 0;
            var unchanged = 0;
            var warnings = 0;
            var configUpdated = false;

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
                var sourcePath = Path.Combine(definition.SourceRoot, definition.Path);
                if (!File.Exists(sourcePath))
                {
                    lines.Add($"  WARN    {definition.ModuleKey}: source file was not found ({sourcePath}).");
                    warnings++;
                    continue;
                }

                var packagePath = FindPackageModuleDefinitionPath(definition);
                if (CopyFileIfDifferent(sourcePath, packagePath))
                {
                    var location = IsAvailablePackagePath(packagePath) ? "available" : "initial";
                    lines.Add($"  UPDATED {definition.ModuleKey}: copied {definition.DefinitionVersion} to {location} package library.");
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
                var packageName = GetArtifactPackageFileName(component);
                var expectedTarget = component.RelativePathTemplate.Replace("{version}", component.Version, StringComparison.OrdinalIgnoreCase);
                var sourcePackage = FindSourceArtifactPackage(packageName, artifactSearchRoots);
                var current = FindConfiguredArtifact(component);

                if (current is not null)
                {
                    var expectedSource = $"payload/{packageName}";
                    var payloadPath = ResolvePackagePath(expectedSource);
                    var targetMatches = string.Equals(
                        NormalizePathForMatch(current.Target),
                        NormalizePathForMatch(expectedTarget),
                        StringComparison.OrdinalIgnoreCase);

                    if (!targetMatches)
                    {
                        if (sourcePackage is null)
                        {
                            lines.Add($"  WARN    {component.ComponentKey}: package target is {current.Target}, source expects {expectedTarget}, but {packageName} was not found in any artifact package search root.");
                            warnings++;
                            continue;
                        }

                        CopyFileIfDifferent(sourcePackage, payloadPath);
                        CopyFileIfDifferent(sourcePackage, Path.Combine(_payloadRoot, "available-artifacts", packageName));
                        current.Source = expectedSource;
                        current.Target = expectedTarget;
                        lines.Add($"  UPDATED {component.ComponentKey}: copied {packageName} and updated initial artifact target to {expectedTarget}.");
                        updated++;
                        configUpdated = true;
                        continue;
                    }

                    if (File.Exists(payloadPath))
                    {
                        if (sourcePackage is not null && CopyFileIfDifferent(sourcePackage, payloadPath))
                        {
                            lines.Add($"  UPDATED {component.ComponentKey}: refreshed initial artifact package {packageName}.");
                            updated++;
                        }
                        else
                        {
                            lines.Add($"  OK      {component.ComponentKey}: initial artifact package is present.");
                            unchanged++;
                        }

                        if (sourcePackage is not null)
                        {
                            CopyFileIfDifferent(sourcePackage, Path.Combine(_payloadRoot, "available-artifacts", packageName));
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
                        lines.Add($"  WARN    {component.ComponentKey}: configured artifact source is missing and {packageName} was not found in any artifact package search root.");
                        warnings++;
                        continue;
                    }

                    CopyFileIfDifferent(sourcePackage, payloadPath);
                    CopyFileIfDifferent(sourcePackage, Path.Combine(_payloadRoot, "available-artifacts", packageName));
                    current.Source = expectedSource;
                    current.Target = expectedTarget;
                    lines.Add($"  UPDATED {component.ComponentKey}: restored missing initial artifact package {packageName}.");
                    updated++;
                    configUpdated = true;
                    continue;
                }

                var availablePath = Path.Combine(_payloadRoot, "available-artifacts", packageName);
                if (File.Exists(availablePath))
                {
                    if (sourcePackage is not null && CopyFileIfDifferent(sourcePackage, availablePath))
                    {
                        lines.Add($"  UPDATED {component.ComponentKey}: refreshed available artifact package {packageName}.");
                        updated++;
                    }
                    else
                    {
                        lines.Add($"  OK      {component.ComponentKey}: available artifact package is present.");
                        unchanged++;
                    }

                    continue;
                }

                if (sourcePackage is null)
                {
                    lines.Add($"  WARN    {component.ComponentKey}: {packageName} was not found. Build the artifact first or use Create updated installer package.");
                    warnings++;
                    continue;
                }

                CopyFileIfDifferent(sourcePackage, availablePath);
                lines.Add($"  UPDATED {component.ComponentKey}: copied available artifact package {packageName}.");
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
                AddIfDirectory(Path.Combine(runtimeRoot ?? string.Empty, "ArtifactArchive"));
                AddIfDirectory(Path.Combine(artifactStoreRoot, "_available", "artifacts"));
            }

            foreach (var sourceRoot in sourceRoots)
            {
                AddIfDirectory(Path.Combine(sourceRoot, "artifacts"));
            }

            AddIfDirectory(Path.Combine(_payloadRoot, "available-artifacts"));
            AddIfDirectory(Path.Combine(_payloadRoot, "payload"));

            return roots;
        }

        private static string? FindSourceArtifactPackage(
            string packageName,
            IReadOnlyList<string> artifactSearchRoots)
        {
            foreach (var root in artifactSearchRoots)
            {
                var candidate = Path.Combine(root, packageName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private string ResolvePackagePath(string packageRelativePath)
        {
            if (Path.IsPathRooted(packageRelativePath))
            {
                throw new InvalidOperationException("Package object sources must be relative to the installer package root.");
            }

            return Path.GetFullPath(Path.Combine(
                _payloadRoot,
                packageRelativePath.Replace('/', Path.DirectorySeparatorChar)));
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
                    var packagePath = FindPackageModuleDefinitionPath(definition);
                    var isAvailableOnly = File.Exists(packagePath) && IsAvailablePackagePath(packagePath);
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
                    if (string.IsNullOrWhiteSpace(installedVersion) && isAvailableOnly)
                    {
                        lines.Add($"  INFO    module {definition.ModuleKey}: not installed, source {definition.DefinitionVersion} is available for later import.");
                    }
                    else
                    {
                        hasUpdates |= IsDeveloperUpdateStatus(status);
                        lines.Add($"  {status,-7} module {definition.ModuleKey}: installed {installedVersion ?? "(missing)"}, source {definition.DefinitionVersion}.");
                    }
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
            var initialPath = Path.Combine(_payloadRoot, "module-definitions", fileName);
            if (File.Exists(initialPath))
            {
                return initialPath;
            }

            return Path.Combine(_payloadRoot, "available-module-definitions", fileName);
        }

        private string? FindAvailableArtifactPackage(ManifestComponent component)
        {
            var packageName = GetArtifactPackageFileName(component);
            var availablePath = Path.Combine(_payloadRoot, "available-artifacts", packageName);
            if (File.Exists(availablePath))
            {
                return availablePath;
            }

            var payloadPath = Path.Combine(_payloadRoot, "payload", packageName);
            return File.Exists(payloadPath) ? payloadPath : null;
        }

        private static string GetArtifactPackageFileName(ManifestComponent component)
            => string.Join(
                "__",
                component.ModuleKey,
                component.AppKey,
                component.PackageType,
                component.TargetName,
                component.Version) + ".zip";

        private bool IsAvailablePackagePath(string path)
            => Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(Path.Combine(_payloadRoot, "available-module-definitions")) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

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
                var manifestPath = Path.Combine(root, "omp-components.json");
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
            foreach (var sourceRoot in sourceRoots)
            {
                if (emitted.Add(sourceRoot))
                {
                    yield return sourceRoot;
                }
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

            var openDocViewerRoot = Path.Combine(workspaceRoot, "OpenDocViewer");
            if (File.Exists(Path.Combine(openDocViewerRoot, "omp-components.json")) && emitted.Add(openDocViewerRoot))
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
                    GetJsonStringProperty(item, "relativePathTemplate")))
                .Where(static item => item.HasCompleteArtifactIdentity)
                .ToArray();
        }

        private string? ResolveDeveloperSourceRoot(bool throwIfMissing)
        {
            foreach (var sourceRoot in ParseConfiguredDeveloperSourceRoots(_config.DeveloperSource.SourceRoot))
            {
                var resolved = Path.GetFullPath(sourceRoot);
                if (IsDeveloperSourceRoot(resolved))
                {
                    return resolved;
                }
            }

            foreach (var start in GetDeveloperSourceSearchStarts())
            {
                foreach (var candidate in EnumerateSelfAndParents(start))
                {
                    if (IsDeveloperSourceRoot(candidate))
                    {
                        return candidate;
                    }
                }
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
                    .Where(root => File.Exists(Path.Combine(root, "omp-components.json")))
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
            => File.Exists(Path.Combine(path, "omp-components.json"))
                && File.Exists(Path.Combine(path, "OpenModulePlatform.slnx"))
                && File.Exists(Path.Combine(path, "scripts", "deployment", "package-hostagent-first.ps1"));

        private async Task SaveCurrentConfigAsync()
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
                ExitCode = await Task.Run(() => operation().GetAwaiter().GetResult());
                SetReadyStatus(ExitCode == 0 ? successText : incompleteText);

                MessageBox.Show(
                    ExitCode == 0 ? successText : incompleteText,
                    "OpenModulePlatform installer",
                    MessageBoxButtons.OK,
                    ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ExitCode = 1;
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
            _installButton.Enabled = enabled;
            _checkSourceButton.Enabled = enabled;
            _syncPackageObjectsButton.Enabled = enabled;
            _createUpdatedInstallerPackageButton.Enabled = enabled;
            _uninstallRuntimeButton.Enabled = enabled;
            _cleanUninstallButton.Enabled = enabled;
            _fullUninstallButton.Enabled = enabled;
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
        string RelativePathTemplate)
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
