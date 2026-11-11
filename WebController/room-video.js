/* One receive-only peer per approved grant. Signaling uses the existing room socket. */
(() => {
    "use strict";
    class NaWaBaRoomVideoReceiver {
        constructor({ video, stats, playButton, onAnswer, onReady, onFailure, onStatus }) {
            Object.assign(this, { video, stats, playButton, onAnswer, onReady, onFailure, onStatus });
            this.session = null;
            playButton.addEventListener("click", () => this.tryPlay());
        }
        current(s) { return this.session === s && !s.closed; }
        start(grantId) {
            this.stop();
            if (typeof RTCPeerConnection !== "function" ||
                typeof this.video.requestVideoFrameCallback !== "function") {
                this.onFailure("영상 재생을 지원하는 최신 Chrome 또는 Edge로 접속하세요.");
                return;
            }
            const s = { grantId, peer: null, stream: null, closed: false, offerSeen: false,
                answerSent: false, readySent: false, frameAt: 0, frames: 0, frameCallback: null,
                cancelGather: null, statsTimer: null, readingStats: false, previousStats: null };
            this.session = s;
            try {
                s.peer = new RTCPeerConnection({ iceServers: [{ urls: "stun:stun.l.google.com:19302" }] });
                s.peer.onconnectionstatechange = () => {
                    if (!this.current(s)) return;
                    if (["failed", "disconnected", "closed"].includes(s.peer.connectionState))
                        this.onFailure("영상 연결이 끊겨 조작을 중지했습니다. 다시 신청하세요.");
                };
                s.peer.ontrack = event => {
                    if (!this.current(s) || event.track.kind !== "video") return;
                    if (s.stream) return;
                    s.stream = new MediaStream([event.track]);
                    event.track.onended = () => {
                        if (this.current(s)) this.onFailure("영상 전송이 종료되었습니다.");
                    };
                    this.video.srcObject = s.stream;
                    this.watchFrames(s);
                    this.tryPlay();
                };
                s.statsTimer = setInterval(() => this.readStats(s), 1000);
                this.stats.textContent = "영상 연결 중…";
            } catch {
                if (this.current(s)) this.onFailure("영상 수신기를 만들 수 없습니다.");
            }
        }
        async acceptOffer(grantId, sdp) {
            const s = this.session;
            if (!s || s.grantId !== grantId || s.offerSeen) return;
            s.offerSeen = true;
            try {
                if (typeof sdp !== "string" || sdp.length > 60000 || !sdp.startsWith("v=0"))
                    throw new Error("Invalid offer");
                await s.peer.setRemoteDescription({ type: "offer", sdp });
                if (!this.current(s)) return;
                const answer = await s.peer.createAnswer();
                if (!this.current(s)) return;
                await s.peer.setLocalDescription(answer);
                if (!this.current(s)) return;
                await this.gather(s);
                if (!this.current(s)) return;
                if (!this.onAnswer(s.grantId, s.peer.localDescription.sdp)) throw new Error("Signaling failed");
                s.answerSent = true;
                this.maybeReady(s);
            } catch {
                if (this.current(s)) this.onFailure("영상 연결에 실패했습니다. 네트워크를 확인한 뒤 다시 신청하세요.");
            }
        }
        gather(s) {
            if (s.peer.iceGatheringState === "complete") return Promise.resolve();
            return new Promise((resolve, reject) => {
                const finish = error => {
                    clearTimeout(timer);
                    s.peer.removeEventListener("icegatheringstatechange", check);
                    s.cancelGather = null;
                    if (error) reject(error); else resolve();
                };
                const check = () => { if (s.peer.iceGatheringState === "complete") finish(); };
                const timer = setTimeout(() => finish(new Error("ICE timeout")), 15000);
                s.cancelGather = () => finish(new Error("Cancelled"));
                s.peer.addEventListener("icegatheringstatechange", check);
                check();
            });
        }
        async tryPlay() {
            const s = this.session;
            if (!s || !s.stream) return;
            try {
                await this.video.play();
                if (this.current(s)) this.playButton.hidden = true;
            } catch {
                if (!this.current(s)) return;
                this.playButton.hidden = false;
                this.onStatus("‘영상 재생’을 누르면 화면 확인 후 카운트다운이 시작됩니다.");
            }
        }
        watchFrames(s) {
            if (!this.current(s)) return;
            s.frameCallback = this.video.requestVideoFrameCallback((now, metadata) => {
                if (!this.current(s)) return;
                if (this.video.videoWidth > 0 && metadata.presentedFrames > s.frames) {
                    s.frames = metadata.presentedFrames;
                    s.frameAt = performance.now();
                    this.maybeReady(s);
                }
                this.watchFrames(s);
            });
        }
        maybeReady(s) {
            if (!this.current(s) || s.readySent || !s.answerSent || !s.frameAt) return;
            s.readySent = true;
            this.onReady(s.grantId);
        }
        isFresh() {
            const s = this.session;
            return !!s && s.readySent && s.frameAt > 0 && performance.now() - s.frameAt < 1000;
        }
        async readStats(s) {
            if (!this.current(s) || s.readingStats) return;
            s.readingStats = true;
            try {
                const reports = await s.peer.getStats();
                if (!this.current(s)) return;
                for (const report of reports.values()) {
                    if (report.type !== "inbound-rtp" || (report.kind || report.mediaType) !== "video") continue;
                    const previous = s.previousStats;
                    const seconds = previous ? (report.timestamp - previous.timestamp) / 1000 : 0;
                    const fps = report.framesPerSecond ?? (seconds > 0
                        ? (report.framesDecoded - previous.framesDecoded) / seconds : 0);
                    const mbps = seconds > 0 ? (report.bytesReceived - previous.bytesReceived) * 8 / seconds / 1e6 : 0;
                    const codec = reports.get(report.codecId)?.mimeType || "video";
                    this.stats.textContent = `${report.frameWidth || this.video.videoWidth} × ${report.frameHeight || this.video.videoHeight}` +
                        ` | ${Number(fps || 0).toFixed(1)} fps | ${mbps.toFixed(2)} Mbps | ${codec}`;
                    s.previousStats = { timestamp: report.timestamp, framesDecoded: report.framesDecoded,
                        bytesReceived: report.bytesReceived };
                }
            } catch { /* Stats are diagnostic; frame freshness governs safety. */ }
            finally { s.readingStats = false; }
        }
        stop() {
            const s = this.session;
            this.session = null; // Invalidate awaited operations before closing native resources.
            if (s) {
                s.closed = true;
                if (s.cancelGather) s.cancelGather();
                clearInterval(s.statsTimer);
                if (s.frameCallback !== null) this.video.cancelVideoFrameCallback(s.frameCallback);
                if (s.peer) { s.peer.ontrack = null; s.peer.onconnectionstatechange = null; s.peer.close(); }
                if (s.stream) for (const track of s.stream.getTracks()) { track.onended = null; track.stop(); }
            }
            this.video.pause();
            this.video.srcObject = null;
            this.playButton.hidden = true;
            this.stats.textContent = "승인 후 게임 영상이 표시됩니다.";
        }
    }
    window.NaWaBaRoomVideoReceiver = NaWaBaRoomVideoReceiver;
})();
