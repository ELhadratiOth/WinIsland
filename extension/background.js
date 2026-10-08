// WinIsland for YouTube - background worker.
// Relays what the YouTube page reports to WinIsland (127.0.0.1 only) and carries the island's
// like / dislike presses back to that page. Nothing leaves this PC.
const BASE = "http://127.0.0.1:43822";
const HEADERS = { "Content-Type": "application/json", "X-WinIsland": "1" };

let activeTab = null; // the YouTube tab the island is currently talking about
let polling = false;

async function post(path, body) {
  try {
    await fetch(BASE + path, { method: "POST", headers: HEADERS, body: JSON.stringify(body) });
  } catch {
    // WinIsland isn't running: nothing to tell.
  }
}

async function pollLoop() {
  if (polling) return;
  polling = true;
  let failures = 0;
  try {
    while (activeTab !== null) {
      try {
        const response = await fetch(BASE + "/poll", { headers: HEADERS });
        const { command } = await response.json();
        failures = 0;
        if (command && activeTab !== null) {
          await chrome.tabs.sendMessage(activeTab, { type: "react", command }).catch(() => {});
        }
        await chrome.runtime.getPlatformInfo(); // any extension call keeps the worker alive
      } catch {
        failures++;
        await new Promise((resolve) => setTimeout(resolve, Math.min(30000, 2000 * failures)));
      }
    }
  } finally {
    polling = false;
  }
}

chrome.runtime.onMessage.addListener((message, sender) => {
  const tabId = sender.tab && sender.tab.id;
  if (tabId === undefined) return;
  if (message.type === "state") {
    // A tab that is playing wins over one that is merely open.
    if (message.state.playing || activeTab === null || activeTab === tabId) {
      activeTab = tabId;
      post("/state", message.state);
      pollLoop();
    }
  } else if (message.type === "gone" && tabId === activeTab) {
    activeTab = null;
    post("/gone", {});
  }
});

chrome.tabs.onRemoved.addListener((tabId) => {
  if (tabId === activeTab) {
    activeTab = null;
    post("/gone", {});
  }
});

// The worker may be stopped by the browser; pages report again every 25 s, which wakes it.
chrome.alarms.create("winisland-keepalive", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(() => {
  if (activeTab !== null) pollLoop();
});
