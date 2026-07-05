const NATIVE_HOST = "com.local.fastdownloader";
const CONTEXT_MENU_ID = "download-with-local-downloader";
const RESPONSE_TIMEOUT_MS = 3000;
const BYPASS_TTL_MS = 10 * 60 * 1000;

// Built-in default intercept lists. The App holds the authoritative copy; these are the
// fallback used when settings.get cannot reach the App (and before first sync).
const DEFAULT_INTERCEPT_EXTENSIONS = [
  // Archives
  ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab",
  // Installers
  ".exe", ".msi", ".msix", ".apk", ".dmg", ".pkg", ".deb", ".rpm",
  // Video
  ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m4v",
  // Audio
  ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma",
  // Documents
  ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".epub", ".mobi",
  // Disc images and misc
  ".iso", ".img", ".bin", ".torrent", ".ttf", ".otf", ".psd",
];

const DEFAULT_INTERCEPT_MIME_PREFIXES = [
  "application/octet-stream",
  "application/x-msdownload",
  "application/x-msi",
  "application/zip",
  "application/x-7z-compressed",
  "application/x-rar-compressed",
  "application/gzip",
  "application/x-tar",
  "application/x-iso9660-image",
  "video/",
  "audio/",
  "application/pdf",
];

let interceptExtensions = new Set(DEFAULT_INTERCEPT_EXTENSIONS);
let interceptMimePrefixes = [...DEFAULT_INTERCEPT_MIME_PREFIXES];

// --- Native messaging long-lived port -------------------------------------------------------

let nativePort = null;
const pendingRequests = new Map(); // message id -> { resolve, reject, timer }

function getNativePort() {
  if (nativePort) {
    return nativePort;
  }

  const port = chrome.runtime.connectNative(NATIVE_HOST);
  port.onMessage.addListener(onNativeMessage);
  port.onDisconnect.addListener(() => {
    nativePort = null;
    rejectAllPending(new Error("native port disconnected"));
  });

  nativePort = port;
  return port;
}

function rejectAllPending(error) {
  for (const pending of pendingRequests.values()) {
    clearTimeout(pending.timer);
    pending.reject(error);
  }
  pendingRequests.clear();
}

function onNativeMessage(message) {
  if (!message || typeof message !== "object") {
    return;
  }

  if (message.type === "download.returnToBrowser") {
    handleReturnToBrowser(message);
    return;
  }

  const pending = message.id ? pendingRequests.get(message.id) : null;
  if (!pending) {
    return;
  }

  pendingRequests.delete(message.id);
  clearTimeout(pending.timer);

  if (message.type === "download.error") {
    pending.reject(new Error(message.code || "download.error"));
  } else {
    pending.resolve(message);
  }
}

function sendNativeRequest(message) {
  return new Promise((resolve, reject) => {
    let port;
    try {
      port = getNativePort();
    } catch (error) {
      reject(error);
      return;
    }

    const timer = setTimeout(() => {
      pendingRequests.delete(message.id);
      reject(new Error("native host response timeout"));
    }, RESPONSE_TIMEOUT_MS);

    pendingRequests.set(message.id, { resolve, reject, timer });

    try {
      port.postMessage(message);
    } catch (error) {
      clearTimeout(timer);
      pendingRequests.delete(message.id);
      nativePort = null;
      reject(error);
    }
  });
}

// --- Intercept settings sync ----------------------------------------------------------------

function applyInterceptConfig(extensions, mimePrefixes) {
  if (Array.isArray(extensions) && extensions.length > 0) {
    interceptExtensions = new Set(extensions.map((e) => String(e).toLowerCase()));
  }

  if (Array.isArray(mimePrefixes) && mimePrefixes.length > 0) {
    interceptMimePrefixes = mimePrefixes.map((p) => String(p).toLowerCase());
  }
}

function loadCachedInterceptConfig() {
  try {
    chrome.storage.local.get(["interceptExtensions", "interceptMimePrefixes"], (stored) => {
      if (chrome.runtime.lastError || !stored) {
        return;
      }

      applyInterceptConfig(stored.interceptExtensions, stored.interceptMimePrefixes);
    });
  } catch (_error) {
    // Defaults stay in effect.
  }
}

async function syncInterceptSettings() {
  try {
    const response = await sendNativeRequest({ type: "settings.get", id: createMessageId() });
    if (response && response.type === "settings.value") {
      applyInterceptConfig(response.interceptExtensions, response.interceptMimePrefixes);
      try {
        chrome.storage.local.set({
          interceptExtensions: response.interceptExtensions,
          interceptMimePrefixes: response.interceptMimePrefixes,
        });
      } catch (_error) {
        // Cache write is best-effort.
      }
    }
  } catch (_error) {
    // App unreachable: keep cached/default lists.
  }
}

