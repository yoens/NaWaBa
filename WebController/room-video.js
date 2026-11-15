/* One receive-only peer per approved grant. Signaling uses the existing room socket. */
(() => {
    "use strict";
    class NaWaBaRoomVideoReceiver {
        constructor({ video, stats, playButton, onAnswer, onReady, onFailure, onStatus }) {
            Object.assign(this, { video, stats, playButton, onAnswer, onReady, onFailure, onStatus });
            this.session = null;
            this.iceServers = [{ urls: "stun:stun.l.google.com:19302" }];
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
                cancelGather: null, statsTimer: null, readingStats: false, previousStats: null,
                // Diagnostics: what this browser could gather, and why it failed.
                candidates: { host: 0, srflx: 0, relay: 0 }, gatherStart: performance.now(),
                gatherMs: 0, stunError: "", dropTimer: null };
            this.session = s;
            try {
                s.peer = new RTCPeerConnection({ iceServers: this.iceServers });
                s.peer.onicecandidate = event => {
                    const type = event.candidate && event.candidate.type;
                    if (type && type in s.candidates) s.candidates[type]++;
                    if (!event.candidate) s.gatherMs = performance.now() - s.gatherStart;
                };
                s.peer.onicecandidateerror = event => {
                    // 701 = the STUN/TURN server could not be reached from this PC.
                    s.stunError = `${event.errorCode || "?"} ${event.url || ""}`.trim();
                };
                s.peer.onconnectionstatechange = () => {
                    if (!this.current(s)) return;
                    const state = s.peer.connectionState;
                    clearTimeout(s.dropTimer);
                    if (state === "failed" && !s.route) {
                        console.warn("[NaWaBa video] ICE failed", this.diagnosis(s));
                        const noStun = s.candidates.srflx === 0 && s.candidates.relay === 0;
                        this.onFailure((noStun
                            ? "영상 연결 실패: 이 PC에서 STUN 서버에 닿지 못했습니다. VPN·백신·방화벽이 UDP를 막고 있는지 확인하세요. "
                            : this.hasRelay()
                                ? "영상 연결 실패: 릴레이 서버를 거쳐도 연결되지 않았습니다. "
                                : "영상 연결 실패: 이 네트워크에서는 직접 연결이 막혀 있을 수 있습니다 (TURN 릴레이 서버 필요). ") +
                            this.diagnosis(s));
                    } else if (state === "failed" || state === "closed")
                        this.onFailure("영상 연결이 끊겨 조작을 중지했습니다. 다시 신청하세요. " + this.diagnosis(s));
                    else if (state === "disconnected")
                        // Often recovers by itself within a few seconds; fail only if it does not.
                        s.dropTimer = setTimeout(() => {
                            if (this.current(s) && s.peer.connectionState === "disconnected")
                                this.onFailure("영상 연결이 끊겨 조작을 중지했습니다. 다시 신청하세요. " + this.diagnosis(s));
                        }, 4000);
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
            } catch (error) {
                console.warn("[NaWaBa video] negotiation failed", error, this.diagnosis(s));
                if (this.current(s)) this.onFailure("영상 연결에 실패했습니다. 네트워크를 확인한 뒤 다시 신청하세요. " +
                    `(${error && error.message ? error.message : error}) ` + this.diagnosis(s));
            }
        }
        // One line for bug reports: what was gathered and where it stopped.
        diagnosis(s) {
            const c = s.candidates;
            const seconds = ((s.gatherMs || performance.now() - s.gatherStart) / 1000).toFixed(1);
            return `[진단: 후보 host ${c.host} / srflx ${c.srflx} / relay ${c.relay} · 수집 ${seconds}초 · ` +
                `ICE ${s.peer ? s.peer.iceConnectionState : "?"}` + (s.stunError ? ` · STUN 오류 ${s.stunError}` : "") + "]";
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
                const found = () => s.candidates.host + s.candidates.srflx + s.candidates.relay > 0;
                const check = () => { if (s.peer.iceGatheringState === "complete") finish(); };
                // A blocked STUN server or extra network adapters (VPN, virtual machines) can make
                // gathering take 10 s or more. Do not wait for it: answer with what we have.
                const early = setTimeout(() => { if (found()) finish(); }, 4000);
                const timer = setTimeout(() => finish(found() ? null : new Error("ICE 후보를 하나도 찾지 못함")), 15000);
                const done = finish;
                s.cancelGather = () => { clearTimeout(early); done(new Error("Cancelled")); };
                s.peer.addEventListener("icegatheringstatechange", check);
                s.peer.addEventListener("icegatheringstatechange", () => {
                    if (s.peer.iceGatheringState === "complete") clearTimeout(early);
                });
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
                s.route = this.readRoute(reports) || s.route;
                for (const report of reports.values()) {
                    if (report.type !== "inbound-rtp" || (report.kind || report.mediaType) !== "video") continue;
                    const previous = s.previousStats;
                    const seconds = previous ? (report.timestamp - previous.timestamp) / 1000 : 0;
                    const fps = report.framesPerSecond ?? (seconds > 0
                        ? (report.framesDecoded - previous.framesDecoded) / seconds : 0);
                    const mbps = seconds > 0 ? (report.bytesReceived - previous.bytesReceived) * 8 / seconds / 1e6 : 0;
                    const codec = reports.get(report.codecId)?.mimeType || "video";
                    this.stats.textContent = `${report.frameWidth || this.video.videoWidth} × ${report.frameHeight || this.video.videoHeight}` +
                        ` | ${Number(fps || 0).toFixed(1)} fps | ${mbps.toFixed(2)} Mbps | ${codec}` +
                        (s.route ? ` | ${s.route}` : "");
                    s.previousStats = { timestamp: report.timestamp, framesDecoded: report.framesDecoded,
                        bytesReceived: report.bytesReceived };
                }
            } catch { /* Stats are diagnostic; frame freshness governs safety. */ }
            finally { s.readingStats = false; }
        }
        // Self-test for "video never connects on this PC": gathers candidates with the same
        // servers and reports what this browser / network can do. Returns text lines.
        async diagnose() {
            const lines = [`브라우저: ${navigator.userAgent}`];
            if (typeof RTCPeerConnection !== "function") {
                lines.push("✗ 이 브라우저는 WebRTC를 쓸 수 없습니다 (확장 프로그램·설정에서 차단됐거나 지원 안 함).");
                return lines;
            }
            const codecs = (RTCRtpReceiver.getCapabilities && RTCRtpReceiver.getCapabilities("video") || { codecs: [] }).codecs
                .map(c => c.mimeType.replace("video/", "")).filter((c, i, all) => all.indexOf(c) === i && !/rtx|red|ulpfec|flexfec/i.test(c));
            lines.push(`영상 코덱: ${codecs.join(", ") || "없음"}` + (codecs.length ? "" : "  ✗ 영상을 받을 수 없습니다."));
            lines.push(`서버 목록: ${this.iceServers.map(x => [].concat(x.urls).join(" ")).join(" | ")}`);
            const peer = new RTCPeerConnection({ iceServers: this.iceServers });
            const count = { host: 0, srflx: 0, relay: 0 }, errors = [];
            const started = performance.now();
            let mdns = false;
            peer.onicecandidate = e => {
                if (!e.candidate) return;
                if (e.candidate.type in count) count[e.candidate.type]++;
                if (/\.local/.test(e.candidate.address || e.candidate.candidate)) mdns = true;
            };
            peer.onicecandidateerror = e => errors.push(`${e.errorCode} ${e.url || ""} ${e.errorText || ""}`.trim());
            try {
                peer.addTransceiver("video", { direction: "recvonly" });
                await peer.setLocalDescription(await peer.createOffer());
                await new Promise(resolve => {
                    const timer = setTimeout(resolve, 12000);
                    peer.onicegatheringstatechange = () => {
                        if (peer.iceGatheringState === "complete") { clearTimeout(timer); resolve(); }
                    };
                });
            } catch (error) {
                lines.push(`✗ 연결 준비 실패: ${error && error.message ? error.message : error}`);
            }
            const seconds = ((performance.now() - started) / 1000).toFixed(1);
            const finished = peer.iceGatheringState === "complete";
            peer.close();
            lines.push(`후보: host ${count.host} / srflx ${count.srflx} / relay ${count.relay}` +
                ` · 수집 ${seconds}초${finished ? "" : " (12초 안에 끝나지 않음)"}${mdns ? " · 내부 IP 숨김(mDNS)" : ""}`);
            if (errors.length) lines.push(`STUN/TURN 오류: ${errors.slice(0, 4).join(" ; ")}`);
            if (count.host + count.srflx + count.relay === 0)
                lines.push("✗ 후보가 하나도 없습니다. 브라우저 확장·정책이 WebRTC를 막고 있습니다.");
            else if (count.srflx === 0 && count.relay === 0)
                lines.push("✗ STUN 서버에 닿지 못했습니다. 이 PC/네트워크에서 UDP가 막혀 있습니다 " +
                    "(VPN, 백신·방화벽, 회사·학교망). 같은 공유기 안의 호스트와만 연결될 수 있습니다.");
            else if (count.relay === 0)
                lines.push("✓ STUN 통과. 대부분의 호스트와 직접 연결됩니다. 그래도 실패하면 양쪽 공유기 조합(대칭형 NAT) 문제라 TURN 릴레이가 필요합니다.");
            else lines.push("✓ STUN·TURN 모두 사용 가능.");
            if (Number(seconds) > 6) lines.push("△ 후보 수집이 느립니다 (VPN·가상 네트워크 어댑터가 많거나 STUN이 일부 막힘).");
            return lines;
        }
        hasRelay() {
            return this.iceServers.some(server => [].concat(server.urls).some(url => /^turns?:/.test(url)));
        }
        // How the video reaches this browser: direct (host), through NAT (srflx) or TURN relay.
        readRoute(reports) {
            let pair = null;
            for (const report of reports.values()) {
                if (report.type === "transport" && report.selectedCandidatePairId)
                    pair = reports.get(report.selectedCandidatePairId);
            }
            if (!pair) for (const report of reports.values())
                if (report.type === "candidate-pair" && report.state === "succeeded" && report.nominated) pair = report;
            if (!pair) return "";
            const local = reports.get(pair.localCandidateId), remote = reports.get(pair.remoteCandidateId);
            const name = type => ({ host: "직접", srflx: "NAT 통과", prflx: "NAT 통과", relay: "TURN 릴레이" })[type] || type || "?";
            const rtt = pair.currentRoundTripTime ? ` ${Math.round(pair.currentRoundTripTime * 1000)}ms` : "";
            return `연결: ${name(local?.candidateType)}↔${name(remote?.candidateType)}${rtt}`;
        }
        stop() {
            const s = this.session;
            this.session = null; // Invalidate awaited operations before closing native resources.
            if (s) {
                s.closed = true;
                if (s.cancelGather) s.cancelGather();
                clearInterval(s.statsTimer);
                clearTimeout(s.dropTimer);
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
