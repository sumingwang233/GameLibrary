import { mkdirSync, writeFileSync, readFileSync } from "node:fs";
import path from "node:path";

export async function verifyBatch(client, dataDirectory) {
  const root = path.join(path.dirname(dataDirectory), "batch-games");
  mkdirSync(root, { recursive: true });
  const files = ["Batch-A.exe", "Batch-B.exe", "Batch-C.exe"].map(name => path.join(root, name));
  for (const file of files) writeFileSync(file, "fixture-not-executable");
  const evaluate = async expression => {
    const response = await client.send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
    if (response.exceptionDetails) throw new Error(JSON.stringify(response.exceptionDetails));
    return response.result.value;
  };
  await evaluate(`(async () => {
    window.batchOp = async (operationId, parameters = {}) => {
      const r = await window.__TAURI__.core.invoke('bridge_request', { request: { requestId: crypto.randomUUID(), operationId, parameters: { ...parameters, idempotencyKey: crypto.randomUUID() } } });
      if (!r.ok) throw new Error(JSON.stringify(r));
      return r.data;
    };
    window.batchWait = async predicate => { const end = Date.now() + 10000; while (!predicate()) { if (Date.now() > end) throw new Error('UI timeout: ' + document.body.innerText); await new Promise(r => setTimeout(r, 100)); } };
    window.batchButton = text => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === text);
    await batchWait(() => document.querySelector('[aria-label="刷新"]'));
    if (!(await batchOp('host.status')).libraryInitialized) await batchOp('library.init');
    await batchOp('roots.add', { root: ${JSON.stringify(root)} });
    for (const sourcePath of ${JSON.stringify(files)}) await batchOp('games.create', { sourcePath });
    await batchOp('tags.create', { name: 'BatchTag' });
    document.querySelector('[aria-label="刷新"]').click();
    await batchWait(() => document.querySelectorAll('input[type="checkbox"]').length === 3);
  })()`);
  await evaluate(`(async () => {
    [...document.querySelectorAll('input[type="checkbox"]')].forEach(e => e.click());
    await batchWait(() => !batchButton('批量收藏').disabled);
    if (document.querySelector('[role="dialog"]')) throw new Error('checkbox opened details');
    batchButton('批量收藏').click();
    await batchWait(() => document.body.innerText.includes('已完成 3 个'));
    const games = await batchOp('games.list', { limit: 100 });
    if (games.items.filter(g => g.favorite).length !== 3) throw new Error('favorite failed');
    document.querySelector('[aria-label="紧凑视图"]').click();
    await batchWait(() => document.querySelector('ul input[type="checkbox"]'));
    [...document.querySelectorAll('input[type="checkbox"]')].forEach(e => { if (!e.checked) e.click(); });
    await batchWait(() => !batchButton('取消收藏').disabled);
    batchButton('取消收藏').click();
    await batchWait(() => [...document.querySelectorAll('input[type="checkbox"]')].every(e => !e.checked));
    await batchWait(() => [...document.querySelectorAll('input[type="checkbox"]')].every(e => !e.disabled));
    if ((await batchOp('games.list', { limit: 100 })).items.some(g => g.favorite)) throw new Error('unfavorite failed');
  })()`);
  await evaluate(`(async () => {
    [...document.querySelectorAll('input[type="checkbox"]')].forEach(e => e.click());
    const select = document.querySelector('select[aria-label="批量添加标签"]');
    select.value = [...select.options].find(o => o.text === 'BatchTag').value;
    select.dispatchEvent(new Event('change', { bubbles: true }));
    await batchWait(() => !batchButton('添加标签').disabled);
    batchButton('添加标签').click();
    await batchWait(() => [...document.querySelectorAll('input[type="checkbox"]')].every(e => !e.checked));
    await batchWait(() => [...document.querySelectorAll('input[type="checkbox"]')].every(e => !e.disabled));
    if ((await batchOp('tags.list')).items.find(t => t.name === 'BatchTag').gameCount !== 3) throw new Error('tag failed');
    [...document.querySelectorAll('input[type="checkbox"]')].slice(0, 2).forEach(e => e.click());
    await batchWait(() => !batchButton('批量移除').disabled);
    batchButton('批量移除').click();
    await batchWait(() => batchButton('移除游戏'));
    batchButton('移除游戏').click();
    await batchWait(() => document.querySelectorAll('input[type="checkbox"]').length === 1);
    if ((await batchOp('games.list', { limit: 100 })).total !== 1) throw new Error('batch remove failed');
  })()`);
  await evaluate(`(async () => {
    [...document.querySelectorAll('button')].find(b => b.textContent.includes('游戏库目录')).click();
    await batchWait(() => document.querySelector('button[aria-label^="移除目录 "]'));
    document.querySelector('button[aria-label^="移除目录 "]').click();
    await batchWait(() => batchButton('移除目录'));
    batchButton('移除目录').click();
    await batchWait(() => !document.querySelector('[role="dialog"]'));
    if ((await batchOp('games.list', { limit: 100 })).total !== 0) throw new Error('root removal failed');
    if ((await batchOp('roots.list')).total !== 0) throw new Error('root remains');
  })()`);
  for (const file of files) if (readFileSync(file, "utf8") !== "fixture-not-executable") throw new Error("game file changed");
  return { favorite: true, unfavorite: true, tags: true, batchRemove: true, rootRemove: true, gameFilesPreserved: true };
}
