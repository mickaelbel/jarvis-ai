window.jarvis = {
    scrollToBottom: function (id) {
        var el = document.getElementById(id);
        if (el) { el.scrollTop = el.scrollHeight; }
    },

    copyText: function (text) {
        navigator.clipboard.writeText(text).catch(function () { });
    },

    playTts: function (text, voice, speed) {
        return fetch('/api/voice/speak', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ text: text, voice: voice || null, speed: speed || 1.0 })
        }).then(function (resp) {
            if (!resp.ok) { throw new Error('HTTP ' + resp.status); }
            return resp.arrayBuffer();
        }).then(function (buffer) {
            var ctx = window.__jarvisAudioCtx;
            if (!ctx) { ctx = new (window.AudioContext || window.webkitAudioContext)(); window.__jarvisAudioCtx = ctx; }
            return ctx.decodeAudioData(buffer).then(function (audio) {
                var src = ctx.createBufferSource();
                src.buffer = audio;
                var gain = ctx.createGain();
                gain.connect(ctx.destination);
                src.connect(gain);
                src.start();
                window.__jarvisTtsStop = function () { try { src.stop(); } catch (e) { } };
            });
        });
    },

    stopTts: function () {
        if (window.__jarvisTtsStop) { window.__jarvisTtsStop(); window.__jarvisTtsStop = null; }
    },

    copyCode: function (btn) {
        var code = btn.closest('.code-header').nextElementSibling;
        if (code) {
            navigator.clipboard.writeText(code.textContent).catch(function () { });
            var orig = btn.innerHTML;
            btn.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="14" height="14"><polyline points="20 6 9 17 4 12"></polyline></svg> Copied!';
            setTimeout(function () { btn.innerHTML = orig; }, 2000);
        }
    },

    highlightCode: function () {
        if (typeof hljs !== 'undefined') {
            document.querySelectorAll('pre code').forEach(function (block) {
                if (!block.dataset.highlighted) {
                    hljs.highlightElement(block);
                    block.dataset.highlighted = 'true';
                }
            });
        }
    },

    renderMath: function () {
        if (typeof renderMathInElement !== 'undefined') {
            renderMathInElement(document.body, {
                delimiters: [
                    { left: '$$', right: '$$', display: true },
                    { left: '$', right: '$', display: false },
                    { left: '\\[', right: '\\]', display: true },
                    { left: '\\(', right: '\\)', display: false }
                ]
            });
        }
    },

    autoResize: function (el) {
        el.style.height = 'auto';
        el.style.height = Math.min(el.scrollHeight, 200) + 'px';
    },

    focusInput: function (id) {
        var el = document.getElementById(id);
        if (el) setTimeout(function () { el.focus(); }, 100);
    },

    resetScroll: function (id) {
        var el = document.getElementById(id);
        if (el) el.scrollTop = 0;
    },

    // Fait défiler la liste interne quand la molette est utilisée n'importe où
    // sur la modale, et empêche le scroll de « fuir » vers la page derrière.
    modalWheel: function (modalId, listId) {
        var modal = document.getElementById(modalId);
        var list = document.getElementById(listId);
        if (!modal || !list) return;
        if (modal.dataset.jarvisWheel === '1') return;
        modal.dataset.jarvisWheel = '1';
        var onWheel = function (e) {
            var delta = e.deltaY;
            if (delta === 0) return;
            var maxScroll = list.scrollHeight - list.clientHeight;
            var atTop = list.scrollTop <= 0 && delta < 0;
            var atBottom = maxScroll <= 0 || (list.scrollTop >= maxScroll - 1 && delta > 0);
            if (!atTop && !atBottom) list.scrollTop += delta;
            e.preventDefault();
        };
        modal.addEventListener('wheel', onWheel, { passive: false });
    },

    downloadFile: function (fileName, contentType, base64) {
        try {
            var byteCharacters = atob(base64);
            var byteNumbers = new Array(byteCharacters.length);
            for (var i = 0; i < byteCharacters.length; i++) {
                byteNumbers[i] = byteCharacters.charCodeAt(i);
            }
            var byteArray = new Uint8Array(byteNumbers);
            var blob = new Blob([byteArray], { type: contentType || 'application/octet-stream' });
            var url = URL.createObjectURL(blob);
            var a = document.createElement('a');
            a.href = url;
            a.download = fileName;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
        } catch (e) {
            console.error('downloadFile failed', e);
        }
    },

    getPref: function (key) {
        try { return window.localStorage.getItem(key) || ''; } catch (e) { return ''; }
    },

    setPref: function (key, value) {
        try { window.localStorage.setItem(key, String(value)); } catch (e) { }
    }
};
