"use strict";
const fs = require("node:fs");
const path = require("node:path");

// Reads configuration, but never writes credentials or prints their contents.
function readServerConfig(env = process.env, projectRoot = path.resolve(__dirname, "..")) {
    const originText = env.PUBLIC_ORIGIN || env.RENDER_EXTERNAL_URL || "";
    const deployed = env.RENDER === "true" || originText !== "";
    let publicOrigin = "";
    if (deployed) {
        let url;
        try { url = new URL(originText); } catch { throw new Error("A valid HTTPS public origin is required."); }
        if (url.protocol !== "https:" || url.username || url.password ||
            url.pathname !== "/" || url.search || url.hash) {
            throw new Error("PUBLIC_ORIGIN must be an HTTPS origin without a path, query, or credentials.");
        }
        publicOrigin = url.origin;
    }
    const portText = env.PORT ?? (deployed ? "10000" : "3000");
    if (!/^\d+$/.test(portText)) throw new Error("PORT must be an integer from 1 to 65535.");
    const port = Number(portText);
    if (port < 1 || port > 65535) throw new Error("PORT must be an integer from 1 to 65535.");

    let hostKey = env.NAWABA_HOST_KEY;
    if (hostKey === undefined && !deployed) {
        let config;
        try {
            config = JSON.parse(fs.readFileSync(path.join(projectRoot, "nawaba-host.local.json"), "utf8"));
        } catch {
            throw new Error("Local host configuration is missing or invalid. Run node setup-local.cjs.");
        }
        hostKey = config.hostKey;
    }
    if (typeof hostKey !== "string" || !/^[a-f0-9]{64}$/.test(hostKey)) {
        throw new Error("NAWABA_HOST_KEY must contain the existing 64-character host key.");
    }
    const iceServers = parseIceServers(env.NAWABA_ICE_SERVERS);
    return { hostKey, publicOrigin, port, listenHost: deployed ? "0.0.0.0" : "127.0.0.1", iceServers };
}

// Optional STUN/TURN list for WebRTC, e.g. (one line in the Render environment variable)
// NAWABA_ICE_SERVERS=[{"urls":["stun:stun.l.google.com:19302"]},
//   {"urls":["turn:turn.example.com:3478"],"username":"user","credential":"secret"}]
// Unset = Google STUN only. Invalid entries are dropped; credentials are never logged.
function parseIceServers(text) {
    if (text === undefined || text === "") return null;
    let list;
    try { list = JSON.parse(text); } catch { throw new Error("NAWABA_ICE_SERVERS must be a JSON array."); }
    const clean = sanitizeIceServers(list);
    if (!clean) throw new Error("NAWABA_ICE_SERVERS has no valid stun:/turn: entries.");
    return clean;
}

function sanitizeIceServers(list) {
    if (!Array.isArray(list)) return null;
    const short = value => typeof value === "string" && value.length > 0 && value.length <= 256;
    const out = [];
    for (const entry of list.slice(0, 6)) {
        if (!entry || typeof entry !== "object") continue;
        const urls = (Array.isArray(entry.urls) ? entry.urls : [entry.urls])
            .filter(url => short(url) && /^(stun|turn|turns):[^\s]+$/.test(url)).slice(0, 4);
        if (urls.length === 0) continue;
        const server = { urls };
        if (urls.some(url => !url.startsWith("stun:"))) {
            if (!short(entry.username) || !short(entry.credential)) continue; // TURN needs a login
            server.username = entry.username;
            server.credential = entry.credential;
        }
        out.push(server);
    }
    return out.length ? out : null;
}
module.exports = { readServerConfig, sanitizeIceServers };
