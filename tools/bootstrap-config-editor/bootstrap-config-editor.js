const fields = {
    profileDisplayName: document.getElementById('profileDisplayName'),
    profileMachineNames: document.getElementById('profileMachineNames'),
    sqlServer: document.getElementById('sqlServer'),
    sqlDatabase: document.getElementById('sqlDatabase'),
    bootstrapPrincipal: document.getElementById('bootstrapPrincipal'),
    sqlTimeout: document.getElementById('sqlTimeout'),
    sqlEnabled: document.getElementById('sqlEnabled'),
    integratedSecurity: document.getElementById('integratedSecurity'),
    trustServerCertificate: document.getElementById('trustServerCertificate'),
    createDatabase: document.getElementById('createDatabase'),
    artifactStoreRoot: document.getElementById('artifactStoreRoot'),
    hostAgentInstallPath: document.getElementById('hostAgentInstallPath'),
    localArtifactCacheRoot: document.getElementById('localArtifactCacheRoot'),
    webAppsRoot: document.getElementById('webAppsRoot'),
    portalPhysicalPath: document.getElementById('portalPhysicalPath'),
    servicesRoot: document.getElementById('servicesRoot'),
    serviceName: document.getElementById('serviceName'),
    serviceAccountName: document.getElementById('serviceAccountName'),
    serviceAccountPassword: document.getElementById('serviceAccountPassword'),
    hostKey: document.getElementById('hostKey'),
    hostName: document.getElementById('hostName'),
    iisSiteName: document.getElementById('iisSiteName'),
    iisBindingPort: document.getElementById('iisBindingPort'),
    iisBindingHostHeader: document.getElementById('iisBindingHostHeader'),
    iisAppPoolNamePrefix: document.getElementById('iisAppPoolNamePrefix'),
    iisAppPoolUserName: document.getElementById('iisAppPoolUserName'),
    iisAppPoolPassword: document.getElementById('iisAppPoolPassword'),
    hostAgentEnabled: document.getElementById('hostAgentEnabled'),
    deployWebApps: document.getElementById('deployWebApps'),
    ensureIisSite: document.getElementById('ensureIisSite'),
    deployServiceApps: document.getElementById('deployServiceApps'),
    developerSourceRoot: document.getElementById('developerSourceRoot'),
    packageConfigPath: document.getElementById('packageConfigPath'),
    packageOutputRoot: document.getElementById('packageOutputRoot')
};

const fileInput = document.getElementById('fileInput');
const newButton = document.getElementById('newButton');
const downloadButton = document.getElementById('downloadButton');
const applyPreviewButton = document.getElementById('applyPreviewButton');
const jsonPreview = document.getElementById('jsonPreview');
const message = document.getElementById('message');

function createDefaultConfig() {
    return {
        profile: {
            displayName: '',
            machineNames: []
        },
        sql: {
            enabled: true,
            server: 'localhost',
            database: 'OpenModulePlatform',
            integratedSecurity: true,
            trustServerCertificate: true,
            createDatabase: false,
            commandTimeoutSeconds: 3600,
            bootstrapPortalAdminPrincipal: '',
            bootstrapPortalAdminPrincipalType: 'ADUser',
            scripts: [],
            artifactVersionOverrides: {},
            artifactVersionVariableOverrides: {}
        },
        developerSource: {
            sourceRoot: '',
            packageConfigPath: '',
            packageOutputRoot: ''
        },
        artifactStoreRoot: 'E:\\OMP\\ArtifactStore',
        includeExampleApps: true,
        artifacts: [],
        hostAgent: {
            enabled: true,
            serviceName: 'OpenModulePlatform.HostAgent',
            additionalServiceNamesToRemove: [],
            displayName: 'OpenModulePlatform HostAgent',
            description: 'OpenModulePlatform artifact provisioning agent.',
            serviceAccountName: '',
            serviceAccountPassword: '',
            installPath: 'E:\\OMP\\Services\\HostAgent',
            packagePath: 'payload\\OpenModulePlatform.HostAgent.zip',
            backupExistingInstall: true,
            startService: true,
            settingsFileName: 'appsettings.Production.json',
            localArtifactCacheRoot: 'E:\\OMP\\ArtifactCache',
            hostKey: '',
            hostName: '',
            refreshSeconds: 30,
            deployWebApps: true,
            iisSiteName: 'OpenModulePlatform',
            ensureIisSite: true,
            iisBindingProtocol: 'http',
            iisBindingPort: 8088,
            iisBindingHostHeader: '',
            webAppsRoot: 'E:\\OMP\\WebApps',
            portalPhysicalPath: 'E:\\OMP\\Sites\\Portal',
            iisAppPoolNamePrefix: 'OMP_',
            iisAppPoolUserName: '',
            iisAppPoolPassword: '',
            iisAppPoolOverrides: {},
            deployServiceApps: true,
            servicesRoot: 'E:\\OMP\\Services'
        }
    };
}

