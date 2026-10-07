using OpenModulePlatform.Installation;
using OpenModulePlatform.Installer.Core;
using OpenModulePlatform.Installer.Install;

namespace OpenModulePlatform.Installer.Ui;

/// <summary>
/// The installer's single window: confirmation card, prerequisite panel with
/// per-line green/red status, then the install progress list. No upgrade,
/// repair, uninstall or package functions exist here by design.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly Label _heading;
    private readonly Panel _messagePanel;
    private readonly Label _messageText;
    private readonly Button _messageClose;

    private readonly Panel _confirmPanel;
    private readonly TableLayoutPanel _cardTable;
    private readonly Button _confirmContinue;
    private readonly Button _confirmClose;

    private readonly Panel _prereqPanel;
    private readonly Label _prereqSummary;
    private readonly ListView _checkList;
    private readonly Label _passwordLabel;
    private readonly TextBox _passwordBox;
    private readonly Button _passwordVerify;
    private readonly Label _passwordStatus;
    private readonly Button _recheckButton;
    private readonly Button _installButton;
    private readonly Button _prereqClose;

    private readonly Panel _installPanel;
    private readonly Label _installStatus;
    private readonly ListBox _installLog;
    private readonly Label _logFileLabel;
    private readonly Button _installClose;

    private InstallerSession? _session;
    private PrerequisiteEvaluation? _evaluation;
    private InstallSessionLog? _log;
    private bool _passwordValidated;
    private bool _installRunning;

    public MainForm()
    {
        Text = UiText.Get("AppTitle");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ClientSize = new Size(780, 640);
        Font = new Font("Segoe UI", 9.75f);

        _heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0),
            Font = new Font(Font.FontFamily, 14f, FontStyle.Bold)
        };
        Controls.Add(_heading);

        _messagePanel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(16) };
        _messageText = new Label { Dock = DockStyle.Top, Height = 220, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true };
        _messageClose = new Button { Text = UiText.Get("ButtonClose"), Width = 110, Height = 32, Top = 240, Left = 0 };
        _messageClose.Click += (_, _) => Close();
        _messagePanel.Controls.Add(_messageClose);
        _messagePanel.Controls.Add(_messageText);
        Controls.Add(_messagePanel);

        _confirmPanel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(16) };
        _cardTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 300,
            ColumnCount = 2,
            AutoSize = false
        };
        _cardTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        _cardTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _confirmContinue = new Button { Width = 120, Height = 34, Top = 330, Left = 0 };
        _confirmClose = new Button { Text = UiText.Get("ButtonClose"), Width = 110, Height = 34, Top = 330, Left = 130 };
        _confirmContinue.Click += OnConfirmContinue;
        _confirmClose.Click += (_, _) => Close();
        _confirmPanel.Controls.Add(_confirmContinue);
        _confirmPanel.Controls.Add(_confirmClose);
        _confirmPanel.Controls.Add(_cardTable);
        Controls.Add(_confirmPanel);

        _prereqPanel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(16) };
        _prereqSummary = new Label { Dock = DockStyle.Top, Height = 40 };
        _checkList = new ListView
        {
            Dock = DockStyle.Top,
            Height = 260,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None,
            MultiSelect = false
        };
        _checkList.Columns.Add("", 300);
        _checkList.Columns.Add("", 430);
        _passwordLabel = new Label { Top = 312, Left = 0, Width = 320, Height = 24, Visible = false };
        _passwordBox = new TextBox { Top = 336, Left = 0, Width = 260, UseSystemPasswordChar = true, Visible = false };
        _passwordVerify = new Button { Width = 150, Height = 28, Top = 334, Left = 270, Visible = false };
        _passwordStatus = new Label { Top = 366, Left = 0, Width = 720, Height = 24, Visible = false };
        _passwordVerify.Click += OnVerifyPassword;
        _recheckButton = new Button { Width = 140, Height = 34, Top = 400, Left = 0 };
        _installButton = new Button { Width = 140, Height = 34, Top = 400, Left = 150, Enabled = false };
        _prereqClose = new Button { Text = UiText.Get("ButtonClose"), Width = 110, Height = 34, Top = 400, Left = 300 };
        _recheckButton.Click += async (_, _) => await RefreshPrerequisitesAsync();
        _installButton.Click += async (_, _) => await RunInstallAsync();
        _prereqClose.Click += (_, _) => Close();
        _prereqPanel.Controls.Add(_recheckButton);
        _prereqPanel.Controls.Add(_installButton);
        _prereqPanel.Controls.Add(_prereqClose);
        _prereqPanel.Controls.Add(_passwordStatus);
        _prereqPanel.Controls.Add(_passwordVerify);
        _prereqPanel.Controls.Add(_passwordBox);
        _prereqPanel.Controls.Add(_passwordLabel);
        _prereqPanel.Controls.Add(_checkList);
        _prereqPanel.Controls.Add(_prereqSummary);
        Controls.Add(_prereqPanel);

        _installPanel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(16) };
        _installStatus = new Label { Dock = DockStyle.Top, Height = 30 };
        _installLog = new ListBox { Dock = DockStyle.Top, Height = 470, IntegralHeight = false };
        _logFileLabel = new Label { Top = 508, Left = 0, Width = 740, Height = 40, AutoEllipsis = true };
        _installClose = new Button { Text = UiText.Get("ButtonClose"), Width = 110, Height = 34, Top = 552, Left = 0, Enabled = false };
        _installClose.Click += (_, _) => Close();
        _installPanel.Controls.Add(_installClose);
        _installPanel.Controls.Add(_logFileLabel);
        _installPanel.Controls.Add(_installLog);
        _installPanel.Controls.Add(_installStatus);
        Controls.Add(_installPanel);

        _heading.Text = UiText.Get("AppTitle");
        _recheckButton.Text = UiText.Get("ButtonRecheck");
        _installButton.Text = UiText.Get("ButtonInstall");
        _passwordVerify.Text = UiText.Get("ButtonVerifyPassword");

        Shown += async (_, _) => await ResolveProfileAsync();
        FormClosing += OnFormClosing;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_installRunning)
        {
            e.Cancel = true;
        }
    }

    private void ShowOnly(Panel panel)
    {
        _messagePanel.Visible = panel == _messagePanel;
        _confirmPanel.Visible = panel == _confirmPanel;
        _prereqPanel.Visible = panel == _prereqPanel;
        _installPanel.Visible = panel == _installPanel;
    }

    private void ShowMessage(string heading, string message)
    {
        _heading.Text = heading;
        _messageText.Text = message;
        ShowOnly(_messagePanel);
    }

    private async Task ResolveProfileAsync()
    {
        var resolution = await Task.Run(() => InstallerProfileResolver.DiscoverAndResolve());
        switch (resolution.Outcome)
        {
            case ProfileMatchOutcome.NoProfilesFound:
                ShowMessage(UiText.Get("AppTitle"), UiText.Get("NoProfilesMessage"));
                return;
            case ProfileMatchOutcome.NoMatch:
                ShowMessage(
                    UiText.Get("AppTitle"),
                    UiText.Format("NoMatchMessage", Environment.MachineName)
                        + Environment.NewLine + Environment.NewLine
                        + UiText.Get("NoMatchHint"));
                return;
            case ProfileMatchOutcome.MultipleMatches:
                ShowMessage(
                    UiText.Get("AppTitle"),
                    UiText.Format(
                        "MultipleMatchMessage",
                        string.Join(", ", resolution.MatchingProfiles.Select(profile => profile.DisplayName))));
                return;
        }

        var session = await InstallerSessionLoader.LoadAsync(resolution.Profile!);
        var alreadyInstalled = await Task.Run(() => AlreadyInstalledDetector.IsAlreadyInstalled(session.Config));
        if (alreadyInstalled)
        {
            ShowMessage(UiText.Get("AppTitle"), UiText.Get("AlreadyInstalledMessage"));
            return;
        }

        _session = session;
        _heading.Text = UiText.Get("ConfirmHeading");
        _cardTable.Controls.Clear();
        _cardTable.RowStyles.Clear();
        var row = 0;
        foreach (var (label, value) in ProfileSummaryBuilder.Build(session, Environment.MachineName))
        {
            _cardTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            _cardTable.Controls.Add(
                new Label
                {
                    Text = UiText.CardLabel(label),
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font(Font, FontStyle.Bold)
                },
                0,
                row);
            _cardTable.Controls.Add(
                new Label
                {
                    Text = string.IsNullOrWhiteSpace(value) ? "-" : value,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true
                },
                1,
                row);
            row++;
        }

        var intro = new Label
        {
            Text = UiText.Get("ConfirmIntro"),
            Dock = DockStyle.Top,
            Height = 30
        };
        _confirmPanel.Controls.Add(intro);
        intro.BringToFront();
        _confirmContinue.Text = UiText.Get("ButtonContinue");
        _confirmContinue.Top = 70 + (row * 34);
        _confirmClose.Top = _confirmContinue.Top;
        ShowOnly(_confirmPanel);
    }

    private async void OnConfirmContinue(object? sender, EventArgs e)
    {
        _heading.Text = UiText.Get("PrereqHeading");
        ShowOnly(_prereqPanel);
        await RefreshPrerequisitesAsync();
    }

    private async Task RefreshPrerequisitesAsync()
    {
        if (_session is null)
        {
            return;
        }

        _recheckButton.Enabled = false;
        _installButton.Enabled = false;
        _checkList.Items.Clear();
        try
        {
            var evaluation = await Task.Run(() => PrerequisiteEvaluator.EvaluateAsync(
                _session.Config,
                _session.PayloadRoot,
                new WindowsPrerequisiteEnvironment(),
                _passwordValidated));
            _evaluation = evaluation;

            foreach (var check in evaluation.Checks)
            {
                var item = new ListViewItem(UiText.CheckTitle(check.Id));
                item.SubItems.Add(check.Detail);
                item.ForeColor = check.IsSatisfied ? Color.DarkGreen : Color.Firebrick;
                _checkList.Items.Add(item);
            }

            var missing = evaluation.Checks.Where(check => !check.IsSatisfied).ToArray();
            var blocking = missing.Where(check =>
                !check.CanAutoFix && check.Id != PrerequisiteCheckId.ServiceAccount).ToArray();
            _prereqSummary.Text = missing.Length == 0
                ? UiText.Get("PrereqAllOk")
                : blocking.Length > 0
                    ? UiText.Get("PrereqBlocking")
                    : UiText.Get("PrereqAutoFix");

            var needsPassword = evaluation.ServiceAccount.RequiresPassword;
            _passwordLabel.Visible = needsPassword;
            _passwordBox.Visible = needsPassword;
            _passwordVerify.Visible = needsPassword;
            _passwordStatus.Visible = needsPassword;
            if (needsPassword)
            {
                _passwordLabel.Text = UiText.Format("PasswordFor", evaluation.ServiceAccount.DisplayName);
                _passwordStatus.Text = _passwordValidated ? UiText.Get("PasswordVerified") : string.Empty;
            }

            _installButton.Enabled = evaluation.CanInstall;
        }
        finally
        {
            _recheckButton.Enabled = true;
        }
    }

    private void OnVerifyPassword(object? sender, EventArgs e)
    {
        var account = _evaluation?.ServiceAccount;
        if (account is null || !account.RequiresPassword)
        {
            return;
        }

        var ok = AccountPasswordValidator.Validate(
            account.LogonUserName,
            account.LogonDomain,
            _passwordBox.Text,
            out var error);
        _passwordValidated = ok;
        _passwordStatus.Text = ok
            ? UiText.Get("PasswordVerified")
            : UiText.Format("PasswordRejected", error);
        // Mirrors PrerequisiteEvaluation.CanInstall: every red line except the
        // service-account line (which this verification satisfies) must be
        // something the installer fixes itself.
        _installButton.Enabled = ok && _evaluation is not null
            && _evaluation.Checks.Where(check => !check.IsSatisfied)
                .All(check => check.CanAutoFix || check.Id == PrerequisiteCheckId.ServiceAccount);
    }

    private async Task RunInstallAsync()
    {
        if (_session is null || _evaluation is null)
        {
            return;
        }

        _log?.Dispose();
        _log = InstallSessionLog.CreateNextToExecutable();
        var uiProgress = new UiInstallProgress(this);
        var progress = new CompositeInstallProgress([uiProgress, _log]);

        _heading.Text = UiText.Get("AppTitle");
        _installStatus.Text = UiText.Get("InstallInProgress");
        _installLog.Items.Clear();
        _logFileLabel.Text = UiText.Format("LogFileLabel", _log.Path);
        _installClose.Enabled = false;
        ShowOnly(_installPanel);
        _installRunning = true;

        var request = new InstallRequest(
            _session.Config,
            _session.Profile.ConfigPath,
            _session.PayloadRoot,
            _evaluation,
            _passwordBox.Text);
        try
        {
            await Task.Run(() => InstallOrchestrator.RunAsync(
                request,
                new WindowsInstallActions(),
                progress,
                dryRun: false));
            _installStatus.Text = UiText.Get("InstallDone");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or SystemException or IOException)
        {
            progress.Error(ex.Message);
            _installStatus.Text = UiText.Format("InstallFailed", ex.Message);
        }
        finally
        {
            _installRunning = false;
            _installClose.Enabled = true;
        }
    }

    private sealed class UiInstallProgress(MainForm form) : IInstallProgress
    {
        public void Info(string message) => Append(message);

        public void Error(string message) => Append("ERROR: " + message);

        private void Append(string message)
        {
            if (form.IsDisposed)
            {
                return;
            }

            try
            {
                form.BeginInvoke(() =>
                {
                    form._installLog.Items.Add(message);
                    form._installLog.TopIndex = Math.Max(0, form._installLog.Items.Count - 1);
                });
            }
            catch (InvalidOperationException)
            {
                // The window is closing; the log file still has every line.
            }
        }
    }
}
