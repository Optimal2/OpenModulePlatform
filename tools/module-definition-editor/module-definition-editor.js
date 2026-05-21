(() => {
    const textEncoder = new TextEncoder();
    const textDecoder = new TextDecoder();

    let documentModel = createEmptyDocument();
    let currentFileName = "module-definition.json";

    const elements = {
        fileInput: document.getElementById("fileInput"),
        newButton: document.getElementById("newButton"),
        downloadButton: document.getElementById("downloadButton"),
        addAppButton: document.getElementById("addAppButton"),
        addScriptButton: document.getElementById("addScriptButton"),
        applyPreviewButton: document.getElementById("applyPreviewButton"),
        refreshPreviewButton: document.getElementById("refreshPreviewButton"),
        message: document.getElementById("message"),
        moduleKey: document.getElementById("moduleKey"),
        definitionVersion: document.getElementById("definitionVersion"),
        formatVersion: document.getElementById("formatVersion"),
        definitionType: document.getElementById("definitionType"),
        apps: document.getElementById("apps"),
        scripts: document.getElementById("scripts"),
        preview: document.getElementById("preview"),
        appTemplate: document.getElementById("appTemplate"),
        scriptTemplate: document.getElementById("scriptTemplate")
    };

    function createEmptyDocument() {
        return {
            formatVersion: 1,
            moduleKey: "",
            definitionVersion: "",
            module: {
                displayName: "",
                moduleType: "WebApp",
                schemaName: "",
                description: "",
                sortOrder: 100,
                isEnabled: true
            },
            apps: [],
            moduleDependencies: [],
            compatibleArtifacts: [],
            artifactConfigurationFiles: [],
            sqlScripts: [],
            integrity: {
                source: "Created with the OMP module definition editor.",
                requiredSchemas: [],
                requiredTables: [],
                requiredOmpRows: {},
                requiredModuleRows: {},
                excludedSeedData: []
            }
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

    function decodeBase64Utf8(value) {
        if (!value) {
            return "";
        }

        const binary = atob(value);
        const bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index += 1) {
            bytes[index] = binary.charCodeAt(index);
        }

        return textDecoder.decode(bytes);
    }

    function encodeBase64Utf8(value) {
        const bytes = textEncoder.encode(value || "");
        let binary = "";
        const chunkSize = 0x8000;
        for (let index = 0; index < bytes.length; index += chunkSize) {
            binary += String.fromCharCode(...bytes.subarray(index, index + chunkSize));
        }

        return btoa(binary);
    }

    function rightRotate(value, amount) {
        return (value >>> amount) | (value << (32 - amount));
    }

    function sha256FallbackHex(text) {
        const source = textEncoder.encode(text);
        const bitLength = source.length * 8;
        const paddedLength = Math.ceil((source.length + 9) / 64) * 64;
        const bytes = new Uint8Array(paddedLength);
        bytes.set(source);
        bytes[source.length] = 0x80;

        const highLength = Math.floor(bitLength / 0x100000000);
        const lowLength = bitLength >>> 0;
        bytes[paddedLength - 8] = (highLength >>> 24) & 0xff;
        bytes[paddedLength - 7] = (highLength >>> 16) & 0xff;
        bytes[paddedLength - 6] = (highLength >>> 8) & 0xff;
        bytes[paddedLength - 5] = highLength & 0xff;
        bytes[paddedLength - 4] = (lowLength >>> 24) & 0xff;
        bytes[paddedLength - 3] = (lowLength >>> 16) & 0xff;
        bytes[paddedLength - 2] = (lowLength >>> 8) & 0xff;
        bytes[paddedLength - 1] = lowLength & 0xff;

        const k = [
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
        ];

        const h = [
            0x6a09e667,
            0xbb67ae85,
            0x3c6ef372,
            0xa54ff53a,
            0x510e527f,
            0x9b05688c,
            0x1f83d9ab,
            0x5be0cd19
        ];

        const w = new Array(64);
        for (let offset = 0; offset < bytes.length; offset += 64) {
            for (let index = 0; index < 16; index += 1) {
                const position = offset + index * 4;
                w[index] = (
                    (bytes[position] << 24) |
                    (bytes[position + 1] << 16) |
                    (bytes[position + 2] << 8) |
                    bytes[position + 3]
                ) >>> 0;
            }

            for (let index = 16; index < 64; index += 1) {
                const s0 = rightRotate(w[index - 15], 7) ^ rightRotate(w[index - 15], 18) ^ (w[index - 15] >>> 3);
                const s1 = rightRotate(w[index - 2], 17) ^ rightRotate(w[index - 2], 19) ^ (w[index - 2] >>> 10);
                w[index] = (w[index - 16] + s0 + w[index - 7] + s1) >>> 0;
            }

            let a = h[0];
            let b = h[1];
            let c = h[2];
            let d = h[3];
            let e = h[4];
            let f = h[5];
            let g = h[6];
            let hh = h[7];

            for (let index = 0; index < 64; index += 1) {
                const s1 = rightRotate(e, 6) ^ rightRotate(e, 11) ^ rightRotate(e, 25);
                const ch = (e & f) ^ ((~e) & g);
                const temp1 = (hh + s1 + ch + k[index] + w[index]) >>> 0;
                const s0 = rightRotate(a, 2) ^ rightRotate(a, 13) ^ rightRotate(a, 22);
                const maj = (a & b) ^ (a & c) ^ (b & c);
                const temp2 = (s0 + maj) >>> 0;

                hh = g;
                g = f;
                f = e;
                e = (d + temp1) >>> 0;
                d = c;
                c = b;
                b = a;
                a = (temp1 + temp2) >>> 0;
            }

            h[0] = (h[0] + a) >>> 0;
            h[1] = (h[1] + b) >>> 0;
            h[2] = (h[2] + c) >>> 0;
            h[3] = (h[3] + d) >>> 0;
            h[4] = (h[4] + e) >>> 0;
            h[5] = (h[5] + f) >>> 0;
            h[6] = (h[6] + g) >>> 0;
            h[7] = (h[7] + hh) >>> 0;
        }

        return h.map(part => part.toString(16).padStart(8, "0")).join("");
    }

    async function sha256Hex(text) {
        const subtle = globalThis.crypto?.subtle;
        if (subtle) {
            try {
                const hash = await subtle.digest("SHA-256", textEncoder.encode(text));
                return Array.from(new Uint8Array(hash), value => value.toString(16).padStart(2, "0")).join("");
            } catch {
                // file:// execution can restrict Web Crypto in some browsers; the local fallback keeps the editor standalone.
            }
        }

        return sha256FallbackHex(text);
    }

    function getScriptSql(script) {
        if (typeof script.inlineSql === "string" && script.inlineSql.length > 0) {
            return script.inlineSql;
        }

        if (script.contentEncoding === "base64-utf8" && typeof script.content === "string") {
            return decodeBase64Utf8(script.content);
        }

        return typeof script.content === "string" ? script.content : "";
    }

    function normalizeLoadedDocument(source) {
        const loaded = source && typeof source === "object" ? source : createEmptyDocument();
        if (!Array.isArray(loaded.sqlScripts)) {
            loaded.sqlScripts = [];
        }

        if (!Array.isArray(loaded.apps)) {
            loaded.apps = [];
        }

        for (const script of loaded.sqlScripts) {
            script._editorSql = getScriptSql(script);
        }

        return loaded;
    }

    function populateForm() {
        elements.moduleKey.value = documentModel.moduleKey || "";
        elements.definitionVersion.value = documentModel.definitionVersion || "";
        elements.formatVersion.value = String(documentModel.formatVersion || 1);
        elements.definitionType.value = documentModel.definitionType || "";
        renderApps();
        renderScripts();
        void refreshPreview();
    }

    function renderApps() {
        elements.apps.replaceChildren();

        for (const app of documentModel.apps || []) {
            const card = elements.appTemplate.content.firstElementChild.cloneNode(true);
            card.querySelector(".app-title").textContent = app.appKey || app.displayName || "App";
            card.querySelector(".app-key").value = app.appKey || "";
            card.querySelector(".app-display-name").value = app.displayName || "";
            card.querySelector(".app-type").value = app.appType || "";
            card.querySelector(".app-sort-order").value = Number.isFinite(Number(app.sortOrder)) ? String(app.sortOrder) : "";
            card.querySelector(".app-is-enabled").checked = app.isEnabled !== false;
            card.querySelector(".app-allow-multiple-active-instances").checked = app.allowMultipleActiveInstances === true;
            card.querySelector(".app-description").value = app.description || "";
            card.querySelector(".remove-app").addEventListener("click", () => {
                const index = Array.from(elements.apps.children).indexOf(card);
                documentModel.apps.splice(index, 1);
                renderApps();
                void refreshPreview();
            });
            elements.apps.append(card);
        }
    }

    function renderScripts() {
        elements.scripts.replaceChildren();

        for (const script of documentModel.sqlScripts || []) {
            const card = elements.scriptTemplate.content.firstElementChild.cloneNode(true);
            card.querySelector(".script-title").textContent = script.key || "SQL script";
            card.querySelector(".script-key").value = script.key || "";
            card.querySelector(".script-phase").value = script.phase || "";
            card.querySelector(".script-scope").value = script.scope || "";
            card.querySelector(".script-order").value = Number.isFinite(Number(script.order)) ? String(script.order) : "";
            card.querySelector(".script-execution").value = script.execution || "idempotent";
            card.querySelector(".script-path").value = script.path || "";
            card.querySelector(".script-sql").value = script._editorSql || "";
            card.querySelector(".remove-script").addEventListener("click", () => {
                const index = Array.from(elements.scripts.children).indexOf(card);
                documentModel.sqlScripts.splice(index, 1);
                renderScripts();
                void refreshPreview();
            });
            elements.scripts.append(card);
        }
    }

    function updateModelFromForm() {
        documentModel.formatVersion = Number.parseInt(elements.formatVersion.value || "1", 10);
        documentModel.moduleKey = elements.moduleKey.value.trim();
        documentModel.definitionVersion = elements.definitionVersion.value.trim();

        const definitionType = elements.definitionType.value.trim();
        if (definitionType) {
            documentModel.definitionType = definitionType;
        } else {
            delete documentModel.definitionType;
        }

        documentModel.apps = Array.from(elements.apps.children).map(card => ({
            appKey: card.querySelector(".app-key").value.trim(),
            displayName: card.querySelector(".app-display-name").value.trim(),
            appType: card.querySelector(".app-type").value.trim(),
            description: card.querySelector(".app-description").value.trim(),
            sortOrder: Number.parseInt(card.querySelector(".app-sort-order").value || "0", 10),
            isEnabled: card.querySelector(".app-is-enabled").checked,
            allowMultipleActiveInstances: card.querySelector(".app-allow-multiple-active-instances").checked
        }));

        documentModel.sqlScripts = Array.from(elements.scripts.children).map(card => ({
            key: card.querySelector(".script-key").value.trim(),
            phase: card.querySelector(".script-phase").value.trim(),
            scope: card.querySelector(".script-scope").value.trim(),
            order: Number.parseInt(card.querySelector(".script-order").value || "0", 10),
            path: card.querySelector(".script-path").value.trim(),
            execution: card.querySelector(".script-execution").value,
            _editorSql: card.querySelector(".script-sql").value
        }));
    }

    async function createExportDocument() {
        updateModelFromForm();
        const exportDocument = JSON.parse(JSON.stringify(documentModel));

        for (const script of exportDocument.sqlScripts || []) {
            const sql = script._editorSql || "";
            delete script._editorSql;
            script.inlineSql = null;
            script.contentEncoding = "base64-utf8";
            script.content = encodeBase64Utf8(sql);
            script.sha256 = await sha256Hex(sql);
        }

        return exportDocument;
    }

    function validateDocument(exportDocument) {
        const errors = [];
        if (!exportDocument.moduleKey) {
            errors.push("Module key is required.");
        }

        if (!exportDocument.definitionVersion) {
            errors.push("Definition version is required.");
        }

        const appKeys = new Set();
        for (const [index, app] of (exportDocument.apps || []).entries()) {
            if (!app.appKey) {
                errors.push(`App ${index + 1} is missing app key.`);
                continue;
            }

            if (appKeys.has(app.appKey)) {
                errors.push(`App key '${app.appKey}' is duplicated.`);
            }

            appKeys.add(app.appKey);
        }

        for (const [index, artifact] of (exportDocument.compatibleArtifacts || []).entries()) {
            if (artifact.appKey && appKeys.size > 0 && !appKeys.has(artifact.appKey)) {
                errors.push(`Compatible artifact ${index + 1} references unknown app key '${artifact.appKey}'.`);
            }
        }

        for (const [index, script] of (exportDocument.sqlScripts || []).entries()) {
            if (!script.key) {
                errors.push(`SQL script ${index + 1} is missing key.`);
            }
        }

        return errors;
    }

    async function refreshPreview() {
        try {
            const exportDocument = await createExportDocument();
            const errors = validateDocument(exportDocument);
            elements.preview.value = JSON.stringify(exportDocument, null, 2);
            if (errors.length > 0) {
                showMessage(errors.join(" "), true);
            } else {
                clearMessage();
            }
        } catch (error) {
            showMessage(error instanceof Error ? error.message : String(error), true);
        }
    }

    async function downloadJson() {
        try {
            const exportDocument = await createExportDocument();
            const errors = validateDocument(exportDocument);
            if (errors.length > 0) {
                showMessage(errors.join(" "), true);
                return;
            }

            const json = JSON.stringify(exportDocument, null, 2) + "\n";
            const fileName = exportDocument.moduleKey
                ? `${exportDocument.moduleKey}.module-definition.json`
                : currentFileName;
            const blob = new Blob([json], { type: "application/json" });
            const url = URL.createObjectURL(blob);
            const link = document.createElement("a");
            link.href = url;
            link.download = fileName;
            document.body.append(link);
            link.click();
            link.remove();
            window.setTimeout(() => URL.revokeObjectURL(url), 1000);
            showMessage(`Prepared ${fileName}.`);
        } catch (error) {
            showMessage(error instanceof Error ? error.message : String(error), true);
        }
    }

    function applyPreviewJson() {
        try {
            documentModel = normalizeLoadedDocument(JSON.parse(elements.preview.value));
            currentFileName = documentModel.moduleKey
                ? `${documentModel.moduleKey}.module-definition.json`
                : currentFileName;
            populateForm();
            showMessage("Applied JSON preview to the form.");
        } catch (error) {
            showMessage(error instanceof Error ? error.message : String(error), true);
        }
    }

    function addApp() {
        updateModelFromForm();
        documentModel.apps.push({
            appKey: "new_app",
            displayName: "New app",
            appType: "WebApp",
            description: "",
            sortOrder: (documentModel.apps.length + 1) * 10,
            isEnabled: true,
            allowMultipleActiveInstances: false
        });
        renderApps();
        void refreshPreview();
    }

    function addScript() {
        updateModelFromForm();
        documentModel.sqlScripts.push({
            key: "new-script",
            phase: "setup",
            scope: "module",
            order: (documentModel.sqlScripts.length + 1) * 10,
            path: "",
            execution: "idempotent",
            _editorSql: ""
        });
        renderScripts();
        void refreshPreview();
    }

    function loadNewDocument() {
        documentModel = createEmptyDocument();
        currentFileName = "module-definition.json";
        populateForm();
        showMessage("Started a new module definition.");
    }

    async function loadFile(file) {
        const text = await file.text();
        const parsed = JSON.parse(text);
        documentModel = normalizeLoadedDocument(parsed);
        currentFileName = file.name || "module-definition.json";
        populateForm();
        showMessage(`Loaded ${currentFileName}.`);
    }

    elements.fileInput.addEventListener("change", event => {
        const file = event.target.files?.[0];
        if (!file) {
            return;
        }

        loadFile(file).catch(error => showMessage(error instanceof Error ? error.message : String(error), true));
        elements.fileInput.value = "";
    });

    for (const input of [elements.moduleKey, elements.definitionVersion, elements.formatVersion, elements.definitionType]) {
        input.addEventListener("input", () => void refreshPreview());
    }

    elements.newButton.addEventListener("click", loadNewDocument);
    elements.downloadButton.addEventListener("click", () => void downloadJson());
    elements.addAppButton.addEventListener("click", addApp);
    elements.addScriptButton.addEventListener("click", addScript);
    elements.applyPreviewButton.addEventListener("click", applyPreviewJson);
    elements.refreshPreviewButton.addEventListener("click", () => void refreshPreview());
    elements.apps.addEventListener("input", () => void refreshPreview());
    elements.apps.addEventListener("change", () => void refreshPreview());
    elements.scripts.addEventListener("input", () => void refreshPreview());

    populateForm();
})();
