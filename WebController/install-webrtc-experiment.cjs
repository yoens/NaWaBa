"use strict";
// Preserve the existing game server: only insert the experiment's three hooks.
const fs = require("node:fs");
const path = require("node:path");
const filename = path.join(__dirname, "server.cjs");
const original = fs.readFileSync(filename, "utf8");
const newline = original.includes("\r\n") ? "\r\n" : "\n";
let source = original.replace(/\r\n/g, "\n");
const markers = [
    'const { createVideoExperiment } = require("./webrtc-experiment.cjs");',
    '    const videoExperiment = createVideoExperiment({ hostKey, getOrigin: () => origin() });',
    '        if (videoExperiment(request, response)) return;'
];
const installed = markers.map(marker => source.includes(marker));
if (installed.every(Boolean)) {
    console.log("WebRTC experiment hooks already installed. No changes made.");
    process.exit(0);
}
if (installed.some(Boolean)) throw new Error("Partial installation found. Keep server.cjs unchanged and inspect the diff.");
const anchors = [
    'const { readServerConfig } = require("./server-config.cjs");',
    '    const server = http.createServer((request, response) => {',
    '    const origin = () => options.publicOrigin || `http://127.0.0.1:${server.address().port}`;'
];
for (const anchor of anchors) {
    if (source.split(anchor).length !== 2)
        throw new Error("Server layout differs from the supported version. No changes written. Share server.cjs code (not host configuration).");
}
source = source.replace(anchors[0], anchors[0] + "\n" + markers[0]);
source = source.replace(anchors[1], markers[1] + "\n\n" + anchors[1] + "\n" + markers[2]);
fs.writeFileSync(filename, source.replace(/\n/g, newline));
console.log("Installed /webrtc-experiment/ into the existing server. Review git diff -- server.cjs.");
