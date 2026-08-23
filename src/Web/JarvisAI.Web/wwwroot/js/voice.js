window.jarvisVoice = (() => {
    'use strict';

    let connection = null;
    let audioContext = null;
    let mediaStream = null;
    let processorNode = null;
    let sourceNode = null;
    let audioElement = null;
    let playbackUrl = null;

    let settings = {
        voiceEnabled: true,
        micDeviceId: '',
        speakerDeviceId: '',
        wakeWordEnabled: true,
        bargeInEnabled: true,
        silenceTimeoutMs: 800,
        vadThreshold: 0.02,
        maxUtteranceSeconds: 20
    };

    let dotNetRef = null;
    let engineActive = false;

    function notify(method, arg) {
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync(method, arg).catch(() => {});
        }
    }

    function buildDiag() {
        let trackInfo = 'none';
        try {
            const t = mediaStream ? mediaStream.getAudioTracks()[0] : null;
            if (t) {
                const s = t.getSettings ? t.getSettings() : {};
                trackInfo = (t.label || '?') + '|' + t.readyState + '|muted=' + t.muted +
                    '|dev=' + ((s.deviceId || '').slice(0, 8) || '');
            }
        } catch (e) { trackInfo = 'err'; }
        return {
            rms: lastRms,
            frames: processedFrames,
            state: audioContext ? audioContext.state : 'none',
            listening,
            mic: mediaStream ? (mediaStream.getTracks().length + 'piste') : 'aucune',
            reconnecting: connection ? connection.state : 'none',
            rate: currentSampleRate,
            nz: lastNonZero,
            track: trackInfo
        };
    }

    setInterval(() => {
        if (dotNetRef) {
            notify('OnLevel', buildDiag());
        }
    }, 1000);

    function ensureAudioRunning() {
        if (audioContext && audioContext.state === 'suspended') {
            audioContext.resume().catch(() => {});
        }
        if (audioContext && audioContext.state === 'running') {
            document.removeEventListener('click', resumeHandler);
            document.removeEventListener('keydown', resumeHandler);
        }
    }
    function resumeHandler() {
        ensureAudioRunning();
    }
    if (typeof document !== 'undefined') {
        document.addEventListener('click', resumeHandler);
        document.addEventListener('keydown', resumeHandler);
    }

    let listening = false;
    let recording = false;
    let speaking = false;
    let vadState = {
        buffer: [],          // pcm16 chunks of current utterance
        rmsAbove: 0,         // consecutive frames above threshold
        silenceFrames: 0,    // consecutive frames below threshold
        silenceFramesReq: 2, // frames of silence to end (800ms @ 256ms/frame)
        utteranceBytes: 0,
        lastSend: 0,
        minChunkBytes: 3200  // ~100ms of 16k mono int16
    };
    let bargeInFrames = 0;
    let noiseFloor = 0.002;
    let quietSeconds = 0;
    let lastLevelNotify = 0;
    let processedFrames = 0;
    let lastRms = 0;
    let lastNonZero = 0;
    let currentSampleRate = 16000;

    function frameDurationMs() { return (4096 / currentSampleRate) * 1000; }

    function connect() {
        connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/voice')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Warning)
            .build();

        connection.on('voiceState', (state) => notify('OnState', state));
        connection.on('voiceTranscript', (text) => notify('OnTranscript', text));
        connection.on('voiceStatus', (msg) => notify('OnStatus', msg));
        connection.on('voiceStatusPanel', (json) => notify('OnStatusPanel', json));
        connection.on('voiceAudio', (b64wav) => playWav(b64wav));

        return connection.start().catch(() => {});
    }

    async function ensureConnection() {
        if (connection && connection.state === signalR.HubConnectionState.Connected) return true;
        if (!connection) {
            await connect();
        } else if (connection.state === signalR.HubConnectionState.Disconnected) {
            await connection.start().catch(() => {});
        }
        return connection && connection.state === signalR.HubConnectionState.Connected;
    }

    function updateSettings(newSettings) {
        settings = Object.assign({}, settings, newSettings);
        vadState.silenceFramesReq = Math.max(1, Math.round(settings.silenceTimeoutMs / frameDurationMs()));
    }

    async function getDevices() {
        if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) return [];
        try {
            const devices = await navigator.mediaDevices.enumerateDevices();
            return {
                mics: devices.filter(d => d.kind === 'audioinput').map(d => ({ id: d.deviceId, label: d.label || 'Microphone' })),
                speakers: devices.filter(d => d.kind === 'audiooutput').map(d => ({ id: d.deviceId, label: d.label || 'Haut-parleur' }))
            };
        } catch (e) {
            return { mics: [], speakers: [] };
        }
    }

    async function startListening(silent) {
        if (listening) return true;
        if (engineActive) {
            notify('OnStatus', 'Moteur vocal actif dans l’application');
            notify('OnState', 'engine');
            return false;
        }
        if (!await ensureConnection()) {
            notify('OnStatus', 'Connexion vocale impossible');
            return false;
        }

        try {
            const constraints = {
                audio: {
                    deviceId: settings.micDeviceId ? { exact: settings.micDeviceId } : undefined,
                    echoCancellation: false,
                    noiseSuppression: false,
                    autoGainControl: false
                }
            };
            mediaStream = await Promise.race([
                navigator.mediaDevices.getUserMedia(constraints),
                new Promise((_, reject) => setTimeout(() => reject(new Error('timeout (8s)')), 8000))
            ]);
            audioContext = new (window.AudioContext || window.webkitAudioContext)();
            currentSampleRate = audioContext.sampleRate || 16000;
            audioContext.resume().catch(() => {});
            if (audioContext.state === 'suspended') {
                notify('OnStatus', 'Clique ou tape une touche pour activer le micro');
            }

            sourceNode = audioContext.createMediaStreamSource(mediaStream);
            processorNode = audioContext.createScriptProcessor(4096, 1, 1);
            processorNode.onaudioprocess = onAudioProcess;
            sourceNode.connect(processorNode);
            processorNode.connect(audioContext.destination);

            listening = true;
            if (!silent) notify('OnListening', true);
            notify('OnState', 'listening');
            return true;
        } catch (e) {
            notify('OnStatus', 'Microphone inaccessible : ' + e.message);
            await stopListening();
            return false;
        }
    }

    async function stopListening(silent) {
        listening = false;
        recording = false;
        vadState.buffer = [];

        if (processorNode) { try { processorNode.disconnect(); } catch (e) {} processorNode = null; }
        if (sourceNode) { try { sourceNode.disconnect(); } catch (e) {} sourceNode = null; }
        if (mediaStream) { mediaStream.getTracks().forEach(t => t.stop()); mediaStream = null; }
        if (audioContext) { try { await audioContext.close(); } catch (e) {} audioContext = null; }

        if (!silent) notify('OnListening', false);
        notify('OnState', 'idle');
    }

    function computeRms(float32Buffer) {
        let sum = 0;
        for (let i = 0; i < float32Buffer.length; i++) sum += float32Buffer[i] * float32Buffer[i];
        return Math.sqrt(sum / float32Buffer.length);
    }

    function onAudioProcess(event) {
        const input = event.inputBuffer.getChannelData(0);
        processedFrames++;
        const rms = computeRms(input);
        let nz = 0;
        for (let i = 0; i < input.length; i++) {
            if (Math.abs(input[i]) > 0.0001) nz++;
        }
        lastNonZero = nz;

        if (rms < noiseFloor) noiseFloor = noiseFloor * 0.95 + rms * 0.05;
        else noiseFloor = noiseFloor * 0.9995;

        const quietFactor = quietSeconds > 12 ? 0.25 : quietSeconds > 6 ? 0.5 : quietSeconds > 2 ? 0.75 : 1;
        const threshold = Math.max(noiseFloor * 3, settings.vadThreshold * quietFactor);
        const isSpeech = rms > threshold;

        const now = performance.now();
        lastRms = Math.round(rms * 1000);
        if (now - lastLevelNotify > 250) {
            lastLevelNotify = now;
            notify('OnLevel', buildDiag());
        }

        if (speaking && settings.bargeInEnabled && isSpeech) {
            bargeInFrames++;
            if (bargeInFrames >= 2) {
                bargeInFrames = 0;
                interrupt();
            }
            return;
        }

        if (isSpeech) {
            quietSeconds = 0;
            vadState.rmsAbove++;
            vadState.silenceFrames = 0;
            if (vadState.rmsAbove >= 2 && !recording) {
                recording = true;
                vadState.buffer = [];
                vadState.utteranceBytes = 0;
            }
            if (recording) {
                vadState.buffer.push(int16ArrayToBase64(input));
                vadState.utteranceBytes += input.length * 2;

                if (vadState.utteranceBytes >= settings.maxUtteranceSeconds * 32000) {
                    flushUtterance();
                }
            }
        } else if (recording) {
            quietSeconds += frameDurationMs() / 1000;
            vadState.silenceFrames++;
            if (vadState.silenceFrames >= vadState.silenceFramesReq) {
                flushUtterance();
            }
        } else {
            quietSeconds += frameDurationMs() / 1000;
        }
    }

    function int16ArrayToBase64(float32Buffer) {
        const int16 = new Int16Array(float32Buffer.length);
        for (let i = 0; i < float32Buffer.length; i++) {
            const s = Math.max(-1, Math.min(1, float32Buffer[i]));
            int16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
        }
        let binary = '';
        const chunkSize = 0x8000;
        for (let i = 0; i < int16.length; i += chunkSize) {
            binary += String.fromCharCode.apply(null, int16.subarray(i, i + chunkSize));
        }
        return btoa(binary);
    }

    function flushUtterance() {
        if (!recording) return;
        recording = false;
        const chunks = vadState.buffer;
        vadState.buffer = [];
        vadState.rmsAbove = 0;
        vadState.utteranceBytes = 0;
        notify('OnState', 'processing');

        if (chunks.length === 0 || !connection || connection.state !== signalR.HubConnectionState.Connected) {
            notify('OnState', 'listening');
            return;
        }

        let i = 0;
        const sendNext = () => {
            if (i >= chunks.length) {
                connection.invoke('EndUtterance').catch(() => {});
                notify('OnState', 'listening');
                return;
            }
            connection.invoke('SendAudioChunk', chunks[i], currentSampleRate).then(sendNext).catch(() => {
                notify('OnState', 'listening');
            });
            i++;
        };
        sendNext();
    }

    async function playWav(b64wav) {
        if (engineActive) return;
        try {
            const bytes = Uint8Array.from(atob(b64wav), c => c.charCodeAt(0));
            const blob = new Blob([bytes], { type: 'audio/wav' });
            if (playbackUrl) URL.revokeObjectURL(playbackUrl);
            playbackUrl = URL.createObjectURL(blob);

            if (!audioElement) {
                audioElement = new Audio();
            }
            if (settings.speakerDeviceId && audioElement.setSinkId) {
                audioElement.setSinkId(settings.speakerDeviceId).catch(() => {});
            }
            audioElement.src = playbackUrl;

            speaking = true;
            notify('OnState', 'speaking');

            const started = await audioElement.play();
            await new Promise((resolve) => {
                audioElement.onended = resolve;
                audioElement.onerror = resolve;
            });
            if (started !== undefined) { /* play promise resolved */ }

            speaking = false;
            notify('OnState', 'listening');
        } catch (e) {
            speaking = false;
            notify('OnStatus', 'Lecture audio impossible : ' + e.message);
        }
    }

    async function interrupt() {
        speaking = false;
        if (audioElement) {
            try { audioElement.pause(); } catch (e) {}
            audioElement.currentTime = 0;
        }
        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            connection.invoke('Interrupt').catch(() => {});
        }
    }

    async function testTts(text, voice, engine, volume) {
        try {
            const res = await fetch('/api/voice/test', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ text, voice, engine, volume })
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                notify('OnStatus', err.error || 'Erreur TTS');
                return false;
            }
            const blob = await res.blob();
            if (playbackUrl) URL.revokeObjectURL(playbackUrl);
            playbackUrl = URL.createObjectURL(blob);
            if (!audioElement) audioElement = new Audio();
            if (settings.speakerDeviceId && audioElement.setSinkId) {
                audioElement.setSinkId(settings.speakerDeviceId).catch(() => {});
            }
            audioElement.src = playbackUrl;
            await audioElement.play();
            return true;
        } catch (e) {
            notify('OnStatus', 'Erreur TTS : ' + e.message);
            return false;
        }
    }

    return {
        init: async (cfg, dotNet) => {
            updateSettings(cfg);
            dotNetRef = dotNet;
            await ensureConnection();
        },
        updateSettings,
        getDevices,
        startListening,
        stopListening,
        isListening: () => listening,
        setEngineActive: (active) => {
            engineActive = !!active;
            if (engineActive) {
                stopListening(true);
                notify('OnStatus', 'Moteur vocal actif dans l’application');
                notify('OnState', 'engine');
            }
        },
        isEngineActive: () => engineActive,
        interrupt,
        testTts,
        getState: () => ({ listening, speaking })
    };
})();