let currentConfig = createDefaultConfig();

function showMessage(text, isError = false) {
    message.textContent = text;
    message.classList.toggle('error', isError);
    message.hidden = false;
}

function hideMessage() {
    message.hidden = true;
}

function splitList(value) {
    return value
        .split(';')
        .map(item => item.trim())
        .filter(Boolean);
}

function joinList(values) {
    return Array.isArray(values) ? values.join(';') : '';
}

function getNumber(input, fallback) {
    const parsed = Number.parseInt(input.value, 10);
    return Number.isFinite(parsed) ? parsed : fallback;
}

function loadConfig(config) {
    currentConfig = structuredClone(config);
    const sql = currentConfig.sql ?? {};
    const hostAgent = currentConfig.hostAgent ?? {};
    const profile = currentConfig.profile ?? {};
    const developerSource = currentConfig.developerSource ?? {};

    fields.profileDisplayName.value = profile.displayName ?? '';
    fields.profileMachineNames.value = joinList(profile.machineNames);
    fields.sqlServer.value = sql.server ?? 'localhost';
    fields.sqlDatabase.value = sql.database ?? 'OpenModulePlatform';
    fields.bootstrapPrincipal.value = sql.bootstrapPortalAdminPrincipal ?? '';
    fields.sqlTimeout.value = String(sql.commandTimeoutSeconds ?? 3600);
    fields.sqlEnabled.checked = sql.enabled !== false;
    fields.integratedSecurity.checked = sql.integratedSecurity !== false;
    fields.trustServerCertificate.checked = sql.trustServerCertificate !== false;
    fields.createDatabase.checked = Boolean(sql.createDatabase);
    fields.artifactStoreRoot.value = currentConfig.artifactStoreRoot ?? '';
    fields.hostAgentInstallPath.value = hostAgent.installPath ?? '';
    fields.localArtifactCacheRoot.value = hostAgent.localArtifactCacheRoot ?? '';
    fields.webAppsRoot.value = hostAgent.webAppsRoot ?? '';
    fields.portalPhysicalPath.value = hostAgent.portalPhysicalPath ?? '';
    fields.servicesRoot.value = hostAgent.servicesRoot ?? '';
    fields.serviceName.value = hostAgent.serviceName ?? '';
    fields.serviceAccountName.value = hostAgent.serviceAccountName ?? '';
    fields.serviceAccountPassword.value = hostAgent.serviceAccountPassword ?? '';
    fields.hostKey.value = hostAgent.hostKey ?? '';
    fields.hostName.value = hostAgent.hostName ?? '';
    fields.iisSiteName.value = hostAgent.iisSiteName ?? '';
    fields.iisBindingPort.value = String(hostAgent.iisBindingPort ?? 8088);
    fields.iisBindingHostHeader.value = hostAgent.iisBindingHostHeader ?? '';
    fields.iisAppPoolNamePrefix.value = hostAgent.iisAppPoolNamePrefix ?? 'OMP_';
    fields.iisAppPoolUserName.value = hostAgent.iisAppPoolUserName ?? '';
    fields.iisAppPoolPassword.value = hostAgent.iisAppPoolPassword ?? '';
    fields.hostAgentEnabled.checked = hostAgent.enabled !== false;
    fields.deployWebApps.checked = hostAgent.deployWebApps !== false;
    fields.ensureIisSite.checked = Boolean(hostAgent.ensureIisSite);
    fields.deployServiceApps.checked = hostAgent.deployServiceApps !== false;
    fields.developerSourceRoot.value = developerSource.sourceRoot ?? '';
    fields.packageConfigPath.value = developerSource.packageConfigPath ?? '';
    fields.packageOutputRoot.value = developerSource.packageOutputRoot ?? '';
    updatePreview();
}

