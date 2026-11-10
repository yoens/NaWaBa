"use strict";
const fs = require("node:fs");
const path = require("node:path");
const { randomUUID, randomBytes, timingSafeEqual } = require("node:crypto");

// An isolated HTTP signaling namespace on the existing NaWaBa server.
// This never reads or changes the game rooms or their WebSocket messages.
function createVideoExperiment({ hostKey, getOrigin, ttlMs = 15 * 60 * 1000 }) {
    const prefix = "/webrtc-experiment";
    const sessions = new Map();
    const clients = new Map();
    const hex = value => typeof value === "string" && /^[a-f0-9]{64}$/.test(value);
    const uuid = value => typeof value === "string" && /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/.test(value);
    if (!hex(hostKey)) throw new Error("Video experiment requires the configured host key");
    const matches = (a, b) => hex(a) && hex(b) && timingSafeEqual(Buffer.from(a, "hex"), Buffer.from(b, "hex"));
    const reply = (res, code, data) => {
        if (res.destroyed || res.writableEnded) return;
        res.writeHead(code, { "Content-Type": "application/json; charset=utf-8",
            "Cache-Control": "no-store", "Referrer-Policy": "no-referrer", "X-Content-Type-Options": "nosniff" });
        res.end(JSON.stringify(data));
    };
    function expire() {
        const now = Date.now();
        for (const [id, session] of sessions) {
            if (session.expiresAt > now) continue;
            sessions.delete(id);
            if (clients.get(session.clientId) === id) clients.delete(session.clientId);
        }
    }
    async function readBody(req) {
        if (!/^application\/json(?:;|$)/i.test(req.headers["content-type"] || ""))
            throw Object.assign(new Error("JSON required"), { status: 415 });
        const chunks = [];
        let size = 0;
        const timeout = setTimeout(() => req.destroy(), 10000);
        try {
            for await (const chunk of req) {
                size += chunk.length;
                if (size > 65536) throw Object.assign(new Error("Message too large"), { status: 413 });
                chunks.push(chunk);
            }
            let data;
            try { data = JSON.parse(Buffer.concat(chunks).toString("utf8")); }
            catch { throw Object.assign(new Error("Invalid JSON"), { status: 400 }); }
            if (!data || typeof data !== "object" || Array.isArray(data))
                throw Object.assign(new Error("Invalid JSON object"), { status: 400 });
            return data;
        } finally { clearTimeout(timeout); }
    }
    async function handle(req, res) {
        expire();
        // Canonical server configuration only. Do not trust incoming Host/forwarded headers.
        const origin = getOrigin();
        const url = new URL(req.url, origin);
        if (url.origin !== origin) return reply(res, 403, { error: "Invalid origin" });
        if (req.headers.origin && req.headers.origin !== origin)
            return reply(res, 403, { error: "Invalid origin" });
        if (req.method === "GET" && [prefix, prefix + "/"].includes(url.pathname)) {
            fs.readFile(path.join(__dirname, "webrtc-experiment.html"), (error, html) => {
                if (error) return reply(res, 500, { error: "Receiver page missing" });
                if (res.destroyed) return;
                res.writeHead(200, { "Content-Type": "text/html; charset=utf-8",
                    "Cache-Control": "no-store", "Referrer-Policy": "no-referrer", "X-Content-Type-Options": "nosniff" });
                res.end(html);
            });
            return;
        }
        const route = url.pathname.slice(prefix.length);
        const hostRoute = (req.method === "POST" && ["/api/start", "/api/offer"].includes(route)) ||
            (req.method === "GET" && route === "/api/answer");
        const viewerRoute = (req.method === "GET" && route === "/api/offer") ||
            (req.method === "POST" && route === "/api/answer");
        if (!hostRoute && !viewerRoute) return reply(res, 404, { error: "Not Found" });
        const clientId = req.headers["x-nawaba-video-client"];
        if (hostRoute && (!matches(req.headers["x-nawaba-host-key"], hostKey) || !uuid(clientId)))
            return reply(res, 403, { error: "Host authentication failed" });

        if (route === "/api/start") {
            await readBody(req);
            const previous = clients.get(clientId);
            if (!previous && sessions.size >= 32) return reply(res, 503, { error: "Video session limit; retry later" });
            if (previous) sessions.delete(previous);
            const id = randomUUID();
            const viewToken = randomBytes(32).toString("hex");
            sessions.set(id, { clientId, viewToken, offer: "", answer: "", expiresAt: Date.now() + ttlMs });
            clients.set(clientId, id);
            reply(res, 200, { session: id, inviteUrl: `${origin}${prefix}/#session=${id}&view=${viewToken}` });
            return;
        }

        const id = req.headers["x-nawaba-video-session"];
        let session = uuid(id) ? sessions.get(id) : null;
        if (!session) return reply(res, 410, { error: "Session expired. Create and copy a new link in Unity." });
        if (hostRoute && session.clientId !== clientId) return reply(res, 403, { error: "Host session mismatch" });
        if (viewerRoute && !matches(req.headers["x-nawaba-view-token"], session.viewToken))
            return reply(res, 403, { error: "Invalid video invite. Copy the complete current link from Unity." });
        if (req.method === "GET") {
            reply(res, 200, { session: id, sdp: hostRoute ? session.answer : session.offer, answered: !!session.answer });
            return;
        }
        const data = await readBody(req);
        // A new session can replace this one while the body is being read.
        if (sessions.get(id) !== session || session.expiresAt <= Date.now())
            return reply(res, 410, { error: "Session changed. Use the newest link." });
        if (data.session !== id) return reply(res, 409, { error: "Session mismatch" });
        if (typeof data.sdp !== "string" || !data.sdp.startsWith("v=0") || data.sdp.length > 60000)
            return reply(res, 400, { error: "Invalid SDP" });
        if (hostRoute) {
            if (session.offer) return reply(res, 409, { error: "Offer already published" });
            session.offer = data.sdp;
        } else {
            if (!session.offer || session.answer)
                return reply(res, 409, { error: "Create a new session in Unity and open its new link." });
            session.answer = data.sdp;
        }
        reply(res, 200, { session: id });
    }
    return function routeVideoExperiment(req, res) {
        if (req.url !== prefix && !req.url.startsWith(prefix + "/") && !req.url.startsWith(prefix + "?")) return false;
        void handle(req, res).catch(error => reply(res, error.status || 500,
            { error: error.status ? error.message : "Video signaling error" }));
        return true;
    };
}
module.exports = { createVideoExperiment };
