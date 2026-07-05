const NATIVE_HOST = "com.local.fastdownloader";
const CONTEXT_MENU_ID = "download-with-local-downloader";

const DIRECT_DOWNLOAD_EXTENSIONS = new Set([
  ".7z",
  ".apk",
  ".bz2",
  ".cab",
  ".dmg",
  ".exe",
  ".gz",
  ".iso",
  ".msi",
  ".msix",
  ".pkg",
  ".rar",
  ".tar",
  ".tgz",
  ".xz",
  ".zip",
]);

const DIRECT_DOWNLOAD_MIME_PREFIXES = [
  "application/octet-stream",
  "application/x-msdownload",
  "application/x-msi",
  "application/zip",
  "application/x-7z-compressed",
  "application/x-rar-compressed",
  "application/gzip",
  "application/x-tar",
  "application/x-iso9660-image",
];

const handedOffDownloadIds = new Set();

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: CONTEXT_MENU_ID,
    title: "Download with Local Downloader",
    contexts: ["link", "selection", "page"],
  });
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== CONTEXT_MENU_ID) {
    return;
  }

  handleContextMenuDownload(info, tab).catch((error) => {
    console.warn("Local Downloader context handoff failed:", error);
  });
});

chrome.downloads.onCreated.addListener((downloadItem) => {
  handOffBrowserDownload(downloadItem);
});

chrome.downloads.onDeterminingFilename.addListener((downloadItem, suggest) => {
  handOffBrowserDownload(downloadItem);
});

function handOffBrowserDownload(downloadItem) {
  if (handedOffDownloadIds.has(downloadItem.id)) {
    return;
  }

  if (!shouldHandOffBrowserDownload(downloadItem)) {
    return;
  }

  handedOffDownloadIds.add(downloadItem.id);

  sendDownloadCreate({
    source: "browser-download",
    url: downloadItem.finalUrl || downloadItem.url,
    suggestedFilename: downloadItem.filename,
    referrer: downloadItem.referrer,
  }).then((response) => {
    if (response && response.type === "download.accepted") {
      chrome.downloads.cancel(downloadItem.id);
    }
  }).catch((error) => {
    handedOffDownloadIds.delete(downloadItem.id);
    console.warn("Local Downloader automatic handoff failed; browser download continues:", error);
  });
}

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

  await sendDownloadCreate({
    source: "context-menu",
    url,
    suggestedFilename: info.suggestedFilename || filenameFromUrl(url),
    referrer: info.frameUrl || info.pageUrl || (tab && tab.url),
  });
}

function sendDownloadCreate(details) {
  const message = {
    type: "download.create",
    id: createMessageId(),
    source: details.source,
    url: details.url,
    userAgent: navigator.userAgent,
  };

  if (details.suggestedFilename) {
    message.suggestedFilename = details.suggestedFilename;
  }

  if (details.referrer) {
    message.referrer = details.referrer;
  }

  return new Promise((resolve, reject) => {
    chrome.runtime.sendNativeMessage(NATIVE_HOST, message, (response) => {
      const lastError = chrome.runtime.lastError;
      if (lastError) {
        reject(new Error(lastError.message));
        return;
      }

      resolve(response);
    });
  });
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

function shouldHandOffBrowserDownload(downloadItem) {
  const url = downloadItem.finalUrl || downloadItem.url;
  if (!normalizeHttpUrl(url)) {
    return false;
  }

  if (downloadItem.byExtensionId || downloadItem.incognito) {
    return false;
  }

  if (downloadItem.danger && downloadItem.danger !== "safe") {
    return false;
  }

  const mime = (downloadItem.mime || "").toLowerCase();
  if (mime.startsWith("text/html") || mime.startsWith("text/plain")) {
    return false;
  }

  const fileSize = Number(downloadItem.fileSize);
  if (Number.isFinite(fileSize) && fileSize === 0) {
    return false;
  }

  const filename = downloadItem.filename || filenameFromUrl(url) || "";
  const extension = getLowercaseExtension(filename);

  if (extension && DIRECT_DOWNLOAD_EXTENSIONS.has(extension)) {
    return true;
  }

  return DIRECT_DOWNLOAD_MIME_PREFIXES.some((prefix) => mime.startsWith(prefix));
}

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
