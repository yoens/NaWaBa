const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const { WebSocket, WebSocketServer } = require("ws");

const PORT = 3000;
const ORIGIN = `http://127.0.0.1:${PORT}`;
const INPUT_TIMEOUT = 500;

// 브라우저에 index.html을 제공합니다.
const server = http.createServer((request, response) => {
    if (request.url !== "/" && request.url !== "/index.html") {
        response.writeHead(404);
        response.end("Not Found");
        return;
    }

    fs.readFile(path.join(__dirname, "index.html"), (error, data) => {
        if (error) {
            response.writeHead(500);
            response.end("Cannot read index.html");
            return;
        }

        response.writeHead(200, {
            "Content-Type": "text/html; charset=utf-8",
            "Cache-Control": "no-store"
        });
        response.end(data);
    });
});

const wss = new WebSocketServer({
    server,
    maxPayload: 1024
});

let host = null;
let controller = null;

// Unity가 연결되어 있을 때만 전달합니다.
function sendToHost(message) {
    if (!host || host.readyState !== WebSocket.OPEN) return;

    // 입력이 쌓이면 연결을 종료해 오래된 조작이 밀려가지 않게 합니다.
    if (host.bufferedAmount > 4096) {
        host.terminate();
        return;
    }

    host.send(JSON.stringify(message));
}

wss.on("connection", (socket, request) => {
    socket.on("error", error => console.error(error.message));

    const origin = request.headers.origin;

    if (origin && origin !== ORIGIN) {
        socket.close(1008, "Invalid origin");
        return;
    }

    // 다음 단계에서 Unity가 사용할 연결입니다.
    if (request.url === "/host") {
        if (host) {
            socket.close(1008, "Host already connected");
            return;
        }

        host = socket;
        console.log("[host] connected");

        socket.on("close", () => {
            if (host === socket) host = null;
            console.log("[host] disconnected");
        });

        return;
    }

    if (request.url !== "/controller" || origin !== ORIGIN) {
        socket.close(1008, "Invalid controller connection");
        return;
    }

    if (controller) {
        socket.close(1008, "Controller already connected");
        return;
    }

    controller = socket;
    console.log("[controller] connected");

    let lastSequence = -1;
    let lastReceivedTime = 0;
    let hasFreshInput = false;
    let lastLoggedInput = "";

    const timeoutCheck = setInterval(() => {
        if (hasFreshInput &&
            performance.now() - lastReceivedTime >= INPUT_TIMEOUT) {
            hasFreshInput = false;
            lastLoggedInput = "";

            sendToHost({ protocolVersion: 1, type: "reset" });
            console.log("[input] timeout -> reset");
        }
    }, 100);

    socket.on("message", (data, isBinary) => {
        let message;

        try {
            if (isBinary) throw new Error("Binary input is not supported");

            message = JSON.parse(data.toString());

            if (
                !message ||
                message.protocolVersion !== 1 ||
                message.type !== "input" ||
                !Number.isSafeInteger(message.sequence) ||
                message.sequence < 0 ||
                !Number.isFinite(message.x) ||
                !Number.isFinite(message.y) ||
                Math.abs(message.x) > 1 ||
                Math.abs(message.y) > 1
            ) {
                throw new Error("Invalid input");
            }
        } catch {
            socket.close(1008, "Invalid input");
            return;
        }

        if (message.sequence <= lastSequence) return;

        lastSequence = message.sequence;
        lastReceivedTime = performance.now();
        hasFreshInput = true;

        sendToHost({
            protocolVersion: 1,
            type: "input",
            sequence: message.sequence,
            x: message.x,
            y: message.y
        });

        // 주기적으로 같은 입력이 와도 로그는 방향이 바뀔 때만 출력합니다.
        const text = `x=${message.x}, y=${message.y}`;

        if (text !== lastLoggedInput) {
            console.log(`[input] ${text}`);
            lastLoggedInput = text;
        }
    });

    socket.on("close", () => {
        clearInterval(timeoutCheck);

        if (controller === socket) {
            controller = null;
            sendToHost({ protocolVersion: 1, type: "reset" });
        }

        console.log("[controller] disconnected -> reset");
    });
});

server.on("error", error => {
    console.error("Server error:", error.message);
});

// 같은 PC에서만 접속할 수 있는 개발용 서버입니다.
server.listen(PORT, "127.0.0.1", () => {
    console.log(`Open ${ORIGIN}`);
});