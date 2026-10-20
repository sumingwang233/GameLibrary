/* promo/ui-host/mock.js
 * Browser-side Tauri IPC mock for the promotional film only.
 * Injected by promo/ui-host/server.mjs before the product bundle.
 * The product source is NOT modified; every real side effect (plugin injection,
 * script generation, launching a game) is intercepted here and never leaves memory.
 */
(function () {
  "use strict";

  var DEMO = window.__PROMO_DEMO__ || { demo: { games: [], tags: [] } };
  var PARAMS = new URLSearchParams(location.search);
  var SCENE = PARAMS.get("scene") || "library";
  var THEME = PARAMS.get("theme") === "light" ? "light" : "dark";
  var CLASSICS = "D:\\Games\\Classics";
  var LIBRARY = "D:\\Games\\Library";
  var MTOOL_ROOT = "D:\\Tools\\MTool";
  var PICTURES = "D:\\Pictures";

  // ---------------------------------------------------------------- scenarios
  var SCENARIOS = {
    // C: a settled library, nothing translated yet.
    library: { existing: 12, translated: [], roots: [CLASSICS, LIBRARY], unityKey: false, toolRoot: null },
    // B: nine games already filed; scanning the second folder yields three candidates.
    scan: { existing: 9, translated: [], roots: [CLASSICS], unityKey: false, toolRoot: null, addsCandidates: true },
    // D: same library as C, translation happens on camera.
    translate: { existing: 12, translated: [], roots: [CLASSICS, LIBRARY], unityKey: false, toolRoot: null },
    // E: translation service already configured; Ashen Vale's name was translated in D.
    unity: { existing: 12, translated: [3], roots: [CLASSICS, LIBRARY], unityKey: true, toolRoot: null },
    // F: same, plus MTool not configured yet.
    mtool: { existing: 12, translated: [3], roots: [CLASSICS, LIBRARY], unityKey: true, toolRoot: null },
    // G/H: everything done earlier is visible at once.
    final: { existing: 12, translated: [3, 6, 8, 12], roots: [CLASSICS, LIBRARY], unityKey: true, toolRoot: MTOOL_ROOT }
  };
  var CONF = SCENARIOS[SCENE] || SCENARIOS.library;

  var GENRES = ["动作", "角色扮演", "冒险", "独立", "策略", "模拟", "竞速"];
  var UNITY_TAG = "未翻译";

  // ---------------------------------------------------------------- utilities
  var state = null;
  var coverCache = Object.create(null);

  function nowIso(offsetMinutes) {
    return new Date(Date.now() - (offsetMinutes || 0) * 60000).toISOString();
  }
  function pad(n) { return n < 10 ? "0" + n : String(n); }
  function gameIdAt(index) { return "g" + pad(index + 1); }
  function rootFor(index) { return index < CONF.existing && index < 9 ? CLASSICS : LIBRARY; }
  function b64Utf8(text) {
    var bytes = new TextEncoder().encode(text);
    var bin = "";
    for (var i = 0; i < bytes.length; i += 1) bin += String.fromCharCode(bytes[i]);
    return btoa(bin);
  }
  function envelope(requestId, data) {
    return { requestId: requestId || "promo", ok: true, status: "ok", data: data, libraryInstanceId: "promo-demo", dataEpoch: "1" };
  }
  function fail(requestId, message, code) {
    return { requestId: requestId || "promo", ok: false, status: "failed", data: {},
      error: { code: code || "MockFailure", message: message || "示例数据不支持该操作", retryable: false, recoveryOperation: null } };
  }
  function delay(ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); }

  // ---------------------------------------------------------------- demo state
  function tagItems() {
    return DEMO.demo.tags.map(function (name, index) {
      return { tagId: "tag-" + (index + 1), kind: "user", name: name, category: "special",
        sortOrder: index, starred: 0, displayName: null, revision: 1,
        gameCount: state.games.filter(function (game) { return hasTag(game, name); }).length };
    });
  }
  function hasTag(game, name) {
    return (game.tags || []).some(function (tag) { return tag.name === name; });
  }
  function refreshTagCounts() {
    state.tags = tagItems();
  }

  function makeGame(index) {
    var spec = DEMO.demo.games[index];
    var translated = CONF.translated.indexOf(index + 1) !== -1;
    // v1.7.22 的「全部游戏」查询实际发送 played:false（后端语义为“仅未游玩”），
    // 因此演示库内所有游戏统一保持 played=false，避免卡片在全部游戏视图里消失。
    var played = false;
    var root = rootFor(index);
    var game = {
      gameId: gameIdAt(index),
      title: translated ? spec.zh : spec.title,
      originalTitle: spec.title,
      translatedTitle: translated ? spec.zh : null,
      titleDisplayMode: translated ? "translated" : "original",
      titleSource: "scan", summary: "", rootPath: root,
      kind: "folder",
      engine: spec.unity ? "unity" : (spec.mtool ? "rpgMakerMvMz" : (index % 3 === 0 ? "renpy" : "rpgMakerMvMz")),
      entryPath: root + "\\" + spec.title + "\\game.exe",
      membership: "root",
      favorite: !!spec.favorite,
      played: played,
      rating: 0,
      revision: 1,
      coverAssetId: "cover-" + gameIdAt(index),
      acceptedUtc: nowIso(60 * 24 * (30 - index)),
      updatedUtc: nowIso(60 * 5 * index),
      availability: "available",
      tags: spec.tags.map(function (name) { return { kind: "user", name: name }; }),
      playtimeMinutes: 0,
      lastPlayedUtc: null
    };
    return game;
  }

  function makeCandidate(index) {
    var spec = DEMO.demo.games[index];
    return {
      candidateId: "cand-" + (index + 1),
      relativePath: spec.title,
      physicalPath: LIBRARY + "\\" + spec.title,
      kind: "folder", reviewState: "pendingReview", revision: 1, flash: null
    };
  }

  function resetState() {
    var i;
    state = {
      games: [], candidates: [], roots: [], tags: [], views: [], notifications: [],
      profiles: Object.create(null), revisions: Object.create(null),
      unity: { attempt: null, settings: { provider: "deepseek", endpoint: "https://api.deepseek.com/chat/completions", model: "deepseek-flash", hasKey: !!CONF.unityKey, boundTagId: "tag-5" } },
      mtool: { toolRoot: CONF.toolRoot, statuses: Object.create(null) },
      scanJob: null, jobs: Object.create(null), translatedAt: Object.create(null),
      candidatesAdded: false, launchAttempt: null
    };
    for (i = 0; i < CONF.existing; i += 1) state.games.push(makeGame(i));
    state.roots = CONF.roots.map(function (path, index) {
      return { rootId: "root-" + (index + 1), path: path, kind: "library", revision: 1 };
    });
    state.tags = tagItems();
    state.settings = {
      revision: 3, theme: THEME, uiLanguage: "zh-CN",
      uiFontFamily: "Microsoft YaHei UI", closeToTray: false, autostartEnabled: false,
      scanIntervalMinutes: 30, titleTranslationEngine: "balanced",
      cacheParentDirectory: null, activeViewId: null
    };
  }

  // ---------------------------------------------------------------- covers
  var MOTIFS = {
    rings: function (h) {
      return [[300, 330, 150, 1.5], [300, 330, 210, 1], [300, 330, 275, 0.7]].map(function (c) {
        return '<circle cx="' + c[0] + '" cy="' + c[1] + '" r="' + c[2] + '" fill="none" stroke="' + tint(h, 0.5) + '" stroke-width="' + c[3] + '"/>';
      }).join("") + '<circle cx="300" cy="330" r="86" fill="' + tint(h, 0.22) + '"/>';
    },
    lights: function (h) {
      var out = "";
      for (var i = 0; i < 7; i += 1) out += '<rect x="' + (150 + i * 46) + '" y="150" width="14" height="360" rx="7" fill="' + tint(h, 0.2 + i * 0.06) + '"/>';
      return out;
    },
    peaks: function (h) {
      return '<path d="M60 620 L230 300 L330 470 L430 260 L560 620 Z" fill="' + tint(h, 0.22) + '"/>' +
        '<path d="M230 300 L270 375 L196 375 Z" fill="' + tint(h, 0.5) + '"/>';
    },
    grid: function (h) {
      var out = "";
      for (var i = 0; i < 9; i += 1) out += '<line x1="' + (110 + i * 45) + '" y1="170" x2="' + (110 + i * 45) + '" y2="500" stroke="' + tint(h, 0.28) + '" stroke-width="1.5"/>';
      for (var j = 0; j < 8; j += 1) out += '<line x1="110" y1="' + (170 + j * 45) + '" x2="470" y2="' + (170 + j * 45) + '" stroke="' + tint(h, 0.28) + '" stroke-width="1.5"/>';
      return out + '<circle cx="300" cy="320" r="20" fill="' + tint(h, 0.75) + '"/>';
    },
    leaves: function (h) {
      return [[210, 300, 90], [330, 360, 120], [280, 250, 60]].map(function (c) {
        return '<ellipse cx="' + c[0] + '" cy="' + c[1] + '" rx="' + c[2] + '" ry="' + (c[2] * 0.55) + '" transform="rotate(-24 ' + c[0] + ' ' + c[1] + ')" fill="' + tint(h, 0.24) + '"/>';
      }).join("");
    },
    paper: function (h) {
      return [0, 1, 2].map(function (i) {
        return '<rect x="' + (190 + i * 34) + '" y="' + (230 + i * 22) + '" width="220" height="290" rx="4" fill="' + tint(h, 0.1 + i * 0.06) + '" transform="rotate(' + (-6 + i * 6) + ' 300 360)"/>';
      }).join("");
    },
    bars: function (h) {
      var out = "";
      for (var i = 0; i < 5; i += 1) out += '<rect x="150" y="' + (240 + i * 58) + '" width="' + (300 - i * 34) + '" height="26" rx="13" fill="' + tint(h, 0.45 - i * 0.06) + '"/>';
      return out;
    },
    sun: function (h) {
      return '<circle cx="300" cy="330" r="130" fill="' + tint(h, 0.3) + '"/>' +
        '<circle cx="300" cy="330" r="78" fill="' + tint(h, 0.6) + '"/>' +
        '<rect x="150" y="330" width="300" height="3" fill="' + tint(h, 0.8) + '"/>';
    },
    orbit: function (h) {
      return '<ellipse cx="300" cy="330" rx="200" ry="96" fill="none" stroke="' + tint(h, 0.45) + '" stroke-width="2.5"/>' +
        '<circle cx="300" cy="330" r="64" fill="' + tint(h, 0.22) + '"/>' +
        '<circle cx="500" cy="330" r="16" fill="' + tint(h, 0.8) + '"/>';
    },
    map: function (h) {
      var out = "";
      for (var i = 0; i < 6; i += 1) out += '<circle cx="' + (250 + i * 12) + '" cy="' + (330 - i * 8) + '" r="' + (40 + i * 42) + '" fill="none" stroke="' + tint(h, 0.34 - i * 0.04) + '" stroke-width="1.5"/>';
      return out + '<circle cx="300" cy="330" r="9" fill="' + tint(h, 0.9) + '"/>';
    },
    clouds: function (h) {
      return [[250, 330, 105], [330, 300, 120], [390, 345, 86], [210, 360, 70]].map(function (c) {
        return '<circle cx="' + c[0] + '" cy="' + c[1] + '" r="' + c[2] + '" fill="' + tint(h, 0.18) + '"/>';
      }).join("");
    },
    water: function (h) {
      var out = "";
      for (var i = 0; i < 5; i += 1) {
        var y = 280 + i * 52;
        out += '<path d="M120 ' + y + ' q45 -30 90 0 t90 0 t90 0 t90 0" fill="none" stroke="' + tint(h, 0.42 - i * 0.05) + '" stroke-width="2.4"/>';
      }
      return out;
    }
  };

  function tint(hue, alpha) {
    return "hsla(" + hue + ", 62%, 68%, " + alpha + ")";
  }

  function coverSvg(index) {
    var spec = DEMO.demo.games[index];
    var hue = spec.cover.hue;
    var motif = (MOTIFS[spec.cover.motif] || MOTIFS.rings)(hue);
    var title = esc(spec.title);
    var zh = spec.zh;
    return '<svg xmlns="http://www.w3.org/2000/svg" width="600" height="900" viewBox="0 0 600 900">' +
      '<defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1">' +
      '<stop offset="0" stop-color="hsl(' + hue + ', 26%, 17%)"/>' +
      '<stop offset="0.55" stop-color="hsl(' + hue + ', 24%, 12%)"/>' +
      '<stop offset="1" stop-color="hsl(' + hue + ', 22%, 8%)"/>' +
      '</linearGradient></defs>' +
      '<rect width="600" height="900" fill="url(#g)"/>' +
      '<rect x="18" y="18" width="564" height="864" fill="none" stroke="' + tint(hue, 0.16) + '" stroke-width="1.5"/>' +
      motif +
      '<text x="548" y="70" text-anchor="end" font-family="Segoe UI, sans-serif" font-size="18" fill="hsla(' + hue + ', 18%, 76%, 0.38)" letter-spacing="3">DEMO</text>' +
      '</svg>';
  }
  function esc(text) {
    return String(text).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  }
  function coverBase64(gameId) {
    if (coverCache[gameId]) return coverCache[gameId];
    var index = parseInt(gameId.replace("g", ""), 10) - 1;
    coverCache[gameId] = b64Utf8(coverSvg(index));
    return coverCache[gameId];
  }

  // ---------------------------------------------------------------- queries
  function findGame(gameId) {
    for (var i = 0; i < state.games.length; i += 1) if (state.games[i].gameId === gameId) return state.games[i];
    return null;
  }
  function findTag(tagId) {
    for (var i = 0; i < state.tags.length; i += 1) if (state.tags[i].tagId === tagId) return state.tags[i];
    return null;
  }
  function displayTitle(game) {
    return game.titleDisplayMode === "translated" && game.translatedTitle ? game.translatedTitle : game.originalTitle;
  }
  function applyTranslated(game, zh) {
    game.translatedTitle = zh;
    game.titleDisplayMode = "translated";
    game.title = zh;
    game.revision += 1;
    game.updatedUtc = nowIso(0);
    state.translatedAt[game.gameId] = Date.now();
  }
  function gameDto(game) {
    var copy = JSON.parse(JSON.stringify(game));
    copy.title = displayTitle(game);
    return copy;
  }
  function listGames(p) {
    var items = state.games.slice();
    var search = (p.search || "").trim().toLowerCase();
    if (search) {
      items = items.filter(function (game) {
        return (game.title + " " + game.originalTitle + " " + (game.translatedTitle || "") + " " + game.rootPath)
          .toLowerCase().indexOf(search) !== -1;
      });
    }
    if (p.favorite === true) items = items.filter(function (game) { return game.favorite; });
    if (p.favorite === false) items = items.filter(function (game) { return !game.favorite; });
    if (p.played === true) items = items.filter(function (game) { return game.played; });
    if (p.played === false) items = items.filter(function (game) { return !game.played; });
    if (p.hasCover === true) items = items.filter(function (game) { return !!game.coverAssetId; });
    if (p.hasCover === false) items = items.filter(function (game) { return !game.coverAssetId; });
    if (p.tagId) {
      var tag = findTag(p.tagId);
      if (tag) items = items.filter(function (game) { return hasTag(game, tag.name); });
    }
    var sort = p.sort || "accepted-desc";
    items.sort(function (a, b) {
      if (sort === "title-asc") return displayTitle(a).localeCompare(displayTitle(b));
      if (sort === "title-desc") return displayTitle(b).localeCompare(displayTitle(a));
      if (sort === "updated-desc") return String(b.updatedUtc).localeCompare(String(a.updatedUtc));
      return String(b.acceptedUtc).localeCompare(String(a.acceptedUtc));
    });
    var offset = p.offset || 0;
    var limit = p.limit || 60;
    return { total: items.length, items: items.slice(offset, offset + limit).map(gameDto) };
  }

  // ---------------------------------------------------------------- scan
  function scanProgress(job) {
    var elapsed = Date.now() - job.startedAt;
    var ratio = Math.min(1, elapsed / job.duration);
    return {
      running: elapsed < job.duration,
      coverage: {
        scannedDirectories: Math.round(2 + ratio * 26),
        candidatesFound: Math.min(job.candidates.length, Math.floor(ratio * (job.candidates.length + 1)))
      }
    };
  }
  function finishScan(job) {
    if (state.candidatesAdded) return;
    state.candidatesAdded = true;
    state.candidates = job.candidates.map(function (index) { return makeCandidate(index); });
  }

  // ---------------------------------------------------------------- unity
  var UNITY_STAGES = [
    { until: 700, state: "queued" },
    { until: 1700, state: "inspecting" },
    { until: 3000, state: "installing" }
  ];
  function unityItem() {
    var attempt = state.unity.attempt;
    if (!attempt) return null;
    var game = findGame(attempt.gameId);
    var elapsed = Date.now() - attempt.startedAt;
    var stage = "configured";
    for (var i = 0; i < UNITY_STAGES.length; i += 1) if (elapsed < UNITY_STAGES[i].until) { stage = UNITY_STAGES[i].state; break; }
    return {
      gameId: attempt.gameId,
      title: game ? displayTitle(game) : attempt.gameId,
      attemptId: attempt.attemptId,
      state: stage,
      provider: "deepseek",
      reason: null,
      profileId: "p-" + attempt.gameId,
      jobId: null,
      tagged: stage === "configured"
    };
  }

  // ---------------------------------------------------------------- jobs
  function jobState(job) {
    var elapsed = Date.now() - job.startedAt;
    if (job.kind === "launch") return { state: "running", result: null };
    if (elapsed < job.duration) {
      var progress = null;
      if (job.kind === "titles") {
        var done = 0;
        var items = [];
        for (var i = 0; i < job.gameIds.length; i += 1) {
          var finished = elapsed > (job.duration * (i + 0.35)) / job.gameIds.length;
          if (finished) {
            done += 1;
            var game = findGame(job.gameIds[i]);
            items.push({ gameId: job.gameIds[i], status: "succeeded", reason: null, provider: "bing",
              patch: game ? { gameId: game.gameId, revision: game.revision + 1, title: DEMO.demo.games[parseInt(game.gameId.slice(1), 10) - 1].zh, translatedTitle: DEMO.demo.games[parseInt(game.gameId.slice(1), 10) - 1].zh, titleDisplayMode: "translated" } : null });
          } else {
            items.push({ gameId: job.gameIds[i], status: "pending", reason: null, provider: null, patch: null });
          }
        }
        progress = { total: job.gameIds.length, completed: done, succeeded: done, failed: 0, skipped: 0,
          pending: job.gameIds.length - done, items: items, unprocessedGameIds: [], stopReason: null };
      }
      return { state: "running", result: progress };
    }
    if (job.kind === "titles" && !job.applied) {
      job.applied = true;
      for (var j = 0; j < job.gameIds.length; j += 1) {
        var target = findGame(job.gameIds[j]);
        if (target && !target.translatedTitle) applyTranslated(target, DEMO.demo.games[parseInt(target.gameId.slice(1), 10) - 1].zh);
      }
    }
    var finalItems = job.gameIds.map(function (gameId) {
      var target = findGame(gameId);
      var zh = DEMO.demo.games[parseInt(gameId.slice(1), 10) - 1].zh;
      return { gameId: gameId, status: "succeeded", reason: null, provider: "bing",
        patch: target ? { gameId: target.gameId, revision: target.revision, title: zh, translatedTitle: zh, titleDisplayMode: "translated" } : null };
    });
    return { state: "succeeded", result: job.kind === "titles"
      ? { total: job.gameIds.length, completed: job.gameIds.length, succeeded: job.gameIds.length, failed: 0, skipped: 0, pending: 0, items: finalItems, unprocessedGameIds: [], stopReason: null }
      : null };
  }

  // ---------------------------------------------------------------- IPC
  var OP = {
    "settings.get": function () { return state.settings; },
    "settings.update": function (p) {
      for (var key in p) if (key !== "expectedRevision" && key in state.settings) state.settings[key] = p[key];
      state.settings.revision += 1;
      return state.settings;
    },
    "settings.reset": function () { return state.settings; },
    "host.status": function () { return { libraryInitialized: true, version: "1.7.22", dataDirectory: "D:\\Demo\\GameLibrary" }; },
    "bridge_status": function () { return { connected: true }; },
    "library.init": function () { return { initialized: true }; },
    "capabilities.get": function () {
      return { availableOperations: [
        "candidates.review_batch", "titles.translate", "profiles.discover", "tags.update",
        "mtool.prepare", "unity_translation.status", "launch.plan"
      ] };
    },
    "events.read": function () { return { nextCursor: 1, latestCursor: 1, items: [] }; },
    "roots.list": function () { return { items: state.roots.map(function (root) { return Object.assign({}, root); }) }; },
    "roots.add": function (p) {
      var existing = state.roots.filter(function (root) { return root.path === p.root; })[0];
      if (existing) return existing;
      var root = { rootId: "root-" + (state.roots.length + 1), path: p.root, kind: p.kind || "library", revision: 1 };
      state.roots.push(root);
      return root;
    },
    "roots.remove": function () { return {}; },
    "tags.list": function () { return { items: state.tags }; },
    "tags.create": function (p) {
      var tag = { tagId: "tag-" + (state.tags.length + 1), kind: "user", name: p.name, category: p.category || "special", sortOrder: state.tags.length, starred: 0, displayName: null, revision: 1, gameCount: 0 };
      state.tags.push(tag);
      return tag;
    },
    "tags.assign": function (p) {
      var game = findGame(p.gameId);
      var tag = findTag(p.tagId);
      if (game && tag && !hasTag(game, tag.name)) {
        game.tags.push({ kind: tag.kind, name: tag.name });
        game.revision += 1;
      }
      refreshTagCounts();
      return {};
    },
    "tags.unassign": function (p) {
      var game = findGame(p.gameId);
      var tag = findTag(p.tagId);
      if (game && tag) {
        game.tags = game.tags.filter(function (ref) { return ref.name !== tag.name; });
        game.revision += 1;
      }
      refreshTagCounts();
      return {};
    },
    "views.list": function () { return { items: [] }; },
    "notifications.list": function () { return { items: [] }; },
    "notifications.acknowledge": function () { return {}; },
    "candidates.list": function (p) {
      var offset = p.offset || 0;
      return { total: state.candidates.length, items: state.candidates.slice(offset, offset + (p.limit || 1000)) };
    },
    "candidates.review_batch": function (p) {
      var action = p.action;
      var results = (p.items || []).map(function (item) {
        var candidate = state.candidates.filter(function (entry) { return entry.candidateId === item.candidateId; })[0];
        if (!candidate) return { candidateId: item.candidateId, result: fail("cand", "候选不存在") };
        var index = parseInt(candidate.candidateId.replace("cand-", ""), 10) - 1;
        if (action === "accept") {
          var game = makeGame(index);
          game.acceptedUtc = nowIso(0);
          state.games.push(game);
          candidate.reviewState = "accepted";
        } else if (action === "defer") {
          candidate.reviewState = "deferred";
        } else {
          candidate.reviewState = "ignored";
        }
        state.candidates = state.candidates.filter(function (entry) { return entry.reviewState === "pendingReview"; });
        refreshTagCounts();
        return { candidateId: item.candidateId,
          result: envelope("cand", { reviewState: candidate.reviewState, revision: 2, gameId: action === "accept" ? gameIdAt(index) : null, ignoreId: null, similarTo: [] }) };
      });
      return { items: results };
    },
    "scan.start": function (p) {
      var candidates = [];
      if (CONF.addsCandidates) {
        for (var i = CONF.existing; i < DEMO.demo.games.length; i += 1) candidates.push(i);
      }
      state.scanJob = { jobId: "scan-1", root: p.root, startedAt: Date.now(), duration: 2600, candidates: candidates };
      return { jobId: "scan-1" };
    },
    "scan.coverage": function () {
      var job = state.scanJob;
      if (!job) return { state: "succeeded", coverage: { scannedDirectories: 0, candidatesFound: 0 } };
      var progress = scanProgress(job);
      if (progress.running) return { state: "running", coverage: progress.coverage };
      finishScan(job);
      return { state: "succeeded", coverage: { scannedDirectories: 28, candidatesFound: job.candidates.length } };
    },
    "scan.cancel": function () { if (state.scanJob) state.scanJob.startedAt = Date.now() - 10 * 1000; return {}; },
    "games.list": function (p) { return listGames(p); },
    "games.get": function (p) {
      var game = findGame(p.gameId);
      if (!game) return fail("g", "游戏不存在");
      var dto = gameDto(game);
      dto.fingerprint = null;
      dto.similarTo = [];
      dto.removalPlan = { targetPath: game.rootPath + "\\" + game.originalTitle, scope: "directory", error: null };
      return dto;
    },
    "games.update": function (p) {
      var game = findGame(p.gameId);
      if (!game) return fail("g", "游戏不存在");
      if (typeof p.favorite === "boolean") game.favorite = p.favorite;
      if (typeof p.played === "boolean") game.played = p.played;
      if (typeof p.rating === "number") game.rating = p.rating;
      game.revision += 1;
      game.updatedUtc = nowIso(0);
      return {};
    },
    "games.remove": function () { return {}; },
    "fields.set": function (p) {
      var game = findGame(p.gameId);
      if (!game) return fail("g", "游戏不存在");
      if (p.field === "title") { game.originalTitle = p.value; if (game.titleDisplayMode === "original") game.title = p.value; }
      if (p.field === "summary") game.summary = p.value;
      game.revision += 1;
      return { revision: game.revision };
    },
    "titles.translate": function (p) {
      var jobId = "title-" + Date.now();
      state.jobs[jobId] = { jobId: jobId, kind: "titles", startedAt: Date.now(), duration: 2200, gameIds: p.gameIds || [] };
      return { jobId: jobId };
    },
    "titles.set_translated": function (p) {
      var game = findGame(p.gameId);
      if (game) applyTranslated(game, p.title);
      return {};
    },
    "titles.set_display": function (p) {
      var game = findGame(p.gameId);
      if (game) {
        game.titleDisplayMode = p.mode === "translated" ? "translated" : "original";
        game.title = displayTitle(game);
        game.revision += 1;
      }
      return {};
    },
    "translation.get": function (p) {
      var game = findGame(p.gameId);
      return { userOverride: game && game.userOverride ? game.userOverride : "Auto", inherited: "Auto",
        effective: "NotRequired", isRequired: !!(game && game.userOverride === "Required"), revision: 1 };
    },
    "translation.set": function (p) {
      var game = findGame(p.gameId);
      if (game) game.userOverride = p.override;
      return { userOverride: p.override, isRequired: p.override === "Required", revision: 2 };
    },
    "translation.override": function () { return {}; },
    "profiles.list": function (p) {
      var game = findGame(p.gameId);
      if (!game) return { items: [], recommendedProfileId: null };
      var dir = game.rootPath + "\\" + game.originalTitle;
      var exe = dir + "\\" + game.originalTitle.replace(/[^A-Za-z0-9]/g, "") + ".exe";
      return { items: [{ profileId: "p-" + game.gameId, gameId: game.gameId, executablePath: exe, argv: [], cwd: dir,
        isDefault: true, revision: 1, source: "manual", validationStatus: "verified" }], recommendedProfileId: "p-" + game.gameId };
    },
    "profiles.create": function () { return {}; },
    "profiles.discover": function () { var jobId = "discover-" + Date.now(); state.jobs[jobId] = { jobId: jobId, kind: "discover", startedAt: Date.now(), duration: 900, gameIds: [] }; return { jobId: jobId }; },
    "profiles.set_default": function () { return {}; },
    "profiles.remove": function () { return {}; },
    "profiles.restore": function () { return {}; },
    "assets.list": function (p) {
      var game = findGame(p.gameId);
      if (!game || !game.coverAssetId) return { items: [] };
      return { items: [{ assetId: game.coverAssetId, isCurrent: true, importedUtc: game.acceptedUtc }] };
    },
    "assets.get": function (p) {
      var assetId = String(p.assetId || "");
      if (assetId.indexOf("cover-g") !== 0) return fail("a", "封面不存在");
      return { mimeType: "image/svg+xml", dataBase64: coverBase64(assetId.slice(6)) };
    },
    "assets.choose": function () { return null; },
    "assets.import": function (p) { return { assetId: "cover-" + p.gameId }; },
    "jobs.get": function (p) {
      var job = state.jobs[p.jobId];
      if (!job) return { state: "succeeded", result: null };
      return jobState(job);
    },
    "jobs.cancel": function (p) {
      var job = state.jobs[p.jobId];
      if (job) job.startedAt = 0;
      return {};
    },
    "launch.plan": function (p) { return { planId: "plan-" + p.gameId, gameId: p.gameId, profileId: p.profileId }; },
    "launch.execute": function (p) {
      var jobId = "launch-" + Date.now();
      state.jobs[jobId] = { jobId: jobId, kind: "launch", startedAt: Date.now(), duration: 1e9, gameIds: [] };
      state.launchAttempt = { attemptId: "attempt-1", state: "queued" };
      return { attemptId: "attempt-1", state: "queued", jobId: jobId };
    },
    "launch.status": function () { return { state: "processCreated" }; },
    "unity_translation.status": function () {
      var item = unityItem();
      return { items: item ? [item] : [], needsSettings: !state.unity.settings.hasKey };
    },
    "unity_translation.configure": function (p) {
      state.unity.attempt = { attemptId: "att-" + p.gameIds[0], gameId: p.gameIds[0], startedAt: Date.now() };
      return {};
    },
    "unity_translation.restore": function () { state.unity.attempt = null; return {}; },
    "unity_translation.confirm": function () { state.unity.attempt = null; return {}; },
    "unity_translation.settings.get": function () { return state.unity.settings; },
    "unity_translation.settings.set": function (p) {
      state.unity.settings = { provider: p.provider, endpoint: p.endpoint, model: p.model, hasKey: true, boundTagId: state.unity.settings.boundTagId };
      return state.unity.settings;
    },
    "unity_translation.settings.import": function () { state.unity.settings.hasKey = true; return state.unity.settings; },
    "mtool.settings.get": function () { return { toolRoot: state.mtool.toolRoot }; },
    "mtool.settings.set": function (p) { state.mtool.toolRoot = p.toolRoot; return { toolRoot: state.mtool.toolRoot }; },
    "mtool.status": function (p) {
      var status = state.mtool.statuses[p.gameId];
      if (!status) return { state: "idle", reason: "添加未翻译标签或点击生成配方后检查" };
      return status;
    },
    "mtool.prepare": function (p) {
      var game = findGame(p.gameId);
      var dir = game ? game.rootPath + "\\" + game.originalTitle : LIBRARY;
      state.mtool.statuses[p.gameId] = {
        state: "ready",
        reason: "已生成 MTool 启动配方；尚未验证游戏内翻译效果",
        scriptPath: dir + "\\MToolLaunch.bat",
        backupPath: state.mtool.toolRoot ? state.mtool.toolRoot + "\\loaders\\Config.ini.bak" : null
      };
      return state.mtool.statuses[p.gameId];
    },
    "mtool.launch": function () { return {}; },
    "tools.discover": function (p) { return { path: MTOOL_ROOT }; },
    "ignores.list": function () { return { items: [] }; },
    "ignores.create": function () { return {}; },
    "ignores.remove": function () { return {}; },
    "views.create": function () { return {}; },
    "views.remove": function () { return {}; }
  };

  function nativeCommand(command, args) {
    if (command === "bridge_request") {
      var request = (args && args.request) || {};
      var operationId = request.operationId;
      var parameters = request.parameters || {};
      var handler = OP[operationId];
      var requestId = request.requestId;
      return delay(10).then(function () {
        if (!handler) return fail(requestId, "示例数据未实现 " + operationId, "UnknownOperation");
        try {
          return envelope(requestId, handler(parameters) || {});
        } catch (error) {
          return fail(requestId, String(error && error.message || error));
        }
      });
    }
    return delay(2).then(function () {
      switch (command) {
        case "plugin:window|is_maximized": return false;
        case "plugin:window|minimize":
        case "plugin:window|toggle_maximize":
        case "plugin:window|close":
        case "plugin:window|start_dragging": return null;
        case "plugin:app|version": return "1.7.22";
        case "plugin:app|name": return "GameLibrary";
        case "plugin:opener|open_url": return null;
        case "plugin:path|picture_dir": return PICTURES;
        case "plugin:path|join": return [args && args.directory, args && args.file].filter(Boolean).join("\\");
        case "plugin:path|dirname": return String((args && args.path) || "").replace(/[\\/][^\\/]*$/, "");
        case "plugin:dialog|open": return pickPath(args && args.options);
        case "plugin:dialog|save": return "D:\\Demo\\screenshot.png";
        case "open_game_directory": return { path: args && args.path };
        case "show_main_window": return null;
        case "set_close_to_tray": return { enabled: !!(args && args.enabled) };
        case "set_ui_language": return { language: args && args.language };
        case "resolve_data_directory": return "D:\\Demo\\GameLibrary";
        default: return null;
      }
    });
  }

  function pickPath(options) {
    var title = String(options && options.title || "");
    if (title.indexOf("MTool") !== -1) return MTOOL_ROOT;
    if (title.indexOf("Config.ini") !== -1) return MTOOL_ROOT + "\\loaders\\Config.ini";
    if (title.indexOf("主程序") !== -1) return LIBRARY + "\\Glass Harbor\\GlassHarbor.exe";
    if (title.indexOf("封面") !== -1) return PICTURES + "\\cover.png";
    if (title.indexOf("目录") !== -1) return LIBRARY;
    return LIBRARY;
  }

  // Tauri v2 injects these globals; the product reads them through @tauri-apps/api.
  function install(handler) {
    var internals = {
      invoke: handler,
      transformCallback: function (callback, once) {
        var id = Math.floor(Math.random() * 1e9);
        window["_" + id] = function (payload) { if (once) delete window["_" + id]; callback(payload); };
        return id;
      },
      unregisterCallback: function (id) { delete window["_" + id]; },
      convertFileSrc: function (path) { return path; },
      metadata: { currentWindow: { label: "main" }, currentWebview: { label: "main" }, windows: ["main"] }
    };
    window.__TAURI_INTERNALS__ = internals;
    window.__TAURI__ = { core: { invoke: handler }, event: {}, window: {}, path: {}, app: {}, dialog: {} };
    return internals;
  }

  var handler = function (command, args) { return nativeCommand(command, args); };
  install(handler);

  // ---------------------------------------------------------------- boot
  resetState();
  window.__PROMO__ = {
    scene: SCENE,
    get state() { return state; },
    dispatch: function (operationId, parameters) { return nativeCommand("bridge_request", { request: { requestId: "promo", operationId: operationId, parameters: parameters || {} } }); },
    reset: function () { resetState(); refreshTagCounts(); }
  };
  console.info("[promo mock] scene=" + SCENE + " games=" + state.games.length + " roots=" + state.roots.length);
})();