// --- Bypass list (URLs handed back to the browser) -------------------------------------------

const bypassUrls = new Map(); // url -> expiry timestamp (ms)

function addBypass(url) {
  if (url) {
    bypassUrls.set(url, Date.now() + BYPASS_TTL_MS);
  }
}

function isBypassed(url) {
  const expiry = bypassUrls.get(url);
  if (!expiry) {
    return false;
  }

  if (Date.now() > expiry) {
    bypassUrls.delete(url);
    return false;
  }

  return true;
}

function handleReturnToBrowser(message) {
  const url = normalizeHttpUrl(message.url);
  if (!url) {
    return;
  }

  addBypass(url);
  startBrowserDownload(url, message.suggestedFilename);
}

function startBrowserDownload(url, filename) {
  const options = { url };
  const cleanName = filename ? basename(filename) : null;
  if (cleanName) {
    options.filename = cleanName;
  }

  try {
    chrome.downloads.download(options, () => {
      if (chrome.runtime.lastError && options.filename) {
        // Retry without a filename in case the suggested name was rejected.
        chrome.downloads.download({ url }, () => void chrome.runtime.lastError);
      }
    });
  } catch (error) {
    console.warn("Local Downloader failed to hand the download back to the browser:", error);
  }
}

// --- Download interception -------------------------------------------------------------------

chrome.downloads.onCreated.addListener((_downloadItem) => {
  // Recording hook only; interception decisions happen in onDeterminingFilename, where the
  // final filename and MIME type are known.
});

chrome.downloads.onDeterminingFilename.addListener((downloadItem, _suggest) => {
  maybeInterceptDownload(downloadItem);
});

function maybeInterceptDownload(downloadItem) {
  const url = downloadItem.finalUrl || downloadItem.url;

  if (isBypassed(url) || isBypassed(downloadItem.url)) {
    return;
  }

  if (!shouldInterceptDownload(downloadItem)) {
    return;
  }

  // Cancel first so the browser never writes the file; any handoff failure below re-triggers
  // a browser download through the bypass list (fail-open).
  chrome.downloads.cancel(downloadItem.id, () => void chrome.runtime.lastError);

  handOffDownload(downloadItem).catch((error) => {
    console.warn("Local Downloader handoff failed; failing open to browser download:", error);
    failOpen(url, downloadItem.filename);
  });
}

function shouldInterceptDownload(downloadItem) {
  const url = downloadItem.finalUrl || downloadItem.url;
  if (!normalizeHttpUrl(url)) {
    return false;
  }

  if (downloadItem.incognito) {
    return false;
  }

  if (downloadItem.byExtensionId) {
    return false;
  }

  if (downloadItem.danger && downloadItem.danger !== "safe" && downloadItem.danger !== "accepted") {
    return false;
  }

  const filename = downloadItem.filename || filenameFromUrl(url) || "";
  const extension = getLowercaseExtension(filename);
  if (extension && interceptExtensions.has(extension)) {
    return true;
  }

  const mime = (downloadItem.mime || "").toLowerCase();
  if (!mime) {
    return false;
  }

  return interceptMimePrefixes.some((prefix) => mime.startsWith(prefix));
}

async function handOffDownload(downloadItem) {
  const url = downloadItem.finalUrl || downloadItem.url;
  const cookieHeader = await collectCookieHeader(url);

  const message = {
    type: "download.create",
    id: createMessageId(),
    source: "browser-download",
    url,
    userAgent: navigator.userAgent,
  };

  const suggestedFilename = downloadItem.filename
    ? basename(downloadItem.filename)
    : filenameFromUrl(url);
  if (suggestedFilename) {
    message.suggestedFilename = suggestedFilename;
  }

  if (downloadItem.referrer) {
    message.referrer = downloadItem.referrer;
  }

  if (cookieHeader) {
    message.cookieHeader = cookieHeader;
  }

  const fileSize = Number(downloadItem.fileSize);
  const totalBytes = Number(downloadItem.totalBytes);
  if (Number.isFinite(fileSize) && fileSize > 0) {
    message.fileSize = fileSize;
  } else if (Number.isFinite(totalBytes) && totalBytes > 0) {
    message.fileSize = totalBytes;
  }

  if (downloadItem.mime) {
    message.mime = downloadItem.mime;
  }

  const response = await sendNativeRequest(message);
  if (!response || response.type !== "download.accepted") {
    throw new Error("download.create was not accepted");
  }
}

function failOpen(url, filename) {
  const normalized = normalizeHttpUrl(url);
  if (!normalized) {
    return;
  }

  addBypass(normalized);
  addBypass(url);
  startBrowserDownload(normalized, filename);
}

