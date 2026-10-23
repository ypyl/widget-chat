/*
 * The sandbox half of the rail.
 *
 * Two rules shape this file:
 *
 * 1. The frame is created with sandbox="allow-scripts" and NOT allow-same-origin. That gives the
 *    document an opaque origin: no cookies, no storage, no same-origin fetch, and the parent
 *    cannot reach into contentWindow. Isolation comes from the browser, not from the prompt.
 *
 * 2. Setting srcdoc re-navigates the frame, so streaming updates cannot go through srcdoc. The
 *    frame loads one bootstrap document and the parent then postMessages css/html/js operations
 *    into it. Function declarations from the js channel become globals because they are evaluated
 *    with indirect eval, which keeps inline onclick="fn()" in generated markup working.
 */
(function () {
  "use strict";

  var IMPORTMAP = JSON.stringify({
    imports: {
      "three": "https://esm.sh/three",
      "three/": "https://esm.sh/three/",
      "gsap": "https://esm.sh/gsap",
      "gsap/": "https://esm.sh/gsap/",
      "d3": "https://esm.sh/d3",
      "d3/": "https://esm.sh/d3/",
      "chart.js": "https://esm.sh/chart.js",
      "chart.js/": "https://esm.sh/chart.js/"
    }
  });

  var CSP = [
    "default-src 'none'",
    "script-src 'unsafe-inline' 'unsafe-eval' https://esm.sh",
    "style-src 'unsafe-inline'",
    "img-src data: blob:",
    "font-src data:",
    "connect-src https://esm.sh",
    "base-uri 'none'",
    "form-action 'none'"
  ].join("; ");

  // Theme tokens. Light and dark are automatic, so generated widgets never hardcode a colour.
  var TOKENS = [
    ":root{",
    "--bg:#ffffff;--surface:#f7f7f9;--surface-2:#eceef2;--fg:#15171c;--muted:#5c626c;",
    "--border:#e1e3e9;--accent:#4f6bff;--radius:10px;",
    "--font-sans:ui-sans-serif,system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;",
    "--font-mono:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;",
    "}",
    "@media (prefers-color-scheme: dark){",
    ":root{--bg:#0f1115;--surface:#171a20;--surface-2:#1f242c;--fg:#e8ebf1;--muted:#98a0ad;",
    "--border:#2a2f39;--accent:#7d92ff;}",
    "}",
    "html,body{margin:0;padding:0;background:var(--bg);color:var(--fg);",
    "font-family:var(--font-sans);font-size:14px;line-height:1.5;overflow:hidden;height:auto}",
    "body{padding:12px}",
    "*,*::before,*::after{box-sizing:border-box}",
    "button,input,select,textarea{font:inherit;color:inherit}",
    "button{background:var(--surface-2);border:1px solid var(--border);border-radius:8px;",
    "padding:6px 12px;cursor:pointer}",
    "button:hover{border-color:var(--accent)}",
    "button:focus-visible,input:focus-visible,select:focus-visible{outline:2px solid var(--accent);",
    "outline-offset:2px}",
    "input[type=range]{accent-color:var(--accent);width:100%}",
    "h1,h2,h3{margin:0 0 6px;font-size:15px}"
  ].join("");

  // --- Injected into the frame -----------------------------------------------------------
  // These are real functions serialised with toString() and evaluated inside the frame, the way
  // OpenIntelligentUI injects window.createTripAnimator. The frame CSP is default-src 'none', so
  // it cannot load a script from us; the text has to travel inline. Consequence: every injected
  // function must be self-contained, with no closure over this file's scope.

  /**
   * Morphs `to` into `from`, reusing existing nodes so widget state survives each update.
   * Replacing body.innerHTML would throw away the canvas, focus and scroll position on every
   * streamed chunk.
   */
  function morphDom(from, to) {
    function morphNode(current, incoming) {
      if (current.nodeType !== incoming.nodeType || current.nodeName !== incoming.nodeName) {
        current.parentNode.replaceChild(incoming.cloneNode(true), current);
        return;
      }

      if (current.nodeType === 3 || current.nodeType === 8) {
        if (current.nodeValue !== incoming.nodeValue) {
          current.nodeValue = incoming.nodeValue;
        }
        return;
      }

      if (current.nodeType !== 1) {
        return;
      }

      var active = document.activeElement;

      var next = incoming.attributes;
      for (var i = 0; i < next.length; i++) {
        var name = next[i].name;
        // Do not fight the user for a field they are typing in.
        if (current === active && (name === "value" || name === "checked")) continue;
        if (current.getAttribute(name) !== next[i].value) {
          current.setAttribute(name, next[i].value);
        }
      }

      var own = current.attributes;
      for (var j = own.length - 1; j >= 0; j--) {
        if (!incoming.hasAttribute(own[j].name)) {
          current.removeAttribute(own[j].name);
        }
      }

      if (current.tagName === "SCRIPT" || current.tagName === "STYLE") {
        if (current.textContent !== incoming.textContent) {
          current.textContent = incoming.textContent;
        }
        return;
      }

      morphDom(current, incoming);
    }

    // shortcut: children match by position. A streamed reply only ever appends, so that holds.
    // If generated markup ever needs to reorder keyed children, adopt Idiomorph instead.
    var existing = Array.prototype.slice.call(from.childNodes);
    var incoming = Array.prototype.slice.call(to.childNodes);
    var shared = Math.min(existing.length, incoming.length);

    for (var i = 0; i < shared; i++) {
      morphNode(existing[i], incoming[i]);
    }
    for (var j = shared; j < incoming.length; j++) {
      from.appendChild(incoming[j].cloneNode(true));
    }
    for (var k = existing.length - 1; k >= shared; k--) {
      from.removeChild(existing[k]);
    }
  }

  /**
   * Owns everything a 3D widget gets wrong when it writes it by hand: pixel ratio, lights,
   * damping, resizing from the container, exactly one animation loop, reduced motion, teardown.
   */
  function createStage(options) {
    options = options || {};

    var THREE = options.THREE;
    var canvas = options.canvas;
    if (!THREE) throw new Error('stage.mount: pass THREE from await import("three")');
    if (!canvas) throw new Error("stage.mount: pass a canvas element");

    var container = options.container || canvas.parentElement || canvas;

    var renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));

    var scene = new THREE.Scene();
    var camera = options.camera || new THREE.PerspectiveCamera(42, 1, 0.1, 200);
    if (!options.camera) {
      camera.position.set(3.4, 2.6, 4.2);
    }

    scene.add(new THREE.HemisphereLight(0xffffff, 0x3a3a48, 1.6));
    var keyLight = new THREE.DirectionalLight(0xffffff, 2.4);
    keyLight.position.set(5, 7, 6);
    scene.add(keyLight);

    var reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    var controls = null;
    if (options.OrbitControls) {
      controls = new options.OrbitControls(camera, canvas);
      // Damping is an animation, so it is off when the user asked for less motion.
      controls.enableDamping = !reduceMotion;
      if ("enablePan" in controls) controls.enablePan = false;
      controls.minDistance = 2;
      controls.maxDistance = 24;
    }
    var running = false;
    var handle = 0;
    var disposers = [];
    var frameHandlers = [];
    var observer = null;

    function draw() {
      renderer.render(scene, camera);
    }

    function frame(now) {
      if (!running) return;
      handle = requestAnimationFrame(frame);
      // The loop is the redraw pump and the only place widgets apply state, so it always runs. A
      // widget that animates checks stage.reducedMotion and starts still instead; skipping these
      // callbacks froze every control for users who turned animations off.
      for (var i = 0; i < frameHandlers.length; i++) {
        try { frameHandlers[i](now); } catch (error) {}
      }
      if (controls) controls.update();
      draw();
    }

    function start() {
      if (running) return;
      running = true;
      handle = requestAnimationFrame(frame);
    }

    function stop() {
      running = false;
      cancelAnimationFrame(handle);
    }

    function resize() {
      var width = container.clientWidth;
      var height = container.clientHeight;
      if (!width || !height) return;
      renderer.setSize(width, height, false);
      camera.aspect = width / height;
      camera.updateProjectionMatrix();
      if (!running) draw();
    }

    if (typeof ResizeObserver === "function") {
      observer = new ResizeObserver(resize);
      observer.observe(container);
    }
    window.addEventListener("resize", resize);
    resize();
    // A canvas has no intrinsic size, so a widget whose CSS forgot the height collapses to zero and
    // shows nothing at all, which reads as "the model produced no widget". ResizeObserver never
    // helps here: zero stays zero. Only fires when no height rule applied, because CSS lands before
    // markup and the box is already measured when the stage mounts.
    if (!container.clientHeight) {
      container.style.height = "300px";
      resize();
    }
    start();

    return {
      THREE: THREE,
      scene: scene,
      camera: camera,
      renderer: renderer,
      controls: controls,
      /** True when the user asked their system for less motion. Start continuous animation off. */
      reducedMotion: reduceMotion,
      add: function (object) { scene.add(object); return object; },
      remove: function (object) { scene.remove(object); },
      start: start,
      stop: stop,
      resize: resize,
      /** Run a callback on every frame of the single animation loop. */
      onFrame: function (fn) { if (typeof fn === "function") frameHandlers.push(fn); },
      /** Register cleanup for geometry, materials, textures or listeners you created. */
      onDispose: function (fn) { if (typeof fn === "function") disposers.push(fn); },
      dispose: function () {
        stop();
        window.removeEventListener("resize", resize);
        if (observer) observer.disconnect();
        for (var i = 0; i < disposers.length; i++) {
          try { disposers[i](); } catch (error) {}
        }
        disposers.length = 0;
        if (controls && controls.dispose) controls.dispose();
        renderer.dispose();
      }
    };
  }

  /** Makes a stage and wires pause/resume/dispose so the host can stop the loop off screen. */
  function mountStage(options) {
    var stage = window.__createStage(options || {});
    window.widget = { pause: stage.stop, resume: stage.start, dispose: stage.dispose };
    return stage;
  }

  /** The part of the runtime that also belongs in an exported standalone file. */
  var STAGE_RUNTIME =
    "window.__morphDom = " + morphDom.toString() + ";" +
    "window.__createStage = " + createStage.toString() + ";" +
    "window.__mountStage = " + mountStage.toString() + ";" +
    "window.stage = { mount: window.__mountStage };";

  /** Runs inside the frame: the message protocol, height reporting, morphing, error surface. */
  function frameRuntime() {
    "use strict";

    function send(message) { parent.postMessage(message, "*"); }

    var heightPending = false;

    function reportHeight() {
      heightPending = false;
      var body = document.body;
      var style = getComputedStyle(body);
      var height = body.scrollHeight +
        (parseFloat(style.marginTop) || 0) +
        (parseFloat(style.marginBottom) || 0);
      send({ t: "w", op: "height", value: Math.ceil(height) });
    }

    function scheduleHeight() {
      if (heightPending) return;
      heightPending = true;
      requestAnimationFrame(reportHeight);
    }

    function report(level, message) {
      var text = String(message == null ? "Unknown error" : message);
      var node = document.getElementById("__widget_error");
      if (!node) {
        node = document.createElement("div");
        node.id = "__widget_error";
        node.setAttribute("role", "status");
        node.style.cssText = "margin-top:10px;padding:8px 10px;border:1px solid var(--border);" +
          "border-radius:8px;background:var(--surface-2);font-family:var(--font-mono);" +
          "font-size:12px;white-space:pre-wrap";
        document.body.appendChild(node);
      }
      node.textContent = text.slice(0, 1500);
      send({ t: "w", op: "error", level: level, message: text.slice(0, 1500) });
      scheduleHeight();
    }

    window.addEventListener("error", function (event) {
      report("error", event.message + (event.filename ? " (" + event.filename + ")" : ""));
    });
    window.addEventListener("unhandledrejection", function (event) {
      var reason = event.reason;
      report("error", (reason && reason.stack) || String(reason));
    });

    var nextId = 1;
    var pending = {};
    var lastClick = 0;

    window.addEventListener("click", function (event) {
      if (event.isTrusted) lastClick = Date.now();
    }, true);

    function callHost(name, args) {
      var id = nextId++;
      return new Promise(function (resolve, reject) {
        pending[id] = { resolve: resolve, reject: reject };
        setTimeout(function () {
          if (pending[id]) { delete pending[id]; reject(new Error("host bridge timeout")); }
        }, 15000);
        send({ t: "w", op: "bridge", id: id, call: name, args: args || {} });
      });
    }

    window.host = {
      sendPrompt: function (args) {
        // Raises the bar, not a boundary: code in this frame can post the same bridge message and
        // skip this check. The enforced bound is BRIDGE_SEND_LIMIT in app.js (5 prompts per page
        // load), which is what keeps a chain of widgets from spending without limit.
        if (Date.now() - lastClick > 5000) {
          return Promise.reject(new Error("sendPrompt: only a click the user made may send a prompt"));
        }
        return callHost("sendPrompt", args);
      },
      openLink: function (args) { return callHost("openLink", args); },
      report: report
    };

    function applyCss(css) {
      var node = document.getElementById("__widget_css");
      if (!node) {
        node = document.createElement("style");
        node.id = "__widget_css";
        document.head.appendChild(node);
      }
      node.textContent = css;
    }

    function applyHtml(html) {
      if (!window.__morphDom) {
        document.body.innerHTML = html;
        scheduleHeight();
        return;
      }
      var template = document.createElement("template");
      template.innerHTML = html;
      window.__morphDom(document.body, template.content);
      scheduleHeight();
    }

    function runJs(source) {
      try {
        (0, eval)(source);
      } catch (error) {
        report("error", (error && error.stack) || String(error));
      }
      scheduleHeight();
    }

    window.addEventListener("message", function (event) {
      if (event.source !== parent) return;
      var data = event.data;
      if (!data || data.t !== "host") return;

      switch (data.op) {
        case "css": applyCss(data.value); break;
        case "html": applyHtml(data.value); break;
        case "js": runJs(data.value); break;
        case "bridge-result": {
          var waiter = pending[data.id];
          if (!waiter) break;
          delete pending[data.id];
          if (data.ok) waiter.resolve(data.value);
          else waiter.reject(new Error(data.error || "host call failed"));
          break;
        }
        case "pause": if (window.widget && window.widget.pause) window.widget.pause(); break;
        case "resume": if (window.widget && window.widget.resume) window.widget.resume(); break;
      }

      scheduleHeight();
    });

    new ResizeObserver(scheduleHeight).observe(document.body);
    window.addEventListener("load", scheduleHeight);
    scheduleHeight();
    send({ t: "w", op: "ready" });
  }

  var BOOT = "<script>" + STAGE_RUNTIME + "(" + frameRuntime.toString() + ")();<" + "/script>";

  function buildFrameDocument() {
    return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
      "<meta http-equiv=\"Content-Security-Policy\" content=\"" + CSP + "\">" +
      "<script type=\"importmap\">" + IMPORTMAP + "<\/script>" +
      "<style>" + TOKENS + "</style></head>" +
      "<body>" + BOOT + "</body></html>";
  }

  var MIN_HEIGHT = 120;
  // Generous ceiling: the transcript scrolls, so a tall widget should not be cut off here.
  var MAX_HEIGHT = 2400;

  /** contentWindow -> WidgetHost, so one global listener can route messages. */
  var hosts = new Map();

  window.addEventListener("message", function (event) {
    var host = hosts.get(event.source);
    if (host) host.onFrameMessage(event.data);
  });

  function clampHeight(value) {
    if (typeof value !== "number" || !isFinite(value)) return null;
    return Math.max(MIN_HEIGHT, Math.min(Math.ceil(value), MAX_HEIGHT));
  }

  function stripTrailingFence(text) {
    return text.replace(/```[\w-]*\s*$/, "").replace(/\s+$/, "");
  }

  function WidgetHost(container, initialHeight) {
    this.container = container;
    this.ready = false;
    this.disposed = false;
    this.finalized = false;
    this.segments = { css: "", html: "", js: "" };
    this.height = clampHeight(initialHeight) || 320;
    // The declared height is a floor. A widget that sizes itself with height:100% fills whatever
    // frame it gets, so one small measurement (an empty body before the markup arrives) would
    // shrink the frame and the widget could never report a larger height again.
    this.minHeight = this.height;
    this.htmlTimer = null;

    var iframe = document.createElement("iframe");
    iframe.setAttribute("sandbox", "allow-scripts");
    iframe.setAttribute("referrerpolicy", "no-referrer");
    iframe.setAttribute("title", "Generated widget");
    iframe.style.cssText =
      "width:100%;display:block;border:0;background:transparent;height:" + this.height + "px";
    iframe.srcdoc = buildFrameDocument();
    container.appendChild(iframe);

    this.iframe = iframe;
    hosts.set(iframe.contentWindow, this);

    var self = this;
    this.visibility = "visible";
    if ("IntersectionObserver" in window) {
      // A long thread can accumulate WebGL contexts, and browsers silently drop the oldest one.
      // Stopping the animation loop off screen is what keeps earlier widgets alive.
      this.observer = new IntersectionObserver(function (entries) {
        var entry = entries[0];
        var next = entry.isIntersecting ? "visible" : "hidden";
        if (next === self.visibility || !self.ready) { self.visibility = next; return; }
        self.visibility = next;
        self.post(next === "visible" ? "resume" : "pause");
      }, { rootMargin: "200px 0px" });
      this.observer.observe(container);
    }
  }

  WidgetHost.prototype.post = function (op, extra) {
    if (!this.ready || this.disposed) return;
    var message = { t: "host", op: op };
    if (extra) {
      for (var key in extra) {
        if (Object.prototype.hasOwnProperty.call(extra, key)) message[key] = extra[key];
      }
    }
    this.iframe.contentWindow.postMessage(message, "*");
  };

  WidgetHost.prototype.onFrameMessage = function (data) {
    if (!data || data.t !== "w") return;

    if (data.op === "ready") {
      this.ready = true;
      if (this.segments.css) this.post("css", { value: this.segments.css });
      if (this.segments.html) this.post("html", { value: this.segments.html });
      if (this.finalized && this.segments.js) this.post("js", { value: this.segments.js });
      if (this.visibility === "hidden") this.post("pause");
      return;
    }

    if (data.op === "height") {
      var next = Math.max(this.minHeight, clampHeight(data.value) || 0);
      if (next === this.height) return;
      this.height = next;
      this.iframe.style.height = next + "px";
      if (this.onHeight) this.onHeight(next);
      return;
    }

    if (data.op === "error") {
      console.warn("[widget]", data.message);
      if (this.onError) this.onError(data.message);
      return;
    }

    if (data.op === "bridge") {
      this.handleBridge(data);
    }
  };

  WidgetHost.prototype.handleBridge = function (data) {
    var self = this;
    var reply = function (ok, value) {
      self.post("bridge-result", { id: data.id, ok: ok, value: ok ? value : undefined, error: ok ? undefined : String(value && value.message || value) });
    };

    try {
      var args = data.args || {};

      if (data.call === "sendPrompt") {
        var text = typeof args.text === "string" ? args.text.trim() : "";
        if (!text) throw new Error("sendPrompt: text is required");
        if (text.length > 4000) throw new Error("sendPrompt: text is longer than 4000 characters");
        if (typeof window.widgetChatSend !== "function") throw new Error("sendPrompt: chat is not connected");
        window.widgetChatSend(text);
        reply(true, { ok: true });
        return;
      }

      if (data.call === "openLink") {
        var parsed;
        try { parsed = new URL(String(args.url)); }
        catch (error) { throw new Error("openLink: invalid url"); }
        if (parsed.protocol !== "https:") throw new Error("openLink: only https is allowed");
        window.open(parsed.href, "_blank", "noopener,noreferrer");
        reply(true, { ok: true });
        return;
      }

      throw new Error("Unknown host call: " + data.call);
    } catch (error) {
      reply(false, error);
    }
  };

  WidgetHost.prototype.setCss = function (css) {
    this.segments.css = css;
    this.post("css", { value: css });
  };

  WidgetHost.prototype.setHtml = function (html, immediate) {
    this.segments.html = html;
    var self = this;
    if (immediate) {
      if (this.htmlTimer) { clearTimeout(this.htmlTimer); this.htmlTimer = null; }
      this.post("html", { value: this.segments.html });
      return;
    }
    // shortcut: every update posts the whole markup so far and the frame re-parses it, so the work
    // grows with the square of the widget. Fine at the size the samples reach; send appended
    // fragments instead if a generated widget ever gets large.
    if (this.htmlTimer) return;
    this.htmlTimer = setTimeout(function () {
      self.htmlTimer = null;
      self.post("html", { value: self.segments.html });
    }, 60);
  };

  WidgetHost.prototype.setJs = function (js) {
    this.segments.js = js;
  };

  /** Applies the cleaned final artifact and boots the behaviour exactly once. */
  WidgetHost.prototype.finalize = function () {
    if (this.finalized || this.disposed) return;
    this.finalized = true;

    this.segments.css = stripTrailingFence(this.segments.css);
    this.segments.html = stripTrailingFence(this.segments.html);
    this.segments.js = stripTrailingFence(this.segments.js);

    this.post("css", { value: this.segments.css });
    this.setHtml(this.segments.html, true);
    if (this.segments.js.trim()) this.post("js", { value: this.segments.js });
  };

  WidgetHost.prototype.standaloneHtml = function () {
    var stub = "window.host={sendPrompt:function(){return Promise.reject(new Error('Standalone export: no chat'));}," +
      "openLink:function(a){if(/^https:/.test(a.url))window.open(a.url,'_blank','noopener,noreferrer');return Promise.resolve({ok:true});}," +
      "report:function(l,m){console.warn(m);}};";
    return "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n" +
      "<meta http-equiv=\"Content-Security-Policy\" content=\"" + CSP + "\">\n" +
      "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n" +
      "<title>Widget</title>\n<script type=\"importmap\">" + IMPORTMAP + "<\/script>\n" +
      "<style>" + TOKENS + "</style>\n<style>" + this.segments.css.replace(/<\/style/gi, "<\\/style") + "</style>\n" +
      "</head>\n<body>\n" + this.segments.html + "\n<script>" + stub + "<\/script>\n" +
      "<script>" + STAGE_RUNTIME + "<\/script>\n" +
      "<script>\n" + this.segments.js.replace(/<\/script/gi, "<\\/script") + "\n<\/script>\n</body>\n</html>\n";
  };

  // shortcut: nothing calls this when a turn is removed, because the transcript has no "new chat"
  // action to clear. app.js calls it to release old off-screen widgets.
  WidgetHost.prototype.dispose = function () {
    if (this.disposed) return;
    this.disposed = true;
    if (this.htmlTimer) clearTimeout(this.htmlTimer);
    if (this.observer) this.observer.disconnect();
    if (this.iframe) {
      hosts.delete(this.iframe.contentWindow);
      this.iframe.remove();
    }
  };

  window.WidgetHost = WidgetHost;
})();
