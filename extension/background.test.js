const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

function createChromeMock(options = {}) {
  const listeners = {
    installed: [],
    startup: [],
    contextClicked: [],
    determiningFilename: [],
    downloadCreated: [],
    cancelled: [],
    browserDownloads: [],
    storageSet: null,
    contextMenu: null,
  };

  const ports = [];
  const sessionData = { ...(options.sessionStore || {}) };

  function createPort(host) {
    const port = {
      host,
      posted: [],
      messageListeners: [],
      disconnectListeners: [],
      onMessage: { addListener: (fn) => port.messageListeners.push(fn) },
      onDisconnect: { addListener: (fn) => port.disconnectListeners.push(fn) },
      postMessage: (message) => {
        if (options.postMessageThrows) {
          throw new Error("Attempting to use a disconnected port.");
        }

        port.posted.push(message);

        if (options.autoRespond !== false) {
          const response = options.respondWith
            ? options.respondWith(message)
            : defaultResponse(message);
          if (response) {
            setImmediate(() => port.emitMessage(response));
          }
        }
      },
      emitMessage: (message) => port.messageListeners.forEach((fn) => fn(message)),
      emitDisconnect: () => port.disconnectListeners.forEach((fn) => fn()),
    };

    ports.push(port);
    return port;
  }

  function defaultResponse(message) {
    if (message.type === "settings.get") {
      return {
        type: "settings.value",
        id: message.id,
        interceptExtensions: options.settingsExtensions || null,
        interceptMimePrefixes: options.settingsMimePrefixes || null,
      };
    }

    return { type: "download.accepted", id: message.id, status: "queued" };
  }

  const chromeMock = {
    runtime: {
      onInstalled: { addListener: (fn) => listeners.installed.push(fn) },
      onStartup: { addListener: (fn) => listeners.startup.push(fn) },
      connectNative: (host) => {
        if (options.connectNativeThrows) {
          throw new Error("Specified native messaging host not found.");
        }

        return createPort(host);
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
      onCreated: { addListener: (fn) => listeners.downloadCreated.push(fn) },
      onDeterminingFilename: { addListener: (fn) => listeners.determiningFilename.push(fn) },
      cancel: (downloadId, callback) => {
        listeners.cancelled.push(downloadId);
        if (callback) callback();
      },
      download: (opts, callback) => {
        listeners.browserDownloads.push(opts);
        if (callback) callback(listeners.browserDownloads.length);
      },
    },
    cookies: {
      getAll: (details, callback) => {
        callback(options.cookies || []);
      },
    },
    storage: {
      local: {
        get: (keys, callback) => callback(options.storedConfig || {}),
        set: (value) => {
          listeners.storageSet = value;
        },
      },
      session: {
        data: sessionData,
        get: (keys, callback) => {
          const result = {};
          const list = Array.isArray(keys) ? keys : [keys];
          for (const key of list) {
            if (key in sessionData) {
              result[key] = sessionData[key];
            }
          }
          callback(result);
        },
        set: (value) => {
          Object.assign(sessionData, value);
          listeners.sessionSet = value;
        },
      },
    },
    tabs: {
      sendMessage: (_tabId, _message, callback) => callback({ selectedUrl: null }),
    },
    listeners,
    ports,
  };

  return chromeMock;
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
    setImmediate,
    Date,
    Map,
    Set,
    Promise,
  };
  vm.createContext(sandbox);
  vm.runInContext(code, sandbox, { filename: "background.js" });
  return sandbox;
}

function flush(times = 4) {
  let p = Promise.resolve();
  for (let i = 0; i < times; i++) {
    p = p.then(() => new Promise((resolve) => setImmediate(resolve)));
  }
  return p;
}

const zipDownloadItem = (overrides = {}) => ({
  id: 42,
  url: "https://example.test/downloads/archive.zip",
  finalUrl: "https://example.test/downloads/archive.zip",
  filename: "archive.zip",
  referrer: "https://example.test/",
  mime: "application/zip",
  fileSize: 1234,
  danger: "safe",
  incognito: false,
  byExtensionId: undefined,
  ...overrides,
});

