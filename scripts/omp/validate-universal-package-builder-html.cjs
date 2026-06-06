#!/usr/bin/env node
/*
 * Opens tools/universal-package-builder/index.html in a real browser, adds
 * selected portable object files, creates a universal package, and saves the
 * downloaded zip. Run through npx so repositories do not need a node_modules
 * folder:
 *
 *   npx -p playwright node scripts/omp/validate-universal-package-builder-html.cjs --module path.json --artifact path.zip --output out.zip
 */
const fs = require("node:fs");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

function loadPlaywright() {
    try {
        return require("playwright");
    } catch {
        const candidates = [];
        if (process.env.LOCALAPPDATA) {
            candidates.push(path.join(process.env.LOCALAPPDATA, "npm-cache", "_npx"));
        }

        if (process.env.HOME) {
            candidates.push(path.join(process.env.HOME, ".npm", "_npx"));
        }

        for (const root of candidates) {
            if (!fs.existsSync(root)) {
                continue;
            }

            for (const folder of fs.readdirSync(root)) {
                const packagePath = path.join(root, folder, "node_modules", "playwright");
                if (fs.existsSync(path.join(packagePath, "package.json"))) {
                    return require(packagePath);
                }
            }
        }

        throw new Error("Could not load the 'playwright' package. Run through npx -p playwright or install Playwright locally.");
    }
}

const { chromium } = loadPlaywright();

function readArgs(argv) {
    const result = {
        modules: [],
        artifacts: [],
        output: "",
        builder: path.resolve(__dirname, "../../tools/universal-package-builder/index.html"),
        packageKey: "omp-universal-html-validation",
        packageVersion: "validation"
    };

    for (let i = 0; i < argv.length; i += 1) {
        const arg = argv[i];
        const next = () => {
            if (i + 1 >= argv.length) {
                throw new Error(`Missing value for ${arg}`);
            }

            i += 1;
            return argv[i];
        };

        switch (arg) {
            case "--module":
                result.modules.push(path.resolve(next()));
                break;
            case "--artifact":
                result.artifacts.push(path.resolve(next()));
                break;
            case "--output":
                result.output = path.resolve(next());
                break;
            case "--builder":
                result.builder = path.resolve(next());
                break;
            case "--package-key":
                result.packageKey = next();
                break;
            case "--package-version":
                result.packageVersion = next();
                break;
            default:
                throw new Error(`Unknown argument: ${arg}`);
        }
    }

    if (!result.output) {
        throw new Error("--output is required.");
    }

    if (result.modules.length === 0 && result.artifacts.length === 0) {
        throw new Error("At least one --module or --artifact file is required.");
    }

    return result;
}

async function addFiles(page, kind, files) {
    if (files.length === 0) {
        return;
    }

    await page.selectOption("#objectKind", kind);
    await page.setInputFiles("#objectFiles", files);
    await page.click("#addFiles");
}

async function main() {
    const args = readArgs(process.argv.slice(2));
    for (const file of [args.builder, ...args.modules, ...args.artifacts]) {
        if (!fs.existsSync(file)) {
            throw new Error(`File not found: ${file}`);
        }
    }

    fs.mkdirSync(path.dirname(args.output), { recursive: true });
    if (fs.existsSync(args.output)) {
        fs.rmSync(args.output);
    }

    let browser;
    try {
        browser = await chromium.launch({ channel: "msedge", headless: true });
    } catch (error) {
        console.warn(`Could not launch Microsoft Edge for validation, falling back to bundled/default Chromium: ${error && error.message ? error.message : String(error)}`);
        browser = await chromium.launch({ headless: true });
    }

    try {
        const page = await browser.newPage();
        await page.goto(pathToFileURL(args.builder).href);
        await page.fill("#packageKey", args.packageKey);
        await page.fill("#packageVersion", args.packageVersion);

        await addFiles(page, "module-definition", args.modules);
        await addFiles(page, "artifact-package", args.artifacts);

        const downloadPromise = page.waitForEvent("download");
        await page.click("#createPackage");
        const download = await downloadPromise;
        await download.saveAs(args.output);
    }
    finally {
        await browser.close();
    }

    console.log(`Created ${args.output}`);
}

main().catch(error => {
    console.error(error && error.stack ? error.stack : String(error));
    process.exitCode = 1;
});
