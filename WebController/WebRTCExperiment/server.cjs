"use strict";

// Same-LAN signaling experiment. Video does not pass through this server.
// Uses only Node built-ins; no npm install is needed.
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const { randomUUID, randomBytes, timingSafeEqual } = require("node:crypto");

function getLanAddresses() {
    const result = [];
    let adapters;
    try { adapters = os.networkInterfaces(); }
    catch (error) {
        console.warn("Cannot enumerate network adapters:", error.message);
        return result;
    }
    for (const [name, interfaces] of Object.entries(adapters)) {
        for (const item of interfaces || []) {
            if (item.internal || item.family !== "IPv4") continue;
            const parts = item.address.split(".").map(Number);
            const privateAddress = parts[0] === 10 ||
                (parts[0] === 172 && parts[1] >= 16 && parts[1] <= 31) ||
                (parts[0] === 192 && parts[1] === 168);
            if (privateAddress) result.push({ name, address: item.address });
        }
    }
    return result;
}

function tokenMatches(value, expected) {
    return typeof value === "string" && /^[a-f0-9]{48}$/.test(value) &&
        timingSafeEqual(Buffer.from(value, "hex"), Buffer.from(expected, "hex"));
}

function createServer(port = 3001) {
    const addresses = getLanAddresses();
    const allowedHosts = new Set([`127.0.0.1:${port}`, ...addresses.map(item => `${item.address}:${port}`)]);
    const viewToken = randomBytes(24).toString("hex");
    let session = null;
    const reply = (res, code, body) => {
        res.writeHead(code, { "Content-Type": "application/json; charset=utf-8",
            "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer" });
        res.end(JSON.stringify(body));
    };
    const server = http.createServer(async (req, res) => {
        const host = req.headers.host;
        if (!allowedHosts.has(host) ||
            (req.headers.origin && req.headers.origin !== `http://${host}`)) {
            reply(res, 403, { error: "Use the exact link printed by this server" });
            return;
        }
        let url;
        try { url = new URL(req.url, `http://${host}`); }
        catch { reply(res, 400, { error: "Invalid URL" }); return; }
        const hostRoute = (req.method === "POST" && ["/api/start", "/api/offer"].includes(url.pathname)) ||
            (req.method === "GET" && url.pathname === "/api/answer");
        const viewerRoute = (req.method === "GET" && url.pathname === "/api/offer") ||
            (req.method === "POST" && url.pathname === "/api/answer");
        const loopback = ["127.0.0.1", "::1", "::ffff:127.0.0.1"].includes(req.socket.remoteAddress);
        // Only the local native Unity process creates/publishes sessions.
        if (hostRoute && (!loopback || req.headers.origin || req.headers["sec-fetch-site"])) {
            reply(res, 403, { error: "Host API is for local Unity only" });
            return;
        }
        if (viewerRoute && !tokenMatches(req.headers["x-nawaba-view-token"], viewToken)) {
            reply(res, 403, { error: "Open the complete current link printed by the server (#view= included)" });
            return;
        }
        if (req.method === "GET" && url.pathname === "/") {
            fs.readFile(path.join(__dirname, "index.html"), (error, html) => {
                if (error) return reply(res, 500, { error: "Cannot read index.html" });
                res.writeHead(200, { "Content-Type": "text/html; charset=utf-8",
                    "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer" });
                res.end(html);
            });
            return;
        }
        if (req.method === "GET" && url.pathname === "/api/offer") {
            reply(res, 200, session
                ? { session: session.id, sdp: session.offer, answered: !!session.answer }
                : { session: "", sdp: "", answered: false });
            return;
        }
        if (req.method === "GET" && url.pathname === "/api/answer") {
            if (!session || url.searchParams.get("session") !== session.id) {
                reply(res, 409, { error: "Session changed. Use New Session in Unity." });
            } else reply(res, 200, { session: session.id, sdp: session.answer });
            return;
        }
        if (req.method !== "POST" || !["/api/start", "/api/offer", "/api/answer"].includes(url.pathname)) {
            reply(res, 404, { error: "Not Found" });
            return;
        }
        if (!/^application\/json(?:;|$)/i.test(req.headers["content-type"] || "")) {
            reply(res, 415, { error: "JSON required" });
            return;
        }
        let data;
        try {
            const chunks = [];
            let size = 0;
            for await (const chunk of req) {
                size += chunk.length;
                if (size > 65536) {
                    reply(res, 413, { error: "Message too large" });
                    return;
                }
                chunks.push(chunk);
            }
            data = JSON.parse(Buffer.concat(chunks).toString("utf8"));
            if (!data || typeof data !== "object" || Array.isArray(data)) throw new Error();
        } catch {
            if (!res.destroyed) reply(res, 400, { error: "Invalid JSON" });
            return;
        }
        if (url.pathname === "/api/start") {
            session = { id: randomUUID(), offer: "", answer: "" };
            reply(res, 200, { session: session.id });
            console.log("[session] new");
            return;
        }
        if (!session || data.session !== session.id) {
            reply(res, 409, { error: "Stale session" });
            return;
        }
        if (typeof data.sdp !== "string" || !data.sdp.startsWith("v=0") || data.sdp.length > 60000) {
            reply(res, 400, { error: "Invalid SDP" });
            return;
        }
        if (url.pathname === "/api/offer") {
            if (session.offer) return reply(res, 409, { error: "Offer already published" });
            session.offer = data.sdp;
            console.log("[offer] ready for browser");
        } else {
            if (!session.offer || session.answer) {
                reply(res, 409, { error: "Use New Session in Unity before reconnecting" });
                return;
            }
            session.answer = data.sdp;
            console.log("[answer] ready for Unity");
        }
        reply(res, 200, { session: session.id });
    });
    server.requestTimeout = 10000;
    server.headersTimeout = 10000;
    server.viewerLinks = [
        { name: "This PC", url: `http://127.0.0.1:${port}/#view=${viewToken}` },
        ...addresses.map(item => ({ name: item.name, url: `http://${item.address}:${port}/#view=${viewToken}` }))
    ];
    return server;
}

if (require.main === module) {
    const server = createServer();
    server.on("error", error => console.error("Server error:", error.message));
    server.listen(3001, "0.0.0.0", () => {
        console.log("NaWaBa WebRTC LAN experiment (same router, one viewer)");
        for (const link of server.viewerLinks) console.log(`[${link.name}] ${link.url}`);
        if (server.viewerLinks.length === 1) console.log("No private IPv4 found. Check your Wi-Fi/Ethernet and restart this server.");
        console.log("Laptop: use the Wi-Fi/Ethernet link including #view=. Do not use 127.0.0.1 on the laptop.");
        console.log("The viewer link changes each time this server restarts.");
        console.log("Start Unity, wait for OFFER READY, then click Receive in the browser.");
        console.log("Stop server: Ctrl+C");
    });
}
module.exports = { createServer, getLanAddresses };
