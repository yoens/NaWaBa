"use strict";

const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const { randomUUID, randomBytes, timingSafeEqual } = require("node:crypto");
const { readServerConfig } = require("./server-config.cjs");

// Protocol 4: update server.cjs, index.html and RemoteInputGateway.cs together.
// Each room belongs to one live host connection. No client can select another
// room after authentication. This in-memory registry is for ONE server process.
function createApplication(options = {}) {
    const PROTOCOL = 4;
    const { hostKey } = options;
    const validKey = key => typeof key === "string" && /^[a-f0-9]{64}$/.test(key);
    if (!validKey(hostKey)) throw new Error("A 32-byte host key is required.");
    const matchesKey = (value, expected) => validKey(value) &&
        timingSafeEqual(Buffer.from(value, "hex"), Buffer.from(expected, "hex"));
    const { WebSocket, WebSocketServer } = options.socketLibrary || require("ws");
    const authTimeout = options.authTimeout ?? 3000;
    const inputTimeout = options.inputTimeout ?? 500;
    const readyTimeout = options.readyTimeout ?? 60000;
    const videoTimeout = options.videoTimeout ?? 1500;
    const requestTimeout = options.requestTimeout ?? 30000;
    const startDelay = options.startDelay ?? 3000;
    const heartbeatMs = options.heartbeatMs ?? 20000;
    const maxRooms = options.maxRooms ?? 8;
    const maxConnections = options.maxConnections ?? 32;
    const log = options.log || console.log;
    const rooms = new Map();


    const server = http.createServer((request, response) => {
        if (request.url === "/healthz") {
            response.writeHead(200, { "Content-Type": "application/json", "Cache-Control": "no-store" });
            response.end('{"ok":true}');
            return;
        }
        if (request.url === "/room-video.js") {
            fs.readFile(path.join(__dirname, "room-video.js"), (error, data) => {
                response.writeHead(error ? 500 : 200, { "Content-Type": "text/javascript; charset=utf-8",
                    "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" });
                response.end(error ? "Video script unavailable" : data);
            });
            return;
        }
        if (request.url !== "/" && request.url !== "/index.html") {
            response.writeHead(404);
            response.end("Not Found");
            return;
        }
        fs.readFile(path.join(__dirname, "index.html"), (error, data) => {
            response.writeHead(error ? 500 : 200, {
                "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store",
                "Referrer-Policy": "no-referrer", "X-Content-Type-Options": "nosniff"
            });
            response.end(error ? "Cannot read index.html" : data);
        });
    });
    const wss = new WebSocketServer({ server, maxPayload: 65536 });
    // Never derive this from incoming Host or X-Forwarded-* headers.
    const origin = () => options.publicOrigin || `http://127.0.0.1:${server.address().port}`;

    function send(room, socket, message) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return false;
        if (socket.bufferedAmount > 131072) { socket.terminate(); return false; }
        try {
            socket.send(JSON.stringify({ ...message, protocolVersion: PROTOCOL, roomId: room.id }));
            return true;
        } catch {
            socket.terminate();
            return false;
        }
    }
    // Opt-in keeps older protocol-4 Unity builds compatible: they reject unknown message types.
    function notifyGuestStatus(room, connected) {
        if (!room.reportGuestStatus || room.closed) return;
        send(room, room.host, { type: "controller_status", guestConnected: connected });
    }
    function ids(record) {
        return { controllerId: record.controllerId, requestId: record.requestId,
            grantId: record.grantId || "", nickname: record.nickname };
    }
    function cancelPending(room, reason) {
        const previous = room.pending;
        if (!previous) return;
        room.pending = null;
        const message = { type: "request_rejected", ...ids(previous), reason };
        send(room, room.host, message);
        send(room, room.controller, message);
    }
    function revoke(room, reason) {
        const previous = room.grant;
        if (!previous) return;
        room.grant = null; // Invalidate before notifying either peer.
        const message = { type: "control_revoked", ...ids(previous), reason };
        send(room, room.host, message);
        send(room, room.controller, message);
        log(`[room ${room.id}] revoked: ${reason}`);
    }
    function matches(message, record) {
        return record && message.controllerId === record.controllerId &&
            message.requestId === record.requestId;
    }
    function matchesGrant(room, message) {
        return matches(message, room.grant) && message.grantId === room.grant.grantId;
    }
    function parse(socket, data, isBinary, room = null) {
        if (socket.readyState !== WebSocket.OPEN) return null;
        try {
            if (isBinary) throw new Error();
            const message = JSON.parse(data.toString());
            if (!message || typeof message !== "object" || Array.isArray(message) ||
                message.protocolVersion !== PROTOCOL || typeof message.type !== "string") throw new Error();
            // The socket's captured room is authoritative, never the message's room ID.
            if (room && message.roomId !== room.id) {
                socket.close(1008, "Room mismatch");
                return null;
            }
            return message;
        } catch {
            socket.close(1008, "Invalid message or protocol version");
            return null;
        }
    }
    function validSdp(value) {
        return typeof value === "string" && Buffer.byteLength(value, "utf8") <= 60000 &&
            value.startsWith("v=0") && value.includes("m=video");
    }
    function closeRoom(room) {
        if (room.closed) return;
        room.closed = true;
        rooms.delete(room.id); // Old invites become invalid immediately.
        room.host = null;
        cancelPending(room, "host_disconnected");
        revoke(room, "host_disconnected");
        send(room, room.controller, { type: "host_status", hostOnline: false });
        if (room.controller) room.controller.close(1000, "Room closed");
        room.controller = null;
        log(`[room ${room.id}] closed`);
    }

    wss.on("connection", (socket, request) => {
        socket.on("error", error => log(`[socket] ${error.message}`));
        if (wss.clients.size > maxConnections) {
            socket.close(1013, "Server connection limit");
            return;
        }
        socket.alive = true;
        socket.on("pong", () => { socket.alive = true; });
        let messageWindowAt = performance.now();
        let messagesInWindow = 0;
        let bytesInWindow = 0;
        socket.on("message", data => {
            const now = performance.now();
            if (now - messageWindowAt >= 1000) { messageWindowAt = now; messagesInWindow = 0; bytesInWindow = 0; }
            bytesInWindow += data.length;
            if (++messagesInWindow > 120 || bytesInWindow > 262144) socket.close(1008, "Message rate limit");
        });
        const requestOrigin = request.headers.origin;
        if (requestOrigin && requestOrigin !== origin()) { socket.close(1008, "Invalid origin"); return; }

        if (request.url === "/host") {
            if (!matchesKey(request.headers["x-nawaba-host-key"], hostKey)) {
                socket.close(1008, "Host authentication failed");
                return;
            }
            if (request.headers["x-nawaba-protocol"] !== String(PROTOCOL)) {
                socket.close(1008, "Update Unity client to protocol 4");
                return;
            }
            if (rooms.size >= maxRooms) { socket.close(1013, "Room limit reached"); return; }
            let id;
            do { id = randomUUID(); } while (rooms.has(id));
            let joinKey;
            do { joinKey = randomBytes(32).toString("hex"); } while (joinKey === hostKey);
            const room = { id, joinKey, host: socket, controller: null,
                pending: null, grant: null, closed: false,
                reportGuestStatus: request.headers["x-nawaba-guest-status"] === "1" };
            rooms.set(id, room);
            send(room, socket, { type: "host_hello", inviteUrl: `${origin()}/#room=${id}&join=${joinKey}`,
                guestStatusSupported: room.reportGuestStatus, guestConnected: false });
            log(`[room ${id}] created`);
            socket.on("message", (data, binary) => {
                if (room.closed || room.host !== socket) return;
                const message = parse(socket, data, binary, room);
                if (!message) return;
                if (message.type === "approve_control") {
                    if (!matches(message, room.pending) || room.grant ||
                        !room.controller || room.controller.readyState !== WebSocket.OPEN) return;
                    const approved = room.pending;
                    room.pending = null;
                    room.grant = { ...approved, grantId: randomUUID(), active: false,
                        offerSent: false, answerSent: false, lastVideoAt: 0,
                        approvedAt: performance.now(), startAt: null, lastInputAt: 0, lastSequence: -1 };
                    const notification = { type: "control_granted", ...ids(room.grant) };
                    if (!send(room, socket, notification) || !send(room, room.controller, notification))
                        revoke(room, "connection_lost");
                    return;
                }
                if (message.type === "video_offer") {
                    if (!matchesGrant(room, message) || room.grant.offerSent) return;
                    if (!validSdp(message.sdp)) { revoke(room, "video_failed"); return; }
                    room.grant.offerSent = true;
                    if (!send(room, room.controller, { type: "video_offer", ...ids(room.grant), sdp: message.sdp }))
                        revoke(room, "connection_lost");
                    return;
                }
                if (message.type === "video_failed") {
                    if (matchesGrant(room, message)) revoke(room, "video_failed");
                    return;
                }
                if (message.type === "revoke_control") {
                    // Only the owning host can report an outcome for its currently running grant.
                    if (message.reason === "round_cleared" || message.reason === "round_failed") {
                        if (matchesGrant(room, message) && room.grant.active)
                            revoke(room, message.reason);
                        return;
                    }
                    if (["input_timeout", "video_timeout"].includes(message.reason)) {
                        if (matchesGrant(room, message)) revoke(room, message.reason);
                        return;
                    }
                    // requestId also cancels an approval still in transit to Unity.
                    if (matches(message, room.pending)) cancelPending(room, "host_reclaimed");
                    if (matches(message, room.grant)) revoke(room, "host_reclaimed");
                    return;
                }
                socket.close(1008, "Invalid host message");
            });
            socket.on("close", () => closeRoom(room));
            return;
        }

        if (request.url !== "/controller" || requestOrigin !== origin()) {
            socket.close(1008, "Invalid controller connection");
            return;
        }
        // Unauthenticated sockets do not occupy a room's guest slot.
        const authTimer = setTimeout(() => socket.close(1008, "Authentication timeout"), authTimeout);
        authTimer.unref();
        socket.once("close", () => clearTimeout(authTimer));
        socket.once("message", (data, binary) => {
            clearTimeout(authTimer);
            const authentication = parse(socket, data, binary);
            if (!authentication) return;
            const room = typeof authentication.roomId === "string" ? rooms.get(authentication.roomId) : null;
            if (authentication.type !== "join" || !room || room.closed ||
                !room.host || room.host.readyState !== WebSocket.OPEN ||
                !matchesKey(authentication.joinKey, room.joinKey)) {
                socket.close(1008, "Invalid participant link");
                return;
            }
            if (room.controller) { socket.close(1008, "Controller already connected"); return; }
            room.controller = socket;
            const controllerId = randomUUID();
            send(room, socket, { type: "controller_hello", controllerId, hostOnline: true });
            notifyGuestStatus(room, true);
            log(`[room ${room.id}] guest connected`);
            socket.on("message", (data, binary) => {
                if (room.closed || room.controller !== socket) return;
                const message = parse(socket, data, binary, room);
                if (!message) return;
                if (message.type === "request_control") {
                    if (room.pending || room.grant) return;
                    const nickname = typeof message.nickname === "string" ? message.nickname.trim() : "";
                    if (!nickname || nickname.length > 20 || !room.host ||
                        room.host.readyState !== WebSocket.OPEN) {
                        send(room, socket, { type: "request_rejected", reason:
                            !nickname || nickname.length > 20 ? "invalid_nickname" : "host_disconnected" });
                        return;
                    }
                    room.pending = { controllerId, requestId: randomUUID(), nickname, requestedAt: performance.now() };
                    if (!send(room, room.host, { type: "control_request", ...ids(room.pending) })) {
                        cancelPending(room, "host_disconnected");
                        return;
                    }
                    send(room, socket, { type: "request_received", ...ids(room.pending) });
                    return;
                }
                if (message.type === "video_answer") {
                    if (!matchesGrant(room, message) || !room.grant.offerSent || room.grant.answerSent) return;
                    if (!validSdp(message.sdp)) { revoke(room, "video_failed"); return; }
                    room.grant.answerSent = true;
                    if (!send(room, room.host, { type: "video_answer", ...ids(room.grant), sdp: message.sdp }))
                        revoke(room, "connection_lost");
                    return;
                }
                if (message.type === "video_ready") {
                    if (!matchesGrant(room, message) || !room.grant.answerSent ||
                        room.grant.active || room.grant.startAt !== null) return;
                    // Browser emits this only after a video frame reaches its compositor.
                    const now = performance.now();
                    room.grant.lastVideoAt = now;
                    room.grant.startAt = now + startDelay;
                    const notification = { type: "countdown_started", ...ids(room.grant), countdownMs: startDelay };
                    if (!send(room, socket, notification) || !send(room, room.host, notification))
                        revoke(room, "connection_lost");
                    return;
                }
                if (message.type === "video_alive") {
                    if (!matchesGrant(room, message) || room.grant.startAt === null) return;
                    room.grant.lastVideoAt = performance.now();
                    if (!send(room, room.host, { type: "video_alive", ...ids(room.grant) }))
                        revoke(room, "connection_lost");
                    return;
                }
                if (message.type === "video_failed") {
                    if (matchesGrant(room, message)) revoke(room, "video_failed");
                    return;
                }
                if (message.type === "release_control") {
                    if (matches(message, room.pending)) cancelPending(room, "controller_stopped");
                    if (matchesGrant(room, message)) revoke(room, "controller_stopped");
                    return;
                }
                if (message.type !== "input") { socket.close(1008, "Invalid controller message"); return; }
                if (!matchesGrant(room, message) || !room.grant.active || room.grant.controllerId !== controllerId) return;
                if (!Number.isSafeInteger(message.sequence) || message.sequence < 0 ||
                    !Number.isFinite(message.x) || !Number.isFinite(message.y) ||
                    Math.abs(message.x) > 1 || Math.abs(message.y) > 1) {
                    revoke(room, "invalid_input");
                    socket.close(1008, "Invalid input");
                    return;
                }
                if (message.sequence <= room.grant.lastSequence) return;
                room.grant.lastSequence = message.sequence;
                room.grant.lastInputAt = performance.now();
                if (!send(room, room.host, { type: "input", ...ids(room.grant),
                    sequence: message.sequence, x: message.x, y: message.y })) revoke(room, "host_disconnected");
            });
            socket.on("close", () => {
                if (room.closed || room.controller !== socket) return;
                room.controller = null;
                cancelPending(room, "controller_disconnected");
                revoke(room, "controller_disconnected");
                notifyGuestStatus(room, false);
                log(`[room ${room.id}] guest disconnected`);
            });
        });
    });

    const timer = setInterval(() => {
        const now = performance.now();
        for (const room of rooms.values()) {
            if (room.pending && now - room.pending.requestedAt >= requestTimeout) cancelPending(room, "request_timeout");
            if (room.grant && !room.grant.active && room.grant.startAt === null &&
                now - room.grant.approvedAt >= readyTimeout) revoke(room, "ready_timeout");
            if (room.grant && room.grant.startAt !== null &&
                now - room.grant.lastVideoAt >= videoTimeout) revoke(room, "video_timeout");
            // Revocation removes the grant, so a cancelled countdown cannot restart it.
            if (room.grant && !room.grant.active && room.grant.startAt !== null && now >= room.grant.startAt) {
                room.grant.active = true;
                room.grant.lastInputAt = now;
                const notification = { type: "control_started", ...ids(room.grant) };
                if (!send(room, room.host, notification) || !send(room, room.controller, notification))
                    revoke(room, "connection_lost");
            }
            if (room.grant && room.grant.active && now - room.grant.lastInputAt >= inputTimeout)
                revoke(room, "input_timeout");
        }
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
        const app = createApplication({ hostKey: config.hostKey, publicOrigin: config.publicOrigin });
        app.server.listen(config.port, config.listenHost, () => {
            console.log(`[server] listening on ${config.listenHost}:${config.port} (protocol 4)`);
            console.log("Guest status notifications enabled for supported Unity clients.");
            console.log("Each host creates a room. Copy its invite from the Unity multiplayer lobby.");
        });
    } catch (error) {
        console.error("Cannot start:", error.message);
        process.exitCode = 1;
    }
}
module.exports = { createApplication };