async function testDeterminingFilenameInterceptsAndSendsCreateWithCookies() {
  const chromeMock = createChromeMock({
    cookies: [
      { name: "session", value: "abc123" },
      { name: "token", value: "xyz" },
    ],
  });
  loadBackground(chromeMock);

  chromeMock.listeners.determiningFilename[0](zipDownloadItem(), () => {});
  await flush();

  assert.deepStrictEqual(chromeMock.listeners.cancelled, [42]);
  assert.strictEqual(chromeMock.ports.length, 1);
  assert.strictEqual(chromeMock.ports[0].host, "com.local.fastdownloader");
  assert.strictEqual(chromeMock.ports[0].posted.length, 1);

  const message = chromeMock.ports[0].posted[0];
  assert.strictEqual(message.type, "download.create");
  assert.strictEqual(message.source, "browser-download");
  assert.strictEqual(message.url, "https://example.test/downloads/archive.zip");
  assert.strictEqual(message.suggestedFilename, "archive.zip");
  assert.strictEqual(message.cookieHeader, "session=abc123; token=xyz");
  assert.strictEqual(message.userAgent, "NodeTest/1.0");
  assert.strictEqual(message.fileSize, 1234);
  assert.strictEqual(message.mime, "application/zip");

  // Accepted: no fail-open browser download.
  assert.strictEqual(chromeMock.listeners.browserDownloads.length, 0);
}

async function testDeterminingFilenameIgnoresHtmlNavigation() {
  const chromeMock = createChromeMock();
  loadBackground(chromeMock);

  chromeMock.listeners.determiningFilename[0](
    zipDownloadItem({
      id: 43,
      url: "https://example.test/article",
      finalUrl: "https://example.test/article",
      filename: "article.html",
      mime: "text/html",
    }),
    () => {},
  );
  await flush();

  assert.deepStrictEqual(chromeMock.listeners.cancelled, []);
  assert.strictEqual(chromeMock.ports.length, 0);
}

async function testDeterminingFilenameSkipsIncognitoAndExtensionDownloads() {
  const chromeMock = createChromeMock();
  loadBackground(chromeMock);

  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 44, incognito: true }), () => {});
  chromeMock.listeners.determiningFilename[0](
    zipDownloadItem({ id: 45, byExtensionId: "otherextensionid" }),
    () => {},
  );
  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 46, danger: "file" }), () => {});
  await flush();

  assert.deepStrictEqual(chromeMock.listeners.cancelled, []);
  assert.strictEqual(chromeMock.ports.length, 0);
}

async function testReturnToBrowserAddsBypassAndRetriggersBrowserDownload() {
  const chromeMock = createChromeMock();
  loadBackground(chromeMock);

  // Open a port by intercepting a first download.
  chromeMock.listeners.determiningFilename[0](zipDownloadItem(), () => {});
  await flush();
  assert.strictEqual(chromeMock.ports.length, 1);

  // App pushes returnToBrowser for the same URL.
  chromeMock.ports[0].emitMessage({
    type: "download.returnToBrowser",
    id: "any",
    url: "https://example.test/downloads/archive.zip",
    suggestedFilename: "archive.zip",
  });
  await flush();

  assert.strictEqual(chromeMock.listeners.browserDownloads.length, 1);
  assert.strictEqual(chromeMock.listeners.browserDownloads[0].url, "https://example.test/downloads/archive.zip");
  assert.strictEqual(chromeMock.listeners.browserDownloads[0].filename, "archive.zip");

  // The re-triggered download must NOT be intercepted again (bypass list hit).
  const cancelledBefore = chromeMock.listeners.cancelled.length;
  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 99 }), () => {});
  await flush();
  assert.strictEqual(chromeMock.listeners.cancelled.length, cancelledBefore);
}

async function testFailOpenOnPortErrorRetriggersBrowserDownload() {
  const chromeMock = createChromeMock({ postMessageThrows: true });
  loadBackground(chromeMock);

  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 50 }), () => {});
  await flush();

  // Original download was cancelled, then failed open: browser re-download triggered.
  assert.deepStrictEqual(chromeMock.listeners.cancelled, [50]);
  assert.strictEqual(chromeMock.listeners.browserDownloads.length, 1);
  assert.strictEqual(
    chromeMock.listeners.browserDownloads[0].url,
    "https://example.test/downloads/archive.zip",
  );

  // And the re-triggered download passes through untouched.
  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 51 }), () => {});
  await flush();
  assert.deepStrictEqual(chromeMock.listeners.cancelled, [50]);
}

async function testFailOpenOnDownloadErrorResponse() {
  const chromeMock = createChromeMock({
    respondWith: (message) => ({
      type: "download.error",
      id: message.id,
      code: "host_protocol_error",
      message: "App unavailable",
    }),
  });
  loadBackground(chromeMock);

  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 60 }), () => {});
  await flush();

  assert.deepStrictEqual(chromeMock.listeners.cancelled, [60]);
  assert.strictEqual(chromeMock.listeners.browserDownloads.length, 1);
}

