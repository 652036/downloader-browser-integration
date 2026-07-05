const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

function createChromeMock() {
  const listeners = {
    installed: [],
    contextClicked: [],
    determiningFilename: [],
    downloadCreated: [],
    messages: [],
  };

  return {
    runtime: {
      onInstalled: { addListener: (fn) => listeners.installed.push(fn) },
      sendNativeMessage: (host, message, callback) => {
        listeners.messages.push({ host, message });
        if (callback) callback({ type: "download.accepted", id: message.id });
      },
      lastError: null,
    },
    contextMenus: {
      create: (item) => {
        listeners.contextMenu = item;
      },
      onClicked: { addListener: (fn) => listeners.contextClicked.push(fn) },
    },
    downloads: {
      onCreated: {
        addListener: (fn) => listeners.downloadCreated.push(fn),
      },
      onDeterminingFilename: {
        addListener: (fn) => listeners.determiningFilename.push(fn),
      },
      cancel: (downloadId) => {
        listeners.cancelled = listeners.cancelled || [];
        listeners.cancelled.push(downloadId);
      },
    },
    scripting: {
      executeScript: (_details, callback) => callback([{ result: { selectedUrl: null } }]),
    },
    tabs: {
      sendMessage: (_tabId, _message, callback) => callback({ selectedUrl: null }),
    },
    listeners,
  };
}

function loadBackground(chromeMock) {
  const code = fs.readFileSync(path.join(__dirname, "background.js"), "utf8");
  const sandbox = {
    chrome: chromeMock,
    navigator: { userAgent: "NodeTest/1.0" },
    URL,
    console,
    setTimeout,
    clearTimeout,
  };
  vm.createContext(sandbox);
  vm.runInContext(code, sandbox, { filename: "background.js" });
  return sandbox;
}

async function testContextMenuSendsNativeMessage() {
  const chromeMock = createChromeMock();
  loadBackground(chromeMock);

  assert.strictEqual(chromeMock.listeners.installed.length, 1);
  chromeMock.listeners.installed[0]();
  assert.strictEqual(chromeMock.listeners.contextMenu.title, "Download with Local Downloader");

  chromeMock.listeners.contextClicked[0](
    {
      menuItemId: "download-with-local-downloader",
      linkUrl: "https://example.test/files/report.zip",
      pageUrl: "https://example.test/page",
      suggestedFilename: "report.zip",
    },
    { id: 7, url: "https://example.test/page" },
  );

  await new Promise((resolve) => setImmediate(resolve));
  assert.strictEqual(chromeMock.listeners.messages.length, 1);
  assert.strictEqual(chromeMock.listeners.messages[0].host, "com.local.fastdownloader");
  assert.deepStrictEqual(
    {
      type: chromeMock.listeners.messages[0].message.type,
      source: chromeMock.listeners.messages[0].message.source,
      url: chromeMock.listeners.messages[0].message.url,
      suggestedFilename: chromeMock.listeners.messages[0].message.suggestedFilename,
      referrer: chromeMock.listeners.messages[0].message.referrer,
      userAgent: chromeMock.listeners.messages[0].message.userAgent,
    },
    {
      type: "download.create",
      source: "context-menu",
      url: "https://example.test/files/report.zip",
      suggestedFilename: "report.zip",
      referrer: "https://example.test/page",
      userAgent: "NodeTest/1.0",
    },
  );
}

async function testContextMenuHandlesMalformedFilenameEncoding() {
  const chromeMock = createChromeMock();
  loadBackground(chromeMock);

  chromeMock.listeners.contextClicked[0](
    {
      menuItemId: "download-with-local-downloader",
      linkUrl: "https://example.test/files/bad-%E0%A4%A.zip",
      pageUrl: "https://example.test/page",
    },
    { id: 7, url: "https://example.test/page" },
  );

  await new Promise((resolve) => setImmediate(resolve));
  assert.strictEqual(chromeMock.listeners.messages.length, 1);
  assert.strictEqual(
    chromeMock.listeners.messages[0].message.suggestedFilename,
    "bad-%E0%A4%A.zip",
  );
}

