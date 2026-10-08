# WinIsland for YouTube (browser extension)

Browsers don't let other apps press a page's like button, so WinIsland ships this tiny
extension. With it installed, the island shows 👍 / 👎 for the YouTube or YouTube Music video that
is playing (any tab, even in the background) and presses the real buttons on the page.

* It runs only on `youtube.com` and `music.youtube.com`.
* It talks only to WinIsland on this PC (`http://127.0.0.1:43822`). WinIsland answers only
  browser extensions, never ordinary web pages.
* It sends WinIsland the video title, channel and whether it is liked/disliked, and nothing else.

## Install (Chrome, Edge, Brave, Vivaldi, Opera)

1. Open `chrome://extensions` (Edge: `edge://extensions`).
2. Turn on **Developer mode**.
3. **Load unpacked** and pick this `extension` folder.

## Install (Firefox)

1. Open `about:debugging#/runtime/this-firefox`.
2. **Load Temporary Add-on…** and pick `manifest.json` in this folder
   (Firefox forgets temporary add-ons when it restarts).