async function testFailOpenOnPortDisconnectWhileWaiting() {
  const chromeMock = createChromeMock({ autoRespond: false });
  loadBackground(chromeMock);

  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 70 }), () => {});
  await flush();

  assert.strictEqual(chromeMock.ports.length, 1);
  chromeMock.ports[0].emitDisconnect();
  await flush();

  assert.deepStrictEqual(chromeMock.listeners.cancelled, [70]);
  assert.strictEqual(chromeMock.listeners.browserDownloads.length, 1);
}

async function testSettingsSyncUpdatesInterceptList() {
  const chromeMock = createChromeMock({
    settingsExtensions: [".customext"],
    settingsMimePrefixes: ["application/x-custom"],
  });
  loadBackground(chromeMock);

  // onInstalled triggers settings.get sync.
  chromeMock.listeners.installed[0]();
  await flush();

  assert.strictEqual(chromeMock.listeners.contextMenu.title, "Download with Local Downloader");
  assert.ok(chromeMock.listeners.storageSet);
  assert.deepStrictEqual(chromeMock.listeners.storageSet.interceptExtensions, [".customext"]);

  // The new custom extension is now intercepted...
  chromeMock.listeners.determiningFilename[0](
    zipDownloadItem({
      id: 80,
      url: "https://example.test/files/data.customext",
      finalUrl: "https://example.test/files/data.customext",
      filename: "data.customext",
      mime: "application/x-unknown",
    }),
    () => {},
  );
  await flush();
  assert.ok(chromeMock.listeners.cancelled.includes(80));

  // ...while .zip (no longer in the synced list, and MIME not matching) is not.
  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 81 }), () => {});
  await flush();
  assert.ok(!chromeMock.listeners.cancelled.includes(81));
}

async function testCachedConfigLoadedFromStorage() {
  const chromeMock = createChromeMock({
    storedConfig: {
      interceptExtensions: [".cachedext"],
      interceptMimePrefixes: ["application/x-cached"],
    },
  });
  loadBackground(chromeMock);
  await flush();

  chromeMock.listeners.determiningFilename[0](
    zipDownloadItem({
      id: 90,
      url: "https://example.test/files/data.cachedext",
      finalUrl: "https://example.test/files/data.cachedext",
      filename: "data.cachedext",
      mime: "application/x-unknown",
    }),
    () => {},
  );
  await flush();

  assert.ok(chromeMock.listeners.cancelled.includes(90));
}

async function testContextMenuSendsDownloadCreateOverPort() {
  const chromeMock = createChromeMock({
    cookies: [{ name: "sid", value: "42" }],
  });
  loadBackground(chromeMock);

  chromeMock.listeners.installed[0]();
  await flush();

  chromeMock.listeners.contextClicked[0](
    {
      menuItemId: "download-with-local-downloader",
      linkUrl: "https://example.test/files/report.zip",
      pageUrl: "https://example.test/page",
      suggestedFilename: "report.zip",
    },
    { id: 7, url: "https://example.test/page" },
  );
  await flush();

  const port = chromeMock.ports[0];
  const createMessages = port.posted.filter((m) => m.type === "download.create");
  assert.strictEqual(createMessages.length, 1);
  assert.strictEqual(createMessages[0].source, "context-menu");
  assert.strictEqual(createMessages[0].url, "https://example.test/files/report.zip");
  assert.strictEqual(createMessages[0].suggestedFilename, "report.zip");
  assert.strictEqual(createMessages[0].referrer, "https://example.test/page");
  assert.strictEqual(createMessages[0].cookieHeader, "sid=42");
  assert.strictEqual(createMessages[0].userAgent, "NodeTest/1.0");
}

async function testContextMenuFailsOpenWhenHostUnavailable() {
  const chromeMock = createChromeMock({ connectNativeThrows: true });
  loadBackground(chromeMock);

  chromeMock.listeners.contextClicked[0](
    {
      menuItemId: "download-with-local-downloader",
      linkUrl: "https://example.test/files/report.zip",
      pageUrl: "https://example.test/page",
    },
    { id: 7, url: "https://example.test/page" },
  );
  await flush();

  assert.strictEqual(chromeMock.listeners.browserDownloads.length, 1);
  assert.strictEqual(chromeMock.listeners.browserDownloads[0].url, "https://example.test/files/report.zip");
}

async function testSuggestIsCalledOnInterceptAndBypass() {
  const chromeMock = createChromeMock();
  loadBackground(chromeMock);

  const suggested = [];
  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 110 }), () => suggested.push("intercept"));
  await flush();
  assert.deepStrictEqual(suggested, ["intercept"]);
  assert.deepStrictEqual(chromeMock.listeners.cancelled, [110]);

  chromeMock.ports[0].emitMessage({
    type: "download.returnToBrowser",
    url: "https://example.test/downloads/archive.zip",
    suggestedFilename: "archive.zip",
  });
  await flush();

  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 111 }), () => suggested.push("bypass"));
  await flush();
  assert.deepStrictEqual(suggested, ["intercept", "bypass"]);
  assert.ok(!chromeMock.listeners.cancelled.includes(111));
}