async function testDeterminingFilenameConservativeHandoff() {
  const chromeMock = createChromeMock();
  loadBackground(chromeMock);

  let suggestion;
  chromeMock.listeners.determiningFilename[0](
    {
      id: 42,
      url: "https://example.test/downloads/app.exe",
      filename: "app.exe",
      finalUrl: "https://example.test/downloads/app.exe",
      referrer: "https://example.test/",
      mime: "application/octet-stream",
      fileSize: 1234,
      danger: "safe",
      byExtensionId: undefined,
    },
    (value) => {
      suggestion = value;
    },
  );

  await new Promise((resolve) => setImmediate(resolve));
  assert.strictEqual(suggestion, undefined);
  assert.deepStrictEqual(chromeMock.listeners.cancelled, [42]);
  assert.strictEqual(chromeMock.listeners.messages.length, 1);
  assert.strictEqual(chromeMock.listeners.messages[0].message.source, "browser-download");
}

async function testDeterminingFilenameHandsOffDirectDownloadWithUnknownSize() {
  const chromeMock = createChromeMock();
  loadBackground(chromeMock);

  chromeMock.listeners.determiningFilename[0](
    {
      id: 44,
      url: "https://example.test/downloads/archive.zip",
      filename: "archive.zip",
      finalUrl: "https://example.test/downloads/archive.zip",
      referrer: "https://example.test/",
      mime: "application/zip",
      danger: "safe",
    },
    () => {},
  );

  await new Promise((resolve) => setImmediate(resolve));
  assert.deepStrictEqual(chromeMock.listeners.cancelled, [44]);
  assert.strictEqual(chromeMock.listeners.messages.length, 1);
  assert.strictEqual(chromeMock.listeners.messages[0].message.source, "browser-download");
}

async function testDownloadCreatedHandsOffDirectDownload() {
  const chromeMock = createChromeMock();
  loadBackground(chromeMock);

  chromeMock.listeners.downloadCreated[0]({
    id: 45,
    url: "https://example.test/downloads/created.zip",
    filename: "created.zip",
    finalUrl: "https://example.test/downloads/created.zip",
    referrer: "https://example.test/",
    mime: "application/zip",
    danger: "safe",
  });

  await new Promise((resolve) => setImmediate(resolve));
  assert.deepStrictEqual(chromeMock.listeners.cancelled, [45]);
  assert.strictEqual(chromeMock.listeners.messages.length, 1);
  assert.strictEqual(chromeMock.listeners.messages[0].message.source, "browser-download");
}

async function testDeterminingFilenameIgnoresHtmlNavigation() {
  const chromeMock = createChromeMock();
  loadBackground(chromeMock);

  let suggestion;
  chromeMock.listeners.determiningFilename[0](
    {
      id: 43,
      url: "https://example.test/article",
      filename: "article.html",
      finalUrl: "https://example.test/article",
      mime: "text/html",
      fileSize: -1,
      danger: "safe",
    },
    (value) => {
      suggestion = value;
    },
  );

  await new Promise((resolve) => setImmediate(resolve));
  assert.strictEqual(suggestion, undefined);
  assert.strictEqual(chromeMock.listeners.cancelled, undefined);
  assert.strictEqual(chromeMock.listeners.messages.length, 0);
}

function testManifestReferencesIconAndScripts() {
  const manifestPath = path.join(__dirname, "manifest.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));

  assert.strictEqual(manifest.manifest_version, 3);
  assert.strictEqual(manifest.background.service_worker, "background.js");
  assert.deepStrictEqual(manifest.host_permissions, ["http://*/*", "https://*/*"]);
  assert.ok(manifest.permissions.includes("nativeMessaging"));
  assert.strictEqual(manifest.icons["128"], "icons/icon.svg");
}

(async () => {
  await testContextMenuSendsNativeMessage();
  await testContextMenuHandlesMalformedFilenameEncoding();
  await testDeterminingFilenameConservativeHandoff();
  await testDeterminingFilenameHandsOffDirectDownloadWithUnknownSize();
  await testDownloadCreatedHandsOffDirectDownload();
  await testDeterminingFilenameIgnoresHtmlNavigation();
  testManifestReferencesIconAndScripts();
  console.log("extension tests ok");
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
