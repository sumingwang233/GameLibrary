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
    document.querySelector('button[aria-label="最大化"]').click();
    await batchWait(() => document.querySelector('button[aria-label="还原窗口"]'));
    document.querySelector('button[aria-label="还原窗口"]').click();
    await batchWait(() => document.querySelector('button[aria-label="最大化"]'));
    if (!(await batchOp('host.status')).libraryInitialized) await batchOp('library.init');
    await batchOp('roots.add', { root: ${JSON.stringify(root)} });
    for (const sourcePath of ${JSON.stringify(files)}) await batchOp('games.create', { sourcePath });
    await batchOp('tags.create', { name: 'BatchTag' });
    document.querySelector('[aria-label="刷新"]').click();
    await batchWait(() => document.querySelectorAll('input[type="checkbox"]').length === 3);
  })()`);
  await evaluate(`(async () => {
    document.querySelector('article[role="button"]').click();
    await batchWait(() => document.querySelector('[role="dialog"]'));
    for (const label of ['收藏', '取消收藏']) {
      await batchWait(() => [...document.querySelectorAll('[role="dialog"] button')].some(b => b.textContent.trim() === label && !b.disabled));
      [...document.querySelectorAll('[role="dialog"] button')].find(b => b.textContent.trim() === label).click();
      await batchWait(() => [...document.querySelectorAll('[role="dialog"] button')].some(b => b.textContent.trim() === (label === '收藏' ? '取消收藏' : '收藏') && !b.disabled));
      if (!document.querySelector('#root > *')) throw new Error('favorite caused blank screen');
    }
    const title = document.querySelector('input[aria-label="游戏标题"]');
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(title, '中文标题回归');
    title.dispatchEvent(new Event('input', { bubbles: true }));
    await batchWait(() => [...document.querySelectorAll('[role="dialog"] button')].some(b => b.textContent.trim() === '保存' && !b.disabled));
    [...document.querySelectorAll('[role="dialog"] button')].find(b => b.textContent.trim() === '保存').click();
    await batchWait(() => document.querySelector('[role="dialog"] h2')?.textContent === '中文标题回归');
    const tagsTab = [...document.querySelectorAll('[role="tab"]')].find(b => b.textContent.trim() === '标签');
    tagsTab.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await batchWait(() => document.querySelector('input[aria-label="新标签名称"]'));
    const tagInput = document.querySelector('input[aria-label="新标签名称"]');
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(tagInput, '详情新建标签');
    tagInput.dispatchEvent(new Event('input', { bubbles: true }));
    await batchWait(() => !batchButton('新建并添加').disabled);
    batchButton('新建并添加').click();
    await batchWait(() => [...document.querySelectorAll('[role="dialog"] button[aria-pressed="true"]')].some(b => b.textContent.trim() === '详情新建标签'));
    const detail = (await batchOp('games.list', { limit: 100 })).items.find(g => g.title === '中文标题回归');
    await batchOp('profiles.create', { gameId: detail.gameId, executablePath: detail.rootPath, argv: [], cwd: ${JSON.stringify(root)}, isDefault: true });
    const policy = await batchOp('translation.get', { gameId: detail.gameId });
    await batchOp('translation.set', { gameId: detail.gameId, override: 'Required', expectedRevision: policy.revision });
    await batchWait(() => !batchButton('开始游戏').disabled);
    batchButton('开始游戏').click();
    await batchWait(() => document.querySelector('[role="dialog"] [role="alert"]')?.textContent.includes('翻译'));
    if ((await batchOp('launch.history', { gameId: detail.gameId })).items?.length) throw new Error('Required launched without translation');
    document.querySelector('[role="dialog"] button[aria-label="关闭"]').click();
    await batchWait(() => !document.querySelector('[role="dialog"]'));
  })()`);
  await evaluate(`(async () => {
    [...document.querySelectorAll('input[type="checkbox"]')].forEach(e => e.click());
    await batchWait(() => !batchButton('批量收藏').disabled);
    if (document.querySelector('[role="dialog"]')) throw new Error('checkbox opened details');
    batchButton('批量收藏').click();
    await batchWait(() => document.body.innerText.includes('已完成 3 个'));
    await batchWait(() => [...document.querySelectorAll('input[type="checkbox"]')].every(e => !e.disabled));
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
  return { windowControls: true, detailCreateTag: true, launchErrorVisible: true, favorite: true, unfavorite: true, tags: true, batchRemove: true, rootRemove: true, gameFilesPreserved: true };
}