async function testFailOpenPersistsBypassInSessionStorage() {
  const chromeMock = createChromeMock({ postMessageThrows: true });
  loadBackground(chromeMock);

  const suggested = [];
  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 120 }), () => suggested.push("fail-open"));
  await flush();

  assert.deepStrictEqual(suggested, ["fail-open"]);
  assert.ok(chromeMock.listeners.sessionSet);
  assert.ok(chromeMock.listeners.sessionSet.bypassUrls["https://example.test/downloads/archive.zip"]);

  // A fresh service worker that only has session storage should honor the bypass.
  const reloaded = createChromeMock({
    sessionStore: chromeMock.storage.session.data,
  });
  loadBackground(reloaded);
  await flush();

  reloaded.listeners.determiningFilename[0](zipDownloadItem({ id: 121 }), () => {});
  await flush();
  assert.deepStrictEqual(reloaded.listeners.cancelled, []);
}

async function testSettingsChangedPushUpdatesInterceptCache() {
  const chromeMock = createChromeMock();
  loadBackground(chromeMock);

  // Open a port by intercepting a first download.
  chromeMock.listeners.determiningFilename[0](zipDownloadItem(), () => {});
  await flush();
  assert.strictEqual(chromeMock.ports.length, 1);

  chromeMock.ports[0].emitMessage({
    type: "settings.changed",
    interceptExtensions: [".customext"],
    interceptMimePrefixes: ["application/x-custom"],
  });
  await flush();

  assert.ok(chromeMock.listeners.storageSet);
  assert.deepStrictEqual(chromeMock.listeners.storageSet.interceptExtensions, [".customext"]);

  chromeMock.listeners.determiningFilename[0](
    zipDownloadItem({
      id: 130,
      url: "https://example.test/files/data.customext",
      finalUrl: "https://example.test/files/data.customext",
      filename: "data.customext",
      mime: "application/x-unknown",
    }),
    () => {},
  );
  await flush();
  assert.ok(chromeMock.listeners.cancelled.includes(130));

  chromeMock.listeners.determiningFilename[0](zipDownloadItem({ id: 131 }), () => {});
  await flush();
  assert.ok(!chromeMock.listeners.cancelled.includes(131));
}

function testResponseTimeoutAllowsColdStart() {
  const code = fs.readFileSync(path.join(__dirname, "background.js"), "utf8");
  const match = code.match(/RESPONSE_TIMEOUT_MS\s*=\s*(\d+)/);
  assert.ok(match, "RESPONSE_TIMEOUT_MS should be declared");
  assert.ok(
    Number(match[1]) >= 10000,
    `RESPONSE_TIMEOUT_MS must be at least 10000ms to cover Host's 5s App launch, got ${match[1]}`,
  );
}

function testManifestDeclaresRequiredPermissions() {
  const manifestPath = path.join(__dirname, "manifest.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));

  assert.strictEqual(manifest.manifest_version, 3);
  assert.strictEqual(manifest.background.service_worker, "background.js");
  assert.ok(manifest.permissions.includes("nativeMessaging"));
  assert.ok(manifest.permissions.includes("downloads"));
  assert.ok(manifest.permissions.includes("cookies"));
  assert.ok(manifest.permissions.includes("storage"));
  assert.deepStrictEqual(manifest.host_permissions, ["<all_urls>"]);
  assert.strictEqual(manifest.icons["128"], "icons/icon.svg");
}

(async () => {
  await testDeterminingFilenameInterceptsAndSendsCreateWithCookies();
  await testDeterminingFilenameIgnoresHtmlNavigation();
  await testDeterminingFilenameSkipsIncognitoAndExtensionDownloads();
  await testReturnToBrowserAddsBypassAndRetriggersBrowserDownload();
  await testFailOpenOnPortErrorRetriggersBrowserDownload();
  await testFailOpenOnDownloadErrorResponse();
  await testFailOpenOnPortDisconnectWhileWaiting();
  await testSettingsSyncUpdatesInterceptList();
  await testCachedConfigLoadedFromStorage();
  await testContextMenuSendsDownloadCreateOverPort();
  await testContextMenuFailsOpenWhenHostUnavailable();
  await testSuggestIsCalledOnInterceptAndBypass();
  await testFailOpenPersistsBypassInSessionStorage();
  await testSettingsChangedPushUpdatesInterceptCache();
  testResponseTimeoutAllowsColdStart();
  testManifestDeclaresRequiredPermissions();
  console.log("extension tests ok");
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
