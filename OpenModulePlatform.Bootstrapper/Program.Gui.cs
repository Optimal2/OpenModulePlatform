using System.Drawing;
using System.Text;
using System.Windows.Forms;

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
        private readonly TextBox _logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly Button _installButton = new() { Text = "Install", AutoSize = true };
        private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true };
        private readonly CheckBox _runSql = new() { Text = "Run SQL bootstrap" };
        private readonly CheckBox _installHostAgent = new() { Text = "Install/update HostAgent" };
        private readonly CheckBox _deployWebApps = new() { Text = "Let HostAgent deploy web apps" };
        private readonly CheckBox _ensureIisSite = new() { Text = "Create/update IIS site and app pools" };
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
            MinimumSize = new Size(900, 720);
            Size = new Size(980, 780);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(CreateHeader(), 0, 0);
            root.Controls.Add(CreateSettingsTabs(), 0, 1);

            _logBox.Font = new Font(FontFamily.GenericMonospace, 9);
            root.Controls.Add(_logBox, 0, 2);

            var warning = new Label
            {
                AutoSize = true,
                ForeColor = IsWindowsAdministrator() ? SystemColors.ControlText : Color.DarkRed,
                Text = IsWindowsAdministrator()
                    ? "Review the settings and click Install to start."
                    : "Run this installer as Administrator before installing Windows services or IIS settings."
            };
            root.Controls.Add(warning, 0, 3);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                AutoSize = true
            };
            buttons.Controls.Add(_cancelButton);
            buttons.Controls.Add(_installButton);
            root.Controls.Add(buttons, 0, 4);

            LoadValues();
            _installButton.Click += async (_, _) => await InstallAsync();
            _cancelButton.Click += (_, _) => Close();
        }

        public int ExitCode { get; private set; }

        private Control CreateHeader()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1
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
                Text = $"Config: {_configPath}"
            });
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = $"Payload: {_payloadRoot}"
            });
            return panel;
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

            var optionsPage = new TabPage("Options") { Padding = new Padding(12) };
            var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
            options.Controls.Add(_runSql);
            options.Controls.Add(_installHostAgent);
            options.Controls.Add(_deployWebApps);
            options.Controls.Add(_ensureIisSite);
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

            _runSql.Checked = _config.Sql.Enabled;
            _installHostAgent.Checked = _config.HostAgent.Enabled;
            _deployWebApps.Checked = _config.HostAgent.DeployWebApps;
            _ensureIisSite.Checked = _config.HostAgent.EnsureIisSite;
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

            _config.Sql.Enabled = _runSql.Checked;
            _config.HostAgent.Enabled = _installHostAgent.Checked;
            _config.HostAgent.DeployWebApps = _deployWebApps.Checked;
            _config.HostAgent.EnsureIisSite = _ensureIisSite.Checked;
        }

        private async Task InstallAsync()
        {
            _installButton.Enabled = false;
            _cancelButton.Enabled = false;
            _logBox.Clear();
            ApplyValues();

            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var writer = new TextBoxWriter(_logBox);
            Console.SetOut(writer);
            Console.SetError(writer);

            try
            {
                ExitCode = await Task.Run(() => RunBootstrapAsync(
                    _config,
                    _configPath,
                    _payloadRoot,
                    _payloadZipPath,
                    yes: true).GetAwaiter().GetResult());

                MessageBox.Show(
                    ExitCode == 0 ? "Installation completed." : "Installation did not complete.",
                    "OpenModulePlatform installer",
                    MessageBoxButtons.OK,
                    ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ExitCode = 1;
                writer.WriteLine("Installation failed.");
                writer.WriteLine(ex.Message);
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
                _installButton.Enabled = true;
                _cancelButton.Enabled = true;
            }
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
}
