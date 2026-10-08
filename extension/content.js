// WinIsland for YouTube - runs on YouTube / YouTube Music pages.
// Reports which video is playing and whether it is liked or disliked, and presses the
// like / dislike button when the island asks. The page content is never sent anywhere
// except to WinIsland on this PC.
(() => {
  const isMusic = location.hostname === "music.youtube.com";
  const site = isMusic ? "ytmusic" : "youtube";
  const first = (root, selectors) => {
    for (const selector of selectors) {
      const element = root.querySelector(selector);
      if (element) return element;
    }
    return null;
  };

  function findButtons() {
    if (isMusic) {
      const bar = document.querySelector("ytmusic-player-bar ytmusic-like-button-renderer");
      if (!bar) return null;
      return {
        like: first(bar, ["#button-shape-like button", "#like-button-renderer button", "button[aria-label*='ike' i]"]),
        dislike: first(bar, ["#button-shape-dislike button", "#dislike-button-renderer button"]),
        status: bar.getAttribute("like-status"),
      };
    }
    // Shorts show one video at a time; otherwise the watch page's own button row.
    const root = document.querySelector("ytd-reel-video-renderer[is-active]") || document.querySelector("ytd-watch-metadata") || document;
    const like = first(root, ["like-button-view-model button", "#segmented-like-button button", "ytd-toggle-button-renderer#like-button button", "#like-button button"]);
    const dislike = first(root, ["dislike-button-view-model button", "#segmented-dislike-button button", "ytd-toggle-button-renderer#dislike-button button", "#dislike-button button"]);
    return like ? { like, dislike, status: null } : null;
  }

  const pressed = (button) => !!button && button.getAttribute("aria-pressed") === "true";

  function describe() {
    const buttons = findButtons();
    if (!buttons) return null;
    const meta = navigator.mediaSession && navigator.mediaSession.metadata;
    let title = (meta && meta.title) || "";
    let channel = (meta && meta.artist) || "";
    if (isMusic) {
      title = title || (document.querySelector("ytmusic-player-bar .title") || {}).textContent || "";
    } else if (!title) {
      title = document.title.replace(/ - YouTube$/, "");
    }
    const video = document.querySelector("video");
    return {
      site,
      title: title.trim(),
      channel: channel.trim(),
      liked: buttons.status ? buttons.status === "LIKE" : pressed(buttons.like),
      disliked: buttons.status ? buttons.status === "DISLIKE" : pressed(buttons.dislike),
      playing: !!video && !video.paused && !video.ended,
    };
  }

  let reported = false;
  let timer = null;

  // After the extension is updated or removed, an old page can't reach it any more.
  function send(message) {
    try {
      chrome.runtime.sendMessage(message).catch(() => {});
    } catch {
      // extension context gone
    }
  }

  function report() {
    timer = null;
    const state = describe();
    if (state && state.title) {
      reported = true;
      send({ type: "state", state });
    } else if (reported) {
      reported = false;
      send({ type: "gone" });
    }
  }

  function schedule(delay = 400) {
    if (timer === null) timer = setTimeout(report, delay);
  }

  new MutationObserver(() => schedule()).observe(document.body, {
    subtree: true,
    attributes: true,
    attributeFilter: ["aria-pressed", "like-status"],
  });
  document.addEventListener("play", () => schedule(150), true);
  document.addEventListener("pause", () => schedule(150), true);
  window.addEventListener("yt-navigate-finish", () => {
    schedule(300);
    setTimeout(report, 1500); // the media session catches up with the new video a moment later
  });
  setInterval(report, 25000); // heartbeat: tells the island the page is still there
  window.addEventListener("pagehide", () => {
    if (reported) send({ type: "gone" });
  });

  chrome.runtime.onMessage.addListener((message) => {
    if (message.type !== "react") return;
    const buttons = findButtons();
    const target = message.command === "dislike" ? buttons && buttons.dislike : buttons && buttons.like;
    if (target) {
      target.click();
      setTimeout(report, 500);
    }
  });

  schedule(1000);
})();
