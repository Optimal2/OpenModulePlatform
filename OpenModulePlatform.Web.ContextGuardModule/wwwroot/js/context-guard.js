(() => {
    "use strict";

    const root = document.getElementById("context-guard");
    if (!root) return;

    // The one-time key has been redeemed. Keep it out of bookmarks, history and later referrers.
    if (window.location.search) {
        window.history.replaceState(null, "", window.location.pathname + window.location.hash);
    }

    const form = document.getElementById("context-delivery");
    const frame = document.getElementById("context-target");
    const status = document.getElementById("context-status");
    const delivery = root.dataset.delivery;
    let stopped = false;
    let failures = 0;
    let timer = null;

    const stop = (kind) => {
        if (stopped) return;
        stopped = true;
        if (timer !== null) window.clearTimeout(timer);
        if (frame) {
            // Blank first: removing a frame alone does not reliably stop its document.
            frame.src = "about:blank";
            frame.remove();
        }
        if (form) form.remove();
        root.dataset.contextId = "";
        root.dataset.antiforgeryToken = "";
        status.textContent = kind === "superseded"
            ? root.dataset.supersededMessage
            : root.dataset.endedMessage;
        status.dataset.outcome = kind;
        try { window.close(); } catch { /* Script-opened windows only; blanking is the guarantee. */ }
    };

    const heartbeat = async () => {
        if (stopped) return;
        try {
            const response = await fetch(root.dataset.heartbeatUrl, {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    "RequestVerificationToken": root.dataset.antiforgeryToken,
                    "Accept": "application/json"
                }
            });
            if (!response.ok) throw new Error("heartbeat failed");
            const body = await response.json();
            if (body.state === "superseded" || body.state === "ended") {
                stop(body.state);
                return;
            }
            if (body.state !== "current") throw new Error("heartbeat state invalid");
            failures = 0;
        } catch {
            failures += 1;
            if (failures >= Number(root.dataset.heartbeatFailureLimit)) {
                stop("ended");
                return;
            }
        }
        if (!stopped) timer = window.setTimeout(heartbeat, Number(root.dataset.heartbeatIntervalMs));
    };

    if (form) form.submit();
    if (delivery === "embed") {
        timer = window.setTimeout(heartbeat, Number(root.dataset.heartbeatIntervalMs));
    }
})();
