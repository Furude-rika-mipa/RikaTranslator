/* Rika Translator: Flower Knight Girl Viewer dialogue adapter.
 * Game scripts stay untouched; translations use a separate display layer.
 * Only a local bridge credential is installed here, never an upstream API key.
 */
(function () {
    "use strict";
    if (window.RikaNwAdapter) return;
    var config = /*__RIKA_NW_SETTINGS__*/ null;
    if (!config || !/^http:\/\/127\.0\.0\.1:\d{4,5}\/bridge\/translate$/.test(config.endpoint) ||
        !/^[a-zA-Z0-9_-]{32,128}$/.test(config.token) || !/^[a-zA-Z0-9_-]{1,64}$/.test(config.gameId)) return;

    var ui = typeof window.RikaViewerUiFactory === "function" ? window.RikaViewerUiFactory() : null;
    var box = null, observer = null, overlay = null, savedVisibility = "";
    var lastMarkup = null, lastContext = null, revision = 0, timer = null;
    var enabled = false, applied = 0, heartbeatBusy = false, lastWarning = -60000;
    var cache = new Map(), pending = new Map(), retryAt = new Map();
    var prefetchEnabled = false, configuration = "", cacheRevision = 0, prefetchJob = null, prefetchRetry = 0;
    var session = Date.now().toString(36) + "-" + Math.random().toString(36).slice(2);
    var controls = /(?:\\[A-Za-z]+(?:\[[^\]\r\n]*\])?|%\d*\$?[sdiuf]|\{[^{}\r\n]*\})/g;

    function context() {
        var s = window.scene || {}, story = s.story || {}, view = window.main && window.main.view;
        return JSON.stringify([s.id, s.type, s.index, s.pauseIdx, story.id, story.section, story.part, view && view.current]);
    }
    function currentBox() {
        if (window.tlTools && window.tlTools.active) return null;
        var s = window.scene;
        var node = s && s.elements && s.elements.textBoxText;
        return node && node.isConnected ? node : null;
    }
    function hideTranslation() {
        if (overlay) { overlay.remove(); overlay = null; }
        if (box && box.style.visibility === "hidden") box.style.visibility = savedVisibility;
    }
    function plainText(node) {
        var copy = node.cloneNode(true);
        copy.querySelectorAll("rt,rp,script,style").forEach(function (n) { n.remove(); });
        copy.querySelectorAll("br").forEach(function (n) { n.replaceWith("\n"); });
        return copy.textContent.replace(/\u00a0/g, " ").trim();
    }
    function usable(text) {
        return text.length > 0 && text.length <= 8192 && /[A-Za-z\u00c0-\uffff]/.test(text.replace(controls, ""));
    }
    function safe(source, translated) {
        return typeof translated === "string" && translated.trim().length > 0 && translated.length <= 32768 &&
            JSON.stringify(source.match(controls) || []) === JSON.stringify(translated.match(controls) || []);
    }
    function post(url, body, job) {
        return new Promise(function (resolve, reject) {
            var xhr = new XMLHttpRequest(), done = false;
            if (job) job.xhr = xhr;
            function fail() { if (!done) { done = true; reject(new Error("Local translation unavailable")); } }
            xhr.open("POST", url, true);
            xhr.timeout = 65000;
            xhr.setRequestHeader("Content-Type", "application/json");
            xhr.onload = function () {
                if (done) return;
                if (xhr.status < 200 || xhr.status >= 300) { fail(); return; }
                try { var value = JSON.parse(xhr.responseText); done = true; resolve(value); }
                catch (_) { fail(); }
            };
            xhr.onerror = xhr.ontimeout = xhr.onabort = fail;
            try { xhr.send(JSON.stringify(body)); } catch (_) { fail(); }
        });
    }
    function remember(source, translated) {
        cache.delete(source); cache.set(source, translated);
        while (cache.size > 2000) cache.delete(cache.keys().next().value);
    }
    function request(text) {
        if (cache.has(text)) return Promise.resolve(cache.get(text));
        if (pending.has(text)) return pending.get(text);
        if (pending.size >= 2 || (retryAt.get(text) || 0) > Date.now()) return null;
        var epoch = cacheRevision;
        var task = post(config.endpoint, { gameId: config.gameId, token: config.token, text: text }).then(function (result) {
            if (epoch !== cacheRevision || (result.configuration && configuration && result.configuration !== configuration)) return null;
            if (!safe(text, result.text)) throw new Error("Invalid translation");
            remember(text, result.text); retryAt.delete(text); return result.text;
        }).catch(function () {
            if (epoch !== cacheRevision) return null;
            retryAt.set(text, Date.now() + 5000);
            while (retryAt.size > 256) retryAt.delete(retryAt.keys().next().value);
            if (Date.now() - lastWarning > 60000) {
                lastWarning = Date.now();
                console.warn("[Rika Translator] 暂时未取得译文，继续显示原文。请查看工具运行日志。");
            }
            return null;
        }).then(function (value) { if (pending.get(text) === task) pending.delete(text); return value; });
        pending.set(text, task);
        return task;
    }
    function sceneKey() {
        var s = window.scene || {}, story = s.story || {};
        return JSON.stringify([s.id, s.type, story.id, story.section, story.part]);
    }
    function cancelPrefetch() {
        var job = prefetchJob; prefetchJob = null;
        if (job && job.xhr) job.xhr.abort();
    }
    function futureText(raw) {
        var text = raw.replace(/\\n/g, "<br />"), s = window.scene;
        // Match addRPGXText's optional ruby handling without running any game command.
        if (!s.translated && window.prefs && prefs.scene && prefs.scene.furigana && typeof furigana !== "undefined" && text.includes("（")) {
            var reading = (text.split("（")[1] || "").split("）")[0], word = furigana.get(reading);
            if (word !== undefined) text = text.replace("（" + reading + "）", "");
        }
        var fragment = document.createElement("template");
        fragment.innerHTML = text;
        return plainText(fragment.content);
    }
    function lookahead() {
        var s = window.scene;
        if (!s || !s.paused || s.choice || !Array.isArray(s.script) || !Number.isInteger(s.index) || s.index < 0 ||
            !((typeof H_RPGX !== "undefined" && s.type === H_RPGX) || (typeof STORY_RPGX !== "undefined" && s.type === STORY_RPGX))) return [];
        var lines = [], candidate = null, characters = 0;
        var visual = /^(?:GROUP|\/GROUP|NAME_PLATE|VOICE_PLAY|SPINE|SPINE_PLAY|SPINE_EFFECT|SPINE_WAIT|SPINE_IMAGE|ACTOR|ACTOR_FRONT|ACTOR_OUT|ACTOR_PRIORITY|VIDEO_PLAY|VIDEO_STOP|BG|BG_OUT|BGM_PLAY|BGM_STOP|EFFECT_FLASH|EFFECT_QUAKE|EV|EV_OUT|FADE|FADE_IN|FADE_IN2|FADE_OUT|FADE_OUT2|SE_PLAY|SE_STOP|TRANSITION|TXT_CLEAR|UI_DISP|WAIT|WAIT2)$/;
        for (var i = s.index; i < Math.min(s.script.length, s.index + 128) && lines.length < 3; i++) {
            var command = s.script[i];
            if (typeof command !== "string" || command.length > 16384) break;
            var match = command.match(/^<([^=]*?)>(.*)/);
            if (!match) { candidate = futureText(command); continue; }
            var tag = match[1];
            if (tag === "PAUSE") {
                if (candidate && usable(candidate) && candidate.length <= 4096) {
                    if (characters + candidate.length > 8192) break;
                    if (lines.indexOf(candidate) < 0) { lines.push(candidate); characters += candidate.length; }
                }
                candidate = null;
            } else if (tag === "TXT_CLEAR") candidate = null;
            else if (!visual.test(tag)) break; // Includes choices, jumps, labels, endings and unknown commands.
        }
        return lines;
    }
    function maintainPrefetch() {
        if (!prefetchJob) return;
        var s = window.scene, text = box ? plainText(box) : "";
        if (!enabled || !prefetchEnabled || !s || s.choice || s.script !== prefetchJob.script || sceneKey() !== prefetchJob.scene ||
            (text !== prefetchJob.current && prefetchJob.sources.indexOf(text) < 0)) cancelPrefetch();
    }
    function prefetch(source) {
        if (!prefetchEnabled || !configuration || prefetchJob || Date.now() < prefetchRetry) return;
        var all = lookahead(), sources = all.filter(function (text) { return text !== source && !cache.has(text) && !pending.has(text); });
        if (!sources.length || sources.length < 2 && all.length > 1) return;
        var job = { script: window.scene.script, scene: sceneKey(), current: source, sources: sources, epoch: cacheRevision };
        prefetchJob = job;
        post(config.endpoint.replace(/translate$/, "prefetch"), { gameId: config.gameId, token: config.token,
            configuration: configuration, texts: sources }, job).then(function (result) {
            if (prefetchJob !== job || job.epoch !== cacheRevision || result.configuration !== configuration || !Array.isArray(result.items)) return;
            if (result.items.length !== sources.length || !result.items.every(function (item, index) {
                return item.source === sources[index] && safe(item.source, item.text);
            })) { prefetchRetry = Date.now() + 10000; return; }
            result.items.forEach(function (item) { remember(item.source, item.text); });
        }).catch(function () { if (prefetchJob === job) prefetchRetry = Date.now() + 10000; }).then(function () {
            if (prefetchJob === job) prefetchJob = null;
        });
    }
    function schedule(delay) {
        clearTimeout(timer);
        timer = setTimeout(translateCurrent, delay == null ? 180 : delay);
    }
    function changed() {
        maintainPrefetch();
        var node = currentBox();
        if (node !== box) {
            hideTranslation();
            if (observer) observer.disconnect();
            box = node; lastMarkup = null; lastContext = null;
            savedVisibility = box ? box.style.visibility : "";
            if (box) {
                observer = new MutationObserver(changed);
                observer.observe(box, { childList: true, subtree: true, characterData: true });
            }
        }
        var markup = box ? box.innerHTML : "", key = context();
        if (markup === lastMarkup && key === lastContext) { layoutTranslation(); return; }
        lastMarkup = markup; lastContext = key; revision++;
        hideTranslation(); schedule();
    }
    function layoutTranslation() {
        if (!overlay || !box) return;
        var display = overlay, node = box;
        display.className = node.className;
        display.style.cssText = node.style.cssText;
        display.style.visibility = "visible";
        display.style.whiteSpace = "pre-line";
        display.style.pointerEvents = "none";
        display.style.position = "absolute";
        display.style.left = node.offsetLeft + "px";
        display.style.top = node.offsetTop + "px";
        display.style.width = node.offsetWidth + "px";
        display.style.fontFamily = window.getComputedStyle(node).fontFamily + ',"Microsoft YaHei UI","Microsoft YaHei",sans-serif';
        node.style.visibility = "hidden";
    }
    function showTranslation(node, text) {
        hideTranslation();
        overlay = document.createElement("div");
        overlay.setAttribute("data-rika-translation", "true");
        overlay.setAttribute("aria-live", "polite");
        overlay.textContent = text;
        savedVisibility = node.style.visibility;
        node.parentNode.appendChild(overlay);
        layoutTranslation();
        applied++;
    }
    function translateCurrent() {
        timer = null;
        if (!enabled || !box || currentBox() !== box) return;
        var node = box, sequence = revision, key = context(), markup = node.innerHTML;
        var source = plainText(node);
        if (!usable(source)) return;
        var result = request(source);
        if (!result) { schedule(500); return; }
        result.then(function (translated) {
            if (!enabled || sequence !== revision || currentBox() !== node ||
                key !== context() || markup !== node.innerHTML) return;
            if (!translated) { schedule(5000); return; }
            if (translated !== source) showTranslation(node, translated);
            else hideTranslation();
            prefetch(source);
        });
    }
    function heartbeat() {
        if (heartbeatBusy) return;
        heartbeatBusy = true;
        post(config.endpoint.replace(/translate$/, "status"), {
            gameId: config.gameId, token: config.token, session: session, applied: applied, fontStatus: "original",
            uiApplied: ui ? ui.getState().applied : 0
        }).then(function (result) {
            var previous = enabled;
            if (result.configuration && result.configuration !== configuration) {
                if (configuration) { cache.clear(); pending.clear(); retryAt.clear(); cacheRevision++; revision++; hideTranslation(); schedule(0); }
                configuration = result.configuration;
                cancelPrefetch(); prefetchRetry = 0;
            }
            prefetchEnabled = result.prefetchEnabled === true;
            enabled = result.enabled === true;
            if (!prefetchEnabled || !enabled) cancelPrefetch();
            if (ui) ui.setEnabled(enabled && /^zh(?:-|$)/i.test(result.targetLanguage || config.uiLanguage || "zh-CN"));
            if (previous !== enabled) {
                revision++; hideTranslation();
                if (enabled) schedule(0);
            }
        }).catch(function () {
            if (enabled) { enabled = false; revision++; hideTranslation(); }
            prefetchEnabled = false; cancelPrefetch();
            if (ui) ui.setEnabled(false);
        }).then(function () { heartbeatBusy = false; });
    }
    window.RikaNwAdapter = {
        version: "0.7.8",
        getState: function () { return { enabled: enabled, applied: applied, cacheSize: cache.size, pending: pending.size, bound: !!box,
            prefetchEnabled: prefetchEnabled, prefetching: !!prefetchJob, ui: ui ? ui.getState() : null }; }
    };
    window.addEventListener("resize", function () { setTimeout(layoutTranslation, 0); });
    changed(); heartbeat();
    setInterval(changed, 200);
    setInterval(heartbeat, 3000);
}());
