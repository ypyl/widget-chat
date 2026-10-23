(function () {
  "use strict";

  var messagesEl = document.getElementById("messages");
  var badgeEl = document.getElementById("badge");
  var form = document.getElementById("composer");
  var input = document.getElementById("input");
  var sendButton = document.getElementById("send");
  var stopButton = document.getElementById("stop");

  var sessionId = sessionStorage.getItem("widgetchat.session");
  if (!sessionId) {
    sessionId = typeof crypto.randomUUID === "function"
      ? crypto.randomUUID()
      : String(Date.now()) + "-" + Math.random().toString(16).slice(2);
    sessionStorage.setItem("widgetchat.session", sessionId);
  }

  var busy = false;
  var controller = null;

  // Browsers keep only about 8-16 live WebGL contexts and silently drop the oldest, so a long
  // thread of 3D widgets blanks the earliest ones. Keep the newest few and release older widgets
  // once they are off screen. The Download HTML button keeps working: WidgetHost.dispose leaves
  // the stored css/html/js in place.
  var MAX_LIVE_WIDGETS = 12;
  var liveTurns = [];

  // Sticky bottom. Following the stream is the default; only a deliberate scroll away from the
  // bottom stops it, and coming back resumes it. Recomputing "am I near the bottom" on every
  // scroll event is wrong here because a widget grows while streaming and would flip the flag.
  var follow = true;

  function atBottom() {
    return messagesEl.scrollHeight - messagesEl.scrollTop - messagesEl.clientHeight < 90;
  }

  messagesEl.addEventListener("wheel", function () { follow = false; }, { passive: true });
  messagesEl.addEventListener("touchmove", function () { follow = false; }, { passive: true });
  messagesEl.addEventListener("keydown", function (event) {
    if (event.key === "PageUp" || event.key === "ArrowUp" || event.key === "Home") {
      follow = false;
    }
  });
  messagesEl.addEventListener("scroll", function () {
    if (atBottom()) follow = true;
  });

  function scrollToBottom(force) {
    if (force || follow) {
      messagesEl.scrollTop = messagesEl.scrollHeight;
    }
  }

  function createAssistant() {
    var root = document.createElement("article");
    root.className = "msg assistant";

    var prose = document.createElement("div");
    prose.className = "prose";

    var widgetSlot = document.createElement("div");
    widgetSlot.className = "widget";

    var meta = document.createElement("div");
    meta.className = "meta";
    // A status region, so errors and "Stopped." reach a screen reader. The transcript itself must
    // not be live: it changes with every streamed token.
    meta.setAttribute("role", "status");

    // Widget errors get their own line: meta also holds the Download button, and writing text into
    // meta would remove it.
    var error = document.createElement("div");
    error.className = "error";
    error.setAttribute("role", "status");

    root.append(prose, widgetSlot, error, meta);
    messagesEl.appendChild(root);

    return {
      root: root,
      prose: prose,
      widgetSlot: widgetSlot,
      error: error,
      meta: meta,
      host: null,
      height: 320,
      truncated: false,
      acc: { css: "", html: "", js: "" }
    };
  }

  function ensureHost(turn) {
    if (turn.host) {
      return turn.host;
    }

    var host = new WidgetHost(turn.widgetSlot, turn.height);
    host.onHeight = function () { scrollToBottom(); };
    host.onError = function (message) {
      turn.error.textContent = "Widget error: " + message;
      turn.root.classList.add("failed");
    };

    turn.host = host;
    liveTurns.push(turn);
    releaseOldWidgets();
    return host;
  }

  function releaseOldWidgets() {
    if (liveTurns.length <= MAX_LIVE_WIDGETS) {
      return;
    }

    // Only widgets that have scrolled fully above the transcript are candidates, so the one that
    // just started is never the one removed.
    var top = messagesEl.getBoundingClientRect().top;
    var excess = liveTurns.length - MAX_LIVE_WIDGETS;
    var keep = [];

    for (var i = 0; i < liveTurns.length; i++) {
      var turn = liveTurns[i];
      if (excess > 0 && turn.root.getBoundingClientRect().bottom < top) {
        turn.host.dispose();
        var note = document.createElement("p");
        note.className = "released";
        note.textContent = "This widget was released to free graphics memory.";
        turn.widgetSlot.appendChild(note);
        excess--;
      } else {
        keep.push(turn);
      }
    }

    liveTurns = keep;
  }

  function addDownload(turn) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "link";
    button.textContent = "Download HTML";
    button.addEventListener("click", function () {
      var html = turn.host.standaloneHtml();
      var blob = new Blob([html], { type: "text/html;charset=utf-8" });
      var url = URL.createObjectURL(blob);
      var link = document.createElement("a");
      link.href = url;
      link.download = "widget.html";
      link.click();
      setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
    });

    turn.meta.appendChild(button);
  }

  function handleEvent(turn, event) {
    switch (event.type) {
      case "text":
        turn.prose.textContent += event.delta;
        break;

      case "height":
        turn.height = event.value;
        break;

      case "css":
        turn.acc.css += event.delta;
        ensureHost(turn).setCss(turn.acc.css);
        break;

      case "html":
        turn.acc.html += event.delta;
        ensureHost(turn).setHtml(turn.acc.html);
        break;

      case "js":
        // Behaviour runs once at done, where setJs is called again; storing it live changes nothing.
        turn.acc.js += event.delta;
        break;

      case "truncated":
        // The reply stopped before @@END@@, so the JS is half written. Running it would produce a
        // confusing error, and the download would be a broken file.
        turn.truncated = true;
        turn.meta.textContent = "The reply stopped early, so this widget is incomplete.";
        turn.root.classList.add("failed");
        break;

      case "done":
        // Models tend to leave a blank line before the markers; it would render as a gap.
        turn.prose.textContent = turn.prose.textContent.replace(/\s+$/, "");
        if (turn.host && !turn.truncated) {
          turn.host.setJs(turn.acc.js);
          turn.host.finalize();
          addDownload(turn);
        }
        break;

      case "error":
        turn.meta.textContent = "Error: " + event.message;
        turn.root.classList.add("failed");
        break;
    }

    scrollToBottom();
  }

  async function send(message) {
    var text = String(message || "").trim();
    if (!text || busy) {
      return;
    }

    busy = true;
    follow = true;
    sendButton.disabled = true;
    stopButton.hidden = false;
    controller = new AbortController();

    var user = document.createElement("article");
    user.className = "msg user";
    var bubble = document.createElement("div");
    bubble.className = "bubble";
    bubble.textContent = text;
    user.appendChild(bubble);
    messagesEl.appendChild(user);

    var turn = createAssistant();
    scrollToBottom(true);

    try {
      var response = await fetch("/api/chat", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sessionId: sessionId, message: text }),
        signal: controller.signal
      });

      if (!response.ok) {
        var detail = await response.text();
        throw new Error(detail || "HTTP " + response.status);
      }

      var reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
      var buffer = "";

      for (;;) {
        var chunk = await reader.read();
        if (chunk.done) {
          break;
        }

        buffer += chunk.value;
        var newline;
        while ((newline = buffer.indexOf("\n")) >= 0) {
          var line = buffer.slice(0, newline);
          buffer = buffer.slice(newline + 1);
          if (line.trim()) {
            handleEvent(turn, JSON.parse(line));
          }
        }
      }

      if (buffer.trim()) {
        handleEvent(turn, JSON.parse(buffer));
      }
    } catch (error) {
      if (error && error.name === "AbortError") {
        turn.meta.textContent = "Stopped.";
      } else {
        turn.meta.textContent = "Error: " + (error && error.message ? error.message : String(error));
        turn.root.classList.add("failed");
      }
    } finally {
      busy = false;
      controller = null;
      sendButton.disabled = false;
      stopButton.hidden = true;
      input.focus();
      scrollToBottom();
    }
  }

  // The enforced bound on widget-started prompts. The click check in sendPrompt (widget-host.js)
  // raises the bar but cannot be enforced, because the frame can post the bridge message itself.
  // This cap is what stops a chain of widgets from spending without limit.
  var BRIDGE_SEND_LIMIT = 5;
  var bridgeSends = 0;

  window.widgetChatSend = function (text) {
    if (busy || bridgeSends >= BRIDGE_SEND_LIMIT) {
      return;
    }
    bridgeSends++;
    send(text);
  };

  stopButton.addEventListener("click", function () {
    if (controller) controller.abort();
  });

  form.addEventListener("submit", function (event) {
    event.preventDefault();
    // Enter submits even while Send is disabled. Keep the text until a send can actually start.
    if (busy) return;
    var text = input.value;
    input.value = "";
    input.style.height = "auto";
    send(text);
  });

  input.addEventListener("keydown", function (event) {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      form.requestSubmit();
    }
  });

  input.addEventListener("input", function () {
    input.style.height = "auto";
    input.style.height = Math.min(input.scrollHeight, 180) + "px";
  });

  document.querySelectorAll(".chip").forEach(function (chip) {
    chip.addEventListener("click", function () {
      send(chip.textContent);
    });
  });

  fetch("/api/info")
    .then(function (response) { return response.ok ? response.json() : null; })
    .then(function (info) {
      badgeEl.textContent = info ? info.model + " · " + info.mode : "unknown";
    })
    .catch(function () {
      badgeEl.textContent = "unavailable";
    });

  input.focus();
})();
