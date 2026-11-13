"use strict";

const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const { randomUUID, randomBytes, timingSafeEqual } = require("node:crypto");
const { readServerConfig, sanitizeIceServers } = require("./server-config.cjs");

// Protocol 8: update server.cjs, index.html and RemoteInputGateway.cs together.
// Each room belongs to one live host connection. No client can select another
// room after authentication. This in-memory registry is for ONE server process.
function createApplication(options = {}) {
    const PROTOCOL = 8;
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
    const startDelay = options.startDelay ?? 3000;
    // A controller carried into the next stage must get a new video within this time.
    const transitionTimeout = options.transitionTimeout ?? 30000;
    const heartbeatMs = options.heartbeatMs ?? 20000;
    const maxRooms = options.maxRooms ?? 8;
    const maxConnections = options.maxConnections ?? 96;
    // Web participants per room (watchers + the one controller). Step 2: no simultaneous control.
    const maxParticipants = options.maxParticipants ?? 8;
    const log = options.log || console.log;
    // STUN/TURN servers handed to both the Unity host and the browsers (same list for both).
    const iceServers = sanitizeIceServers(options.iceServers) ||
        [{ urls: ["stun:stun.l.google.com:19302"] }];
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
    // Socket of one participant, or null. Request/grant messages go only to their owner.
    function participant(room, controllerId) {
        return (controllerId && room.participants.get(controllerId)) || null;
    }
    // Room-wide messages (stage, host state, headcount) go to every participant.
    function broadcast(room, message) {
        for (const socket of room.participants.values()) send(room, socket, message);
    }
    // Opt-in keeps older Unity builds compatible: they reject unknown message types.
    function notifyParticipants(room) {
        if (room.closed) return;
        const participantCount = room.participants.size;
        if (room.reportGuestStatus)
            send(room, room.host, { type: "controller_status", guestConnected: participantCount > 0, participantCount });
        broadcast(room, { type: "room_status", participantCount });
    }
    function ids(record) {
        return { controllerId: record.controllerId, requestId: record.requestId,
            grantId: record.grantId || "", nickname: record.nickname };
    }
    // Request list: first come, first served. Entries stay until approved, cancelled,
    // removed by the host or their owner leaves; there is no short expiry.
    function queueIndex(room, controllerId) {
        return room.queue.findIndex(entry => entry.controllerId === controllerId);
    }
    function publishQueue(room) {
        if (room.closed) return;
        send(room, room.host, { type: "queue_status", queue: room.queue.map(entry => ids(entry)) });
        room.queue.forEach((entry, index) => send(room, participant(room, entry.controllerId),
            { type: "queue_position", ...ids(entry), position: index + 1, total: room.queue.length }));
    }
    // Removes one entry. The owner is told why unless they already left.
    function dropRequest(room, controllerId, reason, tellOwner = true) {
        const index = queueIndex(room, controllerId);
        if (index < 0) return false;
        const [entry] = room.queue.splice(index, 1);
        if (tellOwner) send(room, participant(room, controllerId), { type: "request_rejected", ...ids(entry), reason });
        publishQueue(room);
        return true;
    }
    function revoke(room, reason) {
        const previous = room.grant;
        if (!previous) return;
        room.grant = null; // Invalidate before notifying either peer.
        const message = { type: "control_revoked", ...ids(previous), reason };
        send(room, room.host, message);
        send(room, participant(room, previous.controllerId), message);
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
        for (const entry of room.queue)
            send(room, participant(room, entry.controllerId), { type: "request_rejected", ...ids(entry), reason: "host_disconnected" });
        room.queue = [];
        revoke(room, "host_disconnected");
        broadcast(room, { type: "host_status", hostOnline: false });
        for (const socket of room.participants.values()) socket.close(1000, "Room closed");
        room.participants.clear();
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
                socket.close(1008, "Update Unity client to protocol 8");
                return;
            }
            if (rooms.size >= maxRooms) { socket.close(1013, "Room limit reached"); return; }
            let id;
            do { id = randomUUID(); } while (rooms.has(id));
            let joinKey;
            do { joinKey = randomBytes(32).toString("hex"); } while (joinKey === hostKey);
            const room = { id, joinKey, host: socket, participants: new Map(),
                queue: [], grant: null, closed: false,
                stage: { stageId: "lobby", stageName: "", controls: "" },
                reportGuestStatus: request.headers["x-nawaba-guest-status"] === "1" };
            rooms.set(id, room);
            send(room, socket, { type: "host_hello", inviteUrl: `${origin()}/#room=${id}&join=${joinKey}`,
                guestStatusSupported: room.reportGuestStatus, guestConnected: false, participantCount: 0,
                iceServers });
            log(`[room ${id}] created`);
            socket.on("message", (data, binary) => {
                if (room.closed || room.host !== socket) return;
                const message = parse(socket, data, binary, room);
                if (!message) return;
                if (message.type === "approve_control") {
                    // Only the front of the list can be approved; a repeated click finds a new front
                    // (or none) and is refused, so one approval creates exactly one grant.
                    const front = room.queue[0];
                    const owner = front && participant(room, front.controllerId);
                    if (!matches(message, front) || !owner || owner.readyState !== WebSocket.OPEN) {
                        send(room, socket, { type: "request_rejected", controllerId: message.controllerId,
                            requestId: message.requestId, grantId: "", nickname: "", reason: "not_available" });
                        return;
                    }
                    // Approving someone else while a participant controls hands over control:
                    // the previous controller goes back to watching.
                    if (room.grant) revoke(room, "replaced");
                    const approved = room.queue.shift();
                    publishQueue(room);
                    room.grant = { ...approved, grantId: randomUUID(), active: false,
                        offerSent: false, answerSent: false, lastVideoAt: 0,
                        approvedAt: performance.now(), startAt: null, lastInputAt: 0, lastSequence: -1 };
                    const notification = { type: "control_granted", ...ids(room.grant) };
                    if (!send(room, socket, notification) || !send(room, owner, notification))
                        revoke(room, "connection_lost");
                    return;
                }
                if (message.type === "video_offer") {
                    if (!matchesGrant(room, message) || room.grant.offerSent) return;
                    if (!validSdp(message.sdp)) { revoke(room, "video_failed"); return; }
                    room.grant.offerSent = true;
                    if (!send(room, participant(room, room.grant.controllerId),
                        { type: "video_offer", ...ids(room.grant), sdp: message.sdp }))
                        revoke(room, "connection_lost");
                    return;
                }
                if (message.type === "video_failed") {
                    if (matchesGrant(room, message)) revoke(room, "video_failed");
                    return;
                }
                if (message.type === "stage_transition") {
                    // The controller cleared a stage and keeps their turn in the next one.
                    // Inputs stop (grant inactive) and the old video/grant ID become invalid.
                    if (!matchesGrant(room, message) || !room.grant.active) return;
                    const grant = room.grant;
                    Object.assign(grant, { active: false, transitioning: true, transitionAt: performance.now(),
                        offerSent: false, answerSent: false, startAt: null, lastVideoAt: 0 });
                    send(room, participant(room, grant.controllerId), { type: "stage_transition", ...ids(grant) });
                    return;
                }
                if (message.type === "resume_control") {
                    // Next stage is ready: same turn, fresh grant ID, normal video + countdown again.
                    if (!matches(message, room.grant) || !room.grant.transitioning) return;
                    const grant = room.grant;
                    Object.assign(grant, { grantId: randomUUID(), transitioning: false,
                        approvedAt: performance.now(), lastSequence: -1, lastInputAt: 0 });
                    const notification = { type: "control_granted", ...ids(grant), resumed: true };
                    if (!send(room, socket, notification) || !send(room, participant(room, grant.controllerId), notification))
                        revoke(room, "connection_lost");
                    return;
                }
                if (message.type === "run_event") {
                    // Display-only game events (fail, clear, final result) for every participant.
                    const kinds = ["failed", "fell", "checkpoint", "stage_cleared", "run_result"];
                    const text = typeof message.text === "string" ? message.text : "";
                    const lines = Array.isArray(message.lines) ? message.lines : [];
                    if (!kinds.includes(message.kind) || text.length > 100 || lines.length > 16 ||
                        !lines.every(line => typeof line === "string" && line.length <= 100)) {
                        socket.close(1008, "Invalid run event");
                        return;
                    }
                    broadcast(room, { type: "run_event", kind: message.kind, text, lines });
                    return;
                }
                if (message.type === "stage_status") {
                    // Display-only: which stage the host is on and which web buttons apply.
                    const stageId = message.stageId;
                    const stageName = typeof message.stageName === "string" ? message.stageName : "";
                    const controls = typeof message.controls === "string" ? message.controls : "";
                    if (typeof stageId !== "string" || !/^[a-z0-9_-]{1,32}$/.test(stageId) ||
                        stageName.length > 40 || !/^[a-z,]{0,40}$/.test(controls)) {
                        socket.close(1008, "Invalid stage status");
                        return;
                    }
                    room.stage = { stageId, stageName, controls };
                    broadcast(room, { type: "stage_status", ...room.stage });
                    return;
                }
                if (message.type === "revoke_control") {
                    // Only the owning host can report an outcome for its currently running grant.
                    if (message.reason === "round_cleared" || message.reason === "round_failed") {
                        if (matchesGrant(room, message) && room.grant.active)
                            revoke(room, message.reason);
                        return;
                    }
                    if (["input_timeout", "video_timeout", "run_complete"].includes(message.reason)) {
                        if (matchesGrant(room, message)) revoke(room, message.reason);
                        return;
                    }
                    if (matches(message, room.grant)) revoke(room, "host_reclaimed");
                    return;
                }
                if (message.type === "remove_request") {
                    const entry = room.queue[queueIndex(room, message.controllerId)];
                    if (matches(message, entry)) dropRequest(room, entry.controllerId, "host_removed");
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
        // Unauthenticated sockets do not occupy a participant slot.
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
            if (room.participants.size >= maxParticipants) { socket.close(1008, "Room full"); return; }
            // One ID per connection, bound to this socket and room. Nicknames never identify anyone.
            let controllerId;
            do { controllerId = randomUUID(); } while (room.participants.has(controllerId));
            room.participants.set(controllerId, socket);
            send(room, socket, { type: "controller_hello", controllerId, hostOnline: true, iceServers });
            send(room, socket, { type: "stage_status", ...room.stage });
            notifyParticipants(room);
            log(`[room ${room.id}] participant joined (${room.participants.size})`);
            // Every grant-scoped message must come from the connection that owns it.
            const ownsGrant = message => matchesGrant(room, message) && room.grant.controllerId === controllerId;
            socket.on("message", (data, binary) => {
                if (room.closed || room.participants.get(controllerId) !== socket) return;
                const message = parse(socket, data, binary, room);
                if (!message) return;
                if (message.type === "request_control") {
                    // Already waiting or already playing: a second request changes nothing.
                    if (queueIndex(room, controllerId) >= 0 || room.grant?.controllerId === controllerId) return;
                    const nickname = typeof message.nickname === "string" ? message.nickname.trim() : "";
                    if (!nickname || nickname.length > 20 || !room.host ||
                        room.host.readyState !== WebSocket.OPEN) {
                        send(room, socket, { type: "request_rejected", reason:
                            !nickname || nickname.length > 20 ? "invalid_nickname" : "host_disconnected" });
                        return;
                    }
                    const entry = { controllerId, requestId: randomUUID(), nickname, requestedAt: performance.now() };
                    room.queue.push(entry);
                    send(room, socket, { type: "request_received", ...ids(entry) });
                    publishQueue(room);
                    return;
                }
                if (message.type === "video_answer") {
                    if (!ownsGrant(message) || !room.grant.offerSent || room.grant.answerSent) return;
                    if (!validSdp(message.sdp)) { revoke(room, "video_failed"); return; }
                    room.grant.answerSent = true;
                    if (!send(room, room.host, { type: "video_answer", ...ids(room.grant), sdp: message.sdp }))
                        revoke(room, "connection_lost");
                    return;
                }
                if (message.type === "video_ready") {
                    if (!ownsGrant(message) || !room.grant.answerSent ||
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
                    if (!ownsGrant(message) || room.grant.startAt === null) return;
                    room.grant.lastVideoAt = performance.now();
                    if (!send(room, room.host, { type: "video_alive", ...ids(room.grant) }))
                        revoke(room, "connection_lost");
                    return;
                }
                if (message.type === "video_failed") {
                    if (ownsGrant(message)) revoke(room, "video_failed");
                    return;
                }
                if (message.type === "release_control") {
                    const entry = room.queue[queueIndex(room, controllerId)];
                    if (matches(message, entry)) dropRequest(room, controllerId, "controller_stopped");
                    if (ownsGrant(message)) revoke(room, "controller_stopped");
                    return;
                }
                if (message.type !== "input") { socket.close(1008, "Invalid controller message"); return; }
                if (!ownsGrant(message) || !room.grant.active) return;
                const jump = message.jump ?? 0;
                const look = message.look ?? 0;
                const lookY = message.lookY ?? 0;
                if (!Number.isSafeInteger(message.sequence) || message.sequence < 0 ||
                    !Number.isFinite(message.x) || !Number.isFinite(message.y) ||
                    Math.abs(message.x) > 1 || Math.abs(message.y) > 1 ||
                    !Number.isSafeInteger(jump) || jump < 0 || jump > 1000000 ||
                    !Number.isFinite(look) || Math.abs(look) > 1 ||
                    !Number.isFinite(lookY) || Math.abs(lookY) > 1) {
                    revoke(room, "invalid_input");
                    socket.close(1008, "Invalid input");
                    return;
                }
                if (message.sequence <= room.grant.lastSequence) return;
                room.grant.lastSequence = message.sequence;
                room.grant.lastInputAt = performance.now();
                if (!send(room, room.host, { type: "input", ...ids(room.grant),
                    sequence: message.sequence, x: message.x, y: message.y, jump, look, lookY })) revoke(room, "host_disconnected");
            });
            socket.on("close", () => {
                if (room.closed || room.participants.get(controllerId) !== socket) return;
                room.participants.delete(controllerId);
                // Only this participant's own request or grant ends; the others stay connected.
                dropRequest(room, controllerId, "controller_disconnected", false);
                if (room.grant?.controllerId === controllerId) revoke(room, "controller_disconnected");
                notifyParticipants(room);
                log(`[room ${room.id}] participant left (${room.participants.size})`);
            });
        });
    });

    const timer = setInterval(() => {
        const now = performance.now();
        for (const room of rooms.values()) {
            if (room.grant && room.grant.transitioning &&
                now - room.grant.transitionAt >= transitionTimeout) revoke(room, "transition_timeout");
            if (room.grant && !room.grant.transitioning && !room.grant.active && room.grant.startAt === null &&
                now - room.grant.approvedAt >= readyTimeout) revoke(room, "ready_timeout");
            if (room.grant && room.grant.startAt !== null &&
                now - room.grant.lastVideoAt >= videoTimeout) revoke(room, "video_timeout");
            // Revocation removes the grant, so a cancelled countdown cannot restart it.
            if (room.grant && !room.grant.active && room.grant.startAt !== null && now >= room.grant.startAt) {
                room.grant.active = true;
                room.grant.lastInputAt = now;
                const notification = { type: "control_started", ...ids(room.grant) };
                if (!send(room, room.host, notification) ||
                    !send(room, participant(room, room.grant.controllerId), notification))
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
        const app = createApplication({ hostKey: config.hostKey, publicOrigin: config.publicOrigin,
            iceServers: config.iceServers });
        const relay = config.iceServers && config.iceServers.some(s => s.urls.some(u => !u.startsWith("stun:")));
        console.log(`[NaWaBa] ICE: ${relay ? "STUN + TURN relay configured" : "STUN only (no TURN relay)"}`);
        app.server.listen(config.port, config.listenHost, () => {
            console.log(`[server] listening on ${config.listenHost}:${config.port} (protocol 8)`);
            console.log("Guest status notifications enabled for supported Unity clients.");
            console.log("Each host creates a room. Copy its invite from the Unity multiplayer lobby.");
        });
    } catch (error) {
        console.error("Cannot start:", error.message);
        process.exitCode = 1;
    }
}
module.exports = { createApplication };