function buildConfigFromForm() {
    const config = structuredClone(currentConfig);
    config.profile = config.profile ?? {};
    config.sql = config.sql ?? {};
    config.developerSource = config.developerSource ?? {};
    config.hostAgent = config.hostAgent ?? {};

    config.profile.displayName = fields.profileDisplayName.value.trim();
    config.profile.machineNames = splitList(fields.profileMachineNames.value);
    config.sql.enabled = fields.sqlEnabled.checked;
    config.sql.server = fields.sqlServer.value.trim();
    config.sql.database = fields.sqlDatabase.value.trim();
    config.sql.integratedSecurity = fields.integratedSecurity.checked;
    config.sql.trustServerCertificate = fields.trustServerCertificate.checked;
    config.sql.createDatabase = fields.createDatabase.checked;
    config.sql.commandTimeoutSeconds = getNumber(fields.sqlTimeout, 3600);
    config.sql.bootstrapPortalAdminPrincipal = fields.bootstrapPrincipal.value.trim();
    config.sql.bootstrapPortalAdminPrincipalType = config.sql.bootstrapPortalAdminPrincipalType || 'ADUser';
    config.sql.scripts = config.sql.scripts ?? [];
    config.sql.artifactVersionOverrides = config.sql.artifactVersionOverrides ?? {};
    config.sql.artifactVersionVariableOverrides = config.sql.artifactVersionVariableOverrides ?? {};

    config.developerSource.sourceRoot = fields.developerSourceRoot.value.trim();
    config.developerSource.packageConfigPath = fields.packageConfigPath.value.trim();
    config.developerSource.packageOutputRoot = fields.packageOutputRoot.value.trim();
    config.artifactStoreRoot = fields.artifactStoreRoot.value.trim();
    config.includeExampleApps = config.includeExampleApps !== false;
    config.artifacts = config.artifacts ?? [];

    config.hostAgent.enabled = fields.hostAgentEnabled.checked;
    config.hostAgent.serviceName = fields.serviceName.value.trim();
    config.hostAgent.additionalServiceNamesToRemove = config.hostAgent.additionalServiceNamesToRemove ?? [];
    config.hostAgent.displayName = config.hostAgent.displayName || 'OpenModulePlatform HostAgent';
    config.hostAgent.description = config.hostAgent.description || 'OpenModulePlatform artifact provisioning agent.';
    config.hostAgent.serviceAccountName = fields.serviceAccountName.value.trim();
    config.hostAgent.serviceAccountPassword = fields.serviceAccountPassword.value;
    config.hostAgent.installPath = fields.hostAgentInstallPath.value.trim();
    config.hostAgent.packagePath = config.hostAgent.packagePath || 'payload\\OpenModulePlatform.HostAgent.zip';
    config.hostAgent.backupExistingInstall = config.hostAgent.backupExistingInstall !== false;
    config.hostAgent.startService = config.hostAgent.startService !== false;
    config.hostAgent.settingsFileName = config.hostAgent.settingsFileName || 'appsettings.Production.json';
    config.hostAgent.localArtifactCacheRoot = fields.localArtifactCacheRoot.value.trim();
    config.hostAgent.hostKey = fields.hostKey.value.trim();
    config.hostAgent.hostName = fields.hostName.value.trim();
    config.hostAgent.refreshSeconds = config.hostAgent.refreshSeconds || 30;
    config.hostAgent.deployWebApps = fields.deployWebApps.checked;
    config.hostAgent.iisSiteName = fields.iisSiteName.value.trim();
    config.hostAgent.ensureIisSite = fields.ensureIisSite.checked;
    config.hostAgent.iisBindingProtocol = config.hostAgent.iisBindingProtocol || 'http';
    config.hostAgent.iisBindingPort = getNumber(fields.iisBindingPort, 8088);
    config.hostAgent.iisBindingHostHeader = fields.iisBindingHostHeader.value.trim();
    config.hostAgent.webAppsRoot = fields.webAppsRoot.value.trim();
    config.hostAgent.portalPhysicalPath = fields.portalPhysicalPath.value.trim();
    config.hostAgent.iisAppPoolNamePrefix = fields.iisAppPoolNamePrefix.value.trim();
    config.hostAgent.iisAppPoolUserName = fields.iisAppPoolUserName.value.trim();
    config.hostAgent.iisAppPoolPassword = fields.iisAppPoolPassword.value;
    config.hostAgent.iisAppPoolOverrides = config.hostAgent.iisAppPoolOverrides ?? {};
    config.hostAgent.deployServiceApps = fields.deployServiceApps.checked;
    config.hostAgent.servicesRoot = fields.servicesRoot.value.trim();
    return config;
}