// --- Cookie collection ------------------------------------------------------------------------

function collectCookieHeader(url) {
  return new Promise((resolve) => {
    try {
      chrome.cookies.getAll({ url }, (cookies) => {
        if (chrome.runtime.lastError || !Array.isArray(cookies) || cookies.length === 0) {
          resolve(null);
          return;
        }

        resolve(cookies.map((cookie) => `${cookie.name}=${cookie.value}`).join("; "));
      });
    } catch (_error) {
      resolve(null);
    }
  });
}

// --- Context menu ------------------------------------------------------------------------------

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: CONTEXT_MENU_ID,
    title: "Download with Local Downloader",
    contexts: ["link", "selection", "page"],
  });

  syncInterceptSettings();
});

if (chrome.runtime.onStartup) {
  chrome.runtime.onStartup.addListener(() => {
    syncInterceptSettings();
  });
}

loadCachedInterceptConfig();

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== CONTEXT_MENU_ID) {
    return;
  }

  handleContextMenuDownload(info, tab).catch((error) => {
    console.warn("Local Downloader context handoff failed:", error);
  });
});

async function handleContextMenuDownload(info, tab) {
  const selectedContext = await getSelectedContext(info, tab);
  const url = firstHttpUrl(
    info.linkUrl,
    selectedContext && selectedContext.linkUrl,
    selectedContext && selectedContext.selectedUrl,
    info.srcUrl,
    tab && tab.url,
    info.pageUrl,
  );

  if (!url) {
    console.warn("Local Downloader ignored context menu handoff without an HTTP(S) URL.");
    return;
  }

  const cookieHeader = await collectCookieHeader(url);
  const message = {
    type: "download.create",
    id: createMessageId(),
    source: "context-menu",
    url,
    userAgent: navigator.userAgent,
  };

  const suggestedFilename = info.suggestedFilename || filenameFromUrl(url);
  if (suggestedFilename) {
    message.suggestedFilename = suggestedFilename;
  }

  const referrer = info.frameUrl || info.pageUrl || (tab && tab.url);
  if (referrer) {
    message.referrer = referrer;
  }

  if (cookieHeader) {
    message.cookieHeader = cookieHeader;
  }

  try {
    await sendNativeRequest(message);
  } catch (error) {
    console.warn("Local Downloader context handoff failed; failing open to browser download:", error);
    failOpen(url, suggestedFilename);
  }
}

async function getSelectedContext(info, tab) {
  if (!tab || typeof tab.id !== "number" || !info.selectionText) {
    return null;
  }

  const directSelectionUrl = normalizeHttpUrl(info.selectionText);
  if (directSelectionUrl) {
    return { selectedUrl: directSelectionUrl };
  }

  return sendTabMessage(tab.id, { type: "local-downloader.get-context" });
}

function sendTabMessage(tabId, message) {
  return new Promise((resolve) => {
    chrome.tabs.sendMessage(tabId, message, (response) => {
      if (chrome.runtime.lastError) {
        resolve(null);
        return;
      }

      resolve(response || null);
    });
  });
}

// --- Helpers -----------------------------------------------------------------------------------

function firstHttpUrl(...candidates) {
  for (const candidate of candidates) {
    const normalized = normalizeHttpUrl(candidate);
    if (normalized) {
      return normalized;
    }
  }

  return null;
}

function normalizeHttpUrl(value) {
  if (typeof value !== "string") {
    return null;
  }

  const trimmed = value.trim();
  if (!trimmed) {
    return null;
  }

  try {
    const parsed = new URL(trimmed);
    if (parsed.protocol === "http:" || parsed.protocol === "https:") {
      return parsed.href;
    }
  } catch (_error) {
    return null;
  }

  return null;
}

function filenameFromUrl(url) {
  const normalized = normalizeHttpUrl(url);
  if (!normalized) {
    return null;
  }

  const pathname = new URL(normalized).pathname;
  const lastSegment = pathname.split("/").filter(Boolean).pop();
  if (!lastSegment) {
    return null;
  }

  try {
    return decodeURIComponent(lastSegment);
  } catch (_error) {
    return lastSegment;
  }
}

function basename(path) {
  if (typeof path !== "string" || !path) {
    return null;
  }

  return path.split(/[\\/]/).filter(Boolean).pop() || null;
}

function getLowercaseExtension(filename) {
  const cleanName = filename.split(/[\\/]/).pop() || "";
  const dotIndex = cleanName.lastIndexOf(".");
  if (dotIndex < 0) {
    return "";
  }

  return cleanName.slice(dotIndex).toLowerCase();
}

function createMessageId() {
  const random = Math.random().toString(36).slice(2, 10);
  return `browser-${Date.now()}-${random}`;
}
