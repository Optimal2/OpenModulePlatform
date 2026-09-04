// File: OpenModulePlatform.Portal/wwwroot/js/admin-artifact-upload.js
(() => {
    'use strict';

    const fileInput = document.getElementById('Input_ZipFile');
    const appSelect = document.getElementById('Input_AppId');
    const packageType = document.getElementById('Input_PackageType');
    const targetName = document.getElementById('Input_TargetName');
    const version = document.getElementById('Input_Version');
    const relativePath = document.getElementById('Input_RelativePath');
    const tokenPattern = /^[A-Za-z0-9][A-Za-z0-9._+-]*$/;

    const fillBlank = (element, value) => {
        if (element && !element.value && value) {
            element.value = value;
        }
    };

    const pathSegment = value =>
        value.trim().replace(/\s+/g, '-').replace(/[^A-Za-z0-9._+-]/g, '-');

    const packagePathSegment = value => {
        switch (value.trim().toLowerCase()) {
            case 'web-app':
                return 'web';
            case 'service-app':
                return 'service';
            case 'host-agent':
                return 'hostagent';
            case 'worker-host':
                return 'host';
            case 'worker':
            case 'worker-plugin':
                return 'worker';
            default:
                return pathSegment(value);
        }
    };

    const defaultRelativePath = metadata => {
        let targetSegment = pathSegment(metadata.targetName);
        let packageSegment = packagePathSegment(metadata.packageType);

        if ((packageSegment === 'web' || packageSegment === 'service')
            && targetSegment.toLowerCase().endsWith(`-${packageSegment}`)) {
            targetSegment = targetSegment.slice(0, -1 * (`-${packageSegment}`.length));
        } else if (packageSegment === 'service' && targetSegment.toLowerCase().endsWith('-backend')) {
            // Match existing installer artifact roots such as ikrock2/backend,
            // even when the upload target uses the friendlier ikrock2-backend.
            targetSegment = targetSegment.slice(0, -1 * '-backend'.length);
            packageSegment = 'backend';
        }

        return `${targetSegment}/${packageSegment}/${pathSegment(metadata.version)}`;
    };

    const parseMetadata = name => {
        if (!name || !name.toLowerCase().endsWith('.zip')) {
            return null;
        }

        const baseName = name.slice(0, -4);
        const parts = baseName.split('__');
        if (parts.length !== 5 || parts.some(part => !tokenPattern.test(part))) {
            return null;
        }

        return {
            moduleKey: parts[0],
            appKey: parts[1],
            packageType: parts[2],
            targetName: parts[3],
            version: parts[4]
        };
    };

    const selectApp = metadata => {
        if (!appSelect || appSelect.value) {
            return;
        }

        for (const option of appSelect.options) {
            if ((option.dataset.moduleKey || '').toLowerCase() === metadata.moduleKey.toLowerCase()
                && (option.dataset.appKey || '').toLowerCase() === metadata.appKey.toLowerCase()) {
                appSelect.value = option.value;
                return;
            }
        }
    };

    fileInput?.addEventListener('change', () => {
        const metadata = parseMetadata(fileInput.files?.[0]?.name || '');
        if (!metadata) {
            return;
        }

        selectApp(metadata);
        fillBlank(packageType, metadata.packageType);
        fillBlank(targetName, metadata.targetName);
        fillBlank(version, metadata.version);
        fillBlank(relativePath, defaultRelativePath(metadata));
    });
})();
