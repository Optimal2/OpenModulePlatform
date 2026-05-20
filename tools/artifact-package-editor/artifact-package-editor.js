(() => {
    const textEncoder = new TextEncoder();
    const filenameTokenPattern = /^[A-Za-z0-9][A-Za-z0-9._+-]*$/;

    let packageModel = createEmptyPackage();
    let payloadFile = null;
    let currentFileName = "omp-artifact-package.json";

    const elements = {
        manifestInput: document.getElementById("manifestInput"),
        newButton: document.getElementById("newButton"),
        downloadManifestButton: document.getElementById("downloadManifestButton"),
        downloadPackageButton: document.getElementById("downloadPackageButton"),
        addConfigButton: document.getElementById("addConfigButton"),
        applyPreviewButton: document.getElementById("applyPreviewButton"),
        refreshPreviewButton: document.getElementById("refreshPreviewButton"),
        message: document.getElementById("message"),
        moduleKey: document.getElementById("moduleKey"),
        appKey: document.getElementById("appKey"),
        packageType: document.getElementById("packageType"),
        targetName: document.getElementById("targetName"),
        version: document.getElementById("version"),
        formatVersion: document.getElementById("formatVersion"),
        filenamePreview: document.getElementById("filenamePreview"),
        payloadInput: document.getElementById("payloadInput"),
        payloadSummary: document.getElementById("payloadSummary"),
        configFiles: document.getElementById("configFiles"),
        preview: document.getElementById("preview"),
        configTemplate: document.getElementById("configTemplate")
    };

    function createEmptyPackage() {
        return {
            identity: {
                moduleKey: "",
                appKey: "",
                packageType: "web-app",
                targetName: "",
                version: ""
            },
            manifest: {
                formatVersion: 1,
                payload: {
                    type: "zip",
                    path: "payload/artifact.zip"
                },
                configurationFiles: []
            },
            configurationContent: []
        };
    }

    function showMessage(text, isError = false) {
        elements.message.textContent = text;
        elements.message.classList.toggle("error", isError);
        elements.message.hidden = false;
    }

    function clearMessage() {
        elements.message.hidden = true;
        elements.message.textContent = "";
        elements.message.classList.remove("error");
    }

    function formatBytes(bytes) {
        if (!Number.isFinite(bytes)) {
            return "";
        }

        if (bytes < 1024) {
            return `${bytes} bytes`;
        }

        if (bytes < 1024 * 1024) {
            return `${(bytes / 1024).toFixed(1)} KB`;
        }

        return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
    }

    function normalizeZipPath(value) {
        return (value || "").trim().replaceAll("\\", "/").replace(/^\/+|\/+$/g, "");
    }

    function normalizeSourcePath(value) {
        const path = normalizeZipPath(value);
        if (!path) {
            return "";
        }

        if (isUnsafeRelativePath(path)) {
            throw new Error(`Package source path must stay inside the package: ${path}`);
        }

        return path;
    }

    function isUnsafeRelativePath(path) {
        return path.includes(":")
            || path.includes("\0")
            || path.split("/").some(segment => segment.length === 0 || segment === "." || segment === "..");
    }

    function sourcePathFromRelativePath(relativePath, index) {
        const fallback = `config-${index + 1}.txt`;
        const normalized = normalizeZipPath(relativePath);
        const fileName = normalized.split("/").filter(Boolean).pop() || fallback;
        const safeName = fileName.replace(/[^A-Za-z0-9._+-]/g, "-") || fallback;
        return `configuration/${index + 1}-${safeName}`;
    }

    function getArtifactFileName() {
        const identity = readIdentityFromForm();
        return `${identity.moduleKey}__${identity.appKey}__${identity.packageType}__${identity.targetName}__${identity.version}.zip`;
    }

    function readIdentityFromForm() {
        return {
            moduleKey: elements.moduleKey.value.trim(),
            appKey: elements.appKey.value.trim(),
            packageType: elements.packageType.value.trim(),
            targetName: elements.targetName.value.trim(),
            version: elements.version.value.trim()
        };
    }

    function validateIdentity(identity) {
        const errors = [];
        for (const [name, value] of Object.entries(identity)) {
            if (!value) {
                errors.push(`${name} is required.`);
            } else if (!filenameTokenPattern.test(value)) {
                errors.push(`${name} must match ${filenameTokenPattern.source}.`);
            }
        }

        return errors;
    }

    function updatePayloadSummary() {
        elements.payloadSummary.textContent = payloadFile
            ? `${payloadFile.name} (${formatBytes(payloadFile.size)})`
            : "No payload selected.";
    }

    function updateFilenamePreview() {
        try {
            elements.filenamePreview.textContent = getArtifactFileName();
        } catch {
            elements.filenamePreview.textContent = "";
        }
    }

    function populateForm() {
        const identity = packageModel.identity || {};
        const manifest = packageModel.manifest || {};
        elements.moduleKey.value = identity.moduleKey || "";
        elements.appKey.value = identity.appKey || "";
        elements.packageType.value = identity.packageType || "web-app";
        elements.targetName.value = identity.targetName || "";
        elements.version.value = identity.version || "";
        elements.formatVersion.value = String(manifest.formatVersion || 1);
        updatePayloadSummary();
        renderConfigurationFiles();
        updateFilenamePreview();
        void refreshPreview();
    }

    function renderConfigurationFiles() {
        elements.configFiles.replaceChildren();
        const manifestFiles = packageModel.manifest.configurationFiles || [];
        const contentRows = packageModel.configurationContent || [];

        for (const [index, file] of manifestFiles.entries()) {
            const card = elements.configTemplate.content.firstElementChild.cloneNode(true);
            card.querySelector(".config-title").textContent = file.relativePath || `Configuration file ${index + 1}`;
            card.querySelector(".config-relative-path").value = file.relativePath || "";
            card.querySelector(".config-source-path").value = file.source || file.path || "";
            card.querySelector(".config-content").value = contentRows[index]?.content || "";
            card.querySelector(".remove-config").addEventListener("click", () => {
                updateModelFromForm();
                packageModel.manifest.configurationFiles.splice(index, 1);
                packageModel.configurationContent.splice(index, 1);
                renderConfigurationFiles();
                void refreshPreview();
            });

            const fileInput = card.querySelector(".config-file-input");
            fileInput.addEventListener("change", event => {
                const selected = event.target.files?.[0];
                if (!selected) {
                    return;
                }

                selected.text()
                    .then(text => {
                        const relativePathInput = card.querySelector(".config-relative-path");
                        const sourcePathInput = card.querySelector(".config-source-path");
                        if (!relativePathInput.value.trim()) {
                            relativePathInput.value = selected.name;
                        }

                        if (!sourcePathInput.value.trim()) {
                            sourcePathInput.value = sourcePathFromRelativePath(relativePathInput.value, index);
                        }

                        card.querySelector(".config-content").value = text;
                        updateModelFromForm();
                        renderConfigurationFiles();
                        void refreshPreview();
                        showMessage(`Loaded ${selected.name}.`);
                    })
                    .catch(error => showMessage(error instanceof Error ? error.message : String(error), true));
                fileInput.value = "";
            });

            elements.configFiles.append(card);
        }
    }

    function updateModelFromForm() {
        packageModel.identity = readIdentityFromForm();
        packageModel.manifest.formatVersion = Number.parseInt(elements.formatVersion.value || "1", 10);
        packageModel.manifest.payload = {
            type: "zip",
            path: "payload/artifact.zip"
        };

        packageModel.manifest.configurationFiles = Array.from(elements.configFiles.children).map((card, index) => {
            const relativePath = normalizeZipPath(card.querySelector(".config-relative-path").value);
            const enteredSourcePath = card.querySelector(".config-source-path").value;
            return {
                relativePath,
                source: normalizeSourcePath(enteredSourcePath || sourcePathFromRelativePath(relativePath, index))
            };
        });

        packageModel.configurationContent = Array.from(elements.configFiles.children).map(card => ({
            content: card.querySelector(".config-content").value
        }));
        updateFilenamePreview();
    }

    function createManifest() {
        updateModelFromForm();
        return JSON.parse(JSON.stringify(packageModel.manifest));
    }

    function validateManifest(manifest) {
        const errors = [];
        if (manifest.formatVersion !== 1) {
            errors.push("formatVersion must be 1.");
        }

        if (!manifest.payload || manifest.payload.type !== "zip" || manifest.payload.path !== "payload/artifact.zip") {
            errors.push("payload must be a zip at payload/artifact.zip.");
        }

        const seenRelativePaths = new Set();
        const seenSourcePaths = new Set();
        for (const [index, file] of (manifest.configurationFiles || []).entries()) {
            const relativePath = normalizeZipPath(file.relativePath);
            const source = normalizeZipPath(file.source || file.path);
            if (!relativePath || isUnsafeRelativePath(relativePath)) {
                errors.push(`Configuration file ${index + 1} has an invalid relativePath.`);
            }

            if (!source || isUnsafeRelativePath(source)) {
                errors.push(`Configuration file ${index + 1} has an invalid source.`);
            }

            const relativeKey = relativePath.toLowerCase();
            if (seenRelativePaths.has(relativeKey)) {
                errors.push(`Configuration file ${index + 1} duplicates relativePath ${relativePath}.`);
            }

            seenRelativePaths.add(relativeKey);
            const sourceKey = source.toLowerCase();
            if (seenSourcePaths.has(sourceKey)) {
                errors.push(`Configuration file ${index + 1} duplicates package source ${source}.`);
            }

            seenSourcePaths.add(sourceKey);
        }

        return errors;
    }

    async function refreshPreview() {
        try {
            const manifest = createManifest();
            const errors = [
                ...validateIdentity(packageModel.identity),
                ...validateManifest(manifest)
            ];
            elements.preview.value = JSON.stringify(manifest, null, 2);
            if (errors.length > 0) {
                showMessage(errors.join(" "), true);
            } else {
                clearMessage();
            }
        } catch (error) {
            showMessage(error instanceof Error ? error.message : String(error), true);
        }
    }

    function applyPreviewJson() {
        try {
            const manifest = JSON.parse(elements.preview.value);
            packageModel.manifest = normalizeLoadedManifest(manifest);
            packageModel.configurationContent = packageModel.manifest.configurationFiles.map((_, index) => (
                packageModel.configurationContent[index] || { content: "" }
            ));
            populateForm();
            showMessage("Applied manifest preview to the form.");
        } catch (error) {
            showMessage(error instanceof Error ? error.message : String(error), true);
        }
    }

    function normalizeLoadedManifest(source) {
        const manifest = source && typeof source === "object" ? source : {};
        const payload = manifest.payload && typeof manifest.payload === "object"
            ? manifest.payload
            : {};
        const files = Array.isArray(manifest.configurationFiles)
            ? manifest.configurationFiles
            : [];

        return {
            formatVersion: Number.parseInt(String(manifest.formatVersion || "1"), 10),
            payload: {
                type: payload.type || "zip",
                path: payload.path || "payload/artifact.zip"
            },
            configurationFiles: files.map((file, index) => {
                const relativePath = normalizeZipPath(file?.relativePath || "");
                return {
                    relativePath,
                    source: normalizeZipPath(file?.source || file?.path || sourcePathFromRelativePath(relativePath, index))
                };
            })
        };
    }

    function addConfigurationFile() {
        updateModelFromForm();
        const index = packageModel.manifest.configurationFiles.length;
        packageModel.manifest.configurationFiles.push({
            relativePath: "",
            source: sourcePathFromRelativePath("", index)
        });
        packageModel.configurationContent.push({ content: "" });
        renderConfigurationFiles();
        void refreshPreview();
    }

    function downloadBlob(blob, fileName) {
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        document.body.append(link);
        link.click();
        link.remove();
        window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    async function downloadManifest() {
        try {
            const manifest = createManifest();
            const errors = validateManifest(manifest);
            if (errors.length > 0) {
                showMessage(errors.join(" "), true);
                return;
            }

            const json = JSON.stringify(manifest, null, 2) + "\n";
            downloadBlob(new Blob([json], { type: "application/json" }), "omp-artifact-package.json");
            showMessage("Prepared omp-artifact-package.json.");
        } catch (error) {
            showMessage(error instanceof Error ? error.message : String(error), true);
        }
    }

    async function buildPackageZip() {
        try {
            if (!payloadFile) {
                showMessage("Select a deployable payload zip before building the package.", true);
                return;
            }

            updateModelFromForm();
            const manifest = createManifest();
            const errors = [
                ...validateIdentity(packageModel.identity),
                ...validateManifest(manifest)
            ];
            if (errors.length > 0) {
                showMessage(errors.join(" "), true);
                return;
            }

            const files = [
                {
                    path: "omp-artifact-package.json",
                    data: textEncoder.encode(JSON.stringify(manifest, null, 2) + "\n")
                },
                {
                    path: "payload/artifact.zip",
                    data: new Uint8Array(await payloadFile.arrayBuffer())
                }
            ];

            for (const [index, config] of manifest.configurationFiles.entries()) {
                files.push({
                    path: config.source,
                    data: textEncoder.encode(packageModel.configurationContent[index]?.content || "")
                });
            }

            const zipBytes = createStoredZip(files);
            const fileName = getArtifactFileName();
            downloadBlob(new Blob([zipBytes], { type: "application/zip" }), fileName);
            showMessage(`Prepared ${fileName}.`);
        } catch (error) {
            showMessage(error instanceof Error ? error.message : String(error), true);
        }
    }

    function createStoredZip(files) {
        const localParts = [];
        const centralParts = [];
        let offset = 0;
        const now = new Date();
        const dosTime = toDosTime(now);
        const dosDate = toDosDate(now);

        for (const file of files) {
            const name = normalizeSourcePath(file.path);
            const nameBytes = textEncoder.encode(name);
            const data = file.data instanceof Uint8Array ? file.data : new Uint8Array(file.data);
            if (nameBytes.length > 0xffff || data.length > 0xffffffff) {
                throw new Error(`File is too large for this simple zip writer: ${name}`);
            }

            const crc = crc32(data);
            const localHeader = new Uint8Array(30 + nameBytes.length);
            const local = new DataView(localHeader.buffer);
            writeLocalHeader(local, nameBytes, data.length, crc, dosTime, dosDate);
            localHeader.set(nameBytes, 30);
            localParts.push(localHeader, data);

            const centralHeader = new Uint8Array(46 + nameBytes.length);
            const central = new DataView(centralHeader.buffer);
            writeCentralHeader(central, nameBytes, data.length, crc, dosTime, dosDate, offset);
            centralHeader.set(nameBytes, 46);
            centralParts.push(centralHeader);

            offset += localHeader.length + data.length;
        }

        const centralOffset = offset;
        const centralSize = centralParts.reduce((sum, part) => sum + part.length, 0);
        const end = new Uint8Array(22);
        const endView = new DataView(end.buffer);
        endView.setUint32(0, 0x06054b50, true);
        endView.setUint16(8, files.length, true);
        endView.setUint16(10, files.length, true);
        endView.setUint32(12, centralSize, true);
        endView.setUint32(16, centralOffset, true);

        return concatUint8Arrays([...localParts, ...centralParts, end]);
    }

    function writeLocalHeader(view, nameBytes, size, crc, dosTime, dosDate) {
        view.setUint32(0, 0x04034b50, true);
        view.setUint16(4, 20, true);
        view.setUint16(6, 0x0800, true);
        view.setUint16(8, 0, true);
        view.setUint16(10, dosTime, true);
        view.setUint16(12, dosDate, true);
        view.setUint32(14, crc, true);
        view.setUint32(18, size, true);
        view.setUint32(22, size, true);
        view.setUint16(26, nameBytes.length, true);
    }

    function writeCentralHeader(view, nameBytes, size, crc, dosTime, dosDate, offset) {
        view.setUint32(0, 0x02014b50, true);
        view.setUint16(4, 20, true);
        view.setUint16(6, 20, true);
        view.setUint16(8, 0x0800, true);
        view.setUint16(10, 0, true);
        view.setUint16(12, dosTime, true);
        view.setUint16(14, dosDate, true);
        view.setUint32(16, crc, true);
        view.setUint32(20, size, true);
        view.setUint32(24, size, true);
        view.setUint16(28, nameBytes.length, true);
        view.setUint32(42, offset, true);
    }

    function concatUint8Arrays(parts) {
        const totalLength = parts.reduce((sum, part) => sum + part.length, 0);
        const result = new Uint8Array(totalLength);
        let offset = 0;
        for (const part of parts) {
            result.set(part, offset);
            offset += part.length;
        }

        return result;
    }

    function toDosTime(date) {
        return (date.getHours() << 11) | (date.getMinutes() << 5) | Math.floor(date.getSeconds() / 2);
    }

    function toDosDate(date) {
        return ((date.getFullYear() - 1980) << 9) | ((date.getMonth() + 1) << 5) | date.getDate();
    }

    function crc32(bytes) {
        let crc = 0xffffffff;
        for (const byte of bytes) {
            crc = (crc >>> 8) ^ crcTable[(crc ^ byte) & 0xff];
        }

        return (crc ^ 0xffffffff) >>> 0;
    }

    const crcTable = (() => {
        const table = new Uint32Array(256);
        for (let index = 0; index < 256; index += 1) {
            let value = index;
            for (let bit = 0; bit < 8; bit += 1) {
                value = (value & 1) ? (0xedb88320 ^ (value >>> 1)) : (value >>> 1);
            }

            table[index] = value >>> 0;
        }

        return table;
    })();

    function loadNewPackage() {
        packageModel = createEmptyPackage();
        payloadFile = null;
        currentFileName = "omp-artifact-package.json";
        elements.payloadInput.value = "";
        populateForm();
        showMessage("Started a new artifact package.");
    }

    async function loadManifestFile(file) {
        const text = await file.text();
        packageModel.manifest = normalizeLoadedManifest(JSON.parse(text));
        packageModel.configurationContent = packageModel.manifest.configurationFiles.map(() => ({ content: "" }));
        currentFileName = file.name || "omp-artifact-package.json";
        populateForm();
        showMessage(`Loaded ${currentFileName}.`);
    }

    elements.manifestInput.addEventListener("change", event => {
        const file = event.target.files?.[0];
        if (!file) {
            return;
        }

        loadManifestFile(file).catch(error => showMessage(error instanceof Error ? error.message : String(error), true));
        elements.manifestInput.value = "";
    });

    elements.payloadInput.addEventListener("change", event => {
        payloadFile = event.target.files?.[0] || null;
        updatePayloadSummary();
        void refreshPreview();
    });

    for (const input of [
        elements.moduleKey,
        elements.appKey,
        elements.packageType,
        elements.targetName,
        elements.version,
        elements.formatVersion
    ]) {
        input.addEventListener("input", () => void refreshPreview());
    }

    elements.configFiles.addEventListener("input", () => void refreshPreview());
    elements.newButton.addEventListener("click", loadNewPackage);
    elements.downloadManifestButton.addEventListener("click", () => void downloadManifest());
    elements.downloadPackageButton.addEventListener("click", () => void buildPackageZip());
    elements.addConfigButton.addEventListener("click", addConfigurationFile);
    elements.applyPreviewButton.addEventListener("click", applyPreviewJson);
    elements.refreshPreviewButton.addEventListener("click", () => void refreshPreview());

    populateForm();
})();
