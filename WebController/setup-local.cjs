"use strict";
const fs = require("node:fs");
const path = require("node:path");
const { randomBytes } = require("node:crypto");

function setupLocal(projectRoot) {
    const ignorePath = path.join(projectRoot, ".gitignore");
    const configPath = path.join(projectRoot, "nawaba-host.local.json");
    const ignore = fs.existsSync(ignorePath) ? fs.readFileSync(ignorePath, "utf8") : "";
    // Append, rather than replacing Unity's existing ignore rules.
    if (!ignore.split(/\r?\n/).includes("nawaba-host.local.json")) {
        fs.appendFileSync(ignorePath, "\n# NaWaBa local host credential\nnawaba-host.local.json\n", "utf8");
    }
    if (!fs.existsSync(configPath)) {
        fs.writeFileSync(configPath, JSON.stringify({
            schemaVersion: 1,
            serverUrl: "ws://127.0.0.1:3000/host",
            hostKey: randomBytes(32).toString("hex")
        }, null, 2) + "\n", { encoding: "utf8", flag: "wx", mode: 0o600 });
    } else {
        const existing = JSON.parse(fs.readFileSync(configPath, "utf8"));
        if (existing.schemaVersion !== 1 || !/^[a-f0-9]{64}$/.test(existing.hostKey || "") ||
            existing.serverUrl !== "ws://127.0.0.1:3000/host") {
            throw new Error("Existing local configuration is invalid. It was not overwritten.");
        }
    }
    return configPath;
}

if (require.main === module) {
    try {
        const configPath = setupLocal(path.resolve(__dirname, ".."));
        console.log("Local host configuration ready:");
        console.log(configPath);
        console.log(".gitignore updated. The host key is not printed.");
        console.log("Unity Editor reads this file from the project root.");
    } catch {
        console.error("Setup failed. Check the local configuration and folder permissions. Existing keys were not replaced.");
        process.exitCode = 1;
    }
}
module.exports = { setupLocal };
