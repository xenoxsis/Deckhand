"use strict";

/*
  The service worker: what makes the page installable, and what makes an installed copy
  launch without waiting on the laptop.

  It is deliberately thin. This dashboard is a window onto another machine — every tile it
  draws and every tap it sends only mean anything while that machine is answering — so
  there is nothing here that tries to work offline. What it caches is the shell: the page,
  the manifest, the icons. Enough to open, say the laptop isn't answering, and be ready the
  moment it is.

  Two rules matter more than the caching:

   - The network wins. Everything is fetched fresh and the cache is only the fallback. A
     panel is worth nothing if it's showing what the laptop looked like yesterday, and a
     cache-first shell is the usual way an installed web app gets stuck on an old build.
   - /api/ is never touched. The snapshot and the taps go straight past, unwrapped and
     uncached, so nothing here can answer for the laptop or replay a tap.

  A worker only exists on a secure page, and the tablet reaches the laptop over plain
  http, so on that path none of this runs at all — the page notices and carries on without
  it. See the README, "Installing it on the tablet".
*/

// Which build of the panel this worker belongs to. The laptop substitutes the real value
// as it serves this file: a short fingerprint of the page, this script and the manifest
// together. That's what makes updating work — a rebuilt app serves a script whose bytes
// differ, and a byte-for-byte difference is exactly what the browser's own update check
// looks for.
const BUILD = "__BUILD__";

const SHELL = `deckhand-${BUILD}`;
const SHELL_FILES = [
  "/",
  "/manifest.webmanifest",
  "/icon-192.png",
  "/icon-512.png",
  "/icon-180.png",
];

self.addEventListener("install", (event) => {
  event.waitUntil((async () => {
    const cache = await caches.open(SHELL);

    // One at a time, and failures ignored: cache.addAll fails the whole install if any
    // single file 404s, and an install that fails leaves the old worker in charge — which
    // is the one case where a stale build would stay stale.
    await Promise.all(SHELL_FILES.map(async (file) => {
      try {
        const response = await fetch(file, { cache: "no-store" });
        if (response.ok) await cache.put(file, response);
      } catch (error) {
        // The laptop went away mid-install. The fetch handler fills these in later.
      }
    }));

    // Don't wait for every tab to close before taking over. The page is told when this
    // happens and reloads itself, so the swap is deliberate rather than a surprise.
    await self.skipWaiting();
  })());
});

self.addEventListener("activate", (event) => {
  event.waitUntil((async () => {
    // Each build gets its own cache, so this is what throws the last build's shell away.
    for (const name of await caches.keys()) {
      if (name !== SHELL) await caches.delete(name);
    }
    await self.clients.claim();
  })());
});

self.addEventListener("fetch", (event) => {
  const request = event.request;

  // Not answering at all is different from answering with a fetch: it leaves the browser
  // to make the request itself, which is what a tap should be.
  if (request.method !== "GET") return;

  const url = new URL(request.url);
  if (url.origin !== location.origin) return;
  if (url.pathname.startsWith("/api/")) return;

  event.respondWith(fresh(request));
});

/** The live copy, falling back to the cached one only when the laptop can't be reached. */
async function fresh(request) {
  const key = keyFor(request);

  try {
    const response = await fetch(request);
    if (response.ok) {
      // Kept for the next cold launch. Not awaited: the page shouldn't wait on the write.
      const copy = response.clone();
      caches.open(SHELL).then((cache) => cache.put(key, copy)).catch(() => {});
    }
    return response;
  } catch (error) {
    const cached = await caches.match(key);
    if (cached) return cached;

    // Nothing cached and nothing answering — the first launch after installing, with the
    // laptop off. A page rather than the browser's own error, because in a standalone
    // window that error has no address bar to retry from.
    return request.mode === "navigate" ? offline() : Response.error();
  }
}

/*
  A navigation to /index.html and one to / are the same page here, and the shell is cached
  under /. Without this the cached page would be missed on any launch that didn't ask for
  exactly the address it was stored under.
*/
function keyFor(request) {
  return request.mode === "navigate" ? "/" : request;
}

function offline() {
  return new Response(
    `<!doctype html><html lang="en"><head><meta charset="utf-8">
     <meta name="viewport" content="width=device-width, initial-scale=1">
     <title>Deckhand</title></head>
     <body style="margin:0;display:grid;place-items:center;height:100vh;background:#1e1e1e;
                  color:#8a8a8a;font:16px/1.5 system-ui,sans-serif;text-align:center">
       <div>
         <p style="color:#eaeaea">The laptop isn't answering.</p>
         <p>Check it's on, on this network, and running the dashboard in tablet mode.</p>
         <button onclick="location.reload()"
                 style="min-height:60px;padding:10px 24px;background:#2d2d30;color:#eaeaea;
                        border:1px solid #3f3f46;border-radius:8px;font:inherit">
           Try again
         </button>
       </div>
     </body></html>`,
    { status: 503, headers: { "Content-Type": "text/html; charset=utf-8" } });
}
