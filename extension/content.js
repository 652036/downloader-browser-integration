let lastContextLinkUrl = null;

document.addEventListener("contextmenu", (event) => {
  const link = event.target && event.target.closest && event.target.closest("a[href]");
  lastContextLinkUrl = link ? link.href : null;
}, true);

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.type !== "local-downloader.get-context") {
    return false;
  }

  sendResponse({
    linkUrl: lastContextLinkUrl,
    selectedUrl: selectedTextUrl(),
  });

  return false;
});

function selectedTextUrl() {
  const text = String(window.getSelection ? window.getSelection() : "").trim();
  if (!text) {
    return null;
  }

  try {
    const parsed = new URL(text);
    return parsed.protocol === "http:" || parsed.protocol === "https:" ? parsed.href : null;
  } catch (_error) {
    return null;
  }
}
