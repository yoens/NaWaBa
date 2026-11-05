"use strict";

const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const { randomUUID, randomBytes, timingSafeEqual } = require("node:crypto");
const { readServerConfig } = require("./server-config.cjs");

// Protocol 2: server.cjs, index.html, RemoteInputGateway.cs must be updated together.
function createApplication(options = {}) {
    const { hostKey, joinKey } = options;
    const validKey = key => typeof key === "string" && /^[a-f0-9]{64}$/.test(key);
    if (!validKey(hostKey) || !validKey(joinKey) || hostKey === joinKey) {
        throw new Error("Two distinct 32-byte keys are required.");
    }
    const matchesKey = (value, expected) => validKey(value) &&
        timingSafeEqual(Buffer.from(value, "hex"), Buffer.from(expected, "hex"));
    const { WebSocket, WebSocketServer } = options.socketLibrary || require("ws");
    const authTimeout = options.authTimeout ?? 3000;
    const inputTimeout = options.inputTimeout || 500;
    const readyTimeout = options.readyTimeout || 15000;
    const requestTimeout = options.requestTimeout || 30000;
    const startDelay = options.startDelay ?? 3000;
    const log = options.log || console.log;
    const heartbeatMs = options.heartbeatMs ?? 20000;
    let host = null;
    let controller = null;
    let pending = null;
    let grant = null;

    const server = http.createServer((request, response) => {
        if (request.url === "/healthz") {
            response.writeHead(200, { "Content-Type": "application/json", "Cache-Control": "no-store" });
            response.end('{"ok":true}');
            return;
        }
        if (request.url !== "/" && request.url !== "/index.html") {
            response.writeHead(404);
            response.end("Not Found");
            return;
        }
        fs.readFile(path.join(__dirname, "index.html"), (error, data) => {
            response.writeHead(error ? 500 : 200, {
                "Content-Type": "text/html; charset=utf-8",
                "Cache-Control": "no-store",
                "Referrer-Policy": "no-referrer",
                "X-Content-Type-Options": "nosniff"
            });
            response.end(error ? "Cannot read index.html" : data);
        });
    });
    const wss = new WebSocketServer({ server, maxPayload: 2048 });
    // Do not derive this from incoming Host or X-Forwarded-* headers.
    const origin = () => options.publicOrigin || `http://127.0.0.1:${server.address().port}`;

    function send(socket, message) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return false;
        if (socket.bufferedAmount > 4096) {
            socket.terminate();
            return false;
        }
        try {
            socket.send(JSON.stringify({ ...message, protocolVersion: 2 }));
            return true;
        } catch {
            socket.terminate();
            return false;
        }
    }
    function ids(record) {
        return {
            controllerId: record.controllerId,
            requestId: record.requestId,
            grantId: record.grantId || "",
            nickname: record.nickname
        };
    }
    function cancelPending(reason) {
        if (!pending) return;
        const previous = pending;
        pending = null;
        const message = { type: "request_rejected", ...ids(previous), reason };
        send(host, message);
        send(controller, message);
    }
    function revoke(reason) {
        if (!grant) return;
        const previous = grant;
        grant = null; // Invalidate first, before notifying either peer.
        const message = { type: "control_revoked", ...ids(previous), reason };
        send(host, message);
        send(controller, message);
        log(`[control] revoked: ${reason}`);
    }
    function matches(message, record) {
        return record && message.controllerId === record.controllerId &&
            message.requestId === record.requestId;
    }
    function matchesGrant(message) {
        return matches(message, grant) && message.grantId === grant.grantId;
    }
    function parse(socket, data, isBinary) {
        if (socket.readyState !== WebSocket.OPEN) return null;
        try {
            if (isBinary) throw new Error();
            const message = JSON.parse(data.toString());
            if (!message || typeof message !== "object" ||
                message.protocolVersion !== 2 || typeof message.type !== "string") {
                throw new Error();
            }
            return message;
        } catch {
            socket.close(1008, "Invalid message or protocol version");
            return null;
        }
    }

    wss.on("connection", (socket, request) => {
        socket.on("error", error => log(`[socket] ${error.message}`));
        if (wss.clients.size > 16) {
            socket.close(1013, "Server connection limit");
            return;
        }
        socket.alive = true;
        socket.on("pong", () => { socket.alive = true; });
        let messageWindowAt = performance.now();
        let messagesInWindow = 0;
        socket.on("message", () => {
            const now = performance.now();
            if (now - messageWindowAt >= 1000) {
                messageWindowAt = now;
                messagesInWindow = 0;
            }
            if (++messagesInWindow > 120) socket.close(1008, "Message rate limit");
        });
        const requestOrigin = request.headers.origin;
        if (requestOrigin && requestOrigin !== origin()) {
            socket.close(1008, "Invalid origin");
            return;
        }

        if (request.url === "/host") {
            if (!matchesKey(request.headers["x-nawaba-host-key"], hostKey)) {
                socket.close(1008, "Host authentication failed");
                return;
            }
            if (host) {
                socket.close(1008, "Host already connected");
                return;
            }
            host = socket;
            send(host, { type: "host_hello" });
            send(controller, { type: "host_status", hostOnline: true });
            log("[host] connected");
            socket.on("message", (data, binary) => {
                if (host !== socket) return;
                const message = parse(socket, data, binary);
                if (!message) return;
                if (message.type === "approve_control") {
                    if (!matches(message, pending) || grant ||
                        !controller || controller.readyState !== WebSocket.OPEN) return;
                    const approved = pending;
                    pending = null;
                    grant = {
                        ...approved, grantId: randomUUID(), active: false,
                        approvedAt: performance.now(), startAt: null, lastInputAt: 0, lastSequence: -1
                    };
                    const notification = { type: "control_granted", ...ids(grant) };
                    if (!send(host, notification) || !send(controller, notification)) {
                        revoke("connection_lost");
                    } else {
                        log(`[control] approved: ${approved.nickname}`);
                    }
                    return;
                }
                if (message.type === "revoke_control") {
                    // requestId also cancels an approval still in transit to Unity.
                    if (matches(message, pending)) cancelPending("host_reclaimed");
                    if (matches(message, grant)) revoke("host_reclaimed");
                    return;
                }
                socket.close(1008, "Invalid host message");
            });
            socket.on("close", () => {
                if (host !== socket) return;
                host = null;
                cancelPending("host_disconnected");
                revoke("host_disconnected");
                send(controller, { type: "host_status", hostOnline: false });
                log("[host] disconnected");
            });
            return;
        }

        if (request.url !== "/controller" || requestOrigin !== origin()) {
            socket.close(1008, "Invalid controller connection");
            return;
        }
        // Authentication does not reserve the single participant slot.
        const authTimer = setTimeout(() => socket.close(1008, "Authentication timeout"), authTimeout);
        authTimer.unref();
        socket.once("close", () => clearTimeout(authTimer));
        socket.once("message", (data, binary) => {
            clearTimeout(authTimer);
            if (socket.readyState !== WebSocket.OPEN) return;
            const authentication = parse(socket, data, binary);
            if (!authentication) return;
            if (authentication.type !== "join" || !matchesKey(authentication.joinKey, joinKey)) {
                socket.close(1008, "Invalid participant link");
                return;
            }
            if (controller) {
                socket.close(1008, "Controller already connected");
                return;
            }
            controller = socket;
            const controllerId = randomUUID();
            send(socket, { type: "controller_hello", controllerId,
                hostOnline: !!host && host.readyState === WebSocket.OPEN });
            log("[controller] connected");

            socket.on("message", (data, binary) => {
                if (controller !== socket) return;
                const message = parse(socket, data, binary);
                if (!message) return;
                if (message.type === "request_control") {
                    if (pending || grant) return;
                    const nickname = typeof message.nickname === "string"
                        ? message.nickname.trim() : "";
                    if (!nickname || nickname.length > 20 || !host ||
                        host.readyState !== WebSocket.OPEN) {
                        send(socket, { type: "request_rejected", reason:
                            !nickname || nickname.length > 20 ? "invalid_nickname" : "host_disconnected" });
                        return;
                    }
                    pending = { controllerId, requestId: randomUUID(), nickname,
                        requestedAt: performance.now() };
                    if (!send(host, { type: "control_request", ...ids(pending) })) {
                        cancelPending("host_disconnected");
                        return;
                    }
                    send(socket, { type: "request_received", ...ids(pending) });
                    log(`[request] ${nickname}`);
                    return;
                }
                if (message.type === "ready") {
                    if (!matchesGrant(message) || grant.active || grant.startAt !== null) return;
                    // Only the server decides when the countdown ends. Duplicate ready is ignored.
                    grant.startAt = performance.now() + startDelay;
                    if (!send(socket, { type: "countdown_started", ...ids(grant), countdownMs: startDelay })) {
                        revoke("connection_lost");
                    }
                    return;
                }
                if (message.type === "release_control") {
                    if (matches(message, pending)) cancelPending("controller_stopped");
                    if (matchesGrant(message)) revoke("controller_stopped");
                    return;
                }
                if (message.type !== "input") {
                    socket.close(1008, "Invalid controller message");
                    return;
                }
                // Only the actual connected participant's currently active grant can move Unity.
                if (!matchesGrant(message) || !grant.active ||
                    grant.controllerId !== controllerId) return;
                if (!Number.isSafeInteger(message.sequence) || message.sequence < 0 ||
                    !Number.isFinite(message.x) || !Number.isFinite(message.y) ||
                    Math.abs(message.x) > 1 || Math.abs(message.y) > 1) {
                    revoke("invalid_input");
                    socket.close(1008, "Invalid input");
                    return;
                }
                if (message.sequence <= grant.lastSequence) return;
                grant.lastSequence = message.sequence;
                grant.lastInputAt = performance.now();
                if (!send(host, { type: "input", ...ids(grant),
                    sequence: message.sequence, x: message.x, y: message.y })) {
                    revoke("host_disconnected");
                }
            });
            socket.on("close", () => {
                if (controller !== socket) return;
                controller = null;
                cancelPending("controller_disconnected");
                revoke("controller_disconnected");
                log("[controller] disconnected");
            });
        }); // authenticated participant
    });

    const timer = setInterval(() => {
        const now = performance.now();
        if (pending && now - pending.requestedAt >= requestTimeout) cancelPending("request_timeout");
        if (grant && !grant.active && grant.startAt === null &&
            now - grant.approvedAt >= readyTimeout) revoke("ready_timeout");
        // No detached callback: revoking the current grant also cancels its countdown.
        if (grant && !grant.active && grant.startAt !== null && now >= grant.startAt) {
            grant.active = true;
            grant.lastInputAt = now;
            const notification = { type: "control_started", ...ids(grant) };
            if (!send(host, notification) || !send(controller, notification)) revoke("connection_lost");
        }
        if (grant && grant.active && now - grant.lastInputAt >= inputTimeout) revoke("input_timeout");
    }, 50);
    timer.unref();
    const heartbeat = setInterval(() => {
        for (const socket of wss.clients) {
            if (socket.readyState !== WebSocket.OPEN) continue;
            if (!socket.alive) { socket.terminate(); continue; }
            socket.alive = false;
            socket.ping();
        }
    }, heartbeatMs);
    heartbeat.unref();
    server.on("close", () => { clearInterval(timer); clearInterval(heartbeat); });
    server.on("error", error => log(`Server error: ${error.message}`));
    return { server, wss, origin };
}

if (require.main === module) {
    try {
        const config = readServerConfig();
        const joinKey = randomBytes(32).toString("hex");
        const app = createApplication({ hostKey: config.hostKey, joinKey, publicOrigin: config.publicOrigin });
        app.server.listen(config.port, config.listenHost, () => {
            console.log(`[server] listening on ${config.listenHost}:${config.port}`);
            console.log(`Invite URL: ${app.origin()}/#join=${joinKey}`);
            console.log("The invite changes on each server restart. Host approval is still required.");
        });
    } catch (error) {
        console.error("Cannot start:", error.message);
        process.exitCode = 1;
    }
}
module.exports = { createApplication };