function validateConfig(config) {
    const errors = [];
    if (!config.profile?.machineNames?.length && !config.hostAgent?.hostName && !config.hostAgent?.hostKey) {
        errors.push('Add at least one profile machine name, hostAgent.hostName, or hostAgent.hostKey so the installer can match this computer.');
    }

    if (!config.sql?.server) {
        errors.push('SQL server is required.');
    }

    if (!config.sql?.database) {
        errors.push('SQL database is required.');
    }

    if (!config.hostAgent?.serviceName) {
        errors.push('HostAgent service name is required.');
    }

    if (!config.hostAgent?.installPath) {
        errors.push('HostAgent install path is required.');
    }

    if (!config.artifactStoreRoot) {
        errors.push('ArtifactStore root is required.');
    }

    return errors;
}

function updatePreview() {
    currentConfig = buildConfigFromForm();
    jsonPreview.value = `${JSON.stringify(currentConfig, null, 2)}\n`;
}

function applyPreview() {
    try {
        const parsed = JSON.parse(jsonPreview.value);
        loadConfig(parsed);
        const errors = validateConfig(parsed);
        showMessage(errors.length ? errors.join(' ') : 'JSON preview applied.');
    } catch (error) {
        showMessage(error.message, true);
    }
}

function downloadJson() {
    updatePreview();
    const errors = validateConfig(currentConfig);
    if (errors.length) {
        showMessage(errors.join(' '), true);
        return;
    }

    const name = (currentConfig.profile?.displayName || currentConfig.hostAgent?.hostName || currentConfig.hostAgent?.hostKey || 'bootstrap')
        .toLowerCase()
        .replace(/[^a-z0-9._-]+/g, '-')
        .replace(/^-+|-+$/g, '') || 'bootstrap';
    const blob = new Blob([jsonPreview.value], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `${name}.json`;
    document.body.append(link);
    link.click();
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    showMessage('Bootstrap config JSON downloaded.');
}

fileInput.addEventListener('change', async () => {
    const file = fileInput.files?.[0];
    if (!file) {
        return;
    }

    try {
        const text = await file.text();
        loadConfig(JSON.parse(text));
        showMessage(`Loaded ${file.name}.`);
    } catch (error) {
        showMessage(error.message, true);
    } finally {
        fileInput.value = '';
    }
});

newButton.addEventListener('click', () => {
    loadConfig(createDefaultConfig());
    hideMessage();
});

downloadButton.addEventListener('click', downloadJson);
applyPreviewButton.addEventListener('click', applyPreview);

for (const field of Object.values(fields)) {
    field.addEventListener('input', updatePreview);
    field.addEventListener('change', updatePreview);
}

loadConfig(createDefaultConfig());
